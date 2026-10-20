using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Voices.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Where this PC's voices are kept for the speaking route (as Companion › Voice › Voices uses it).</summary>
    private string VoiceDestination() =>
        homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts) is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: { } snapshot }
            ? snapshot.DestinationId : F5Destination;

    /// <summary>The voice Martlet speaks with now, by its shared ID: the one the speaking route keeps, else the one chosen on
    /// your computers, else the one applied on this PC; null when none is.</summary>
    private string? CurrentVoiceId()
    {
        if (homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts) is { RouteType: SetupRouteType.GatewayF5, Reference: { } reference })
            return reference.ReferenceRevision;
        if (store is null) return null;
        if (F5Voices.LoadLibrary(store.DataDirectory)?.ChosenVoice is { } chosen) return chosen.Id;
        try { return F5Voices.Applied(store.DataDirectory)?.ReferenceRevision; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or F5Exception) { return null; }
    }

    /// <summary>Applies a voice on this PC and, when a computer speaks, saves it on the speaking route. Returns whether the
    /// route changed.</summary>
    private async Task<bool> ApplyVoiceAsync(SettingsLoadResult loaded, F5ReferenceSnapshot voice, CancellationToken token)
    {
        var route = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        if (f5 is not null && voice.Rights.ProcessingDestinationId != f5.GatewaySnapshot!.DestinationId)
            throw new InvalidOperationException($"Add '{voice.PresetName}' again for the computer that will speak it.");
        var reference = await F5Voices.ApplyAsync(store!.DataDirectory, voice, token);
        if (f5 is null) return false;
        var next = SetupSettings.ApplyF5Reference(loaded.Settings!, reference);
        var saved = await setupService!.SaveAsync(next, loaded.Revision, token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = next;
        FollowSavedSetup(saved.Save.Revision, "Martlet's voice changed.");
        return true;
    }

    /// <summary>Earlier versions shipped voices this one does not (<see cref="F5BundledVoices.Retired"/>): the F5-TTS example
    /// clip and the "anime" voices. A route still speaking with one, or a store that still applies one, moves to the voice
    /// <see cref="F5Voices.DefaultAsync"/> picks; a voice the owner picked is left alone. Returns the saved settings when the
    /// route moved, otherwise null.</summary>
    private async Task<AppSettings?> LeaveRetiredVoiceAsync(SettingsLoadResult loaded, CancellationToken token)
    {
        if (store is null || setupService is null || closing || changes.Busy || loaded.Settings is not { } settings)
            return null;
        var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        var speaking = f5?.Reference is { } used && F5BundledVoices.IsRetired(used.AudioSha256);
        // Without a speaking route the store's applied voice is the chosen one; read it once per run.
        if (!speaking && (f5?.Reference is not null || retiredVoiceChecked)) return null;
        retiredVoiceChecked = true;
        if (!speaking && !System.IO.Directory.Exists(F5Voices.Directory(store.DataDirectory))) return null;
        var destination = f5?.GatewaySnapshot!.DestinationId ?? F5Destination;
        if (changes.TryTake() is not { } turn) return null;
        try
        {
            if (!speaking)
            {
                var applied = F5Voices.Applied(store.DataDirectory);
                if (applied is null || !F5Voices.IsRetired(applied)) return null;
            }
            var voice = await F5Voices.DefaultAsync(store.DataDirectory, destination, token,
                SpeechEngines.ForRoute(f5?.GatewaySnapshot!.RouteId));
            var reference = await F5Voices.ApplyAsync(store.DataDirectory, voice, token);
            AppSettings? next = null;
            if (speaking)
            {
                next = SetupSettings.ApplyF5Reference(settings, reference);
                var saved = await setupService.SaveAsync(next, loaded.Revision, token);
                if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
            }
            // Nothing speaks with the retired recording now: this PC's copy goes (a later pass retries if the store is busy), and
            // the paired computers get the change.
            try { await F5Voices.ReconcileAsync(store.DataDirectory, destination, ClusterDevice, null, null, token); }
            catch (Exception error) when (F5SharedVoices.IsFailure(error)) { }
            QueueSpeakingVoiceSync();
            ActionText.Text = $"Martlet no longer includes the voice it spoke with, so it now speaks with '{voice.PresetName}'.";
            if (next is not null) openConversation?.ReloadWhenIdle("Martlet's voice changed.");
            return next;
        }
        catch (OperationCanceledException) { return null; }
        catch (F5Exception error)
        {
            ActionText.Text = "Couldn't switch from the voice Martlet no longer includes: " + F5Voices.Describe(error);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException)
        {
            ActionText.Text = "Couldn't switch from the voice Martlet no longer includes: " + error.Message;
            return null;
        }
        finally { turn.Dispose(); }
    }

    /// <summary>Adds a voice from a recording, then switches to it. Its words are filled in with Listening's speech-to-text
    /// (or Parakeet, when downloaded) unless that is in the cloud.</summary>
    private async Task AddVoiceAsync(string destination)
    {
        if (store is null || closing) return;
        var transcriber = RecordingTranscriber.Choose(homeSettings?.Setup?.Routes, parakeet, LocalVoices.SpeechRoot(store.DataDirectory));
        F5ReferenceSnapshot? added;
        // A Parakeet loaded just for the dialog waits for any transcription still running before it unloads.
        try { added = F5AddVoiceDialog.Add(this, store.DataDirectory, destination, transcriber); }
        finally { if (transcriber is not null) Task.Run(transcriber.Dispose).Forget(); }
        if (added is null) return;
        QueueSpeakingVoiceSync();
        await UseVoiceAsync(F5SharedVoices.Id(added), destination);
    }

    /// <summary>Records the owner reading Martlet's lines out loud (Record your own voice), adds that voice, then switches to it.</summary>
    private async Task RecordVoiceAsync(string destination)
    {
        if (store is null || closing) return;
        if (setupOperations.IsRunning)
        {
            ActionText.Text = "Martlet is busy with another task; try again when it finishes.";
            return;
        }
        var added = OwnVoiceDialog.Record(this, store.DataDirectory, destination, setupOperations, audioSessionEvents, homeSettings?.Audio?.Input);
        if (added is null) return;
        QueueSpeakingVoiceSync();
        await UseVoiceAsync(F5SharedVoices.Id(added), destination);
    }

    private async Task RemoveVoiceAsync(VoiceItem voice, string destination)
    {
        if (store is null || closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Remove '{voice.Name}' from this PC and your other Martlet computers?", "Remove voice"))
            return;
        try
        {
            await F5Voices.RemoveAsync(store.DataDirectory, destination, voice.Id, lifetime.Token);
            QueueSpeakingVoiceSync();
            ActionText.Text = $"'{voice.Name}' was removed from your voices.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (!closing) RenderHome();
        }
    }

    // ---------- sharing voices with every paired Martlet computer ----------

    private readonly DispatcherTimer speakingVoiceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool speakingVoiceBusy, speakingVoiceQueued;
    private string speakingVoiceStatus = "No other Martlet computers are paired yet, so your voices stay on this PC.";

    private void InitializeSpeakingVoices() => speakingVoiceTimer.Tick += (_, _) => SyncSpeakingVoicesAsync().Forget();

    private void StartSpeakingVoices()
    {
        if (store is null || closing) return;
        speakingVoiceTimer.Start();
        SyncSpeakingVoicesAsync().Forget();
    }

    /// <summary>Shares a change soon (debounced), so every paired computer has it before it speaks.</summary>
    private void QueueSpeakingVoiceSync()
    {
        if (speakingVoiceQueued || closing) return;
        speakingVoiceQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { speakingVoiceQueued = false; }
            await SyncSpeakingVoicesAsync();
        }
    }

    /// <summary>Shares the voices Martlet speaks with through every paired host: merges each host's copy of the list here,
    /// copies recordings this PC lacks from a host that has them (starter voices come from Martlet itself), deletes removed
    /// voices, gives each host the merged list and every recording it lacks, then speaks with the voice chosen on another
    /// computer. Hosts older than shared voices are skipped. Nothing is written while no host is paired.</summary>
    private async Task SyncSpeakingVoicesAsync()
    {
        if (speakingVoiceBusy || closing || store is null) return;
        if (!clusterEnabled)
        {
            speakingVoiceStatus = "Keep Martlet the same on all my computers is off, so your voices stay on this PC.";
            return;
        }
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            speakingVoiceStatus = "No other Martlet computers are paired yet, so your voices stay on this PC.";
            return;
        }
        speakingVoiceBusy = true;
        var dataDirectory = store.DataDirectory;
        var token = lifetime.Token;
        var before = speakingVoiceStatus;
        var changed = false;
        try
        {
            var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
            var destination = f5?.GatewaySnapshot!.DestinationId ?? F5Destination;
            var copies = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    var copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadSpeakingVoicesAsync(token));
                    return (Host: host, Copy: (HostSpeakingVoices?)copy, Old: false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (HostConnectionException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    return (host, null, true);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    return (host, null, false);
                }
            }));
            var reachable = copies.Where(c => c.Copy is not null).ToArray();
            var digest = F5Voices.View(dataDirectory).Digest();
            F5Voices.Commit(dataDirectory, reachable.Aggregate(SpeakingVoiceLibrary.Empty, (merged, c) => SpeakingVoiceLibrary.Merge(merged, c.Copy!.Library)));

            async Task<byte[]?> FetchAsync(string sha256, CancellationToken fetchToken)
            {
                foreach (var (host, _, _) in reachable.Where(c => c.Copy!.Present.Contains(sha256)))
                {
                    try
                    {
                        if (await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadSpeakingVoiceAudioAsync(sha256, fetchToken))
                            is { } audio) return audio;
                    }
                    catch (OperationCanceledException) when (fetchToken.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error)) { }
                }
                return null;
            }
            var keep = f5?.Reference?.ReferenceRevision is { } speaking ? new HashSet<string>([speaking], StringComparer.Ordinal) : null;
            var result = await F5Voices.ReconcileAsync(dataDirectory, destination, ClusterDevice, FetchAsync, keep, token);
            var library = result.Library;
            changed = result.Added > 0 || result.Removed > 0 || library.Digest() != digest;

            var shared = 0;
            foreach (var (host, copy, _) in reachable)
            {
                try
                {
                    var present = copy!.Present;
                    if (copy.Library.Digest() != library.Digest())
                    {
                        var merged = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.MergeSpeakingVoicesAsync(library, token));
                        library = F5Voices.Commit(dataDirectory, merged.Library);
                        present = merged.Present;
                    }
                    foreach (var voice in library.Live.Where(v => !present.Contains(v.AudioSha256!)))
                    {
                        if (!result.Local.TryGetValue(voice.Id, out var snapshot) ||
                            await F5SharedVoices.ReadAsync(F5Voices.Directory(dataDirectory), snapshot, token) is not { } audio)
                            continue;
                        try { await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.SendSpeakingVoiceAudioAsync(voice.AudioSha256!, audio, token)); }
                        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(audio); }
                    }
                    shared++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error) || F5SharedVoices.IsFailure(error)) { }
            }
            changed |= await FollowChosenVoiceAsync(library, result.Local, token);
            var old = copies.Where(c => c.Old).Select(c => c.Host.HostId).ToArray();
            speakingVoiceStatus = $"Voices shared with {shared} of {hosts.Count} computer{(hosts.Count == 1 ? "" : "s")} at {DateTime.Now:t}." +
                (result.Waiting > 0 ? $" {result.Waiting} voice{(result.Waiting == 1 ? " is" : "s are")} still copying to this PC." : "") +
                (result.Full > 0 ? " " + F5Voices.FullText(result.Full) : "") +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} to share voices there." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (F5SharedVoices.IsFailure(error) || ClusterSync.IsHostFailure(error))
        {
            speakingVoiceStatus = "Couldn't share voices just now: " + (error is F5Exception failure ? F5Voices.Describe(failure) : error.Message);
        }
        finally
        {
            speakingVoiceBusy = false;
            // Re-render when something changed, not merely the time of the last check.
            static string Gist(string text) => System.Text.RegularExpressions.Regex.Replace(text, @" at [^.]+\.", ".");
            if (!closing && openTab == CompanionTab.Voice && !CompanionContent.IsKeyboardFocusWithin &&
                (changed || Gist(before) != Gist(speakingVoiceStatus)))
                RenderTab();
        }
    }

    /// <summary>Speaks with the voice chosen on another computer once its recording is here and the speaking engine can use
    /// it. Returns whether this PC switched.</summary>
    private async Task<bool> FollowChosenVoiceAsync(SpeakingVoiceLibrary library, IReadOnlyDictionary<string, F5ReferenceSnapshot> local,
        CancellationToken token)
    {
        if (store is null || setupService is null || closing || changes.Busy || setupOperations.IsRunning) return false;
        if (library.Chosen is not { } chosen || library.ChosenVoice is not { } voice || !local.TryGetValue(voice.Id, out var snapshot))
            return false;
        var loaded = await setupService.LoadAsync(token);
        if (loaded.Error is not null || loaded.Settings is not { } settings) return false;
        var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        if ((f5?.Reference?.ReferenceRevision ?? F5Voices.Applied(store.DataDirectory)?.ReferenceRevision) == voice.Id) return false;
        var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot!.RouteId) ?? SpeakingEngineChoice.Current;
        if (SpeechEngines.ReferenceProblem(engine, snapshot.AudioFormat.DurationMilliseconds, voice.ClipMilliseconds) is not null) return false;
        if (changes.TryTake() is not { } turn) return false;
        try
        {
            await ApplyVoiceAsync(loaded, snapshot, token);
            if (chosen.UpdatedBy != ClusterDevice) ActionText.Text = $"Martlet now speaks with '{voice.Name}', as chosen on {chosen.UpdatedBy}.";
            return true;
        }
        finally { turn.Dispose(); }
    }
}

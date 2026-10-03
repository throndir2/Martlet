using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Voices: the voices Martlet speaks with, copied from short recordings (no training). There are
/// no built-in voices: a new list starts with a few starter voices, and every voice can be heard, used in one click and
/// removed. The list, the chosen voice and every recording are shared with the owner's paired Martlet computers (through
/// each paired host), so whichever computer speaks already holds the recording and any of them can be the companion.
/// Using a voice applies it in the F5 preset store and, when a computer speaks, saves it on the speaking route.</summary>
public partial class MainWindow
{
    private SoundPlayer? voicePlayer;
    private bool retiredSampleChecked;

    /// <summary>One voice in the list: its ID (reference revision), name, where it comes from (<paramref name="Note"/>), this
    /// PC's recording of it (null while it is still being copied here, or for a starter voice not used yet) and its automation
    /// key: a starter recording's key, else the first 16 hex digits of its ID. <paramref name="Clips"/> are the lengths of the
    /// recordings it was made from (null for one recording).</summary>
    private sealed record VoiceItem(string Id, string Name, string? Note, string? Transcript, string AudioSha256, int DurationMilliseconds,
        DateTimeOffset AddedAt, F5ReferenceSnapshot? Local, string Key, bool Starter, IReadOnlyList<int>? Clips = null)
    {
        /// <summary>Its recording is here, or Martlet carries it.</summary>
        internal bool Available => Local is not null || Starter;
    }

    /// <summary>The voices to list: every live voice of the shared list in the order it joined, then recordings only this PC
    /// has (added before voices were shared; they join the list on the next change).</summary>
    private static List<VoiceItem> VoiceItems(SpeakingVoiceLibrary library, IReadOnlyDictionary<string, F5ReferenceSnapshot> local)
    {
        static (string Key, bool Starter) KeyOf(string id, string sha256) =>
            F5BundledVoices.ForAudio(sha256) is { } starter ? (starter.Key, true) : (id[..16], false);
        var items = library.Live.Select(v =>
        {
            var (key, starter) = KeyOf(v.Id, v.AudioSha256!);
            return new VoiceItem(v.Id, v.Name!, v.Note, v.Transcript, v.AudioSha256!, v.DurationMilliseconds, v.AddedAt,
                local.GetValueOrDefault(v.Id), key, starter, v.ClipMilliseconds);
        }).ToList();
        foreach (var (id, snapshot) in local.Where(pair => library.Find(pair.Key) is null).OrderBy(pair => pair.Value.CreatedAtUtc))
        {
            var (key, starter) = KeyOf(id, snapshot.AudioSha256);
            items.Add(new(id, snapshot.PresetName, null, null, snapshot.AudioSha256, snapshot.AudioFormat.DurationMilliseconds,
                snapshot.CreatedAtUtc, snapshot, key, starter));
        }
        return items;
    }

    private Border VoicesCard(SetupRoute? route)
    {
        var f5 = route?.RouteType == SetupRouteType.GatewayF5 ? route : null;
        var destination = f5?.GatewaySnapshot?.DestinationId ?? F5Destination;
        // The engine that speaks (or will): some clone only recordings of certain lengths (GPT-SoVITS: 3-10 seconds).
        var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot?.RouteId) ?? SpeakingEngineChoice.Current;
        string? Cannot(VoiceItem item) => SpeechEngines.ReferenceProblem(engine, item.DurationMilliseconds, item.Clips);
        string Language(string? transcript) => engine == SpeechEngines.GptSovits && transcript is not null
            ? SpeechEngines.ReferenceLanguage(transcript) == "ja" ? " Japanese recording." : " English recording."
            : "";
        var stack = new List<UIElement>
        {
            Heading("Voices"),
            Note($"{engine.Name} copies these voices from short recordings" + (engine.SampleLimits is { } limits ? $" ({limits})" : "") +
                ". A voice can have several recordings" + (engine.MultipleReferences ? $"; {engine.Name} learns from each." : ", heard one after another.") +
                " They're shared with your paired computers.", new Thickness(0, 0, 0, 10))
        };
        var library = SpeakingVoiceLibrary.Empty;
        IReadOnlyDictionary<string, F5ReferenceSnapshot> local = new Dictionary<string, F5ReferenceSnapshot>();
        F5ReferenceSnapshot? applied = null;
        try
        {
            if (store is not null)
            {
                library = F5Voices.View(store.DataDirectory);
                (local, applied) = F5Voices.Local(store.DataDirectory, destination);
            }
        }
        catch (F5Exception error) { stack.Add(Note(F5Voices.Describe(error), new Thickness(0, 0, 0, 8))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stack.Add(Note("Martlet couldn't read your voices: " + error.Message, new Thickness(0, 0, 0, 8)));
        }
        var items = VoiceItems(library, local);
        // The voice in use: the one the speaking route keeps when a computer speaks, else the one applied on this PC.
        var inUseId = f5?.Reference?.ReferenceRevision ?? applied?.ReferenceRevision;
        var mark = f5 is null ? "chosen" : "in use";
        var inUse = items.FirstOrDefault(i => i.Id == inUseId);
        var status = Note($"{items.Count} voice{(items.Count == 1 ? "" : "s")}. " + (inUse is not null
                ? $"{Capitalized(mark)}: {(inUse.Starter ? inUse.Name : "one of your recordings")}."
                : inUseId is not null ? $"{Capitalized(mark)}: a voice no longer in the list. Use another voice."
                : items.FirstOrDefault(i => i.Available) is { } first ? $"None {mark} yet; Martlet starts with {(first.Starter ? first.Name : "your first voice")}."
                : "Add a voice to speak with."),
            new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "F5VoicesStatus");
        stack.Add(status);
        var shared = Note(speakingVoiceStatus, new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(shared, "F5VoicesShared");
        stack.Add(shared);

        var chosenId = library.ChosenVoice?.Id;
        // A voice made from several recordings says how many and whether the engine learns from each or hears them joined.
        string Recorded(VoiceItem item) => item.Clips is { Count: > 1 } clips
            ? $"{clips.Count} recordings, {item.DurationMilliseconds / 1000d:0.#} seconds joined, added {item.AddedAt.ToLocalTime():d}. " +
              (SpeechEngines.UsesClips(engine, clips) ? $"{engine.Name} learns from each recording."
                  : $"{engine.Name} hears them one after another with a short pause.")
            : $"{item.DurationMilliseconds / 1000d:0.#} second recording, added {item.AddedAt.ToLocalTime():d}.";
        foreach (var item in items)
        {
            var used = item.Id == inUseId;
            var cannot = Cannot(item);
            var detail = (item.Note ?? Recorded(item)) +
                (item.Available ? "" : " Copying to this PC...") + (cannot is null ? Language(item.Transcript) : " " + cannot);
            stack.Add(VoiceRow(item.Name, detail, used, mark, item.Key, status: item.Starter,
                () => PlayVoiceAsync(item), () => UseVoiceAsync(item.Id, destination),
                used || item.Id == chosenId ? null : () => RemoveVoiceAsync(item, destination), cannot, item.Available));
        }
        // A voice still in use here that is not in the list (removed on another computer, or the retired F5-TTS sample).
        if (inUseId is not null && inUse is null)
        {
            var name = f5?.Reference?.PresetName ?? applied?.PresetName ?? "Previous voice";
            stack.Add(VoiceRow(name, "No longer in your voice list. Use another voice; it is removed from this PC then.", true, mark,
                inUseId[..16], status: false, () => PlayAppliedAsync(applied, name), () => Task.CompletedTask, null));
        }
        stack.Add(Row(PageButton("Add a voice...", () => AddVoiceAsync(destination).Forget(), id: "F5AddVoice")));
        return Card([.. stack]);
    }

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>One voice: its name (with the in-use mark), a detail line, and Play, Use and (when it may go) Remove. Controls
    /// are identified by <paramref name="key"/>; starter recordings' titles are also readable status (<c>F5VoiceRow-key</c>),
    /// never the names of the owner's own recordings, and every voice's detail line (<c>F5VoiceDetail-key</c>: its length or
    /// recordings, where it comes from and why the engine can't use it; never its name or words) is readable status.
    /// <paramref name="cannot"/> says why the speaking engine cannot use it and <paramref name="available"/> is false while its
    /// recording is still being copied here (Use is then off).</summary>
    private UIElement VoiceRow(string name, string detail, bool inUse, string mark, string key, bool status, Func<Task> play, Func<Task> use,
        Func<Task>? remove, string? cannot = null, bool available = true)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name + (inUse ? $"  \u00b7  {mark}" : "") + (cannot is null ? "" : "  \u00b7  wrong length for this engine"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (status) AutomationProperties.SetAutomationId(title, $"F5VoiceRow-{key}");
        text.Children.Add(title);
        var line = Note(detail, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(line, $"F5VoiceDetail-{key}");
        text.Children.Add(line);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Func<Task> run, string id)
        {
            var button = PageButton(label, () => run().Forget(), id: $"F5Voice{id}-{key}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {name}");
            return button;
        }
        var playButton = Small("Play", play, "Play");
        playButton.IsEnabled = available;
        buttons.Children.Add(playButton);
        var useButton = Small(inUse ? "In use" : "Use", use, "Use");
        useButton.IsEnabled = !inUse && cannot is null && available;
        if (cannot is not null)
        {
            useButton.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(useButton, true);
            AutomationProperties.SetHelpText(useButton, cannot);
        }
        buttons.Children.Add(useButton);
        if (remove is not null) buttons.Children.Add(Small("Remove", remove, "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Plays a voice's recording on Windows' default speakers: this PC's copy, or a starter clip Martlet carries.</summary>
    private async Task PlayVoiceAsync(VoiceItem item)
    {
        if (store is null || closing) return;
        try
        {
            var audio = await F5Voices.ReadAudioAsync(store.DataDirectory, item.Local, item.AudioSha256, lifetime.Token);
            voicePlayer?.Stop();
            voicePlayer = new SoundPlayer(new MemoryStream(audio));
            voicePlayer.Play();
            ActionText.Text = $"Playing '{item.Name}'...";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't play '{item.Name}': {error.Message}";
        }
    }

    private Task PlayAppliedAsync(F5ReferenceSnapshot? applied, string name) =>
        applied is null ? Task.CompletedTask : PlayVoiceAsync(new(applied.ReferenceRevision, name, null, null, applied.AudioSha256,
            applied.AudioFormat.DurationMilliseconds, applied.CreatedAtUtc, applied, applied.ReferenceRevision[..16], false));

    /// <summary>Makes a voice the one Martlet speaks with on all of the owner's computers. This PC applies it now (when a
    /// computer speaks, the speaking route keeps it, so the next conversation uses it); the others follow when they next
    /// share voices.</summary>
    private async Task UseVoiceAsync(string voiceId, string destination)
    {
        if (store is null || setupService is null || closing) return;
        if (savingTab || assigningRole || setupOperations.IsRunning)
        {
            ActionText.Text = "Another change is still finishing. Try again in a moment.";
            return;
        }
        assigningRole = true;
        var token = lifetime.Token;
        try
        {
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var route = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
            var result = await F5Voices.ReconcileAsync(store.DataDirectory, destination, ClusterDevice, null, null, token);
            if (!result.Local.TryGetValue(voiceId, out var voice))
                throw new InvalidOperationException("That voice is still being copied to this PC. Try again in a moment.");
            var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot!.RouteId) ?? SpeakingEngineChoice.Current;
            if (SpeechEngines.ReferenceProblem(engine, voice.AudioFormat.DurationMilliseconds,
                    result.Library.Find(voiceId)?.ClipMilliseconds) is { } problem)
                throw new InvalidOperationException($"{problem} Add another recording of '{voice.PresetName}' or choose another engine.");
            var saved = await ApplyVoiceAsync(loaded, voice, token);
            F5Voices.Choose(store.DataDirectory, voiceId);
            QueueSpeakingVoiceSync();
            ActionText.Text = saved
                ? $"Martlet now uses the voice '{voice.PresetName}' on all your computers. Reload any open conversation to use it."
                : $"'{voice.PresetName}' is your voice on all your computers.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            assigningRole = false;
            if (!closing) RenderHome();
        }
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
        return true;
    }

    /// <summary>Earlier versions started F5 with the F5-TTS example clip, a male voice Martlet no longer ships, and kept it on
    /// the speaking route. A route still speaking with it, or a store that still applies it, moves to the voice
    /// <see cref="F5Voices.DefaultAsync"/> picks; a voice the owner picked is left alone. Returns the saved settings when the
    /// route moved, otherwise null.</summary>
    private async Task<AppSettings?> LeaveRetiredSampleAsync(SettingsLoadResult loaded, CancellationToken token)
    {
        if (store is null || setupService is null || closing || savingTab || assigningRole || loaded.Settings is not { } settings)
            return null;
        var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        var speaking = f5?.Reference is { } used && F5BundledVoices.IsRetiredSample(used.AudioSha256);
        // Without a speaking route the store's applied voice is the chosen one; read it once per run.
        if (!speaking && (f5?.Reference is not null || retiredSampleChecked)) return null;
        retiredSampleChecked = true;
        if (!speaking && !System.IO.Directory.Exists(F5Voices.Directory(store.DataDirectory))) return null;
        var destination = f5?.GatewaySnapshot!.DestinationId ?? F5Destination;
        assigningRole = true;
        try
        {
            if (!speaking)
            {
                var applied = F5Voices.Applied(store.DataDirectory);
                if (applied is null || !F5Voices.IsRetiredSample(applied)) return null;
            }
            var voice = await F5Voices.DefaultAsync(store.DataDirectory, destination, token,
                SpeechEngines.ForRoute(f5?.GatewaySnapshot!.RouteId));
            var reference = await F5Voices.ApplyAsync(store.DataDirectory, voice, token);
            var moved = $"An old sample voice is no longer used. Martlet now speaks with '{voice.PresetName}'. " +
                "Pick another under Companion › Voice › Voices.";
            if (!speaking)
            {
                ActionText.Text = moved;
                return null;
            }
            var next = SetupSettings.ApplyF5Reference(settings, reference);
            var saved = await setupService.SaveAsync(next, loaded.Revision, token);
            if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
            ActionText.Text = moved;
            openConversation?.ReloadWhenIdle("Martlet's voice changed.");
            return next;
        }
        catch (OperationCanceledException) { return null; }
        catch (F5Exception error)
        {
            ActionText.Text = "Couldn't switch from the old sample voice: " + F5Voices.Describe(error);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException)
        {
            ActionText.Text = "Couldn't switch from the old sample voice: " + error.Message;
            return null;
        }
        finally { assigningRole = false; }
    }

    /// <summary>Adds a voice from a recording, then switches to it.</summary>
    private async Task AddVoiceAsync(string destination)
    {
        if (store is null || closing) return;
        var added = F5AddVoiceDialog.Add(this, store.DataDirectory, destination);
        if (added is null) return;
        QueueSpeakingVoiceSync();
        await UseVoiceAsync(F5SharedVoices.Id(added), destination);
    }

    private async Task RemoveVoiceAsync(VoiceItem voice, string destination)
    {
        if (store is null || closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Remove '{voice.Name}'?\n\nMartlet deletes it on this PC and your other Martlet computers. " +
                "Any original file of yours isn't touched.", "Remove voice"))
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
                catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
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
        if (store is null || setupService is null || closing || savingTab || assigningRole || setupOperations.IsRunning) return false;
        if (library.Chosen is not { } chosen || library.ChosenVoice is not { } voice || !local.TryGetValue(voice.Id, out var snapshot))
            return false;
        var loaded = await setupService.LoadAsync(token);
        if (loaded.Error is not null || loaded.Settings is not { } settings) return false;
        var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        if ((f5?.Reference?.ReferenceRevision ?? F5Voices.Applied(store.DataDirectory)?.ReferenceRevision) == voice.Id) return false;
        var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot!.RouteId) ?? SpeakingEngineChoice.Current;
        if (SpeechEngines.ReferenceProblem(engine, snapshot.AudioFormat.DurationMilliseconds, voice.ClipMilliseconds) is not null) return false;
        assigningRole = true;
        try
        {
            if (await ApplyVoiceAsync(loaded, snapshot, token)) openConversation?.ReloadWhenIdle("Martlet's voice changed.");
            if (chosen.UpdatedBy != ClusterDevice) ActionText.Text = $"Martlet now speaks with '{voice.Name}', as chosen on {chosen.UpdatedBy}.";
            return true;
        }
        finally { assigningRole = false; }
    }
}

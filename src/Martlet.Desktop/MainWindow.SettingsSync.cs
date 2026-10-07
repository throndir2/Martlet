using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>One Martlet on every computer: the settings that make the companion what it is (how it thinks, listens and speaks
/// with their API keys, the Thinking fallback, personality, replies, prompts, memory on or off, lorebooks, the character and
/// its emotes and motions, how you talk, speech bubbles, the theme, recognizing voices, Voice ID, what Martlet may do with Home
/// Assistant, app updates and what Thinking models hear and see) are kept the same on all the owner's computers through the paired hosts, next
/// to who does what (docs/CLUSTER.md). Every 15 seconds while "Keep Martlet the same on all my computers" is on, this PC reads
/// each host's copy when it changed, records what changed here, follows what is newer elsewhere and gives hosts with an older
/// copy the merged one. Changes made while it is off (or offline) are recorded with their time and win only if newer.</summary>
public partial class MainWindow
{
    private const string CharacterKey = "character", TalkKey = "talk", SpeechDisplayKey = "speech-display", AppearanceKey = "appearance",
        CharacterActionsKey = "character-actions", TouchTemperamentKey = "touch-temperament", VoiceRecognitionKey = "voice-recognition", VoiceIdKey = "voice-id",
        SmartHomeKey = "smart-home", UpdatesKey = "updates", ModelAbilitiesKey = "model-abilities";
    private static readonly JsonSerializerOptions SharedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    private readonly DispatcherTimer settingsTimer = new() { Interval = ClusterSync.Interval };
    private SharedSettingsNode? settingsNode;
    private AppSettingsSections? appSections;
    private bool settingsBusy;
    private DateTimeOffset? settingsCheckedAt;
    /// <summary>Each host's copy as last read or merged, with its digest, so a copy is read again only when it changed.</summary>
    private readonly Dictionary<string, (string Digest, SharedSettings Copy)> settingsCopies = new(StringComparer.Ordinal);
    private (int Current, int Hosts, int Down, int Old) settingsHosts;
    private string? settingsLastChange;
    private bool characterChanged;

    private void InitializeSettingsSync()
    {
        settingsTimer.Tick += (_, _) => SyncSettingsAsync().Forget();
        if (store is null || setupService is null)
        {
            SettingsSyncClaimButton.IsEnabled = false;
            return;
        }
        var directory = store.DataDirectory;
        appSections = new AppSettingsSections(setupService, new WindowsCredentialStore(), directory, lorebooks,
            role => HostJob.For(role) is { } job ? JobSavedRoute.Load(directory, job.SavedFile) : null, RouteAvailableAsync);
        settingsNode = new SharedSettingsNode(directory, ClusterDevice, [.. appSections.Sections, .. DesktopSections()], appSections.Invalidate);
        ShowSettingsStatus();
    }

    /// <summary>Starts the settings sync. It runs (recording changes made here) even while sync is off; only then does it
    /// leave the hosts alone.</summary>
    private void StartSettingsSync()
    {
        if (settingsNode is null || closing) return;
        settingsTimer.Start();
        SyncSettingsAsync().Forget();
    }

    private void QueueSettingsSync()
    {
        if (settingsNode is not null && !closing) Dispatcher.InvokeAsync(() => SyncSettingsAsync().Forget(), DispatcherPriority.ContextIdle);
    }

    private async Task SyncSettingsAsync()
    {
        if (settingsNode is null || settingsBusy || closing) return;
        // Pages, Setup and who does what change settings.json too; the next check runs once they are done.
        if (changes.Busy || setupOperations.IsRunning) return;
        settingsBusy = true;
        ChangeTurns.Turn? turn = null;
        SharedSettingsResult? result = null;
        try
        {
            var shared = clusterEnabled;
            var hosts = shared ? NetworkMap.Hosts(Inputs()) : [];
            var reads = await Task.WhenAll(hosts.Select(ReadSettingsCopyAsync));
            if (closing) return;
            if (setupOperations.IsRunning || (turn = changes.TryTake()) is null) return;
            result = await settingsNode.SyncAsync(reads.Select(r => r.Copy).OfType<SharedSettings>(), shared, DateTimeOffset.UtcNow, lifetime.Token);
            turn.Dispose();
            if (shared) await PushSettingsAsync(reads, result.Document);
            if (shared) settingsCheckedAt = DateTimeOffset.UtcNow;
            var digest = settingsNode.Document.Digest();
            var reachable = reads.Where(r => r.Ok).ToArray();
            settingsHosts = (reachable.Count(r => settingsCopies.GetValueOrDefault(r.HostId).Digest == digest), hosts.Count,
                reads.Count(r => !r.Ok && !r.Old), reads.Count(r => r.Old));
            foreach (var key in result.Recorded.Where(k => !SharedPc.IsRoleKey(k)))
                ErrorLog.Info(SharedPc.IsKey(key)
                    ? $"Shared settings: told your other computers that this PC is a {(Role == DeviceRole.Host ? "host" : "companion")} PC" +
                      (ThisPcHost() is { } own ? $" and runs {own.HostId}." : ".")
                    : $"Shared settings: {SharedTitle(key)} changed on this PC; your other computers follow it.");
            if (result.Applied.Count > 0) await AfterSettingsAppliedAsync(result.Applied);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException or JsonException)
        {
            if (!closing) ActionText.Text = "Couldn't sync settings with your other computers: " + error.Message;
            ErrorLog.Warn("Shared settings sync failed.", error);
        }
        finally
        {
            turn?.Dispose();
            settingsBusy = false;
            if (!closing)
            {
                ShowSettingsStatus();
                // Another computer's role (companion or host PC) arrives with the settings and changes how the map draws it.
                if (DevicesPage.IsVisible && NetworkDevicesSignature() != networkDevicesShown) RenderMap();
            }
        }
    }

    private static string SharedTitle(string key) => key switch
    {
        AppSettingsSections.Thinking => "Thinking",
        AppSettingsSections.Listening => "Listening",
        AppSettingsSections.Speaking => "Speaking",
        AppSettingsSections.ThinkingFallback => "the Thinking fallback",
        AppSettingsSections.Companion => "the personality",
        AppSettingsSections.Replies => "reply settings",
        AppSettingsSections.Prompts => "prompts",
        AppSettingsSections.Memory => "memory",
        AppSettingsSections.Lorebooks => "lorebooks",
        CharacterKey => "the character",
        TalkKey => "how you talk",
        SpeechDisplayKey => "speech bubbles and subtitles",
        AppearanceKey => "the theme",
        CharacterActionsKey => "emotes and motions",
        TouchTemperamentKey => "touch temperaments",
        VoiceRecognitionKey => "recognizing voices",
        VoiceIdKey => "Voice ID",
        SmartHomeKey => "what Martlet may do with Home Assistant",
        UpdatesKey => "app updates",
        ModelAbilitiesKey => "what Thinking models hear and see",
        WorkSharingSettings.SharedKey => "sharing work between your computers",
        _ when SharedPc.IsKey(key) || SharedPc.IsRoleKey(key) => "whether this PC is a companion or a host",
        _ => key
    };

    /// <summary>A host's copy of the shared settings: read again only when its digest changed. Hosts older than shared settings
    /// answer request.invalid.</summary>
    private async Task<(string HostId, PairedHost Host, SharedSettings? Copy, bool Ok, bool Old)> ReadSettingsCopyAsync(PairedHost host)
    {
        try
        {
            var copy = await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                var digest = await connection.ReadSettingsDigestAsync(lifetime.Token);
                if (settingsCopies.TryGetValue(host.HostId, out var known) && known.Digest == digest) return known.Copy;
                var read = await connection.ReadSettingsAsync(lifetime.Token);
                settingsCopies[host.HostId] = (read.Digest(), read);
                return read;
            });
            return (host.HostId, host, copy, true, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            return (host.HostId, host, null, false, true);
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
        {
            return (host.HostId, host, null, false, false);
        }
    }

    /// <summary>Gives every reachable host whose copy differs the merged copy (with the API keys this PC knows); a host's reply
    /// may carry newer changes, which the next check follows.</summary>
    private async Task PushSettingsAsync(IEnumerable<(string HostId, PairedHost Host, SharedSettings? Copy, bool Ok, bool Old)> reads,
        SharedSettings merged)
    {
        var digest = merged.Digest();
        foreach (var read in reads.Where(r => r.Ok))
        {
            if (closing) return;
            if (settingsCopies.GetValueOrDefault(read.HostId).Digest == digest) continue;
            try
            {
                var copy = await ClusterSync.WithConnectionAsync(read.Host.Pairing, connection => connection.MergeSettingsAsync(merged, lifetime.Token));
                settingsCopies[read.HostId] = (copy.Digest(), copy);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
            {
                ErrorLog.Warn($"Couldn't give {read.HostId} the shared settings; trying again on the next check.", error);
            }
        }
    }

    /// <summary>Brings the rest of Martlet up to date after this PC took settings from another computer.</summary>
    private async Task AfterSettingsAppliedAsync(IReadOnlyList<SharedSettingsChange> applied)
    {
        foreach (var change in applied)
            ErrorLog.Info($"Shared settings: took {SharedTitle(change.Key)} from {change.By} (changed there {change.At.ToLocalTime():g}).");
        var by = applied.Select(a => a.By).Distinct(StringComparer.Ordinal).ToArray();
        settingsLastChange = $"{Sentence(string.Join(", ", applied.Select(a => SharedTitle(a.Key))))} now match{(applied.Count == 1 ? "es" : "")} " +
            $"{(by.Length == 1 && by[0] != ClusterDevice ? by[0] : "your other computers")} ({DateTimeOffset.Now:t}).";
        ActionText.Text = settingsLastChange;
        if (applied.Any(a => a.Key == AppSettingsSections.Lorebooks)) homeLore = null;
        await RefreshHomeAsync();
        // A route taken from another computer is not a choice made here, so who does what doesn't record it as one.
        if (applied.Any(a => a.Key is AppSettingsSections.Thinking or AppSettingsSections.Listening or AppSettingsSections.Speaking))
            foreach (var job in clusterObserved.Keys.ToArray()) clusterObserved[job] = ObservedJob(job);
        if (characterChanged && avatar.IsShowing && Role == DeviceRole.Companion && !closing)
        {
            characterChanged = false;
            if (await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        }
        characterChanged = false;
        if (openTab is not null && !tabEdited) RenderTab();
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void ShowSettingsStatus()
    {
        if (settingsNode is null)
        {
            SettingsSyncStatusText.Text = "Settings can't be shared until Martlet can use its data folder.";
            SettingsSyncWaitingText.Visibility = Visibility.Collapsed;
            return;
        }
        var shared = settingsNode.Document.Settings.Count(s => !SharedSettings.IsDeviceKey(s.Key));
        string text;
        if (!clusterEnabled) text = "Settings stay on this PC while this is off; changes made here are shared when you turn it on.";
        else if (settingsCheckedAt is not { } checkedAt) text = "Settings: checking your hosts...";
        else if (settingsHosts.Hosts == 0) text = "Settings: pair a Martlet host to share them with your other computers.";
        else
            text = $"Settings: {shared} shared, the same on {settingsHosts.Current} of {settingsHosts.Hosts} host{(settingsHosts.Hosts == 1 ? "" : "s")}; " +
                $"checked {checkedAt.ToLocalTime():t}." +
                (settingsHosts.Down > 0 ? $" {settingsHosts.Down} not responding." : "") +
                (settingsHosts.Old > 0 ? $" Update {(settingsHosts.Old == 1 ? "one host" : settingsHosts.Old + " hosts")} to share settings." : "");
        if (settingsLastChange is not null && clusterEnabled) text += " " + settingsLastChange;
        SettingsSyncStatusText.Text = text;
        var waiting = settingsNode.Waiting;
        SettingsSyncWaitingText.Text = waiting.Count == 0 ? "" :
            "Not followed here yet: " + string.Join(" ", waiting.Select(w => $"{Sentence(SharedTitle(w.Key))}: {w.Value}"));
        SettingsSyncWaitingText.Visibility = waiting.Count == 0 || !clusterEnabled ? Visibility.Collapsed : Visibility.Visible;
        SettingsSyncClaimButton.IsEnabled = clusterEnabled;
    }

    private async void SettingsSyncClaim_Click(object sender, RoutedEventArgs e)
    {
        if (settingsNode is null || closing || !clusterEnabled) return;
        if (!ConfirmationDialog.Confirm(this, "Make all your computers use this PC's settings? How Martlet thinks, listens and speaks " +
                "(with this PC's API keys), its character with its emotes and motions, personality, replies, prompts, lorebooks, how you " +
                "talk, the theme, Voice ID, recognizing voices, what Martlet may do with Home Assistant, app updates and what Thinking models " +
                "hear and see are copied from " +
                "this PC to every paired computer, replacing what they have.", "Use this PC's settings"))
            return;
        while (settingsBusy && !closing) await Task.Delay(100);
        try
        {
            var count = await settingsNode.ClaimAllAsync(DateTimeOffset.UtcNow, lifetime.Token);
            ErrorLog.Info($"Shared settings: the owner made {count} settings from this PC the ones every computer uses.");
            ActionText.Text = $"Your other computers now take {count} settings from this PC.";
            settingsLastChange = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            ActionText.Text = "Couldn't share this PC's settings: " + error.Message;
        }
        await SyncSettingsAsync();
    }

    // ---------- the desktop's own shared preferences ----------

    private IEnumerable<ISharedSection> DesktopSections()
    {
        var directory = store!.DataDirectory;
        // Only this PC writes its own entry, so every computer's Devices map knows what each of the others is.
        yield return new DelegateSection(SharedPc.Key(ClusterDevice), "This PC's role", _ =>
            Task.FromResult<SharedLocal?>(new(new SharedPc(Role == DeviceRole.Host ? SharedPc.HostRole : SharedPc.CompanionRole,
                ThisPcHost()?.HostId).Write(), null, false, DateTimeOffset.UtcNow)),
            (_, _) => Task.FromResult(SharedApply.Done));
        // Whether this PC is a companion or a host PC: this PC records its own choice, and follows it when another computer
        // asks it to switch (Make it a host PC on that computer's Devices map).
        yield return new DelegateSection(SharedPc.RoleKey(ClusterDevice), "Companion or host PC", _ =>
            Task.FromResult<SharedLocal?>(new(SharedPc.WriteRole(Role), null, deviceRole is null,
                FileTime(Path.Combine(directory, DeviceRolePreference.FileName)))),
            (setting, _) => Task.FromResult(FollowRoleRequest(setting)));
        yield return new DelegateSection(CharacterKey, "Character", ReadCharacterAsync, ApplyCharacterAsync);
        yield return new DelegateSection(TalkKey, "How you talk", _ =>
        {
            var prefs = Talk;
            var value = new SharedTalk(prefs.HandsFree, prefs.PauseIndex, prefs.SpeakReplies, prefs.HearVoice == true, prefs.BargeIn, prefs.ScreenChattiness,
                prefs.WordCheck, prefs.TranscribeFirst, prefs.HearVoice, SharedTalk.ThreeWayHearing);
            var path = Path.Combine(directory, "talk-preferences.json");
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(value, SharedJson), null, !File.Exists(path), FileTime(path)));
        }, (setting, _) =>
        {
            var value = JsonSerializer.Deserialize<SharedTalk>(setting.Value, SharedJson) ?? throw new JsonException();
            SaveTalk(Talk with
            {
                HandsFree = value.HandsFree, PauseIndex = Math.Clamp(value.PauseIndex, 0, TalkPreferences.Pauses.Length - 1),
                SpeakReplies = value.SpeakReplies, HearVoice = value.Hearing, BargeIn = value.BargeIn,
                ScreenChattiness = (int)ChattinessTags.Choice(value.ScreenChattiness),
                WordCheck = Enum.IsDefined(value.WordCheck) ? value.WordCheck : ListeningSensitivity.Normal,
                TranscribeFirst = value.TranscribeFirst
            });
            return Task.FromResult(SharedApply.Done);
        });
        yield return new DelegateSection(SpeechDisplayKey, "Speech bubbles", _ =>
        {
            // Where the bubble sits (beside the character or in one place, and its offsets) depends on this PC's screens, like
            // the character's own place, so only whether bubbles and subtitles show travels.
            var path = Path.Combine(directory, "speech-display.json");
            var prefs = captions.Preferences;
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(new SharedSpeechDisplay(prefs.SpeechBubbles, prefs.Subtitles), SharedJson),
                null, !File.Exists(path), FileTime(path)));
        }, (setting, _) =>
        {
            var value = JsonSerializer.Deserialize<SharedSpeechDisplay>(setting.Value, SharedJson) ?? throw new JsonException();
            return Task.FromResult(captions.Update(captions.Preferences with { SpeechBubbles = value.SpeechBubbles, Subtitles = value.Subtitles })
                ? SharedApply.Done : SharedApply.Waiting("They couldn't be saved on this PC."));
        });
        yield return new DelegateSection(AppearanceKey, "Theme", _ =>
        {
            var path = Path.Combine(directory, "appearance.txt");
            var theme = Application.Current is App { SelectedTheme: var selected } ? selected : AppearanceTheme.Light;
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(theme.ToString()), null, !File.Exists(path), FileTime(path)));
        }, (setting, _) =>
        {
            var name = JsonSerializer.Deserialize<string>(setting.Value);
            if (Appearance.Parse(name) is not { } theme)
                return Task.FromResult(SharedApply.Waiting("It was chosen on a newer Martlet. Update this PC to use it."));
            Appearance.Save(directory, theme);
            if (IsLoaded) ThemeChoice.SelectedIndex = (int)theme;
            else (Application.Current as App)?.ApplyTheme(theme, characterThemes.Colors(theme));
            return Task.FromResult(SharedApply.Done);
        });
        yield return new DelegateSection(CharacterActionsKey, "Emotes and motions", _ =>
        {
            var path = CharacterActions.Path(directory);
            return Task.FromResult<SharedLocal?>(new(CharacterActions.Share(directory), null, !CharacterActions.HasAny(directory), FileTime(path)));
        }, async (setting, token) =>
        {
            if (characterActions.Busy) return SharedApply.Waiting("The Thinking model is naming this PC's emotes and motions.");
            await CharacterActions.ReplaceAllAsync(directory, setting.Value, token);
            if (characterActions.Current is not null)
                await characterActions.LoadAsync(avatar.IsShowing ? avatar.InspectedProfile : homeAvatar, force: true, token);
            return SharedApply.Done;
        });
        // Each persona's touch temperament travels with the personas, so every computer's character reacts to touch the same way.
        yield return new DelegateSection(TouchTemperamentKey, "Touch temperament", _ =>
        {
            var path = CharacterTouchTemperaments.Path(directory);
            return Task.FromResult<SharedLocal?>(new(CharacterTouchTemperaments.Share(directory), null, !CharacterTouchTemperaments.HasAny(directory), FileTime(path)));
        }, async (setting, token) =>
        {
            if (characterTemperaments.Busy) return SharedApply.Waiting("The Thinking model is deciding a touch temperament on this PC.");
            await CharacterTouchTemperaments.ReplaceAllAsync(directory, setting.Value, token);
            characterTemperaments.Reload();
            return SharedApply.Done;
        });
        yield return new DelegateSection(VoiceRecognitionKey, "Recognizing voices", _ =>
            Task.FromResult<SharedLocal?>(localVoices.Available
                ? new(JsonSerializer.Serialize(new SharedSwitch(localVoices.Enabled), SharedJson), null, localVoices.EnabledChangedAt is null,
                    localVoices.EnabledChangedAt)
                : null),
            (setting, _) =>
            {
                var value = JsonSerializer.Deserialize<SharedSwitch>(setting.Value, SharedJson) ?? throw new JsonException();
                if (value.On != localVoices.Enabled) localVoices.SetEnabled(value.On);
                return Task.FromResult(SharedApply.Done);
            });
        yield return new DelegateSection(VoiceIdKey, "Voice ID", _ =>
        {
            if (!voiceIdentity.Available) return Task.FromResult<SharedLocal?>(null);
            var print = voiceIdentity.Current;
            var value = new SharedVoiceId(Talk.VoiceId, print is null ? null
                : new SharedVoiceprint(print.Embedding, print.Threshold, print.Consistency, print.CreatedAt, print.SpeechSeconds));
            var times = new[] { FileTime(Path.Combine(directory, VoiceIdentity.FileName)), FileTime(Path.Combine(directory, "talk-preferences.json")) };
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(value, SharedJson), null, !value.On && print is null,
                times.Max()));
        }, (setting, _) =>
        {
            var value = JsonSerializer.Deserialize<SharedVoiceId>(setting.Value, SharedJson) ?? throw new JsonException();
            if (value.Voiceprint is { } print)
            {
                if (print.Embedding is not { Length: Martlet.Audio.SpeakerEncoder.EmbeddingSize } || print.Embedding.Any(v => !float.IsFinite(v)) ||
                    !float.IsFinite(print.Threshold) || print.Threshold is < VoiceIdentity.MinimumThreshold or > VoiceIdentity.MaximumThreshold)
                    return Task.FromResult(SharedApply.Waiting("Its voiceprint was made by a newer Martlet. Update this PC to use it."));
                var current = voiceIdentity.Current;
                if (current is null || !current.Embedding.SequenceEqual(print.Embedding) || current.Threshold != print.Threshold ||
                    current.Consistency != print.Consistency || current.CreatedAt != print.CreatedAt || current.SpeechSeconds != print.SpeechSeconds)
                    voiceIdentity.Save(new Voiceprint(print.Embedding, print.Threshold, print.Consistency, print.CreatedAt, print.SpeechSeconds));
            }
            else if (voiceIdentity.Current is not null) voiceIdentity.Delete();
            if (Talk.VoiceId != value.On) SaveTalk(Talk with { VoiceId = value.On });
            return Task.FromResult(SharedApply.Done);
        });
        yield return new DelegateSection(SmartHomeKey, "Smart home permissions", _ =>
        {
            if (!smartHome.Connected) return Task.FromResult<SharedLocal?>(null);
            var saved = smartHome.Preferences;
            var value = new SharedHomePermissions(saved.Control, saved.AllowSensitive, saved.ModelTools);
            // Turning control on with the connection is what connecting does, not a choice of its own.
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(value, SharedJson), null, !saved.AllowSensitive && !saved.ModelTools,
                smartHome.ChangedAt));
        }, (setting, _) =>
        {
            var value = JsonSerializer.Deserialize<SharedHomePermissions>(setting.Value, SharedJson) ?? throw new JsonException();
            if (!smartHome.Connected) return Task.FromResult(SharedApply.Waiting("Home Assistant isn't connected on this PC yet."));
            if (!smartHome.SetControl(value.Control, value.AllowSensitive, value.ModelTools))
                return Task.FromResult(SharedApply.Waiting("They couldn't be saved on this PC."));
            if (value.ModelTools && value.Control) mcpTools.EnsureStarted(retry: true);
            if (SmartHomeCanRender()) RenderTab();
            return Task.FromResult(SharedApply.Done);
        });
        yield return new DelegateSection(UpdatesKey, "App updates", _ =>
        {
            var value = new SharedUpdates(updateChecksEnabled, updatePreferences.IntervalMinutes, updatePreferences.AutoInstall,
                updatePreferences.AutoUpdateHosts);
            var times = new[] { FileTime(Path.Combine(directory, "update-checks.txt")), FileTime(Path.Combine(directory, UpdatePreferences.FileName)) };
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(value, SharedJson), null, times.All(t => t is null), times.Max()));
        }, (setting, _) =>
        {
            var value = JsonSerializer.Deserialize<SharedUpdates>(setting.Value, SharedJson) ?? throw new JsonException();
            if (!UpdatePreferences.Intervals.Contains(value.IntervalMinutes))
                return Task.FromResult(SharedApply.Waiting("It was chosen on a newer Martlet. Update this PC to use it."));
            ApplySharedUpdates(value.Checks, new UpdatePreferences
            {
                IntervalMinutes = value.IntervalMinutes, AutoInstall = value.AutoInstall, AutoUpdateHosts = value.AutoUpdateHosts
            });
            return Task.FromResult(SharedApply.Done);
        });
        // What Thinking models were found to hear and see (their servers' metadata, Test hearing, a refused recording): found out
        // once, on whichever computer, for all of them.
        yield return new DelegateSection(ModelAbilitiesKey, "What Thinking models hear and see", _ =>
        {
            var path = Path.Combine(directory, ModelAbilities.FileName);
            var saved = ModelAbilities.Load(directory);
            return Task.FromResult<SharedLocal?>(new(saved.Share(), null, saved.Models.Count == 0, FileTime(path)));
        }, (setting, _) =>
        {
            if (ModelAbilities.Parse(setting.Value) is not { } shared)
                return Task.FromResult(SharedApply.Waiting("It was written by a newer Martlet. Update this PC to use it."));
            if (!shared.Save(directory)) return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            conversation?.ReloadAbilities();
            return Task.FromResult(SharedApply.Done);
        });
        // Devices › Sharing work: which computers each job may go to when the one doing it is busy, in what order, and which
        // computers are kept for one companion PC. The same on all computers, so every companion PC follows the same rules.
        yield return new DelegateSection(WorkSharingSettings.SharedKey, "Sharing work", _ =>
        {
            var path = Path.Combine(directory, WorkSharingSettings.FileName);
            var saved = WorkSharingSettings.Load(directory);
            return Task.FromResult<SharedLocal?>(new(saved.Share(), null, saved.IsDefault, FileTime(path)));
        }, (setting, _) =>
        {
            if (WorkSharingSettings.Parse(setting.Value) is not { } shared)
                return Task.FromResult(SharedApply.Waiting("It was written by a newer Martlet. Update this PC to use it."));
            if (!shared.Save(directory)) return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            WorkSharingRoster.Forget();
            if (DevicesPage.IsVisible) RenderWorkSharing();
            return Task.FromResult(SharedApply.Done);
        });
    }

    private sealed record SharedSpeechDisplay(bool SpeechBubbles, bool Subtitles);
    private sealed record SharedSwitch(bool On);
    private sealed record SharedVoiceprint(float[] Embedding, float Threshold, float Consistency, DateTimeOffset CreatedAt, double SpeechSeconds);
    private sealed record SharedVoiceId(bool On, SharedVoiceprint? Voiceprint);
    private sealed record SharedHomePermissions(bool Control, bool AllowSensitive, bool ModelTools);
    private sealed record SharedUpdates(bool Checks, int IntervalMinutes, bool AutoInstall, bool AutoUpdateHosts);

    // TranscribeFirst: Companion › Listening › When Thinking can hear you (an older computer leaves it out: straight, the default).
    // HearVoice is what an older computer reads (on only when chosen); HearChoice is Let Thinking hear my voice as chosen (null:
    // never chosen, on only while the recording stays on that computer), sent with HearVersion so a copy from an older computer,
    // whose off was only its default, leaves it unchosen.
    private sealed record SharedTalk(bool HandsFree, int PauseIndex, bool SpeakReplies, bool HearVoice, bool BargeIn, int ScreenChattiness,
        ListeningSensitivity WordCheck = ListeningSensitivity.Normal, bool TranscribeFirst = false, bool? HearChoice = null, int HearVersion = 0)
    {
        internal const int ThreeWayHearing = 1;
        internal bool? Hearing => HearVersion >= ThreeWayHearing ? HearChoice : HearVoice ? true : null;
    }

    /// <summary>The character as it travels: which model (a bundled one; one of your characters by its ID, shown from each
    /// computer's own copy; or a model file at the same place on every computer), its renderer, its Audio2Face mapping and
    /// whether it shows when Martlet starts. Who does lip-sync travels in the who-does-what plan; the overlay's place on the
    /// screen stays with each computer.</summary>
    private sealed record SharedCharacter
    {
        public required AvatarRenderer Renderer { get; init; }
        public required string Model { get; init; }
        public required JsonElement Configuration { get; init; }
        public bool AutoShow { get; init; }
    }

    private const string SharedModelPrefix = "shared:";

    private async Task<SharedLocal?> ReadCharacterAsync(CancellationToken token)
    {
        var loaded = await setupService!.LoadAsync(token);
        if (loaded.Settings is not { } settings) return null;
        var profiles = new AvatarProfileStore(store!.DataDirectory);
        AvatarProfile? profile;
        try { profile = (await profiles.LoadAsync(settings.Profile.Id, token)).Profile; }
        catch (ContractException error) { throw new ContractException(error.Code, "This PC's character settings can't be read: " + error.Message); }
        if (profile is null) return null;
        // One of your characters is named by its ID: every computer shows its own copy of the same files.
        var model = SharedCharacterModels.ForPath(store.DataDirectory, SharedCharacterModels.View(store.DataDirectory), profile.ModelPath) is { } shared
            ? SharedModelPrefix + shared.Id : profile.ModelPath;
        var value = new SharedCharacter { Renderer = profile.Renderer, Model = model, Configuration = profile.Configuration, AutoShow = profile.AutoShow };
        var isDefault = profile.ModelPath == BundledLive2D.Prefix + BundledLive2D.DefaultCharacter && !profile.AutoShow;
        return new(JsonSerializer.Serialize(value, SharedJson), null, isDefault, FileTime(profiles.FilePath));
    }

    private async Task<SharedApply> ApplyCharacterAsync(SharedSetting setting, CancellationToken token)
    {
        var value = JsonSerializer.Deserialize<SharedCharacter>(setting.Value, SharedJson) ?? throw new JsonException();
        if (avatarWindowOpen) return SharedApply.Waiting("The character settings window is open on this PC.");
        var directory = store!.DataDirectory;
        var path = value.Model;
        if (path.StartsWith(SharedModelPrefix, StringComparison.Ordinal))
        {
            var id = path[SharedModelPrefix.Length..];
            if (SharedCharacterModels.View(directory).Live.FirstOrDefault(m => m.Id == id) is not { } model)
                return SharedApply.Waiting("That character hasn't reached this PC's characters yet.");
            if (!SharedCharacterModels.IsComplete(directory, model))
                return SharedApply.Waiting($"'{model.Name}' is still being copied to this PC.");
            path = SharedCharacterModels.EntryPath(directory, model);
        }
        else if (!BundledLive2D.IsBuiltIn(path) && !File.Exists(path))
            return SharedApply.Waiting($"Its model file isn't on this PC ({Path.GetFileName(path)}). Add it in Companion › Character › Your " +
                "characters on the computer that has it, so it is copied here.");
        // A new PC takes the character before anything else is set up on it (its first settings give the profile its ID).
        AvatarProfile profile;
        string? revision;
        try { (profile, revision) = await Pairings().EnsureProfileAsync(token); }
        catch (InvalidOperationException error) { return SharedApply.Waiting(error.Message); }
        var next = profile with
        {
            Renderer = value.Renderer, ModelPath = path, Configuration = value.Configuration.Clone(), AutoShow = value.AutoShow,
            ResourceRevision = null
        };
        next.Validate();
        await new AvatarProfileStore(directory).SaveAsync(next, revision, token);
        homeAvatar = next;
        characterChanged = profile.ModelPath != next.ModelPath || profile.Renderer != next.Renderer;
        return SharedApply.Done;
    }

    private static DateTimeOffset? FileTime(string path)
    {
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Why this PC can't use a shared route yet: a Windows voice it doesn't have, Parakeet not downloaded, or Ollama
    /// without the model. Null when it can.</summary>
    private async Task<string?> RouteAvailableAsync(SetupRole role, SharedRoute route, CancellationToken token)
    {
        switch (route.Type)
        {
            case SharedRoute.WindowsTts:
                IReadOnlyList<WindowsVoice> voices;
                try { voices = await WindowsVoices.ListAsync(token); }
                catch (InvalidOperationException error) { return error.Message; }
                return voices.Any(v => v.Id == route.Voice) ? null
                    : $"The Windows voice {WindowsVoices.DisplayName(route.Voice)} isn't installed on this PC. Add it in Windows Settings › Time & language › Speech, or choose a voice in Companion › Voice.";
            case SharedRoute.Parakeet:
                return SharedParakeetWaiting(route.Model, model => parakeet?.Installed(model) == true);
            case SharedRoute.ChatCompletions when route.Origin == LocalOllamaBaseUrl:
                if (LocalOllama.Executable() is null) return "Ollama isn't installed on this PC. Set it up in Companion › Thinking.";
                var models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), token);
                return models is not null && !LocalOllama.Serves(models, route.Model)
                    ? $"Ollama on this PC doesn't have {route.Model} yet. Download it in Companion › Thinking."
                    : null;
            default:
                return null;
        }
    }

    /// <summary>For who does what: hands a job a host did back to the shared route, when the owner's computers share one.
    /// Shared is false when they share none (the caller then uses this PC's own Setup choice); Problem says why this PC can't
    /// use the shared route yet, and is null once it does.</summary>
    private async Task<(bool Shared, string? Problem)> HandBackToSharedRouteAsync(HostJob job)
    {
        if (settingsNode is null || appSections is null) return (false, null);
        var key = job.Role switch { SetupRole.Llm => AppSettingsSections.Thinking, SetupRole.Stt => AppSettingsSections.Listening, _ => AppSettingsSections.Speaking };
        if (settingsNode.Document.Find(key) is not { } entry) return (false, null);
        var secret = settingsNode.Document.Secret(entry.SecretSha256);
        if (entry.SecretSha256 is not null && secret is null) return (true, "its API key hasn't reached this PC yet.");
        SharedApply result;
        try { result = await appSections.HandBackAsync(job.Role, job.Title, entry, secret, lifetime.Token); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (true, error.Message);
        }
        if (!result.Applied) return (true, result.Note);
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        homeSettings = loaded.Settings ?? homeSettings;
        return (true, null);
    }
}

/// <summary>What a computer tells your other computers about itself through the shared settings, under its own key
/// ("pc.desktop-imouto"): whether it is a companion or a host PC (<see cref="Role"/>) and the host service Martlet runs on it
/// (<see cref="Host"/>), so every Devices map draws it the same way. Only that computer writes its key, so the newest value is
/// always its own; Martlet versions without it pass it on unchanged.</summary>
internal sealed record SharedPc(string Role, string? Host)
{
    internal const string Prefix = SharedSettings.DevicePrefix;
    internal const string CompanionRole = "companion";
    internal const string HostRole = "host";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The shared setting's name for a device ID: lowercase letters, digits, dots and hyphens only.</summary>
    internal static string Key(string deviceId) => Name(Prefix, deviceId);

    /// <summary>The name of the entry that says whether a computer is a companion or a host PC ("role.desktop-b"). That
    /// computer records its own choice there and follows it when another computer writes it (Make it a host PC on the Devices
    /// map), so any of your computers can switch any other.</summary>
    internal static string RoleKey(string deviceId) => Name(SharedSettings.RolePrefix, deviceId);

    /// <summary>The name of a computer's reminders entry ("reminders.desktop-b"): the reminders set on it and what it did about
    /// anyone's. Only that computer writes it.</summary>
    internal static string ReminderKey(string deviceId) => Name(SharedSettings.RemindersPrefix, deviceId);

    private static string Name(string prefix, string deviceId)
    {
        var clean = new string(deviceId.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' ? c : '-').ToArray());
        var key = prefix + clean;
        return key.Length > 64 ? key[..64] : key;
    }

    internal static bool IsKey(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    internal static bool IsRoleKey(string key) => key.StartsWith(SharedSettings.RolePrefix, StringComparison.Ordinal);

    /// <summary>A role entry's value: "host" or "companion" as JSON text.</summary>
    internal static string WriteRole(DeviceRole role) => JsonSerializer.Serialize(role == Desktop.DeviceRole.Host ? HostRole : CompanionRole);

    /// <summary>The role a role entry asks for, or null when it is unreadable (written by a newer Martlet, say).</summary>
    internal static DeviceRole? ReadRole(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(value) switch
            {
                CompanionRole => Desktop.DeviceRole.Companion,
                HostRole => Desktop.DeviceRole.Host,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    internal DeviceRole? DeviceRole => Role switch
    {
        CompanionRole => Desktop.DeviceRole.Companion,
        HostRole => Desktop.DeviceRole.Host,
        _ => null
    };

    internal string Write() => JsonSerializer.Serialize(this, Json);

    /// <summary>A computer's entry, or null when it is unreadable (written by a newer Martlet, say).</summary>
    internal static SharedPc? Read(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<SharedPc>(value, Json) is { Role.Length: > 0 and <= 32 } pc &&
                (pc.Host is null || pc.Host.Length <= 64) ? pc : null;
        }
        catch (JsonException) { return null; }
    }
}
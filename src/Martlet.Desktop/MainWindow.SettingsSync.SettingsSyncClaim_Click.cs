using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.SettingsSync.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private async void SettingsSyncClaim_Click(object sender, RoutedEventArgs e)
    {
        if (settingsNode is null || closing || !clusterEnabled) return;
        if (!ConfirmationDialog.Confirm(this, "Make all your computers use this PC's settings? This PC's settings and API keys replace what " +
                "every paired computer has" +
                (accountNode is null ? "." : ", and your own settings go only to the computers where you are signed in."),
                "Use this PC's settings"))
            return;
        while (settingsBusy && !closing) await Task.Delay(100);
        try
        {
            var count = await settingsNode.ClaimAllAsync(DateTimeOffset.UtcNow, lifetime.Token);
            if (accountNode is { } own) count += await own.ClaimAllAsync(DateTimeOffset.UtcNow, lifetime.Token);
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
        // Only this PC writes its own entry, so every computer's Devices map knows what each of the others is (and whether a host
        // PC has its host service yet).
        yield return new DelegateSection(SharedPc.Key(ClusterDevice), "This PC's role", _ =>
            Task.FromResult<SharedLocal?>(new(new SharedPc(Role == DeviceRole.Host ? SharedPc.HostRole : SharedPc.CompanionRole,
                OwnHostServiceId(), ThisComputer.Name).Write(), null, false, DateTimeOffset.UtcNow)),
            (_, _) => Task.FromResult(SharedApply.Done));
        // Whether this PC is a companion or a host PC: this PC records its own choice, and follows it when another computer
        // asks it to switch (Make it a host PC on that computer's Devices map). Its time is the time the PC folder's choice
        // last changed, whichever Windows user of this PC made it.
        yield return new DelegateSection(SharedPc.RoleKey(ClusterDevice), "Companion or host PC", _ =>
            Task.FromResult<SharedLocal?>(new(SharedPc.WriteRole(Role), null, deviceRole is null, FileTime(PcRole.FilePath(Pc!)))),
            (setting, _) => Task.FromResult(FollowRoleRequest(setting)));
        yield return new DelegateSection(CharacterKey, "Character", ReadCharacterAsync, ApplyCharacterAsync, DefaultCharacter);
        yield return new DelegateSection(TalkKey, "How you talk", _ =>
        {
            var path = TalkPreferences.PathIn(directory);
            return Task.FromResult<SharedLocal?>(new(TalkSharing.Value(Talk), null, !File.Exists(path), FileTime(path)));
        }, (setting, _) =>
        {
            SaveTalk(TalkSharing.Follow(Talk, setting.Value));
            return Task.FromResult(SharedApply.Done);
        }, () => TalkSharing.Value(new TalkPreferences()));
        // Where the bubble sits (beside the character or in one place, and its offsets) depends on this PC's screens, like the
        // character's own place, so only whether bubbles and subtitles show travels.
        var speechPath = Path.Combine(directory, SpeechDisplayPreferences.FileName);
        yield return FileSection.Json(SpeechDisplayKey, "Speech bubbles", [speechPath],
            () => new SharedSpeechDisplay(captions.Preferences.SpeechBubbles, captions.Preferences.Subtitles), _ => !File.Exists(speechPath),
            value => captions.Update(captions.Preferences with { SpeechBubbles = value.SpeechBubbles, Subtitles = value.Subtitles })
                ? SharedApply.Done : FileSection.NotSaved,
            new SharedSpeechDisplay(new SpeechDisplayPreferences().SpeechBubbles, new SpeechDisplayPreferences().Subtitles));
        // The owner's custom palette travels before the theme, so a computer that switches to Custom has its colors.
        yield return new DelegateSection(AppearanceCustomKey, "Custom palette", _ =>
        {
            var colors = Appearance.LoadCustom(directory);
            return Task.FromResult<SharedLocal?>(colors is null ? null
                : new(Appearance.ShareCustom(colors), null, false, FileTime(Appearance.CustomPath(directory))));
        }, (setting, _) =>
        {
            if (Appearance.ParseCustom(setting.Value) is not { } colors)
                return Task.FromResult(SharedApply.Waiting("It was made on a newer Martlet; update this PC to use it."));
            Appearance.SaveCustom(directory, colors);
            FollowCustomTheme(colors);
            return Task.FromResult(SharedApply.Done);
        });
        yield return new DelegateSection(AppearanceKey, "Theme", _ =>
        {
            var path = Path.Combine(directory, Appearance.ThemeFile);
            var theme = Application.Current is App { SelectedTheme: var selected } ? selected : AppearanceTheme.Light;
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(theme.ToString()), null, !File.Exists(path), FileTime(path)));
        }, (setting, _) =>
        {
            var name = JsonSerializer.Deserialize<string>(setting.Value);
            if (Appearance.Parse(name) is not { } theme)
                return Task.FromResult(SharedApply.Waiting("It was chosen on a newer Martlet; update this PC to use it."));
            Appearance.Save(directory, theme);
            if (IsLoaded) ThemeChoice.SelectedIndex = (int)theme;
            else (Application.Current as App)?.ApplyTheme(theme,
                theme == AppearanceTheme.Custom ? Appearance.LoadColors(directory, theme) : characterThemes.Colors(theme));
            return Task.FromResult(SharedApply.Done);
        }, () => JsonSerializer.Serialize(AppearanceTheme.Light.ToString()));
        // Every model's emotes and motions, and each persona's touch temperament (each person's); this PC reloads them after the
        // sync (AfterSettingsAppliedAsync).
        yield return CharacterSharedSections.Actions(directory, new(
            () => characterActions.Busy ? "The Thinking model is naming this PC's emotes and motions." : null));
        yield return CharacterSharedSections.TouchTemperaments(directory, new(
            () => characterTemperaments.Busy ? "The Thinking model is deciding a touch temperament on this PC." : null));
        // What each character model was found or told to be: its touch zones, its eyes' measurement and its VRoid Hub credit, the
        // household's, so a model is found and measured once for every computer; and the character's own changes to its touch
        // reactions, each person's. The pictures stay on the computer that made them.
        yield return CharacterSharedSections.TouchZones(directory, new(
            () => characterTouchZones.Busy ? "This PC is finding a character's touch zones now." : null, characterTouchZones.Reload));
        yield return CharacterSharedSections.Eyes(directory, new(
            () => characterEyes.Busy ? "This PC is measuring a character's eyes now." : null, characterEyes.Reload));
        yield return CharacterSharedSections.VRoidHub(directory);
        yield return CharacterSharedSections.ReactionChanges(directory, new(null, characterReactionChanges.Reload));
        // Whether Martlet keeps a record of your conversations and may search it on its own: each person's, the same on every
        // computer they are signed in on (the record itself travels as the shared record of conversations).
        if (conversationHistory is { } history)
        {
            var historyPath = Path.Combine(directory, ConversationHistoryPreferences.FileName);
            yield return FileSection.Json(ConversationHistoryKey, "Conversation history", [historyPath],
                () => new SharedConversationHistory(history.Preferences.Keep, history.Preferences.Search), _ => !File.Exists(historyPath),
                value =>
                {
                    var next = new ConversationHistoryPreferences(value.Keep, value.Search);
                    return next == history.Preferences || history.SetPreferences(next) ? SharedApply.Done : FileSection.NotSaved;
                }, new SharedConversationHistory(true, false));
        }
        // The memory brief travels with each person's settings, so every computer sends the same one; a brief taken from another
        // computer (or another person's, on a switch) goes with messages at once.
        yield return MemoryBriefFile.Section(directory, () => conversation?.MemoryBrief?.Followed());
        // Show advanced settings: each person's, so the same pages show on every computer they use (AfterSettingsAppliedAsync).
        yield return AdvancedSettingsFile.Section(directory);
        // Finding by meaning (on or off, and which embedding model) is the household's choice, the same on every computer; each
        // computer makes its own vectors.
        yield return MeaningSearchFile.Section(directory, () => conversation?.Meaning?.Reload());
        yield return FileSection.Json(VoiceRecognitionKey, "Recognizing voices", [Path.Combine(directory, Martlet.Core.Speakers.VoiceChoices.RecognitionFile)],
            () => localVoices.Available ? new SharedSwitch(localVoices.Enabled) : null, _ => localVoices.EnabledChangedAt is null,
            value =>
            {
                if (value.On != localVoices.Enabled) localVoices.SetEnabled(value.On);
                return SharedApply.Done;
            });
        // Whether Martlet keeps the last few clips of voices the owner hasn't named: the household's privacy choice, the same on
        // every computer. Turning it off anywhere deletes the clips each computer kept.
        if (localVoices.Available)
            yield return Martlet.Core.Speakers.VoiceChoices.ClipsSection(directory, localVoices.Clips.SetEnabled);
        yield return new DelegateSection(VoiceIdKey, "Voice ID", _ =>
        {
            if (!voiceIdentity.Available) return Task.FromResult<SharedLocal?>(null);
            var print = voiceIdentity.Current;
            var value = new SharedVoiceId(Talk.VoiceId, print is null ? null
                : new SharedVoiceprint(print.Embedding, print.Threshold, print.Consistency, print.CreatedAt, print.SpeechSeconds));
            var times = new[] { FileTime(Path.Combine(directory, VoiceIdentity.FileName)), FileTime(TalkPreferences.PathIn(directory)) };
            return Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(value, SharedJson), null, !value.On && print is null,
                times.Max()));
        }, (setting, _) =>
        {
            var value = JsonSerializer.Deserialize<SharedVoiceId>(setting.Value, SharedJson) ?? throw new JsonException();
            if (value.Voiceprint is { } print)
            {
                if (print.Embedding is not { Length: Martlet.Audio.SpeakerEncoder.EmbeddingSize } || print.Embedding.Any(v => !float.IsFinite(v)) ||
                    !float.IsFinite(print.Threshold) || print.Threshold is < VoiceIdentity.MinimumThreshold or > VoiceIdentity.MaximumThreshold)
                    return Task.FromResult(SharedApply.Waiting("Its voiceprint was made by a newer Martlet; update this PC to use it."));
                var current = voiceIdentity.Current;
                if (current is null || !current.Embedding.SequenceEqual(print.Embedding) || current.Threshold != print.Threshold ||
                    current.Consistency != print.Consistency || current.CreatedAt != print.CreatedAt || current.SpeechSeconds != print.SpeechSeconds)
                    voiceIdentity.Save(new Voiceprint(print.Embedding, print.Threshold, print.Consistency, print.CreatedAt, print.SpeechSeconds));
            }
            else if (voiceIdentity.Current is not null) voiceIdentity.Delete();
            if (Talk.VoiceId != value.On) SaveTalk(Talk with { VoiceId = value.On });
            return Task.FromResult(SharedApply.Done);
        }, () => JsonSerializer.Serialize(new SharedVoiceId(false, null), SharedJson));
        // Turning control on with the connection is what connecting does, not a choice of its own.
        yield return FileSection.Json(SmartHomeKey, "Smart home permissions", [Path.Combine(directory, Martlet.Home.HomePreferences.FileName)],
            () => smartHome.Connected ? new SharedHomePermissions(smartHome.Preferences.Control, smartHome.Preferences.AllowSensitive,
                smartHome.Preferences.ModelTools) : null,
            value => !value.AllowSensitive && !value.ModelTools,
            value => !smartHome.Connected ? SharedApply.Waiting("Home Assistant isn't connected on this PC yet.")
                : smartHome.SetControl(value.Control, value.AllowSensitive, value.ModelTools) ? SharedApply.Done
                : SharedApply.Waiting("They couldn't be saved on this PC."));
        // Martlet's Discord setup (the bot and its token, the owner, chat modes, channel rules, the people it knows and the call
        // choices that aren't about this PC's devices) is the household's; whether the bot connects here stays with each computer.
        yield return Martlet.Discord.DiscordSharing.Section(directory, new WindowsDiscordTokenVault(settingsVault), discord.Save, discordCalls.Save);
        // Telegram and WhatsApp: the connected bot or number with its secrets, the paired chats and speaking replies aloud; whether
        // Martlet answers them here, and WhatsApp's webhook port, stay with each computer.
        yield return Martlet.Messaging.MessagingSharing.Section(directory, new WindowsMessagingSecretVault(settingsVault), messaging.Follow);
        yield return FileSection.Json(UpdatesKey, "App updates",
            [Path.Combine(directory, UpdateCheckPreferences.FileName), Path.Combine(directory, UpdatePreferences.FileName)],
            () => new SharedUpdates(updateChecksEnabled, updatePreferences.IntervalMinutes, updatePreferences.AutoInstall, updatePreferences.AutoUpdateHosts),
            _ => FileSection.Changed(Path.Combine(directory, UpdateCheckPreferences.FileName), Path.Combine(directory, UpdatePreferences.FileName)) is null,
            value =>
            {
                if (!UpdatePreferences.Intervals.Contains(value.IntervalMinutes)) return FileSection.Newer;
                ApplySharedUpdates(value.Checks, new UpdatePreferences
                {
                    IntervalMinutes = value.IntervalMinutes, AutoInstall = value.AutoInstall, AutoUpdateHosts = value.AutoUpdateHosts
                });
                return SharedApply.Done;
            });
        // The settings backed by one Core store each: what Thinking models hear and see, Devices › Sharing work, Motion, the
        // language models address, the recommendation preferences and the shared pool lists. This PC follows them after the sync
        // (AfterSettingsAppliedAsync).
        foreach (var section in StoreSections.All(directory)) yield return section;
        // The API keys of the pool lists' cloud and server members (Speaking, Listening, Picture making, Vision, Hearing; #805).
        yield return PoolKeyShare.Section(directory, settingsVault);
        // The custom workflows the Picture making list names, Chatterbox Original's style, the recommended setups you declined,
        // and each person's check-ins (their scripts stay on the computer where they were written).
        yield return Martlet.Core.Pictures.PictureWorkflowFiles.Section(directory);
        yield return ChatterboxStyle.Section(directory);
        yield return Martlet.Core.Planning.RecommendedSetupMemory.Section(directory);
        yield return Martlet.Conversation.CheckInSettings.Section(directory, next => SaveCheckIns(next, null));
        // The API keys of Singing's cloud providers (Companion › Singing's lists), so every computer that makes a song can use them.
        yield return new SingingCloudKeys.Section(directory);
    }
}

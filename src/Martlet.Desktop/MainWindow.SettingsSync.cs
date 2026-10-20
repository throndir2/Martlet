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
        AppearanceCustomKey = "appearance-custom",
        CharacterActionsKey = "character-actions", TouchTemperamentKey = "touch-temperament", VoiceRecognitionKey = "voice-recognition", VoiceIdKey = "voice-id",
        SmartHomeKey = "smart-home", UpdatesKey = "updates", ModelAbilitiesKey = "model-abilities", ConversationHistoryKey = SettingScopes.ConversationHistory;
    private static readonly JsonSerializerOptions SharedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    private readonly DispatcherTimer settingsTimer = new() { Interval = ClusterSync.Interval };
    private SharedSettingsNode? settingsNode;
    private AppSettingsSections? appSections;
    private readonly WindowsCredentialStore settingsVault = new();
    /// <summary>The desktop's own sections each account keeps (the character, how you talk, the theme...): the same for every
    /// account's node, since they read and write this PC's files in use.</summary>
    private IReadOnlyList<ISharedSection> accountDesktopSections = [];
    /// <summary>The signed-in account's own settings (docs/ACCOUNTS.md, "Account settings"), null without accounts or until a
    /// switch cut short at the last start is finished.</summary>
    private SharedSettingsNode? accountNode;
    private Guid? accountNodeFor;
    /// <summary>Each host's copy of the signed-in account's settings, with its digest.</summary>
    private readonly Dictionary<string, (Guid Account, string Digest, SharedSettings Copy)> accountSettingsCopies = new(StringComparer.Ordinal);
    private (int Current, int Hosts, int Old) accountHosts;
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
        Func<SetupRole, SetupRoute?> savedRoute = role => HostJob.For(role) is { } job ? JobSavedRoute.Load(directory, job.SavedFile) : null;
        var desktop = DesktopSections().ToArray();
        if (accounts is null)
        {
            // No account session (it couldn't be opened): every setting stays on the household's copy, as before accounts.
            appSections = new AppSettingsSections(setupService, settingsVault, directory, lorebooks, savedRoute, RouteAvailableAsync);
            settingsNode = new SharedSettingsNode(directory, ClusterDevice, [.. appSections.Sections, .. desktop], appSections.Invalidate);
            ShowSettingsStatus();
            return;
        }
        // The household's settings on one node; the signed-in account's on its own (AccountNode), each in its own document.
        appSections = new AppSettingsSections(setupService, settingsVault, directory, null, savedRoute, RouteAvailableAsync);
        settingsNode = new SharedSettingsNode(directory, ClusterDevice,
            [.. appSections.Sections.Where(s => !SettingScopes.IsAccountKey(s.Key)), .. desktop.Where(s => !SettingScopes.IsAccountKey(s.Key))],
            appSections.Invalidate);
        accountDesktopSections = [.. desktop.Where(s => SettingScopes.IsAccountKey(s.Key))];
        AccountSettingsStart.Prepare(directory, accounts.AccountId, DateTimeOffset.UtcNow);
        // A switch cut short at the last start (the files still hold another account's settings) is finished before the
        // conversation starts (FinishAccountSettingsAsync).
        if (AccountWorkingCopy.Load(directory)?.Account == accounts.AccountId)
        {
            accountNode = AccountNode(accounts.AccountId);
            accountNodeFor = accounts.AccountId;
        }
        accounts.AddChangeStep(SwitchSettingsStepAsync);
        ShowSettingsStatus();
    }

    /// <summary>The settings node of <paramref name="account"/>: its own copy in its folder, the account sections, and its
    /// lorebooks (they live in its folder).</summary>
    private SharedSettingsNode AccountNode(Guid account)
    {
        var folder = AccountWorkingCopy.Folder(store!.DataDirectory, account);
        var sections = new AppSettingsSections(setupService!, settingsVault, store.DataDirectory, new LorebookStore(folder));
        return new SharedSettingsNode(folder, ClusterDevice, [.. sections.Sections.Where(s => SettingScopes.IsAccountKey(s.Key)), .. accountDesktopSections],
            sections.Invalidate, account: true);
    }

    /// <summary>The account change step: on a switch, gives this PC's settings files the incoming account's settings, on the
    /// window's thread (the theme, the character and how you talk change with them). At start the files are ready already
    /// (AccountSettingsStart) or are finished by <see cref="FinishAccountSettingsAsync"/>.</summary>
    private Task SwitchSettingsStepAsync(AccountChange change, CancellationToken token) =>
        change.From is null ? Task.CompletedTask : Dispatcher.InvokeAsync(() => UseAccountSettingsAsync(change.To, token)).Task.Unwrap();

    /// <summary>Finishes a switch cut short at the last start, before the character and the conversation load.</summary>
    private async Task FinishAccountSettingsAsync()
    {
        if (accounts is null || accountNode is not null || store is null || setupService is null || closing) return;
        try { await UseAccountSettingsAsync(accounts.AccountId, lifetime.Token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException or JsonException)
        {
            ErrorLog.Warn("Settings: couldn't give this PC the signed-in account's settings at start; they don't sync until it can.", error);
        }
    }

    /// <summary>Gives this PC's settings files the settings of <paramref name="account"/>: records the files into the outgoing
    /// account's copy, reads the incoming account's copies from the hosts (briefly), gives the files its settings (or each
    /// setting's default) and writes the working-copy marker (<see cref="AccountSettingsSync.SwitchAsync"/>). Throws when it
    /// can't; the files then hold the outgoing account's settings again.</summary>
    private async Task UseAccountSettingsAsync(Guid account, CancellationToken token)
    {
        if (store is null || setupService is null) return;
        while (settingsBusy && !closing) await Task.Delay(50, token);
        settingsBusy = true;
        IReadOnlyDictionary<string, string> waiting;
        try
        {
            var directory = store.DataDirectory;
            var marker = AccountWorkingCopy.Load(directory)?.Account;
            var from = accountNodeFor == account ? null : accountNode ?? (marker is { } other && other != account ? AccountNode(other) : null);
            var next = AccountNode(account);
            var copies = clusterEnabled ? await ReadAccountCopiesAsync(account, token) : [];
            using (settingsChanges.From(SettingsChangeSource.AccountSwitch))
                waiting = await AccountSettingsSync.SwitchAsync(from, next, copies, directory, account, DateTimeOffset.UtcNow, token);
            accountNode = next;
            accountNodeFor = account;
            accountSettingsCopies.Clear();
            lorebooks?.UseDirectory(AccountWorkingCopy.Folder(directory, account));
        }
        finally { settingsBusy = false; }
        ErrorLog.Info($"Settings: this PC now uses account {AccountSession.Short(account)}'s settings" +
            (waiting.Count == 0 ? "." : $"; not yet: {string.Join(", ", waiting.Keys.Select(SharedTitle))}."));
        homeLore = null;
        settingsLastChange = null;
        if (closing) return;
        await RefreshHomeAsync(SettingsChangeSource.AccountSwitch);
        if (characterChanged && avatar.IsShowing && Role == DeviceRole.Companion)
        {
            characterChanged = false;
            if (await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        }
        characterChanged = false;
        if (openTab is not null && !tabEdited) RenderTab();
        ShowSettingsStatus();
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>What a computer tells your other computers about itself through the shared settings, under its own key
/// ("pc.desktop-imouto"): whether it is a companion or a host PC (<see cref="Role"/>), the host service Martlet runs on it
/// (<see cref="Host"/>) and the name it gives itself (<see cref="ComputerName"/>, "Imouto": its host name, which may be newer than
/// the name the network roster or a host kept from when it joined), so every Devices map draws it the same way. Only that
/// computer writes its key, so the newest value is always its own; Martlet versions without it pass it on unchanged.</summary>
internal sealed record SharedPc(string Role, string? Host, string? ComputerName = null)
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
                (pc.Host is null || pc.Host.Length <= 64) ? pc with { ComputerName = HostIds.CleanComputerName(pc.ComputerName) } : null;
        }
        catch (JsonException) { return null; }
    }
}

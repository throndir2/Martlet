using Martlet.Core.Cluster;
using Martlet.Core.Motion;
using Martlet.Core.Planning;
using Martlet.Core.Sync;

namespace Martlet.Core.Settings;

/// <summary>Who a settings file belongs to (docs/CLUSTER.md, "One Martlet on every computer").</summary>
public enum SettingsFileScope
{
    /// <summary>The household's: the same on every computer, through a household shared setting.</summary>
    Shared,
    /// <summary>Each person's: the same on every computer where that person is signed in, through an account shared setting
    /// (<see cref="SettingScopes.AccountKeys"/>).</summary>
    Account,
    /// <summary>This computer's only. docs/CLUSTER.md, "What stays with each computer", says why.</summary>
    Local
}

/// <summary>What a settings store found when it read its file.</summary>
public enum SettingsFileState
{
    /// <summary>There is no file: the store gives its defaults.</summary>
    Missing,
    /// <summary>The file was read.</summary>
    Loaded,
    /// <summary>The file could not be read or parsed: the store gives its defaults.</summary>
    Unreadable
}

/// <summary>One settings file in Martlet's data folder.</summary>
/// <param name="FileName">Its path in the data folder.</param>
/// <param name="Scope">Who it belongs to.</param>
/// <param name="SharedKey">The shared setting that carries it to the other computers; null for a <see cref="SettingsFileScope.Local"/> file.</param>
/// <param name="Store">The full name of the type that owns its path, load, save and defaults. Read and write the file only
/// through that type.</param>
/// <param name="StaysHere">For a local file, or a shared file with parts that stay on this computer: the reason in the
/// "Why" column of docs/CLUSTER.md, "What stays with each computer", on the row that names this file. Null when nothing in
/// the file stays here.</param>
public sealed record SettingsFile(string FileName, SettingsFileScope Scope, string? SharedKey, string Store, string? StaysHere = null)
{
    /// <summary>The file's full path in the data folder <paramref name="directory"/>.</summary>
    public string PathIn(string directory) => Path.Combine(directory, FileName);
}

/// <summary>The settings files and who they belong to: the one list to read before a change adds or moves a setting (AGENTS.md,
/// "Martlet is one app"). A new settings file gets a row here, a store that owns it, and (when it is not shared) a row in
/// docs/CLUSTER.md, "What stays with each computer"; <c>SettingsFilesTests</c> checks that they agree. settings.json is not
/// listed: <see cref="AppSettingsSections"/> splits it into its shared settings, and on the Linux and macOS companion
/// <c>CompanionSharedSections</c> (src/Martlet.Companion.Platform) splits that app's own settings.json the same way.</summary>
public static class SettingsFiles
{
    public static readonly SettingsFile TalkPreferences = new("talk-preferences.json", SettingsFileScope.Account, SettingScopes.Talk,
        "Martlet.Desktop.TalkPreferences", "This PC's devices");

    /// <summary>Every registered settings file.</summary>
    public static IReadOnlyList<SettingsFile> All { get; } =
    [
        // The household's.
        new(ModelAbilities.FileName, SettingsFileScope.Shared, "model-abilities", "Martlet.Core.Settings.ModelAbilities"),
        new(WorkSharingSettings.FileName, SettingsFileScope.Shared, WorkSharingSettings.SharedKey, "Martlet.Core.Cluster.WorkSharingSettings"),
        new(MotionSettings.FileName, SettingsFileScope.Shared, MotionSettings.SharedKey, "Martlet.Core.Motion.MotionSettings"),
        new(Access.OpenAiEndpointSettings.FileName, SettingsFileScope.Shared, Access.OpenAiEndpointSettings.SharedKey,
            "Martlet.Core.Access.OpenAiEndpointSettings"),
        new(RecommendationPreferences.FileName, SettingsFileScope.Shared, RecommendationPreferences.SharedKey,
            "Martlet.Core.Planning.RecommendationPreferences"),
        new(PoolSettings.FileName, SettingsFileScope.Shared, PoolSettings.SharedKey, "Martlet.Core.Cluster.PoolSettings"),
        new(ThinkingPoolSettings.FileName, SettingsFileScope.Shared, AppSettingsSections.ThinkingPool, "Martlet.Core.Settings.ThinkingPoolSettings",
            "Paired computers join each computer's pool by themselves, and models in Ollama on this PC are this PC's own"),
        new(Pictures.PicturesSettings.WorkflowFile, SettingsFileScope.Shared, Pictures.PictureWorkflowFiles.SharedKey, "Martlet.Core.Pictures.PicturesSettings"),
        new(ChatterboxStyle.FileName, SettingsFileScope.Shared, ChatterboxStyle.SharedKey, "Martlet.Core.Settings.ChatterboxStyle"),
        new(RecommendedSetupMemory.FileName, SettingsFileScope.Shared, RecommendedSetupMemory.SharedKey, "Martlet.Core.Planning.RecommendedSetupMemory"),
        new(MeaningSearchFile.FileName, SettingsFileScope.Shared, MeaningSearchFile.SharedKey, "Martlet.Core.Settings.MeaningSearchFile"),
        new("character-actions.json", SettingsFileScope.Shared, "character-actions", "Martlet.Avatar.Hosting.CharacterActions"),
        new("character-touch-zones.json", SettingsFileScope.Shared, "character-touch-zones", "Martlet.Avatar.Hosting.CharacterTouchZones"),
        new("character-eyes.json", SettingsFileScope.Shared, "character-eyes", "Martlet.Avatar.Hosting.CharacterEyes"),
        new("vroid-hub-looks.json", SettingsFileScope.Shared, "vroid-hub-looks", "Martlet.Avatar.Hosting.VRoidHubLooks"),
        new("updates.json", SettingsFileScope.Shared, "updates", "Martlet.Desktop.UpdatePreferences"),
        new("update-checks.txt", SettingsFileScope.Shared, "updates", "Martlet.Desktop.UpdateCheckPreferences"),
        new("smart-home.json", SettingsFileScope.Shared, "smart-home", "Martlet.Home.HomePreferences"),
        new(Speakers.VoiceChoices.RecognitionFile, SettingsFileScope.Shared, Speakers.VoiceChoices.RecognitionKey, "Martlet.Core.Speakers.VoiceChoices"),
        new(Speakers.VoiceChoices.ClipsFile, SettingsFileScope.Shared, Speakers.VoiceChoices.ClipsKey, "Martlet.Core.Speakers.VoiceChoices"),
        new(Speakers.VoiceChoices.RosterFile, SettingsFileScope.Shared, VoicesDocument, "Martlet.Desktop.LocalVoices"),
        new("speaking-voices.json", SettingsFileScope.Shared, SpeakingVoicesDocument, "Martlet.Desktop.F5Voices"),
        new("discord.json", SettingsFileScope.Shared, "discord", "Martlet.Discord.DiscordPreferences", "Only one computer runs the bot"),
        new("discord-calls.json", SettingsFileScope.Shared, "discord", "Martlet.Discord.Calls.DiscordCallPreferences", "This PC's devices"),
        new("messaging.json", SettingsFileScope.Shared, "messaging", "Martlet.Messaging.MessagingPreferences",
            "Only one program may read a bot's or a number's messages"),
        // Each person's.
        TalkPreferences,
        new("speech-display.json", SettingsFileScope.Account, SettingScopes.SpeechDisplay, "Martlet.Desktop.SpeechDisplayPreferences",
            "This PC's screens"),
        new("appearance-custom.json", SettingsFileScope.Account, SettingScopes.AppearanceCustom, "Martlet.Desktop.Appearance"),
        new("appearance.txt", SettingsFileScope.Account, SettingScopes.Appearance, "Martlet.Desktop.Appearance"),
        new("character-temperaments.json", SettingsFileScope.Account, SettingScopes.TouchTemperament,
            "Martlet.Avatar.Hosting.CharacterTouchTemperaments"),
        new("conversation-history.json", SettingsFileScope.Account, SettingScopes.ConversationHistory,
            "Martlet.Desktop.ConversationHistoryPreferences"),
        new("check-ins.json", SettingsFileScope.Account, SettingScopes.CheckIns, "Martlet.Conversation.CheckInSettings",
            "It runs commands as you on this PC, so it is allowed at that PC"),
        new(MemoryBriefFile.FileName, SettingsFileScope.Account, MemoryBriefFile.SharedKey, "Martlet.Core.Settings.MemoryBriefFile"),
        new(AdvancedSettingsFile.FileName, SettingsFileScope.Account, AdvancedSettingsFile.SharedKey, "Martlet.Core.Settings.AdvancedSettingsFile"),
        new("voice-id.json", SettingsFileScope.Account, SettingScopes.VoiceId, "Martlet.Desktop.VoiceIdentity"),
        new("avatar.json", SettingsFileScope.Account, SettingScopes.Character, "Martlet.Avatar.Hosting.AvatarProfileStore"),
        new("character-reaction-changes.json", SettingsFileScope.Account, SettingScopes.CharacterReactionChanges,
            "Martlet.Avatar.Hosting.CharacterReactionChanges"),
        new(Lorebooks.LorebookStore.FileName, SettingsFileScope.Account, AppSettingsSections.Lorebooks, "Martlet.Core.Lorebooks.LorebookStore"),
        // This computer's.
        new(PoolSettings.LocalFileName, SettingsFileScope.Local, null, "Martlet.Core.Cluster.PoolSettings", "It reads this PC's screen"),
        new(PoolKeys.FileName, SettingsFileScope.Local, null, "Martlet.Core.Cluster.PoolKeys",
            "The keys are in this PC's Windows Credential Manager"),
        new(Singing.SingingKeys.FileName, SettingsFileScope.Local, null, "Martlet.Core.Singing.SingingKeys",
            "The keys are in this PC's Windows Credential Manager"),
        new(ModelLimits.FileName, SettingsFileScope.Local, null, "Martlet.Core.Settings.ModelLimits", "Measured on this PC"),
        new(MeasuredModelMemory.FileName, SettingsFileScope.Local, null, "Martlet.Core.Planning.MeasuredModelMemory", "Measured on this PC"),
        new("terminal.json", SettingsFileScope.Local, null, "Martlet.Mcp.Client.TerminalSettings",
            "It runs commands as you on this PC, so it is allowed at that PC"),
        new("mcp.json", SettingsFileScope.Local, null, "Martlet.Desktop.McpToolService",
            "Programs on this PC; a shared route that needs one this PC lacks waits and says why"),
        new("conversations/platform-changes.json", SettingsFileScope.Local, null, "Martlet.Conversation.PlatformChanges",
            "Only the computer where you made the change asks the app; the record itself travels"),
        new("character-placement.json", SettingsFileScope.Local, null, "Martlet.Desktop.CharacterPlacementStore", "This PC's screens"),
        new("character-click-through.json", SettingsFileScope.Local, null, "Martlet.Desktop.CharacterClickThroughStore", "This PC's screens"),
        new("character-profiles-local.json", SettingsFileScope.Local, null, "Martlet.Desktop.CharacterProfileLocalStore", "This PC's screens"),
        new("character-themes.json", SettingsFileScope.Local, null, "Martlet.Avatar.Hosting.CharacterThemes", DerivedHere),
        new("hosts.json", SettingsFileScope.Local, null, "Martlet.Desktop.HostRegistry", "How this computer reaches others, and who may reach it"),
        new("cluster-sync.txt", SettingsFileScope.Local, null, "Martlet.Desktop.ClusterSync", "How this computer reaches others, and who may reach it"),
        new("router-port.json", SettingsFileScope.Local, null, "Martlet.Desktop.RouterPort", "How this computer reaches others, and who may reach it"),
        new(Network.NearbyChoice.FileName, SettingsFileScope.Local, null, "Martlet.Core.Network.NearbyChoice", "How this computer reaches others, and who may reach it"),
        new("device-role.txt", SettingsFileScope.Local, null, "Martlet.Desktop.DeviceRolePreference", "What this computer is for"),
        new("background.json", SettingsFileScope.Local, null, "Martlet.Desktop.BackgroundPreferences", "How Martlet starts for this Windows user"),
        new(SenseModels.FileName, SettingsFileScope.Local, null, "Martlet.Core.Settings.SenseModels",
            "Which models a computer can run beside Thinking depends on its hardware"),
        new("speaking-engine.txt", SettingsFileScope.Local, null, "Martlet.Core.Settings.SpeakingEngineFile",
            "It follows the voice engine the speaking host runs now"),
        new("singing.json", SettingsFileScope.Local, null, "Martlet.Core.Singing.SingingPreferences",
            "An older choice, read once into the shared Singing lists, and the computer this PC last saw singing"),
        new("household-signin.json", SettingsFileScope.Local, null, "Martlet.Desktop.HouseholdSignInStore",
            "The client secrets are in this PC's Windows Credential Manager; the providers themselves are on the hosts"),
        new("discord-companion.json", SettingsFileScope.Local, null, "Martlet.Discord.DiscordCompanionState",
            "What the bot saw and made on the computer that runs it")
    ];

    /// <summary>The shared documents that carry a file in place of a shared setting (docs/CLUSTER.md): the voice list and the
    /// voices Martlet speaks with. Both are the household's.</summary>
    public const string VoicesDocument = "/martlet/v1/voices", SpeakingVoicesDocument = "/martlet/v1/speaking-voices";

    /// <summary>The reason for data each computer makes again from what is shared.</summary>
    public const string DerivedHere = "Derived data each computer makes from what is shared; never a second copy of a fact, a guide or a report";

    /// <summary>Files in Martlet's data folder (and on its hosts) that hold no choice of the owner's, so they need no row in
    /// <see cref="All"/>: what each one is (status, cache, record, host, package, test, or migration: an older choice read once into a shared list). A file that holds a choice is never listed here; <c>SettingsFilesTests</c> finds every
    /// file name a store declares and checks that it is in one of the two lists.</summary>
    public static IReadOnlyDictionary<string, string> NotSettings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Status written for the pages and for MCP.
        ["about.txt"] = "status", ["martlet-logs.txt"] = "status", [".martlet-support.v1.json"] = "status", ["check-ins-status.json"] = "status",
        ["image-model-status.json"] = "status", ["sense-models-status.json"] = "status", ["songs-status.json"] = "status",
        ["thinking-pool-status.json"] = "status", ["thinking-requests.json"] = "status", ["tools-unsupported.json"] = "status",
        ["voice-recognition-state.json"] = "status", ["memory-upkeep.json"] = "status", ["model-catalog-status.json"] = "status",
        ["node-presence.txt"] = "status", ["node-command.txt"] = "status", ["node-commands.txt"] = "status", ["network-logs.json"] = "status",
        ["friends.json"] = "status", ["openai-models.json"] = "status", ["host-hardware.json"] = "status", ["status.json"] = "status",
        ["sound-digest.json"] = "status", ["live-floor.json"] = "status",
        // Data made again from what is shared, or measured, or downloaded.
        ["appearance-colors.json"] = "cache", ["clips.json"] = "cache", ["model-catalog.json"] = "cache",
        ["Martlet.Core.model-catalog.json"] = "cache", ["model-speed.json"] = "cache", ["config.json"] = "cache",
        // Records: identities, sync bookkeeping, copies of shared documents, queues and what Martlet did.
        ["device.json"] = "record", ["accounts.json"] = "record", ["session.json"] = "record", ["working-copy.json"] = "record",
        ["joined-account.json"] = "record", ["shared-settings.json"] = "record", ["cluster.json"] = "record", ["network.json"] = "record",
        ["memories.json"] = "record", ["memory-sync.json"] = "record", [".martlet-memory.v1.json"] = "record", ["creations.json"] = "record",
        ["creations-sync.json"] = "record", ["creations-moved.json"] = "record", ["character-models.json"] = "record",
        ["node-presence.json"] = "record", ["network-logs-positions.json"] = "record", ["background-jobs.json"] = "record",
        ["helper-jobs.json"] = "record", ["situation.json"] = "record", ["discord-replies.json"] = "record", ["greeting.json"] = "record",
        ["pending-share-link.txt"] = "record", ["share-links.json"] = "record", ["api-keys.json"] = "record", ["continue-setup.json"] = "record",
        ["this-pc-host-roles.txt"] = "record", ["motion-starters.json"] = "record", [".guide.json"] = "record", ["library.json"] = "record",
        ["probe.json"] = "record", ["sent.json"] = "record", [".martlet-f5-references.v1.json"] = "record",
        // Files of the Linux host service, not of a desktop's data folder.
        ["host.json"] = "host", ["gpus.json"] = "host", ["commands.json"] = "host", ["exposure.json"] = "host", ["home-assistant.json"] = "host",
        ["logs.json"] = "host", ["logs-recent.json"] = "host", ["signin.json"] = "host",
        // Installation, update and package files.
        ["last-install.txt"] = "package", ["resume.txt"] = "package", ["journal-v1.json"] = "package", ["journal-v2.json"] = "package",
        ["journal-v3.json"] = "package", ["journal-v4.json"] = "package", ["import-evidence.v1.json"] = "package",
        ["package-owner.v1.json"] = "package", ["package-receipt.v1.json"] = "package", ["whisper-package.v1.json"] = "package",
        ["sbom.cdx.json"] = "package", ["cloudflared.json"] = "package", ["Martlet.VoiceActivity.model-manifest.json"] = "package",
        // Older choices read once into a shared list or setting, and never written again.
        ["deep-thinking.json"] = "migration", ["reading.json"] = "migration", ["pictures.json"] = "migration",
        // Checks' own lab files.
        ["role-lab.json"] = "test", ["signin-lab.json"] = "test"
    };

    /// <summary>The registered file with this name (its path in the data folder), or null.</summary>
    public static SettingsFile? Find(string fileName) =>
        All.FirstOrDefault(file => string.Equals(file.FileName, fileName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised with a file's full path after its store saved it on this computer, so a reader that keeps a copy (the
    /// desktop's WorkSharingRoster) reads it again at once. Runs on the thread that saved; a handler must not throw.</summary>
    public static event Action<string>? Saved;

    /// <summary>For a store, after it saved <paramref name="path"/>: raises <see cref="Saved"/>.</summary>
    public static void NotifySaved(string path) => Saved?.Invoke(path);
}

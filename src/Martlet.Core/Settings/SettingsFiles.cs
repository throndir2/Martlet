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
/// listed: <see cref="AppSettingsSections"/> splits it into its shared settings.</summary>
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
        new(MeaningSearchFile.FileName, SettingsFileScope.Shared, MeaningSearchFile.SharedKey, "Martlet.Core.Settings.MeaningSearchFile"),
        new("character-actions.json", SettingsFileScope.Shared, "character-actions", "Martlet.Avatar.Hosting.CharacterActions"),
        new("updates.json", SettingsFileScope.Shared, "updates", "Martlet.Desktop.UpdatePreferences"),
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
        new(MemoryBriefFile.FileName, SettingsFileScope.Account, MemoryBriefFile.SharedKey, "Martlet.Core.Settings.MemoryBriefFile"),
        new("voice-id.json", SettingsFileScope.Account, SettingScopes.VoiceId, "Martlet.Desktop.VoiceIdentity"),
        // This computer's.
        new(PoolSettings.LocalFileName, SettingsFileScope.Local, null, "Martlet.Core.Cluster.PoolSettings", "Each computer chooses them itself"),
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
        new("hosts.json", SettingsFileScope.Local, null, "Martlet.Desktop.HostRegistry", "How this computer reaches others, and who may reach it")
    ];

    /// <summary>The registered file with this name (its path in the data folder), or null.</summary>
    public static SettingsFile? Find(string fileName) =>
        All.FirstOrDefault(file => string.Equals(file.FileName, fileName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised with a file's full path after its store saved it on this computer, so a reader that keeps a copy (the
    /// desktop's WorkSharingRoster) reads it again at once. Runs on the thread that saved; a handler must not throw.</summary>
    public static event Action<string>? Saved;

    /// <summary>For a store, after it saved <paramref name="path"/>: raises <see cref="Saved"/>.</summary>
    public static void NotifySaved(string path) => Saved?.Invoke(path);
}

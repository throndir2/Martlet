namespace Martlet.Core.Sync;

/// <summary>Which shared settings belong to the household and which to each account (docs/ACCOUNTS.md, "Scopes"). The household
/// keeps how Martlet thinks, listens and speaks with the paid API keys, the Thinking fallback, Home Assistant, updates and the
/// other household sections in one document (<c>/martlet/v1/settings</c>). Each account keeps its personalities and character
/// profiles, replies, prompts, memory on or off, lorebooks, the character shown, how you talk, speech display, the theme,
/// Voice ID, touch temperaments and its reminders in a document of its own (<c>/martlet/v1/settings/accounts/{account}</c>).
/// Any key not listed here (for example <c>sharing.&lt;account&gt;</c>) is the household's.</summary>
public static class SettingScopes
{
    public const string Household = "household", Account = "account";

    public const string Character = "character", Talk = "talk", SpeechDisplay = "speech-display", Appearance = "appearance",
        AppearanceCustom = "appearance-custom", VoiceId = "voice-id", TouchTemperament = "touch-temperament";

    /// <summary>The account sections, in the order a computer applies them (the custom palette before the theme).</summary>
    public static IReadOnlyList<string> AccountKeys { get; } =
    [
        AppSettingsSections.Companion, AppSettingsSections.Replies, AppSettingsSections.Prompts, AppSettingsSections.Memory,
        AppSettingsSections.Lorebooks, Character, Talk, SpeechDisplay, AppearanceCustom, Appearance, VoiceId, TouchTemperament
    ];

    private static readonly HashSet<string> AccountSet = new(AccountKeys, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="key"/> belongs to each account: one of <see cref="AccountKeys"/>, or a computer's reminders
    /// entry (<c>reminders.&lt;device&gt;</c>), which each account keeps for itself.</summary>
    public static bool IsAccountKey(string key) =>
        AccountSet.Contains(key) || key.StartsWith(SharedSettings.RemindersPrefix, StringComparison.Ordinal);

    /// <summary><see cref="Account"/> or <see cref="Household"/>.</summary>
    public static string Of(string key) => IsAccountKey(key) ? Account : Household;
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The <c>talk</c> shared setting (docs/CLUSTER.md#one-martlet-on-every-computer): the parts of how you talk
/// (<see cref="TalkPreferences"/>) that are the same on every computer, as they travel, and how a computer follows them. What
/// describes one computer (its microphone sensitivity, cameras, Watch on or off, echo reduction, its voice volume) stays there.</summary>
internal static class TalkSharing
{
    // The same options as the desktop's other shared settings: snake_case names, no nulls, enums as names.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    // TranscribeFirst: Companion › Listening › When Thinking can hear you (an older computer leaves it out: straight, the default).
    // BargeInStyle: Companion › Listening › When you talk over Martlet (an older computer leaves it out: pause and decide).
    // ReadEveryMs: Companion › Reading › Read the screen (an older computer leaves it out: this PC keeps its own).
    // HearVoice is what an older computer reads (on only when chosen); HearChoice is Let Thinking hear my voice as chosen (null:
    // never chosen, on only while the recording stays on that computer), sent with HearVersion so a copy from an older computer,
    // whose off was only its default, leaves it unchosen.
    private sealed record SharedTalk(bool HandsFree, int PauseIndex, bool SpeakReplies, bool HearVoice, bool BargeIn, int ScreenChattiness,
        ListeningSensitivity WordCheck = ListeningSensitivity.Normal, bool TranscribeFirst = false, bool? HearChoice = null, int HearVersion = 0,
        Martlet.Conversation.BargeInBehavior BargeInStyle = Martlet.Conversation.BargeInBehavior.PauseAndDecide, int? ReadEveryMs = null)
    {
        internal const int ThreeWayHearing = 1;
        internal bool? Hearing => HearVersion >= ThreeWayHearing ? HearChoice : HearVoice ? true : null;
    }

    /// <summary>The <c>talk</c> shared setting's value for <paramref name="prefs"/>.</summary>
    internal static string Value(TalkPreferences prefs) => JsonSerializer.Serialize(new SharedTalk(prefs.HandsFree, prefs.PauseIndex,
        prefs.SpeakReplies, prefs.HearVoice == true, prefs.BargeIn, prefs.ScreenChattiness, prefs.WordCheck, prefs.TranscribeFirst,
        prefs.HearVoice, SharedTalk.ThreeWayHearing, prefs.BargeInStyle, prefs.ReadEveryMs), Json);

    /// <summary>This computer's <paramref name="current"/> choices after following the <c>talk</c> shared setting
    /// <paramref name="value"/> from another computer: what travels changes, the rest stays (a value from an older computer
    /// without Read the screen keeps this one's).</summary>
    internal static TalkPreferences Follow(TalkPreferences current, string value)
    {
        var shared = JsonSerializer.Deserialize<SharedTalk>(value, Json) ?? throw new JsonException();
        return current with
        {
            HandsFree = shared.HandsFree, PauseIndex = Math.Clamp(shared.PauseIndex, 0, TalkPreferences.Pauses.Length - 1),
            SpeakReplies = shared.SpeakReplies, HearVoice = shared.Hearing, BargeIn = shared.BargeIn,
            ScreenChattiness = (int)ChattinessTags.Choice(shared.ScreenChattiness),
            WordCheck = Enum.IsDefined(shared.WordCheck) ? shared.WordCheck : ListeningSensitivity.Normal,
            TranscribeFirst = shared.TranscribeFirst,
            BargeInStyle = Enum.IsDefined(shared.BargeInStyle) ? shared.BargeInStyle : Martlet.Conversation.BargeInBehavior.PauseAndDecide,
            ReadEveryMs = shared.ReadEveryMs is { } every ? ScreenReadPace.Normalize(every) : current.ReadEveryMs
        };
    }
}

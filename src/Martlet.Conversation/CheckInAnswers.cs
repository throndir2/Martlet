using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Providers;
using static Martlet.Conversation.CheckIns;
using static Martlet.Conversation.CheckInText;

namespace Martlet.Conversation;

/// <summary>How Martlet reads a member's answer to a check-in (<see cref="Read"/>).</summary>
public static partial class CheckInAnswers
{
    /// <summary>Reads a member's answer to <paramref name="checkIn"/> (asked with <paramref name="state"/>, narrowed with
    /// <see cref="Focus"/>). Lingering emotes: the tags of the emotes asked about on "OFF" lines; KEEP is nothing. Where the
    /// character looks: USUAL or KEEP. Callback list: every CALLBACK: line is the new list, NONE empties it, KEEP leaves it. The
    /// others: the text of a "REMIND:" (or "SAY:", or "KNOW:") line, or OK. The last such line decides,
    /// so thinking written before the answer doesn't count; markdown, bullets, quotes and a reasoning model's &lt;think&gt; block
    /// are skipped. Anything else reads as unreadable, which changes nothing.</summary>
    public static CheckInVerdict Read(CheckIn checkIn, string? answer, CheckInState state)
    {
        var lines = Lines(answer);
        switch (checkIn.Outcome)
        {
            // The tool calls were the action; the answer only says what it did.
            case CheckInOutcome.Tools:
                return CheckInVerdict.Nothing;
            case CheckInOutcome.EmotesOff:
            {
                var tags = new List<string>();
                bool off = false, keep = false;
                foreach (var line in lines)
                {
                    if (Starts(line, "OFF"))
                    {
                        off = true;
                        var words = Words(line[3..]);
                        foreach (var emote in state.Emotes)
                            if (words.Contains(emote.Tag, StringComparer.OrdinalIgnoreCase) && !tags.Contains(emote.Tag, StringComparer.OrdinalIgnoreCase))
                                tags.Add(emote.Tag);
                    }
                    else if (Starts(line, "KEEP") || Starts(line, "OK")) keep = true;
                }
                return tags.Count > 0 ? new(true, tags, null, true) : off || keep ? CheckInVerdict.Nothing : CheckInVerdict.Unreadable;
            }
            case CheckInOutcome.GazeUsual:
                foreach (var line in lines.Reverse())
                {
                    if (Starts(line, "USUAL")) return new(true, [], null, true);
                    if (Starts(line, "KEEP") || Starts(line, "OK")) return CheckInVerdict.Nothing;
                }
                return CheckInVerdict.Unreadable;
            case CheckInOutcome.HoldBack:
                foreach (var line in lines.Reverse())
                {
                    if (Starts(line, "ASIDE"))
                    {
                        var about = line[5..].TrimStart(':', '-', ' ', '\u2013', '\u2014').Trim().Trim('"', '\u201C', '\u201D').Trim();
                        if (about.Length == 0) about = "someone else";
                        return NothingSaid(about) ? CheckInVerdict.Nothing : new(true, [], Clip(about, MaximumAsideCharacters), true);
                    }
                    if (Starts(line, "BACK")) return new(true, [], null, true);
                    if (Starts(line, "OK") || Starts(line, "KEEP")) return CheckInVerdict.Nothing;
                }
                return CheckInVerdict.Unreadable;
            // The whole new list: every CALLBACK: line; NONE empties it; KEEP (or OK) leaves it.
            case CheckInOutcome.Callbacks:
            {
                var items = new List<string>();
                bool none = false, keep = false;
                foreach (var line in lines)
                {
                    if (Starts(line, "CALLBACK"))
                    {
                        var item = line["CALLBACK".Length..].TrimStart(':', '-', ' ', '\u2013', '\u2014').Trim();
                        if (item.Length > 0 && !NothingSaid(item) && items.Count < CallbackList.MaximumItems &&
                            !items.Contains(item, StringComparer.OrdinalIgnoreCase))
                            items.Add(Clip(item, CallbackList.MaximumCharacters));
                    }
                    else if (Starts(line, "NONE")) none = true;
                    else if (Starts(line, "KEEP") || Starts(line, "OK")) keep = true;
                }
                return items.Count > 0 ? new(true, items, null, true) : none ? new(true, [], null, true)
                    : keep ? CheckInVerdict.Nothing : CheckInVerdict.Unreadable;
            }
            default:
            {
                string[] keywords = checkIn.Outcome switch
                {
                    CheckInOutcome.Say => ["SAY"],
                    CheckInOutcome.Context => ["KNOW"],
                    CheckInOutcome.Plan => ["PLAN"],
                    _ => ["REMIND", "REMINDER"]
                };
                foreach (var line in lines.Reverse())
                {
                    if (keywords.FirstOrDefault(keyword => Starts(line, keyword)) is { } found)
                    {
                        var text = line[found.Length..].TrimStart(':', '-', ' ', '\u2013', '\u2014').Trim().Trim('"', '\u201C', '\u201D').Trim();
                        return NothingSaid(text) ? CheckInVerdict.Nothing : new(true, [], Clip(text, MaximumAnswerCharacters), true);
                    }
                    if (Starts(line, "OK") || Starts(line, "KEEP")) return CheckInVerdict.Nothing;
                }
                return CheckInVerdict.Unreadable;
            }
        }
    }


    // The answer's lines without a reasoning model's <think> block, markdown, bullets and quotes; empty lines left out.
    internal static IReadOnlyList<string> Lines(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return [];
        var text = Thinking().Replace(answer, "");
        return [.. text.Split('\n').Select(line => line.Trim().TrimStart('-', '*', '\u2022', '#', '>', '`', '"', '\'', ' ', '\t')
            .Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim())
            .Where(line => line.Length > 0)];
    }

    // Whether the line starts with the word (any case), not with a longer word: "OFF {blush}", "Keep.", but not "Office".
    internal static bool Starts(string line, string word) =>
        line.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (line.Length == word.Length || !char.IsLetterOrDigit(line[word.Length]));

    // The words of an OFF line, with braces, slashes and punctuation around them taken off: "{blush}, {/glasses}." → blush, glasses.
    internal static IReadOnlyList<string> Words(string text) =>
        [.. text.Split([' ', ',', ';', '\t', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('{', '}', '[', ']', '(', ')', '/', '.', ':', '!', '"', '\'', '*', '`'))
            .Where(word => word.Length > 0)];

    // A REMIND:, SAY:, KNOW:, PLAN: or ASIDE: line that says there is nothing to do.
    internal static bool NothingSaid(string text)
    {
        var plain = text.Trim().TrimEnd('.', '!').Trim().ToLowerInvariant();
        return plain.Length == 0 || plain is "ok" or "okay" or "none" or "nothing" or "n/a" or "na" or "no" or "keep" or "no reminder" or
            "nothing needed" or "no need" or "nothing to remind" or "nothing to say" or "nothing to bring up" or "nothing new" or
            "nothing to add" or "nothing to plan" or "nothing to plan ahead" or "no plan" or "nobody" or "no one";
    }

    [GeneratedRegex(@"<think>[\s\S]*?(</think>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Thinking();
}

using System.Buffers;
using System.Globalization;
using System.Text;

namespace Martlet.Participation;

internal static class TextRules
{
    private static readonly string[] Greetings = ["", "HEY ", "HI ", "HELLO ", "OKAY "];
    private static readonly string[] Requests =
    [
        "CAN YOU ", "COULD YOU ", "WOULD YOU ", "WILL YOU ", "PLEASE ",
        "WHAT ", "WHAT'S ", "HOW ", "WHY ", "WHERE ", "WHEN ", "WHO ",
        "DO YOU ", "ARE YOU "
    ];
    private static readonly string[] EmptyMarkers =
    [
        "[NO SPEECH]", "[SILENCE]", "[MUSIC]", "[BLANK_AUDIO]", "[INAUDIBLE]",
        "(NO SPEECH)", "(SILENCE)", "(MUSIC)", "(INAUDIBLE)"
    ];
    private static readonly string[] ReportingWords = ["SAID", "SAYS", "ASKED", "WROTE"];

    internal static bool ValidUnicode(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    internal static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    internal static string ValidateName(string name)
    {
        PolicyChecks.Require(name is not null && name.Length is >= 1 and <= 32 && ValidUnicode(name),
            PolicyValidationCode.InvalidConfiguration);
        var normalized = Normalize(name!);
        PolicyChecks.Require(normalized.Length is >= 1 and <= 32 && normalized == normalized.Trim() &&
            normalized.Split(' ').Length <= 3 && !normalized.Contains("  ", StringComparison.Ordinal) &&
            normalized.EnumerateRunes().All(rune => Rune.IsLetter(rune) || rune.Value == ' ' ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) &&
            Rune.IsLetter(Rune.GetRuneAt(normalized, 0)),
            PolicyValidationCode.InvalidConfiguration);
        return name!;
    }

    internal static bool NoSpeech(Transcript transcript)
    {
        if (transcript.Evidence == SpeechEvidence.NoSpeech) return true;
        var normalized = Normalize(transcript.Text).Trim();
        return EmptyMarkers.Contains(normalized, StringComparer.Ordinal) ||
            !normalized.EnumerateRunes().Any(Rune.IsLetterOrDigit);
    }

    private static bool PlainAutomaticText(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            var category = char.GetUnicodeCategory(c);
            if (c is '\'' or '\u2019')
            {
                if (index == 0 || index == text.Length - 1 ||
                    !char.IsLetter(text[index - 1]) || !char.IsLetter(text[index + 1])) return false;
            }
            else if (c is '"' or '`' or '~' or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or
                ':' or ';' or '/' or '\\' or '|' or '\n' or '\r' ||
                category is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                    UnicodeCategory.Control or UnicodeCategory.Format) return false;
        }
        return !ReportingWords.Any(word => ContainsWord(text, word));
    }

    private static bool ContainsWord(string text, string word)
    {
        var start = 0;
        while ((start = text.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + word.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1])) &&
                (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            start = end;
        }
        return false;
    }

    internal static bool NameAddressed(Transcript transcript, ParticipationConfiguration configuration)
    {
        var text = Normalize(transcript.Text).Trim();
        if (!PlainAutomaticText(text)) return false;
        foreach (var greeting in Greetings)
        foreach (var name in configuration.NormalizedNames)
        {
            var prefix = greeting + name + ",";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var request = text[prefix.Length..];
            if (request.Length == 0 || request[0] != ' ') continue;
            request = request.TrimStart(' ');
            if (Requests.Any(cue => HasPayload(request, cue))) return true;
        }
        return false;
    }

    internal static bool GroupInvitation(Transcript transcript)
    {
        var text = Normalize(transcript.Text).Trim();
        return PlainAutomaticText(text) && text.EndsWith('?') &&
            (HasPayload(text, "DOES ANYONE KNOW ") || HasPayload(text, "CAN ANYONE EXPLAIN "));
    }

    private static bool HasPayload(string text, string cue) => text.StartsWith(cue, StringComparison.Ordinal) &&
        text[cue.Length..].EnumerateRunes().Any(Rune.IsLetterOrDigit);
}

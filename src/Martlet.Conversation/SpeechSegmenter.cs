using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

internal sealed record SpeechPiece(string? Text);

// English-oriented plain prose, not a Markdown parser. State survives arbitrary delta boundaries.
// silentWord: a reply sentence that is just this word (for example "[pass]" or "Pass.") means "say nothing".
// eagerFirstClause: until something has been said, a comma, semicolon or dash after a long enough clause also ends a piece, so
// the first audio starts before the first sentence is finished; later pieces stay whole sentences for natural prosody.
// tags: the voice engine's own tags (Martlet.Core.Settings.SpeechEngine.Tags), passed through exactly as the engine spells
// them; every other registered engine's tag is dropped. Neither silences its sentence the way other bracketed text does.
internal sealed class SpeechSegmenter(int byteLimit, int characterLimit, string? silentWord = null, bool eagerFirstClause = false,
    IReadOnlyList<VoiceTag>? tags = null)
{
    internal const int FirstClauseMinimum = 24;
    private readonly StringBuilder sentence = new();
    private readonly IReadOnlyList<VoiceTag> keep = tags ?? [];
    private readonly VoiceTag[] known = [.. VoiceTags.Known.Concat(tags ?? [])
        .DistinctBy(tag => tag.Text, StringComparer.OrdinalIgnoreCase)];
    private readonly StringBuilder candidateTag = new();
    private bool droppedTag;
    private bool spoke;
    private bool pendingBoundary, fenced, suppressLine, atLineStart = true;
    private bool openingRun, markerAtLineStart, closingLine, closingWhitespace;
    private int markerRun, fenceLength, leadingSpaces, characters;
    private char marker, fenceMarker;

    internal IEnumerable<SpeechPiece> Push(string delta)
    {
        foreach (char c in delta)
        {
            if (++characters > characterLimit)
                throw new ConversationException(ConversationFailure.LimitExceeded);
            // A tag may arrive split across deltas: hold its prefix until it completes or stops matching.
            if (!fenced && (candidateTag.Length > 0 || known.Any(tag => char.ToLowerInvariant(tag.Text[0]) == char.ToLowerInvariant(c))))
            {
                candidateTag.Append(c);
                var held = candidateTag.ToString();
                if (known.FirstOrDefault(tag => string.Equals(tag.Text, held, StringComparison.OrdinalIgnoreCase)) is { } whole)
                {
                    candidateTag.Clear();
                    foreach (var piece in AcceptTag(whole)) yield return piece;
                    continue;
                }
                if (known.Any(tag => tag.Text.StartsWith(held, StringComparison.OrdinalIgnoreCase))) continue;
                candidateTag.Clear();
                foreach (var replayed in held)
                    foreach (var piece in Accept(replayed)) yield return piece;
                continue;
            }
            foreach (var piece in Accept(c)) yield return piece;
        }
    }

    // A whole tag: the engine's own tags join the sentence as written; other engines' tags are dropped.
    private IEnumerable<SpeechPiece> AcceptTag(VoiceTag tag)
    {
        if (pendingBoundary)
            foreach (var piece in Flush()) yield return piece;
        pendingBoundary = false;
        atLineStart = false;
        markerRun = 0;
        openingRun = false;
        if (keep.FirstOrDefault(k => string.Equals(k.Text, tag.Text, StringComparison.OrdinalIgnoreCase)) is { } kept)
        {
            if (sentence.Length > 0 && !char.IsWhiteSpace(sentence[^1])) sentence.Append(' ');
            sentence.Append(kept.Text);
        }
        else droppedTag = true;
    }

    private IEnumerable<SpeechPiece> Accept(char c)
    {
        {
            if (pendingBoundary && char.IsWhiteSpace(c))
            {
                foreach (var piece in Flush()) yield return piece;
            }
            pendingBoundary = false;
            if (c is '\r') yield break;
            if (c is '\n')
            {
                foreach (var piece in Flush()) yield return piece;
                if (closingLine) fenced = false;
                atLineStart = true;
                leadingSpaces = markerRun = 0;
                openingRun = closingLine = closingWhitespace = false;
                suppressLine = fenced;
                yield break;
            }
            bool wasLineStart = atLineStart;
            if (atLineStart)
            {
                if (c == ' ') leadingSpaces++;
                else
                {
                    suppressLine |= leadingSpaces >= 4 || c is '\t' or '-' or '+' or '>';
                    atLineStart = false;
                }
            }
            if (closingLine)
            {
                if (char.IsWhiteSpace(c)) closingWhitespace = true;
                else if (c != fenceMarker || closingWhitespace) closingLine = false;
            }
            if (c is '`' or '~')
            {
                markerRun = marker == c ? markerRun + 1 : 1;
                marker = c;
                if (markerRun == 1)
                {
                    markerAtLineStart = wasLineStart && leadingSpaces <= 3;
                    openingRun = false;
                }
                if (!fenced && markerRun == 3)
                {
                    fenced = openingRun = true;
                    fenceMarker = c;
                    fenceLength = markerRun;
                }
                else if (openingRun) fenceLength = markerRun;
                else if (fenced && markerAtLineStart && c == fenceMarker && markerRun >= fenceLength)
                    closingLine = true;
            }
            else
            {
                markerRun = 0;
                openingRun = false;
            }
            if (c is '`' or '~' or '*' or '_' or '#' or '[' or ']' or '{' or '}' or '<' or '>' or
                '|' or '\\' or '/' or ':' or '@' or '=' or '^' or '&' or '\t')
                suppressLine = true;
            sentence.Append(c);
            pendingBoundary = c is '.' or '!' or '?' ||
                eagerFirstClause && !spoke && c is ',' or ';' or '\u2014' or '\u2013' && sentence.Length >= FirstClauseMinimum;
        }
    }

    internal IEnumerable<SpeechPiece> Finish()
    {
        // An unfinished tag prefix at the end is ordinary text.
        var held = candidateTag.ToString();
        candidateTag.Clear();
        foreach (var replayed in held)
            foreach (var piece in Accept(replayed)) yield return piece;
        foreach (var piece in Flush()) yield return piece;
    }

    internal void Clear()
    {
        sentence.Clear();
        candidateTag.Clear();
    }

    private IEnumerable<SpeechPiece> Flush()
    {
        if (sentence.Length == 0) yield break;
        string candidate = sentence.ToString().Trim();
        sentence.Clear();
        if (droppedTag) candidate = VoiceTags.Tidy(candidate);
        droppedTag = false;
        if (candidate.Length == 0) yield break;
        // Numeric list markers and bare dotted addresses are deliberately outside the prose subset.
        if (candidate.Length > 1 && candidate[^1] == '.' && candidate.AsSpan(0, candidate.Length - 1).IndexOfAnyExceptInRange('0', '9') < 0)
            suppressLine = true;
        if (fenced || suppressLine || ContainsDottedToken(candidate) || IsSilent(candidate, silentWord))
        {
            yield return new(null);
            yield break;
        }
        foreach (var part in Split(candidate, byteLimit))
        {
            spoke = true;
            yield return new(part);
        }
    }

    internal static bool IsSilent(string text, string? silentWord) =>
        silentWord is not null && string.Equals(text.Trim().Trim('[', ']', '(', ')', '<', '>', '*', '"', '\'', '.', '!', ' '),
            silentWord, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDottedToken(string text)
    {
        for (int i = 1; i + 1 < text.Length; i++)
            if (text[i] == '.' && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]))
                return true;
        return false;
    }

    internal static IEnumerable<string> Split(string text, int limit)
    {
        ContractRules.Require(limit is >= 4 and <= BoundedSpeechInput.HardMaxUtf8Bytes,
            "Speech segmentation needs a byte limit of 4 through 1536.");
        int start = 0, position = 0, bytes = 0;
        while (position < text.Length)
        {
            if (!Rune.TryGetRuneAt(text, position, out var rune))
                throw new ConversationException(ConversationFailure.InvalidStream);
            if (bytes + rune.Utf8SequenceLength > limit)
            {
                var part = text[start..position].Trim();
                if (part.Length != 0) yield return part;
                start = position;
                bytes = 0;
            }
            position += rune.Utf16SequenceLength;
            bytes += rune.Utf8SequenceLength;
        }
        var last = text[start..].Trim();
        if (last.Length != 0) yield return last;
    }
}

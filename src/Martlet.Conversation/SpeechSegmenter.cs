using System.Text;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Conversation;

internal sealed record SpeechPiece(string? Text);

// English-oriented plain prose, not a Markdown parser. State survives arbitrary delta boundaries.
// silentWord: a reply sentence that is just this word (for example "[pass]" or "Pass.") means "say nothing".
// eagerFirstClause: until something has been said, a comma, semicolon or dash after a long enough clause also ends a piece, so
// the first audio starts before the first sentence is finished; later pieces stay whole sentences for natural prosody.
internal sealed class SpeechSegmenter(int byteLimit, int characterLimit, string? silentWord = null, bool eagerFirstClause = false)
{
    internal const int FirstClauseMinimum = 24;
    private readonly StringBuilder sentence = new();
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
            if (pendingBoundary && char.IsWhiteSpace(c))
            {
                foreach (var piece in Flush()) yield return piece;
            }
            pendingBoundary = false;
            if (c is '\r') continue;
            if (c is '\n')
            {
                foreach (var piece in Flush()) yield return piece;
                if (closingLine) fenced = false;
                atLineStart = true;
                leadingSpaces = markerRun = 0;
                openingRun = closingLine = closingWhitespace = false;
                suppressLine = fenced;
                continue;
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

    internal IEnumerable<SpeechPiece> Finish() => Flush();

    internal void Clear() => sentence.Clear();

    private IEnumerable<SpeechPiece> Flush()
    {
        if (sentence.Length == 0) yield break;
        string candidate = sentence.ToString().Trim();
        sentence.Clear();
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

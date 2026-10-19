namespace Martlet.Core.Text;

/// <summary>Splits a long reply into chat messages that each fit an app's longest message (Discord, Telegram, WhatsApp).</summary>
public static class MessageSplitter
{
    /// <summary>Splits <paramref name="text"/> into messages of at most <paramref name="limit"/> characters, at a paragraph,
    /// line, sentence or word break when there is one in the second half of a piece, and never inside a surrogate pair. Gives
    /// no empty pieces.</summary>
    public static IReadOnlyList<string> Split(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        List<string> pieces = [];
        var rest = text.Trim();
        while (rest.Length > limit)
        {
            var cut = Break(rest, limit);
            var piece = rest[..cut].TrimEnd();
            if (piece.Length > 0) pieces.Add(piece);
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) pieces.Add(rest);
        return pieces;
    }

    private static int Break(string text, int limit)
    {
        var window = text[..limit];
        var least = limit / 2;
        foreach (var separator in new[] { "\n\n", "\n", ". ", "! ", "? ", " " })
        {
            var at = window.LastIndexOf(separator, StringComparison.Ordinal);
            if (at >= least) return at + separator.Length;
        }
        return char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }
}

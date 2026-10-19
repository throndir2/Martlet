using System.Text.RegularExpressions;

namespace Martlet.Core.Text;

/// <summary>Where the sentences of a UI text end. The 1.0 rule for UI text (issue #992) is one short sentence on the page and
/// the rest behind a "?": the desktop's HelpTip shows the <see cref="Split"/> first sentence, and MCP's ui_long_texts lists the
/// texts with a <see cref="Count"/> above one. An abbreviation such as "e.g." is not a sentence end.</summary>
public static partial class Sentences
{
    /// <summary>The first sentence of <paramref name="text"/> and the rest (More), or null when the text has only one sentence.</summary>
    public static (string First, string? More) Split(string text)
    {
        var end = End().Match(text);
        if (!end.Success) return (text.Trim(), null);
        var at = end.Index + end.Length;
        var rest = text[at..].Trim();
        return rest.Length == 0 ? (text.Trim(), null) : (text[..at].Trim(), rest);
    }

    /// <summary>How many sentences <paramref name="text"/> has (0 for an empty text).</summary>
    public static int Count(string text) => string.IsNullOrWhiteSpace(text) ? 0 : End().Count(text.Trim()) + 1;

    [GeneratedRegex(@"(?<!\b(?:e\.g|i\.e|etc|vs|Mr|Dr))[.!?][""”)]?\s+(?=[""“(]?[A-Z0-9{])", RegexOptions.CultureInvariant)]
    private static partial Regex End();
}

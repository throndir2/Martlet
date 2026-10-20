using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Companion › Prompts says, without WPF: each prompt's state, the estimated tokens and how many prompts are edited.
/// Check-ins show their prompts' states with <see cref="PromptState"/> too.</summary>
internal static class PromptsPageModel
{
    /// <summary>A prompt's state in words, against what is saved: built in, edited, empty (sent as nothing), its estimated tokens,
    /// and whether it is still saving.</summary>
    internal static string PromptState(PromptDefinition prompt, string text, PromptSettings? saved)
    {
        var sent = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var state = string.IsNullOrWhiteSpace(text) ? "Empty: nothing is sent for this prompt"
            : (text == prompt.Default ? "Built-in text, about " : "Edited, about ") + Tokens(PromptTokens(sent));
        return (sent == PromptSettings.Text(saved, prompt.Id) ? state : state + ", saving") + ".";
    }

    /// <summary>The estimated tokens of every prompt together, as typed: what all of them would add if one request sent them all.</summary>
    internal static string DescribePromptTokens(IEnumerable<string> texts) =>
        "All prompts together: about " + Tokens(texts.Sum(t => string.IsNullOrWhiteSpace(t) ? 0
            : PromptTokens(t.Replace("\r\n", "\n", StringComparison.Ordinal)))) +
        " (each request sends only the prompts it needs).";

    /// <summary>A prompt's estimated tokens by Martlet's own request-size rule (see <see cref="Martlet.Providers.BoundedTextInput"/>).</summary>
    internal static int PromptTokens(string text) => Martlet.Providers.BoundedTextInput.TextTokens(text);

    private static string Tokens(int count) =>
        count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + (count == 1 ? " token" : " tokens");

    internal static string DescribePrompts(PromptSettings? prompts)
    {
        var total = PromptCatalog.All.Count;
        if (prompts is null) return $"All {total} prompts use Martlet's built-in text.";
        var empty = prompts.Overrides.Count(o => string.IsNullOrWhiteSpace(o.Value));
        var edited = prompts.Overrides.Count - empty;
        var parts = new List<string>();
        if (edited > 0) parts.Add($"{edited} of {total} prompts edited");
        if (empty > 0) parts.Add($"{empty} emptied (not sent)");
        return string.Join(", ", parts) + "; the rest use Martlet's built-in text.";
    }
}

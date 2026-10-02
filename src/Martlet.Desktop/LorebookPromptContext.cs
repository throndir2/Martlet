using System.Text;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Triggered lorebook entries travel in the system instructions as labeled blocks before and/or after the persona.</summary>
internal static class LorebookPromptContext
{
    internal const string Label = "MARTLET_LOREBOOK";

    /// <summary>The before-persona and after-persona blocks for <paramref name="hits"/> (either may be null). The explanation
    /// is written once, in the first block.</summary>
    internal static (string? Before, string? After) Blocks(IReadOnlyList<LorebookHit> hits, PromptSettings? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var before = LorebookScanResult.Arrange(hits, LorebookPosition.BeforePersona);
        var after = LorebookScanResult.Arrange(hits, LorebookPosition.AfterPersona);
        var preamble = PromptSettings.Fill(prompts, PromptCatalog.Lorebook);
        return (before.Count == 0 ? null : Block(before, preamble),
            after.Count == 0 ? null : Block(after, before.Count == 0 ? preamble : null));
    }

    private static string Block(IReadOnlyList<LorebookHit> hits, string? preamble)
    {
        var text = new StringBuilder();
        if (preamble is not null) text.Append(preamble).Append('\n');
        text.Append('[').Append(Label).Append("]\n");
        for (var index = 0; index < hits.Count; index++)
        {
            if (index > 0) text.Append("\n\n");
            text.Append(hits[index].Content.Replace(Label, "lorebook", StringComparison.OrdinalIgnoreCase));
        }
        return text.Append("\n[/").Append(Label).Append(']').ToString();
    }
}

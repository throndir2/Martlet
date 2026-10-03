using System.Globalization;
using System.Text;
using Martlet.Core.Settings;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>Recalled facts travel in the system instructions as one clearly labeled block of background facts.</summary>
internal static class MemoryPromptContext
{
    internal const string Label = "MARTLET_LOCAL_MEMORY";

    internal static string Instructions(IReadOnlyList<MemoryFact> facts, PromptSettings? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var text = new StringBuilder();
        if (PromptSettings.Fill(prompts, PromptCatalog.MemoryRecall, ("label", Label)) is { } preamble) text.Append(preamble).Append('\n');
        text.Append('[').Append(Label).Append("]\n");
        foreach (var fact in facts)
            text.Append("- ").Append(Line(fact)).Append('\n');
        return text.Append("[/").Append(Label).Append(']').ToString();
    }

    private static string Line(MemoryFact fact)
    {
        var content = string.Join(' ', fact.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace(Label, "memory", StringComparison.OrdinalIgnoreCase);
        var expiry = fact.Retention.Kind == MemoryRetentionKind.ExpiresAt
            ? "; until " + fact.Retention.ExpiresAtUtc!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
        return $"{content} ({Source(fact.LastModifiedBy.SourceKind)} " +
            $"{fact.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{expiry})";
    }

    internal static string Source(MemorySourceKind source) => source switch
    {
        MemorySourceKind.UserEntry => "saved by the user",
        MemorySourceKind.UserReviewedImport => "imported by the user",
        MemorySourceKind.Conversation => "from conversation",
        _ => throw new MemoryException(MemoryFailure.InvalidData)
    };
}

using System.Globalization;
using System.Text;
using Martlet.Core.Settings;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>Recalled facts travel in a message's notes as one clearly labeled block of background facts, each fact once while the
/// message that carried it is still in the conversation sent. A fact that belongs to someone starts with their name ("[Sam] "),
/// like their messages in the conversation.</summary>
internal static class MemoryPromptContext
{
    internal const string Label = "MARTLET_LOCAL_MEMORY";

    /// <param name="explainWhose">Whether to say what the names in brackets mean (Companion › Prompts › Whose memories) when a
    /// fact belongs to someone; false when an earlier message the request carries already says it.</param>
    internal static string Instructions(IReadOnlyList<MemoryFact> facts, PromptSettings? prompts = null,
        IReadOnlyDictionary<string, string>? people = null, bool explainWhose = true)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var text = new StringBuilder();
        if (PromptSettings.Fill(prompts, PromptCatalog.MemoryRecall, ("label", Label)) is { } preamble) text.Append(preamble).Append('\n');
        // What the names in brackets mean, only when a fact here belongs to someone.
        if (explainWhose && facts.Any(fact => fact.VoiceId is not null) && PromptSettings.Fill(prompts, PromptCatalog.MemoryPeople) is { } whose)
            text.Append(whose).Append('\n');
        text.Append('[').Append(Label).Append("]\n");
        foreach (var fact in facts)
            text.Append("- ").Append(Line(fact, people)).Append('\n');
        return text.Append("[/").Append(Label).Append(']').ToString();
    }

    /// <param name="people">The label of each voice the facts belong to (<see cref="MemoryPeople.Labels"/>).</param>
    internal static string Line(MemoryFact fact, IReadOnlyDictionary<string, string>? people = null)
    {
        var content = string.Join(' ', fact.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace(Label, "memory", StringComparison.OrdinalIgnoreCase);
        var expiry = fact.Retention.Kind == MemoryRetentionKind.ExpiresAt
            ? "; until " + fact.Retention.ExpiresAtUtc!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
        return Whose(fact, people) + $"{content} ({Source(fact.LastModifiedBy.SourceKind)} " +
            $"{fact.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{expiry})";
    }

    /// <summary>"[Sam] " for a fact that belongs to someone, nothing for a fact about no one in particular.</summary>
    internal static string Whose(MemoryFact fact, IReadOnlyDictionary<string, string>? people) =>
        fact.VoiceId is not { } id ? ""
            : $"[{(people?.TryGetValue(id, out var person) == true ? person : MemoryPeople.Someone).Replace(Label, "memory", StringComparison.OrdinalIgnoreCase)}] ";

    internal static string Source(MemorySourceKind source) => source switch
    {
        MemorySourceKind.UserEntry => "saved by the user",
        MemorySourceKind.UserReviewedImport => "imported by the user",
        MemorySourceKind.Conversation => "from conversation",
        _ => throw new MemoryException(MemoryFailure.InvalidData)
    };
}

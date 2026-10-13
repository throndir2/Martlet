namespace Martlet.Core.Cluster;

/// <summary>An area's list in one sentence: who takes requests, in which order, or what happens while nothing in it is on. The
/// list card on the area's page and Devices › Sharing's line for the area both show it, so they say the same thing.</summary>
public static class PoolListSummary
{
    /// <summary>The sentence for <paramref name="area"/>'s <paramref name="list"/> (null: no list yet). <paramref name="instead"/>:
    /// for a required area while nothing is on, what happens instead, from the area's own decision (Vision and Hearing: which model
    /// takes each picture or recording job); null: the area's <see cref="PoolArea.Fallback"/>. <paramref name="names"/>: name the
    /// members that are on, in order (Devices › Sharing's line), instead of counting them (the list card shows them below).</summary>
    public static string Describe(PoolArea area, PoolList? list, string? instead = null, bool names = false)
    {
        ArgumentNullException.ThrowIfNull(area);
        var on = list?.Members.Where(m => !m.Off).Select(m => m.Name).ToArray() ?? [];
        if (on.Length == 0)
            return area.Required ? $"Nothing in the list is on. Instead: {(instead ?? area.WhenEmpty).TrimEnd('.')}."
                : area.ConversationFirst ? "Its own model only: a reply waits for it when it is busy."
                : "Off: nothing in the list is on.";
        if (area.ConversationFirst)
            return names ? $"Its own model first, then {string.Join(", ", on)}."
                : $"The conversation's own model goes first. When it is busy, a reply goes to the first free one of {on.Length} below.";
        if (names) return $"In order: {string.Join(", ", on)}.";
        return on.Length == 1 ? "One member is on. Requests go to it."
            : $"{on.Length} members are on. A request goes to the first free one, in this order; when all are busy it waits for the first to finish.";
    }
}

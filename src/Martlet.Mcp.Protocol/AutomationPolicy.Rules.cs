namespace Martlet.Mcp;

/// <summary>What the ui_* tools may do in the Martlet desktop without --allow-ui-effects. This is a safety boundary.
/// <see cref="SafeClicks"/> and <see cref="SafeClickPrefixes"/> are the controls ui_click may press: they only navigate, open,
/// close, expand, refresh or read this PC, and start no work. They may also ask the local network a discovery question that
/// carries none of your data (Martlet's own discovery query, one multicast DNS question), and stop what Martlet does at this
/// moment (a reply, a song, finding zones). <see cref="SafeValues"/> and <see cref="SafeValuePrefixes"/> are the controls whose
/// text ui_snapshot returns: read-only status that is never a secret, never personal text and never a file path; ui_snapshot
/// also replaces any e-mail address with [email] and any file path with [path] in that text (<see cref="Redact"/>).
/// A prefix entry covers every automation ID that starts with it. Anything that sends, records, plays, spends, writes files,
/// handles credentials, contacts another computer by its address (a host, a ComfyUI server, a cloud provider), cancels a
/// background job or pauses, stops listening or stops watching stays out of the click lists and needs --allow-ui-effects.
/// Editable fields, credentials and personal data stay out of the value lists. Each line is one automation ID (or prefix) with
/// its page and its effect; the comment lines name the controls next to them that are deliberately left out.</summary>
internal static partial class AutomationPolicy
{
    /// <summary>One automation ID (or the start of a family of generated IDs), the page it is on, and what pressing it does (click
    /// lists) or what its text shows (value lists).</summary>
    internal sealed record Entry(string Id, string Page, string Effect);

    /// <summary>Whether ui_click may press this control without --allow-ui-effects.</summary>
    internal static bool IsSafeClick(string id) => !IsWikiLink(id) &&
        (SafeClickIds.Contains(id) || SafeClickPrefixes.Any(prefix => id.StartsWith(prefix.Id, StringComparison.Ordinal)));

    /// <summary>A help mark's wiki link (Help-&lt;id&gt;-Wiki): a click opens the browser, so it is never a safe click, although
    /// Help- is.</summary>
    private static bool IsWikiLink(string id) =>
        id.StartsWith("Help-", StringComparison.Ordinal) && id.EndsWith("-Wiki", StringComparison.Ordinal);

    /// <summary>Whether ui_snapshot returns this control's text.</summary>
    internal static bool IsSafeValue(string id) =>
        SafeValueIds.Contains(id) || SafeValuePrefixes.Any(prefix => id.StartsWith(prefix.Id, StringComparison.Ordinal));
}

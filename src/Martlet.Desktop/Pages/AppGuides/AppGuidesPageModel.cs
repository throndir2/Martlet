using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Companion › App guides' decisions, without WPF. The page makes one each time it draws (and each time it only updates its
/// status lines while Martlet reads up): it says what the Now line, the In front line and each app's status line show, and reads
/// the Add an app boxes. <see cref="AppGuidesPage"/> draws it.</summary>
internal sealed class AppGuidesPageModel(AppGuideService guides)
{
    /// <summary>The Now card's line (AppGuidesNow).</summary>
    internal string NowText => Now(guides);

    /// <summary>The In front line (AppGuidesFront).</summary>
    internal string FrontText => Front(guides);

    /// <summary>The Now card's warning when the library couldn't be read, or null.</summary>
    internal string? LoadProblemText => guides.LoadProblem is { } unread
        ? $"Martlet couldn't read your guide library ({unread}) and didn't change it; turning App guides on starts a new one and keeps a copy of the old file."
        : null;

    /// <summary>An app's line on the list (AppGuideStatus-&lt;key&gt;).</summary>
    internal string Status(AppGuideEntry entry) => Status(guides, entry);

    /// <summary>The Now card's line: off, or how many guides and what is being read.</summary>
    internal static string Now(AppGuideService guides)
    {
        var library = guides.Library;
        if (!library.On) return "Off: Martlet doesn't read up on games and apps, or notice which one you play.";
        var built = library.Apps.Where(a => a.BuiltAt is not null).ToArray();
        var text = built.Length == 0 ? "On: no guides yet"
            : $"On: {built.Length} guide{(built.Length == 1 ? "" : "s")}, {built.Sum(a => a.Pages)} pages, {Size(built.Sum(a => a.Bytes))}";
        if (guides.Building is { } reading) text += $"; reading up on {reading.Name} ({(reading.Progress ?? "starting").TrimEnd('.')})";
        return text + ".";
    }

    /// <summary>What is in front, as App guides sees it.</summary>
    internal static string Front(AppGuideService guides)
    {
        if (!guides.On) return "Martlet doesn't look at which app is in front while App guides are off.";
        if (guides.Front is not { } front) return "In front: nothing Martlet has noticed yet.";
        var name = front.Entry?.Name ?? (front.Program.Length > 0 ? front.Program : front.File);
        var what = front.Game ? "a game" : front.Entry is not null ? "on your list" : "not a game";
        var guide = front.Entry is { BuiltAt: not null } entry
            ? guides.IsReady(entry.Key) ? "its guide is ready" : "its guide is loading"
            : "no guide";
        return $"In front: {name} ({what}{(front.FullScreen ? ", full screen" : "")}), {guide}.";
    }

    /// <summary>A row's line: its guide (pages, size, when, where from), a problem, being read now, or declined.</summary>
    internal static string Status(AppGuideService guides, AppGuideEntry entry)
    {
        // One sentence: its state first, then the facts, joined with semicolons.
        var parts = new List<string>();
        if (guides.Building is { } reading && reading.Key == entry.Key) parts.Add($"Reading up now ({(reading.Progress ?? "starting").TrimEnd('.')})");
        else if (entry.BuiltAt is { } at) parts.Add($"{entry.Pages} page{(entry.Pages == 1 ? "" : "s")}, {Size(entry.Bytes)}, read {at.ToLocalTime():d MMM yyyy, HH:mm}");
        else parts.Add("No guide yet");
        if (guides.Building?.Key != entry.Key && entry.Problem is { } problem) parts.Add($"the last try didn't work ({problem.TrimEnd('.')})");
        if (guides.GuideProblem(entry.Key) is { } unread) parts.Add($"its guide can't be used ({unread.TrimEnd('.')}), so read it again to make a new one");
        if (entry.Sites.Count > 0)
            parts.Add("reads from " + string.Join(", ", entry.Sites.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var url) ? url.Host : s)));
        if (entry.Programs.Count > 0) parts.Add("programs: " + string.Join(", ", entry.Programs));
        if (entry.Declined) parts.Add("you said no, so Martlet doesn't read up on it on its own");
        return string.Join("; ", parts) + ".";
    }

    internal static string Size(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{Math.Max(1, bytes / 1_000)} KB";

    /// <summary>What the Add an app boxes hold, read: the app's name, its program names, the addresses typed and the web
    /// addresses among them.</summary>
    internal static (string Name, string[] Programs, string[] Given, string[] Sites) AddInput(string name, string programs, string sites)
    {
        var given = sites.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (AppGuideTools.CleanName(name),
            programs.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            given, given.Select(AppGuideTools.Site).OfType<string>().ToArray());
    }

    /// <summary>What Add says once the app is on the list.</summary>
    internal static string Added(AppGuideEntry entry, int sites, int given) =>
        $"Added {entry.Name}" + (sites < given ? ", without the addresses that weren't web links." : ".");
}

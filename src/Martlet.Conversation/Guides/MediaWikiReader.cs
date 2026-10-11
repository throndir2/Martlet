using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Conversation.Guides;

/// <summary>A MediaWiki site found through its api.php: its name, main page, where its articles are and which namespaces hold
/// content.</summary>
internal sealed class MediaWikiSite
{
    public required Uri Api { get; init; }
    public required string Name { get; init; }
    public required string MainPage { get; init; }
    /// <summary>The site's address ("https://game.fandom.com").</summary>
    public required string Server { get; init; }
    /// <summary>Where an article is, with "$1" for its title ("/wiki/$1").</summary>
    public required string ArticlePath { get; init; }
    public int Articles { get; init; }
    /// <summary>The content namespaces' numbers (0, and any other the wiki marks as content).</summary>
    public required IReadOnlySet<int> ContentNamespaces { get; init; }
    /// <summary>The other namespaces' names and aliases, lower case ("user", "file", "category talk").</summary>
    public required IReadOnlySet<string> OtherNamespaces { get; init; }

    public Uri PageUrl(string title)
    {
        var escaped = Uri.EscapeDataString(title.Replace(' ', '_'));
        foreach (var (code, c) in Kept) escaped = escaped.Replace(code, c, StringComparison.OrdinalIgnoreCase);
        return new Uri(Server + ArticlePath.Replace("$1", escaped, StringComparison.Ordinal));
    }

    private static readonly (string Code, string Char)[] Kept = [("%3A", ":"), ("%2F", "/"), ("%28", "("), ("%29", ")"), ("%2C", ","),
        ("%27", "'"), ("%21", "!"), ("%2A", "*"), ("%3B", ";"), ("%40", "@"), ("%24", "$")];

    /// <summary>The article title a link on the site points at, or null when it isn't an article link.</summary>
    public string? TitleOf(Uri url)
    {
        if (!Uri.TryCreate(Server, UriKind.Absolute, out var server) || !url.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) ||
            url.Query.Length > 0)
            return null;
        var at = ArticlePath.IndexOf("$1", StringComparison.Ordinal);
        var prefix = at < 0 ? ArticlePath : ArticlePath[..at];
        var path = url.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) return null;
        var rest = path[prefix.Length..];
        if (rest.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || rest.Contains(".php/", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Uri.UnescapeDataString(rest).Replace('_', ' ').Trim(); }
        catch (UriFormatException) { return null; }
    }

    /// <summary>Whether a title is in a content namespace (no prefix, or a prefix that isn't another namespace's name).</summary>
    public bool IsContent(string title)
    {
        var colon = title.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 || !OtherNamespaces.Contains(title[..colon].Replace('_', ' ').Trim().ToLowerInvariant());
    }
}

/// <summary>Reads a MediaWiki site (Fandom, wiki.gg and most game wikis) through its api.php: the pages the main page links to
/// and the site's most linked pages first (stubs, disambiguation, version history and very large pages left out), then the
/// pages the pages read link to most, each read as clean article HTML (<c>action=parse</c>, redirects followed, edit links and
/// the table of contents off) and turned into outline text.</summary>
internal static class MediaWikiReader
{
    /// <summary>Articles shorter than this (in wiki text) are stubs; longer than <see cref="LargestPage"/> they would take much
    /// of the download cap (a huge table becomes a megabyte of HTML).</summary>
    public const int SmallestPage = 1_500;
    public const int LargestPage = 150_000;
    /// <summary>Readable text shorter than this isn't kept as a page.</summary>
    public const int MinimumText = 200;
    /// <summary>A probed wiki with fewer articles than this isn't used.</summary>
    public const int MinimumArticles = 10;

    private static readonly Regex VersionTitle = new("\\d+\\.\\d+|version history|patch notes|changelog|release notes",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The MediaWiki site whose api.php is <paramref name="api"/>, or null when it isn't one (or can't be read).</summary>
    public static async Task<MediaWikiSite?> ProbeAsync(GuideRun run, Uri api)
    {
        WebDocument? document;
        try
        {
            document = await run.GetAsync(Query(api, ("action", "query"), ("meta", "siteinfo"),
                ("siprop", "general|namespaces|namespacealiases|statistics")), 400_000).ConfigureAwait(false);
        }
        catch (WebResearchException) { return null; }
        if (document is null) return null;
        try
        {
            using var json = JsonDocument.Parse(document.Body);
            if (!json.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("general", out var general) ||
                !(String(general, "generator") ?? "").StartsWith("MediaWiki", StringComparison.OrdinalIgnoreCase))
                return null;
            var final = new Uri(new Uri(document.Url).GetLeftPart(UriPartial.Path));
            var server = String(general, "server") ?? final.GetLeftPart(UriPartial.Authority);
            if (server.StartsWith("//", StringComparison.Ordinal)) server = final.Scheme + ":" + server;
            var content = new HashSet<int> { 0 };
            var other = new HashSet<string>(StringComparer.Ordinal);
            var otherIds = new HashSet<int>();
            if (query.TryGetProperty("namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Object)
                foreach (var entry in namespaces.EnumerateObject())
                {
                    var ns = entry.Value;
                    if (!ns.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id) || id == 0) continue;
                    if (ns.TryGetProperty("content", out var isContent) && isContent.ValueKind == JsonValueKind.True)
                    {
                        if (id > 0) content.Add(id);
                        continue;
                    }
                    otherIds.Add(id);
                    foreach (var name in new[] { String(ns, "name"), String(ns, "canonical") })
                        if (!string.IsNullOrWhiteSpace(name)) other.Add(name.ToLowerInvariant());
                }
            if (query.TryGetProperty("namespacealiases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                foreach (var alias in aliases.EnumerateArray())
                    if (alias.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var id) && otherIds.Contains(id) &&
                        String(alias, "alias") is { Length: > 0 } name)
                        other.Add(name.ToLowerInvariant());
            var articles = query.TryGetProperty("statistics", out var statistics) && statistics.TryGetProperty("articles", out var count) &&
                count.TryGetInt32(out var number) ? number : 0;
            return new()
            {
                Api = final, Name = String(general, "sitename") ?? final.Host, MainPage = String(general, "mainpage") ?? "Main Page",
                Server = server.TrimEnd('/'), ArticlePath = String(general, "articlepath") ?? "/wiki/$1", Articles = articles,
                ContentNamespaces = content, OtherNamespaces = other
            };
        }
        catch (Exception error) when (Malformed(error)) { return null; }
    }

    /// <summary>Reads the site's best pages into the run, the <paramref name="seeds"/> (pages the owner pointed at) first, and
    /// returns how many pages it added.</summary>
    public static async Task<int> ReadAsync(GuideRun run, MediaWikiSite site, IReadOnlyList<string> seeds)
    {
        var before = run.Pages.Count;
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var queue = new List<string>();
        foreach (var seed in seeds)
            if (queued.Add(Key(seed))) queue.Add(seed);
        run.Report("Reading the wiki's page list");
        foreach (var title in await CandidatesAsync(run, site).ConfigureAwait(false))
            if (queued.Add(Key(title))) queue.Add(title);
        var linked = new Dictionary<string, (string Title, int Count)>(StringComparer.Ordinal);
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var next = 0;
        var rounds = 0;
        var blocked = 0;
        while (!run.Full)
        {
            if (next >= queue.Count)
            {
                // Out of pages: the ones the pages read so far link to most.
                var more = linked.Where(l => !queued.Contains(l.Key) && !asked.Contains(l.Key)).OrderByDescending(l => l.Value.Count)
                    .Take(50).ToList();
                if (more.Count == 0 || ++rounds > 4) break;
                asked.UnionWith(more.Select(m => m.Key));
                var (pages, redirects) = await PagesAsync(run, site, [("titles", string.Join('|', more.Select(m => m.Value.Title))),
                    ("prop", "info|pageprops"), ("ppprop", "disambiguation"), ("redirects", "1")]).ConfigureAwait(false);
                var counts = more.ToDictionary(m => m.Key, m => m.Value.Count, StringComparer.Ordinal);
                foreach (var (from, to) in redirects)
                    if (Key(from) != Key(to) && counts.TryGetValue(Key(from), out var count)) counts[Key(to)] = counts.GetValueOrDefault(Key(to)) + count;
                foreach (var page in pages.Where(p => Usable(site, p)).OrderByDescending(p => counts.GetValueOrDefault(Key(p.Title)))
                    .ThenByDescending(p => p.Length))
                    if (queued.Add(Key(page.Title))) queue.Add(page.Title);
                continue;
            }
            var wanted = queue[next++];
            run.Reading();
            WebDocument? document;
            try
            {
                document = await run.GetAsync(Query(site.Api, ("action", "parse"), ("page", wanted), ("prop", "text"), ("redirects", "1"),
                    ("disableeditsection", "1"), ("disabletoc", "1"), ("disablelimitreport", "1"))).ConfigureAwait(false);
            }
            catch (WebResearchException error)
            {
                run.Failed++;
                if (error.Status is 429 or 503) break;
                continue;
            }
            if (document is null)
            {
                if (run.Full || ++blocked >= 3 && run.Pages.Count == before) break;
                continue;
            }
            if (Read(site, document) is not { } read)
            {
                run.Failed++;
                continue;
            }
            if (read.Page.Text.Length >= MinimumText) run.Add(read.Page);
            foreach (var title in read.Links)
            {
                var key = Key(title);
                linked[key] = (linked.TryGetValue(key, out var seen) ? seen.Title : title, seen.Count + 1);
            }
        }
        return run.Pages.Count - before;
    }

    // The pages to read first: those the main page links to and the site's most linked pages, scored by both and by size, then
    // picked one by one with a penalty for each page already picked from the same category, so the guide covers the game's
    // different parts (crops, fish, tools) rather than twenty pages from one navigation box (every villager).
    private static async Task<IReadOnlyList<string>> CandidatesAsync(GuideRun run, MediaWikiSite site)
    {
        var scores = new Dictionary<string, (WikiPage Page, double Score)>(StringComparer.Ordinal);
        var contentNamespaces = string.Join('|', site.ContentNamespaces.Order());
        var (main, _) = await PagesAsync(run, site, [("generator", "links"), ("titles", site.MainPage), ("gplnamespace", contentNamespaces),
            ("gpllimit", "max"), .. Details]).ConfigureAwait(false);
        foreach (var page in main.Where(p => Usable(site, p))) scores[Key(page.Title)] = (page, 2);

        var mostLinked = await MostLinkedAsync(run, site).ConfigureAwait(false);
        var unknown = mostLinked.Where(t => !scores.ContainsKey(Key(t))).Take(50).ToList();
        var (pages, redirects) = unknown.Count == 0 ? ([], new Dictionary<string, string>())
            : await PagesAsync(run, site, [("titles", string.Join('|', unknown)), .. Details]).ConfigureAwait(false);
        var known = pages.Where(p => Usable(site, p)).ToDictionary(p => Key(p.Title), StringComparer.Ordinal);
        for (var rank = 0; rank < mostLinked.Count; rank++)
        {
            var key = Key(redirects.TryGetValue(mostLinked[rank], out var target) ? target : mostLinked[rank]);
            var bonus = 2.0 * (1 - (double)rank / mostLinked.Count);
            if (scores.TryGetValue(key, out var scored)) scores[key] = (scored.Page, scored.Score + bonus);
            else if (known.TryGetValue(key, out var page)) scores[key] = (page, bonus);
        }
        var left = scores.Values.Select(s => (s.Page, Score: s.Score + Math.Min(1, s.Page.Length / 40_000.0)))
            .OrderByDescending(s => s.Score).ToList();
        var picked = new List<string>();
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        while (left.Count > 0)
        {
            var best = 0;
            var bestScore = double.MinValue;
            for (var i = 0; i < left.Count; i++)
            {
                var score = left[i].Score - SameCategory * left[i].Page.Categories.Select(c => categories.GetValueOrDefault(c)).DefaultIfEmpty(0).Max();
                if (score > bestScore) (best, bestScore) = (i, score);
            }
            var page = left[best].Page;
            left.RemoveAt(best);
            picked.Add(page.Title);
            foreach (var category in page.Categories) categories[category] = categories.GetValueOrDefault(category) + 1;
        }
        return picked;
    }

    /// <summary>How much a candidate's score drops for each page already picked from its most picked category.</summary>
    private const double SameCategory = 1.0;

    // What is asked about each candidate page: its size, whether it's a disambiguation page and its visible categories.
    private static readonly (string, string)[] Details = [("prop", "info|pageprops|categories"), ("ppprop", "disambiguation"),
        ("clshow", "!hidden"), ("cllimit", "max"), ("redirects", "1")];

    private static async Task<IReadOnlyList<string>> MostLinkedAsync(GuideRun run, MediaWikiSite site)
    {
        try
        {
            var document = await run.GetAsync(Query(site.Api, ("action", "query"), ("list", "querypage"), ("qppage", "Mostlinked"),
                ("qplimit", "200")), 400_000).ConfigureAwait(false);
            if (document is null) return [];
            using var json = JsonDocument.Parse(document.Body);
            if (!json.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("querypage", out var querypage) ||
                !querypage.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];
            return results.EnumerateArray()
                .Where(r => r.TryGetProperty("ns", out var ns) && ns.TryGetInt32(out var id) && site.ContentNamespaces.Contains(id))
                .Select(r => String(r, "title")).OfType<string>().ToList();
        }
        catch (WebResearchException) { return []; }
        catch (Exception error) when (Malformed(error)) { return []; }
    }

    private sealed record WikiPage(string Title, long Length, bool Missing, bool Disambiguation, int Namespace, IReadOnlyList<string> Categories);

    private static async Task<(IReadOnlyList<WikiPage> Pages, Dictionary<string, string> Redirects)> PagesAsync(GuideRun run, MediaWikiSite site,
        (string, string)[] parameters)
    {
        var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var document = await run.GetAsync(Query(site.Api, [("action", "query"), .. parameters]), 600_000).ConfigureAwait(false);
            if (document is null) return ([], redirects);
            using var json = JsonDocument.Parse(document.Body);
            if (!json.RootElement.TryGetProperty("query", out var query)) return ([], redirects);
            foreach (var list in new[] { "normalized", "redirects" })
                if (query.TryGetProperty(list, out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var entry in entries.EnumerateArray())
                        if (String(entry, "from") is { } from && String(entry, "to") is { } to)
                        {
                            // A title normalized and then redirected maps to where it ends up.
                            foreach (var earlier in redirects.Where(r => r.Value == from).Select(r => r.Key).ToList()) redirects[earlier] = to;
                            redirects[from] = to;
                        }
            if (!query.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) return ([], redirects);
            return (pages.EnumerateArray().Select(p => new WikiPage(String(p, "title") ?? "",
                p.TryGetProperty("length", out var length) && length.TryGetInt64(out var size) ? size : 0,
                p.TryGetProperty("missing", out var missing) && missing.ValueKind != JsonValueKind.False || p.TryGetProperty("invalid", out _),
                p.TryGetProperty("pageprops", out var props) && props.ValueKind == JsonValueKind.Object && props.TryGetProperty("disambiguation", out _),
                p.TryGetProperty("ns", out var ns) && ns.TryGetInt32(out var id) ? id : 0,
                p.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                    ? categories.EnumerateArray().Select(c => String(c, "title")).OfType<string>().ToArray() : []))
                .Where(p => p.Title.Length > 0).ToList(), redirects);
        }
        catch (WebResearchException) { return ([], redirects); }
        catch (Exception error) when (Malformed(error)) { return ([], redirects); }
    }

    private static bool Usable(MediaWikiSite site, WikiPage page) =>
        !page.Missing && !page.Disambiguation && site.ContentNamespaces.Contains(page.Namespace) && page.Length is >= SmallestPage and <= LargestPage &&
        Key(page.Title) != Key(site.MainPage) && !page.Title.Contains('/', StringComparison.Ordinal) && site.IsContent(page.Title) &&
        !VersionLike(page.Title);

    private static bool VersionLike(string title)
    {
        try { return VersionTitle.IsMatch(title); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    // A parsed article: its page (title, address, outline text) and the article titles it links to.
    private static (GuidePage Page, IReadOnlyList<string> Links)? Read(MediaWikiSite site, WebDocument document)
    {
        if (document.Cut) return null;
        try
        {
            using var json = JsonDocument.Parse(document.Body);
            if (!json.RootElement.TryGetProperty("parse", out var parse) || String(parse, "title") is not { Length: > 0 } title) return null;
            var html = parse.TryGetProperty("text", out var text) ? text.ValueKind == JsonValueKind.String ? text.GetString() :
                text.ValueKind == JsonValueKind.Object && text.TryGetProperty("*", out var star) ? star.GetString() : null : null;
            if (html is null) return null;
            var root = HtmlParser.Parse(html);
            var url = site.PageUrl(title);
            var links = HtmlOutline.Links(root, root, url).Where(l => !l.Missing).Select(l => site.TitleOf(l.Url))
                .OfType<string>().Where(t => t.Length > 0 && site.IsContent(t) && !t.Contains('/', StringComparison.Ordinal)).Distinct().ToList();
            return (new GuidePage(url.AbsoluteUri, title, HtmlOutline.Text(root, title), document.Bytes), links);
        }
        catch (Exception error) when (Malformed(error)) { return null; }
    }

    internal static Uri Query(Uri api, params (string Name, string Value)[] parameters) =>
        new(api.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', parameters.Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value))) +
            "&format=json&formatversion=2");

    // Titles compare with underscores as spaces and the first letter in upper case, as MediaWiki stores them.
    internal static string Key(string title)
    {
        var key = string.Join(' ', title.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];
    }

    // An answer that isn't what the API gives (JSON of another shape, a bad address in it): that call found nothing.
    private static bool Malformed(Exception error) => error is JsonException or InvalidOperationException or UriFormatException or FormatException;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

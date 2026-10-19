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
/// pages the pages read link to most, then the other content pages (<c>generator=allpages</c>), each batch chosen by its titles
/// before a page of it is downloaded (<see cref="GuideRun.ChooseAsync"/>), until the run's caps. Each page is read as clean
/// article HTML (<c>action=parse</c>, redirects followed, edit links and the table of contents off) and turned into outline text.</summary>
internal static class MediaWikiReader
{
    /// <summary>Articles shorter than this (in wiki text) are stubs; longer than <see cref="LargestPage"/> they are mostly one
    /// huge table, and their HTML often passes the 2 MB a document cap.</summary>
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
    /// returns how many pages it added; then leaves what is left to read more in <see cref="GuideRun.More"/>. With
    /// <paramref name="more"/> it reads more instead: titles the pages read before link to, and the links of those, as the run's
    /// chooser chooses them (the main page's and the most linked pages when it knows no links), then the wiki's other pages,
    /// never one in <paramref name="have"/> (the guide's pages) or read or left out before; and leaves what is left again.</summary>
    public static async Task<int> ReadAsync(GuideRun run, MediaWikiSite site, IReadOnlyList<string> seeds, GuideMore? more = null,
        IReadOnlyCollection<string>? have = null)
    {
        var before = run.Pages.Count;
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var queue = new List<string>();
        var linked = new Dictionary<string, (string Title, int Count)>(StringComparer.Ordinal);
        // Titles asked about or left out by the choice: never asked about again.
        var asked = new HashSet<string>(StringComparer.Ordinal);
        // On a series wiki, the main page's pages that don't name this game: read after the links of the game's own pages.
        var later = new List<string>();
        if (more is null)
        {
            foreach (var seed in seeds)
                if (queued.Add(Key(seed))) queue.Add(seed);
            run.Report("Reading the wiki's page list");
            var candidates = (await CandidatesAsync(run, site).ConfigureAwait(false)).Where(t => !queued.Contains(Key(t))).ToList();
            asked.UnionWith(candidates.Select(Key));
            var chosen = await run.ChooseAsync(candidates).ConfigureAwait(false);
            if (run.Name is { } app && !WikiDiscovery.IsAbout(app, site.Name))
            {
                // A wiki about a whole series ("Borderlands Wiki" for Borderlands 4): this game's pages first (titles that name it,
                // then the wiki's own search for its name), then their links; the main page's other pages only after those.
                foreach (var title in chosen.Where(t => run.About(t) > 0))
                    if (queued.Add(Key(title))) queue.Add(title);
                later.AddRange(chosen.Where(t => run.About(t) <= 0));
                var hits = (await SearchAsync(run, site, app).ConfigureAwait(false)).Where(t => !queued.Contains(Key(t))).ToList();
                var found = await UsableAsync(run, site, [.. hits.Select((t, i) => KeyValuePair.Create(Key(t), (t, hits.Count - i)))], queued)
                    .ConfigureAwait(false);
                asked.UnionWith(found.Select(Key));
                foreach (var title in await run.ChooseAsync(found).ConfigureAwait(false))
                    if (queued.Add(Key(title))) queue.Add(title);
                later.RemoveAll(t => queued.Contains(Key(t)));
            }
            else
                foreach (var title in chosen)
                    if (queued.Add(Key(title))) queue.Add(title);
        }
        else
        {
            asked.UnionWith(more.Known);
            foreach (var url in have ?? [])
                if (Uri.TryCreate(url, UriKind.Absolute, out var page) && site.TitleOf(page) is { Length: > 0 } title) asked.Add(Key(title));
            run.Know(have ?? []);
            foreach (var (title, count) in more.Related)
                if (!asked.Contains(Key(title))) linked[Key(title)] = (title, count);
            // Nothing left of the links (a guide kept before Martlet saved them): the main page's and the most linked pages first.
            if (linked.Count == 0)
            {
                run.Report("Reading the wiki's page list");
                var candidates = (await CandidatesAsync(run, site).ConfigureAwait(false)).Where(t => !asked.Contains(Key(t))).ToList();
                asked.UnionWith(candidates.Select(Key));
                foreach (var title in await run.ChooseAsync(candidates).ConfigureAwait(false))
                    if (queued.Add(Key(title))) queue.Add(title);
            }
        }
        var next = 0;
        var blocked = 0;
        var everyPage = new AllPages(site);
        while (!run.Full)
        {
            if (next >= queue.Count)
            {
                if (run.ChoiceEnded) break;
                // Out of pages: the ones the pages read so far link to most (those whose title names this game first), then every
                // other content page of the wiki, each batch chosen by its titles before a page of it is read.
                var wanted = linked.Where(l => !queued.Contains(l.Key) && !asked.Contains(l.Key))
                    .OrderByDescending(l => run.About(l.Value.Title) > 0).ThenByDescending(l => l.Value.Count)
                    .Take(run.Choosing ? GuidePagePicker.MaximumTitles : TitlesARequest).ToList();
                if (wanted.Count == 0)
                {
                    if (later.Count > 0)
                    {
                        foreach (var title in later)
                            if (queued.Add(Key(title))) queue.Add(title);
                        later.Clear();
                        continue;
                    }
                    if (everyPage is not { Done: false }) break;
                    var listed = (await everyPage.NextAsync(run).ConfigureAwait(false)).Where(t => !queued.Contains(Key(t)) && !asked.Contains(Key(t))).ToList();
                    asked.UnionWith(listed.Select(Key));
                    foreach (var title in await run.ChooseAsync(listed).ConfigureAwait(false))
                        if (queued.Add(Key(title))) queue.Add(title);
                    continue;
                }
                asked.UnionWith(wanted.Select(m => m.Key));
                var usable = await UsableAsync(run, site, wanted, queued).ConfigureAwait(false);
                asked.UnionWith(usable.Select(Key));
                foreach (var title in await run.ChooseAsync(usable).ConfigureAwait(false))
                    if (queued.Add(Key(title))) queue.Add(title);
                continue;
            }
            var page = queue[next++];
            run.Reading();
            WebDocument? document;
            try
            {
                document = await run.GetAsync(Query(site.Api, ("action", "parse"), ("page", page), ("prop", "text"), ("redirects", "1"),
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
        if (more is not null || run.Pages.Count > before && run.More is null)
        {
            // What is left to read more: the chosen pages not read yet, then the linked titles not asked about, most linked first.
            var left = queue.Skip(next).Concat(later.Where(t => !queued.Contains(Key(t))))
                .Select(t => (Title: t, Count: Math.Max(1, linked.GetValueOrDefault(Key(t)).Count)))
                .Concat(linked.Where(l => !queued.Contains(l.Key) && !asked.Contains(l.Key)).OrderByDescending(l => l.Value.Count)
                    .Select(l => (l.Value.Title, l.Value.Count)))
                .ToList();
            var known = new HashSet<string>(asked, StringComparer.Ordinal);
            known.UnionWith(queued);
            known.ExceptWith(left.Select(l => Key(l.Title)));
            run.More = new GuideMore(site, left, known);
        }
        return run.Pages.Count - before;
    }

    /// <summary>The most titles one page-info request asks about (MediaWiki's limit).</summary>
    private const int TitlesARequest = 50;

    // The wiki's own search for the app's name as a phrase (list=search), in its order: the pages about the app on a series wiki.
    private static async Task<IReadOnlyList<string>> SearchAsync(GuideRun run, MediaWikiSite site, string app)
    {
        try
        {
            var document = await run.GetAsync(Query(site.Api, ("action", "query"), ("list", "search"), ("srsearch", "\"" + app.Replace("\"", "", StringComparison.Ordinal) + "\""),
                ("srnamespace", string.Join('|', site.ContentNamespaces.Order())), ("srlimit", TitlesARequest.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("srprop", "")), 400_000).ConfigureAwait(false);
            if (document is null) return [];
            using var json = JsonDocument.Parse(document.Body);
            if (!json.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("search", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return [];
            return [.. results.EnumerateArray().Select(r => String(r, "title")).OfType<string>().Where(t => t.Length > 0 && site.IsContent(t)).Distinct()];
        }
        catch (WebResearchException) { return []; }
        catch (Exception error) when (Malformed(error)) { return []; }
    }

    // The usable pages among linked titles (redirects followed), the most linked first, the longer first among equals.
    private static async Task<List<string>> UsableAsync(GuideRun run, MediaWikiSite site,
        IReadOnlyList<KeyValuePair<string, (string Title, int Count)>> wanted, HashSet<string> queued)
    {
        var counts = wanted.ToDictionary(m => m.Key, m => m.Value.Count, StringComparer.Ordinal);
        var found = new List<WikiPage>();
        foreach (var part in wanted.Chunk(TitlesARequest))
        {
            var (pages, redirects) = await PagesAsync(run, site, [("titles", string.Join('|', part.Select(m => m.Value.Title))),
                ("prop", "info|pageprops"), ("ppprop", "disambiguation"), ("redirects", "1")]).ConfigureAwait(false);
            foreach (var (from, to) in redirects)
                if (Key(from) != Key(to) && counts.TryGetValue(Key(from), out var count)) counts[Key(to)] = counts.GetValueOrDefault(Key(to)) + count;
            found.AddRange(pages);
        }
        return found.Where(p => Usable(site, p) && !queued.Contains(Key(p.Title))).DistinctBy(p => Key(p.Title))
            .OrderByDescending(p => counts.GetValueOrDefault(Key(p.Title))).ThenByDescending(p => p.Length).Select(p => p.Title).ToList();
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

    // Every content page of the wiki, 500 at a time in title order (each content namespace in turn), as usable titles.
    private sealed class AllPages(MediaWikiSite site)
    {
        private readonly Queue<int> namespaces = new(site.ContentNamespaces.Order());
        private IReadOnlyDictionary<string, string> from = new Dictionary<string, string>();

        public bool Done => namespaces.Count == 0;

        public async Task<IReadOnlyList<string>> NextAsync(GuideRun run)
        {
            var ns = namespaces.Peek().ToString(System.Globalization.CultureInfo.InvariantCulture);
            (string, string)[] parameters = [("generator", "allpages"), ("gapnamespace", ns), ("gapfilterredir", "nonredirects"),
                ("gaplimit", "max"), ("gapminsize", SmallestPage.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("gapmaxsize", LargestPage.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("prop", "info|pageprops"),
                ("ppprop", "disambiguation"), .. from.Select(c => (c.Key, c.Value))];
            var (pages, _, next) = await ListAsync(run, site, parameters).ConfigureAwait(false);
            // A list that ends, or can't be read, ends that namespace.
            if (next is null)
            {
                namespaces.Dequeue();
                from = new Dictionary<string, string>();
            }
            else from = next;
            return pages.Where(p => Usable(site, p)).Select(p => p.Title).ToList();
        }
    }

    private static async Task<(IReadOnlyList<WikiPage> Pages, Dictionary<string, string> Redirects)> PagesAsync(GuideRun run, MediaWikiSite site,
        (string, string)[] parameters)
    {
        var (pages, redirects, _) = await ListAsync(run, site, parameters).ConfigureAwait(false);
        return (pages, redirects);
    }

    // A query's pages, its redirects and, when the list goes on, the parameters that continue it.
    private static async Task<(IReadOnlyList<WikiPage> Pages, Dictionary<string, string> Redirects, IReadOnlyDictionary<string, string>? Continue)>
        ListAsync(GuideRun run, MediaWikiSite site, (string, string)[] parameters)
    {
        var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var document = await run.GetAsync(Query(site.Api, [("action", "query"), .. parameters]), 600_000).ConfigureAwait(false);
            if (document is null) return ([], redirects, null);
            using var json = JsonDocument.Parse(document.Body);
            Dictionary<string, string>? more = null;
            if (json.RootElement.TryGetProperty("continue", out var continues) && continues.ValueKind == JsonValueKind.Object)
                more = continues.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            if (!json.RootElement.TryGetProperty("query", out var query)) return ([], redirects, null);
            foreach (var list in new[] { "normalized", "redirects" })
                if (query.TryGetProperty(list, out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var entry in entries.EnumerateArray())
                        if (String(entry, "from") is { } from && String(entry, "to") is { } to)
                        {
                            // A title normalized and then redirected maps to where it ends up.
                            foreach (var earlier in redirects.Where(r => r.Value == from).Select(r => r.Key).ToList()) redirects[earlier] = to;
                            redirects[from] = to;
                        }
            if (!query.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) return ([], redirects, more is { Count: > 0 } ? more : null);
            return (pages.EnumerateArray().Select(p => new WikiPage(String(p, "title") ?? "",
                p.TryGetProperty("length", out var length) && length.TryGetInt64(out var size) ? size : 0,
                p.TryGetProperty("missing", out var missing) && missing.ValueKind != JsonValueKind.False || p.TryGetProperty("invalid", out _),
                p.TryGetProperty("pageprops", out var props) && props.ValueKind == JsonValueKind.Object && props.TryGetProperty("disambiguation", out _),
                p.TryGetProperty("ns", out var ns) && ns.TryGetInt32(out var id) ? id : 0,
                p.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                    ? categories.EnumerateArray().Select(c => String(c, "title")).OfType<string>().ToArray() : []))
                .Where(p => p.Title.Length > 0).ToList(), redirects, more is { Count: > 0 } ? more : null);
        }
        catch (WebResearchException) { return ([], redirects, null); }
        catch (Exception error) when (Malformed(error)) { return ([], redirects, null); }
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

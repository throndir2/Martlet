namespace Martlet.Conversation.Guides;

/// <summary>Reads up on an app on the web with Martlet's own web client (<see cref="WebAccess"/>, which only connects to public
/// internet addresses). It reads the start pages the owner gave; else it finds the app's wiki: the usual wiki farm sites named
/// after it (<c>&lt;name&gt;.fandom.com</c>, <c>&lt;name&gt;.wiki.gg</c>), then a web search for its wiki (and its help or guide
/// pages) that prefers wiki and help sites. MediaWiki sites are read through their api.php (<see cref="MediaWikiReader"/>), other
/// sites breadth first through their own links (<see cref="SiteCrawler"/>), always within <see cref="GuideBuildLimits"/>, obeying
/// each site's robots.txt and one request at a time with a pause between two (<see cref="GuideRun"/>). Without
/// <see cref="IWebDocuments"/> (a search and page reader only) it reads the start pages or the best search results alone.</summary>
public sealed class WebGuideBuilder(IWebSearch search, IWebFetch fetch) : IGuideBuilder
{
    /// <summary>How many more sites than <see cref="GuideBuildLimits.Sites"/> may be tried (wikis that turn out empty, results
    /// that can't be read).</summary>
    public const int ExtraTries = 4;

    /// <summary>The pause between two requests to one site (checks pass one that doesn't sleep).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    public async Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = request.Limits ?? new();
        var sites = (request.Sites ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (sites.Count == 0 && WikiDiscovery.Words(request.Name).Count == 0) return new([], [], 0, 0, "it has no name to look for");
        if ((fetch as IWebDocuments ?? search as IWebDocuments) is not { } documents)
            return await ReadPagesAsync(request.Name, sites, limits, progress, cancellationToken).ConfigureAwait(false);

        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        time.CancelAfter(limits.Time);
        var run = new GuideRun(documents, limits, progress, Wait, time.Token);
        var job = new Job(run, request.Name, search);
        var late = false;
        try
        {
            await job.RunAsync(sites).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            late = true;
        }
        var problem = run.Pages.Count > 0 ? null
            : late || run.TimeUp ? "reading up took too long"
            : !job.Found && job.SearchProblem is not null ? "the web search didn't work (" + job.SearchProblem + ")"
            : !job.Found ? "no wiki or help pages about it were found"
            : "no page about it could be read";
        return new(run.Pages.ToArray(), run.Hosts.ToArray(), run.Failed, run.Bytes, problem);
    }

    // Without IWebDocuments: the start pages, or the best search results for its wiki, read one page each.
    private async Task<GuideBuildOutcome> ReadPagesAsync(string name, List<string> starts, GuideBuildLimits limits, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (starts.Count == 0)
        {
            progress?.Report("Looking for its wiki");
            try
            {
                var found = await search.SearchAsync(name + " wiki", cancellationToken).ConfigureAwait(false);
                starts.AddRange(WikiDiscovery.Rank(found, name).Select(url => url.AbsoluteUri));
            }
            catch (WebResearchException error)
            {
                return new([], [], 0, 0, "the web search didn't work (" + error.Message + ")");
            }
            if (starts.Count == 0) return new([], [], 0, 0, "no wiki or help pages about it were found");
        }
        var pages = new List<GuidePage>();
        var sites = new List<string>();
        var failed = 0;
        long bytes = 0;
        foreach (var start in starts.Take(limits.Sites))
        {
            if (pages.Count >= limits.Pages || bytes >= limits.Bytes) break;
            if (!Uri.TryCreate(start, UriKind.Absolute, out var url) || !WebAccess.IsWeb(url)) { failed++; continue; }
            progress?.Report($"Reading pages ({pages.Count + 1} of at most {limits.Pages})");
            try
            {
                var page = await fetch.FetchAsync(url, cancellationToken).ConfigureAwait(false);
                bytes += page.Bytes;
                pages.Add(new(page.Url, page.Title, page.Text, page.Bytes));
                sites.Add(new Uri(page.Url).Host);
            }
            catch (WebResearchException) { failed++; }
        }
        return new(pages, sites.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), failed, bytes,
            pages.Count == 0 ? "no page about it could be read" : null);
    }

    private sealed class Job(GuideRun run, string name, IWebSearch search)
    {
        private readonly HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);
        private int tries;

        /// <summary>Whether a site to read was found (the owner's, a wiki farm's or a search result).</summary>
        public bool Found { get; private set; }
        public string? SearchProblem { get; private set; }

        private bool Done => run.Full || run.Hosts.Count >= run.Limits.Sites || tries >= run.Limits.Sites + ExtraTries;

        public async Task RunAsync(IReadOnlyList<string> sites)
        {
            foreach (var site in sites)
            {
                if (Done) return;
                if (Uri.TryCreate(site, UriKind.Absolute, out var url) && WebAccess.IsWeb(url)) await ReadSiteAsync(url).ConfigureAwait(false);
                else run.Failed++;
            }
            // The owner's sites are the guide's sites; Martlet looks for others only when none of them could be read.
            if (sites.Count > 0 && run.Pages.Count > 0) return;
            run.Report("Looking for its wiki");
            await ProbeAsync().ConfigureAwait(false);
            await SearchAsync().ConfigureAwait(false);
        }

        // The wiki farms' sites named after the app, the biggest first; a site counts when its name is about the app.
        private async Task ProbeAsync()
        {
            var found = new List<MediaWikiSite>();
            foreach (var farm in WikiDiscovery.Farms)
                foreach (var slug in WikiDiscovery.Slugs(name))
                {
                    if (run.Full) return;
                    var api = new Uri($"https://{slug}.{farm}/api.php");
                    if (!tried.Add(api.Host)) continue;
                    if (await MediaWikiReader.ProbeAsync(run, api).ConfigureAwait(false) is not { } site) continue;
                    tried.Add(site.Api.Host);
                    if (site.Articles < MediaWikiReader.MinimumArticles || !WikiDiscovery.IsAbout(name, site.Name, site.MainPage)) continue;
                    found.Add(site);
                    break;
                }
            foreach (var site in found.OrderByDescending(s => s.Articles))
            {
                if (Done) return;
                Found = true;
                tries++;
                await MediaWikiReader.ReadAsync(run, site, []).ConfigureAwait(false);
            }
        }

        // Web searches for its wiki, then (when nothing was read yet) its help and guide pages, with the site pause between two
        // searches; a search that is refused ends the searching.
        private async Task SearchAsync()
        {
            var queries = WikiDiscovery.Queries(name);
            for (var i = 0; i < queries.Count; i++)
            {
                if (Done || i > 0 && (run.Pages.Count > 0 || !await run.PauseAsync(run.Limits.Delay).ConfigureAwait(false))) return;
                IReadOnlyList<WebSearchResult> results;
                try { results = await search.SearchAsync(queries[i], run.Token).ConfigureAwait(false); }
                catch (WebResearchException error)
                {
                    SearchProblem ??= error.Message;
                    return;
                }
                foreach (var url in WikiDiscovery.Rank(results, name))
                {
                    if (Done) return;
                    if (!tried.Contains(url.Host)) await ReadSiteAsync(url).ConfigureAwait(false);
                }
            }
        }

        private async Task ReadSiteAsync(Uri url)
        {
            tried.Add(url.Host);
            tries++;
            Found = true;
            if (WikiDiscovery.IsFarm(url.Host) && await MediaWikiReader.ProbeAsync(run, WikiDiscovery.FarmApi(url)).ConfigureAwait(false) is { } farm &&
                (await MediaWikiReader.ReadAsync(run, farm, Seeds(farm, url)).ConfigureAwait(false) > 0 || run.Full))
                return;
            run.Reading();
            WebDocument? document;
            try { document = await run.GetAsync(url).ConfigureAwait(false); }
            catch (WebResearchException)
            {
                run.Failed++;
                return;
            }
            if (document is null) return;
            var final = new Uri(document.Url);
            if (document.MediaType is "text/html" or "application/xhtml+xml" && !WikiDiscovery.IsFarm(final.Host))
            {
                foreach (var api in HtmlOutline.MediaWikiApis(document.Body, HtmlParser.Parse(document.Body), final).Take(2))
                {
                    if (run.Full) return;
                    if (await MediaWikiReader.ProbeAsync(run, api).ConfigureAwait(false) is not { } site) continue;
                    tried.Add(site.Api.Host);
                    if (await MediaWikiReader.ReadAsync(run, site, Seeds(site, final)).ConfigureAwait(false) > 0 || run.Full) return;
                    break;
                }
            }
            tried.Add(final.Host);
            await SiteCrawler.ReadAsync(run, final, document).ConfigureAwait(false);
        }

        // The page the owner or a search pointed at is read first, unless it's the main page.
        private static IReadOnlyList<string> Seeds(MediaWikiSite site, Uri page) =>
            site.TitleOf(page) is { Length: > 0 } title && MediaWikiReader.Key(title) != MediaWikiReader.Key(site.MainPage) && site.IsContent(title)
                ? [title] : [];
    }
}

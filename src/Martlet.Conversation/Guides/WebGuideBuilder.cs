namespace Martlet.Conversation.Guides;

/// <summary>Reads up on an app on the web with Martlet's own web client (<see cref="WebAccess"/>, which only connects to public
/// internet addresses). First version: it reads the start pages the owner gave, or else the first result of a web search for the
/// app's wiki.</summary>
public sealed class WebGuideBuilder(IWebSearch search, IWebFetch fetch) : IGuideBuilder
{
    public async Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = request.Limits ?? new();
        var starts = request.Sites.ToList();
        if (starts.Count == 0)
        {
            progress?.Report("Looking for its wiki");
            try
            {
                var found = await search.SearchAsync(request.Name + " wiki", cancellationToken).ConfigureAwait(false);
                if (found.FirstOrDefault() is { } first) starts.Add(first.Url);
            }
            catch (WebResearchException error)
            {
                return new([], [], 0, 0, "the web search didn't work (" + error.Message + ")");
            }
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
}

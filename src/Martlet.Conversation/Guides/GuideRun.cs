using System.Diagnostics;

namespace Martlet.Conversation.Guides;

/// <summary>One reading-up job's state and manners: the pages read so far and the caps (<see cref="GuideBuildLimits"/>), each
/// site's robots.txt read once and obeyed, and one request at a time to a site with at least the larger of
/// <see cref="GuideBuildLimits.Delay"/> and the site's Crawl-delay between two requests. Before a request it keeps the pages read
/// so far when that is due (<see cref="GuideBuildRequest.Keep"/>).</summary>
internal sealed class GuideRun(IWebDocuments web, GuideBuildLimits limits, IProgress<string>? progress,
    Func<TimeSpan, CancellationToken, Task> wait, CancellationToken token,
    Func<GuideBuildOutcome, CancellationToken, Task<bool>>? keep = null, TimeSpan keepEvery = default)
{
    /// <summary>Requests a job may make before its first pages, to find its wiki (without a page cap).</summary>
    public const int FindingRequests = 100;

    private readonly Dictionary<string, Host> hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RobotsRules> rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> read = new(StringComparer.Ordinal);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastKept;
    private int keptPages;

    public GuideBuildLimits Limits { get; } = limits;
    public CancellationToken Token { get; } = token;
    public List<GuidePage> Pages { get; } = [];
    /// <summary>The sites pages were read from, in the order they were first read.</summary>
    public List<string> Hosts { get; } = [];
    /// <summary>Pages that couldn't be read.</summary>
    public int Failed { get; set; }
    public long Bytes { get; private set; }
    public int Requests { get; private set; }
    public bool TimeUp { get; private set; }
    /// <summary>The guide can hold no more pages (<see cref="GuideBuildRequest.Keep"/> said so).</summary>
    public bool GuideFull { get; private set; }

    /// <summary>The most requests one job makes: 3 a page (every page may need a few requests to find it), plus 30 with a page
    /// cap, else plus <see cref="FindingRequests"/> for the pages read so far, so a site whose pages can't be read ends the job.</summary>
    public int MaxRequests => Limits.Pages is { } cap ? cap * 3 + 30 : Pages.Count * 3 + FindingRequests;
    public bool Full => Limits.Pages is { } cap && Pages.Count >= cap || Limits.Bytes is { } bytes && Bytes >= bytes || TimeUp || GuideFull ||
        Requests >= MaxRequests;

    public void Reading() => progress?.Report(ReadingWords(Pages.Count, Limits.Pages));

    /// <summary>"Reading page 4", or "Reading page 4 of at most 12" with a page cap.</summary>
    public static string ReadingWords(int read, int? cap) =>
        cap is { } most ? $"Reading page {Math.Min(read + 1, most)} of at most {most}" : $"Reading page {read + 1}";

    public void Report(string words) => progress?.Report(words);

    /// <summary>Whether a page at <paramref name="url"/> was read already.</summary>
    public bool Has(string url) => read.Contains(url);

    /// <summary>Keeps a page read (once per address) and returns whether it was kept.</summary>
    public bool Add(GuidePage page)
    {
        if (Limits.Pages is { } cap && Pages.Count >= cap || !read.Add(page.Url)) return false;
        Pages.Add(page);
        var host = new Uri(page.Url).Host;
        if (!Hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) Hosts.Add(host);
        return true;
    }

    /// <summary>The site's robots.txt rules, read the first time a site is asked about.</summary>
    public async Task<RobotsRules> RulesAsync(Uri url)
    {
        var host = HostOf(url);
        var key = url.GetLeftPart(UriPartial.Authority);
        if (rules.TryGetValue(key, out var known)) return known;
        RobotsRules found;
        try
        {
            var robots = await SendAsync(new Uri(key + "/robots.txt"), host, RobotsRules.MaxBytes).ConfigureAwait(false);
            if (robots is null) return RobotsRules.DisallowAll;
            found = RobotsRules.Parse(robots.Body);
        }
        // RFC 9309: a robots.txt the site refuses or doesn't have (4xx) means no rules; one it can't serve means stay away.
        catch (WebResearchException error)
        {
            found = error.Status is >= 400 and < 500 ? RobotsRules.AllowAll : RobotsRules.DisallowAll;
        }
        rules[key] = found;
        if (found.CrawlDelay is { } delay && delay > host.Delay) host.Delay = delay;
        return found;
    }

    public async Task<bool> AllowsAsync(Uri url) => (await RulesAsync(url).ConfigureAwait(false)).Allows(url);

    /// <summary>The document at <paramref name="url"/>, or null when the site's robots.txt doesn't allow it (where it was asked
    /// for, or where a redirect took it), the site asked Martlet to stop (429 or 503), or the job is out of pages, bytes,
    /// requests, time or room in the guide. Throws <see cref="WebResearchException"/> when it can't be read.</summary>
    public async Task<WebDocument?> GetAsync(Uri url, int maxBytes = WebAccess.MaxDocumentBytes)
    {
        await KeepIfDueAsync().ConfigureAwait(false);
        if (Full || !await AllowsAsync(url).ConfigureAwait(false) || Full || HostOf(url).Stopped) return null;
        var document = await SendAsync(url, HostOf(url), maxBytes).ConfigureAwait(false);
        if (document is null || !Uri.TryCreate(document.Url, UriKind.Absolute, out var final) || final == url) return document;
        // A redirect: the site it ended on gets its pause too, and its robots.txt decides whether the page may be kept.
        HostOf(final).Last = clock.Elapsed;
        return HostOf(final).Stopped || !await AllowsAsync(final).ConfigureAwait(false) ? null : document;
    }

    // The pages read since the last keep, once KeepEvery has passed since it (or since the start).
    private async Task KeepIfDueAsync()
    {
        if (keep is null || GuideFull || Pages.Count == keptPages || clock.Elapsed - lastKept < keepEvery) return;
        keptPages = Pages.Count;
        var more = await keep(new GuideBuildOutcome([.. Pages], [.. Hosts], Failed, Bytes, null), Token).ConfigureAwait(false);
        lastKept = clock.Elapsed;
        if (!more) GuideFull = true;
    }

    /// <summary>Waits <paramref name="pause"/> (between two web searches), unless that would pass the job's time.</summary>
    public async Task<bool> PauseAsync(TimeSpan pause)
    {
        if (Limits.Time is { } time && clock.Elapsed + pause >= time)
        {
            TimeUp = true;
            return false;
        }
        if (pause > TimeSpan.Zero) await wait(pause, Token).ConfigureAwait(false);
        return true;
    }

    private async Task<WebDocument?> SendAsync(Uri url, Host host, int maxBytes)
    {
        var left = Limits.Bytes is { } cap ? cap - Bytes : long.MaxValue;
        if (left <= 0 || Requests >= MaxRequests || host.Stopped) return null;
        if (host.Last is { } last)
        {
            var pause = last + host.Delay - clock.Elapsed;
            if (pause > TimeSpan.Zero)
            {
                // A wait that would pass the job's time ends the job now instead.
                if (Limits.Time is { } time && clock.Elapsed + pause >= time)
                {
                    TimeUp = true;
                    return null;
                }
                await wait(pause, Token).ConfigureAwait(false);
            }
        }
        Token.ThrowIfCancellationRequested();
        Requests++;
        try
        {
            var document = await web.ReadAsync(url, (int)Math.Min(maxBytes, left), Token).ConfigureAwait(false);
            Bytes += document.Bytes;
            return document;
        }
        // Too many requests, or the site is busy: Martlet asks it nothing more.
        catch (WebResearchException error) when (error.Status is 429 or 503)
        {
            host.Stopped = true;
            throw;
        }
        finally
        {
            host.Last = clock.Elapsed;
        }
    }

    // Pauses and stops are per host (http and https, any port, are one site to its server); robots.txt is per scheme and authority.
    private Host HostOf(Uri url)
    {
        if (!hosts.TryGetValue(url.Host, out var host)) hosts[url.Host] = host = new() { Delay = Limits.Delay };
        return host;
    }

    private sealed class Host
    {
        public TimeSpan Delay { get; set; }
        public TimeSpan? Last { get; set; }
        public bool Stopped { get; set; }
    }
}

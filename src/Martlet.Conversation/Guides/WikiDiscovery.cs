using System.Globalization;
using System.Text;

namespace Martlet.Conversation.Guides;

/// <summary>Finds an app's wiki or help pages: its name as a wiki farm's site name (<c>&lt;name&gt;.fandom.com</c>,
/// <c>&lt;name&gt;.wiki.gg</c>), then web search results ranked so wikis and official help sites come first and shops, video,
/// social, forum and encyclopedia sites are left out. A candidate counts only when its title or site names the app.</summary>
internal static class WikiDiscovery
{
    /// <summary>Wiki farms whose sites are MediaWiki with api.php at the root (or under a language path).</summary>
    public static readonly string[] Farms = ["fandom.com", "wiki.gg"];
    /// <summary>The lowest search score a result needs (a wiki or help sign besides naming the app).</summary>
    public const int MinimumScore = 2;

    private static readonly HashSet<string> StopWords = ["the", "a", "an", "of", "and", "or", "for", "to", "in", "on", "at", "by", "with"];
    private static readonly string[] RejectedSites =
    [
        "youtube.com", "youtu.be", "twitch.tv", "vimeo.com", "tiktok.com", "dailymotion.com", "reddit.com", "redd.it", "twitter.com",
        "x.com", "facebook.com", "instagram.com", "threads.net", "tumblr.com", "pinterest.com", "discord.com", "discord.gg", "quora.com",
        "linkedin.com", "stackexchange.com", "stackoverflow.com", "steamcommunity.com", "steampowered.com", "steamdb.info", "g2a.com",
        "gog.com", "epicgames.com", "humblebundle.com", "cdkeys.com", "eneba.com", "kinguin.net", "instant-gaming.com",
        "greenmangaming.com", "fanatical.com", "playstation.com", "xbox.com", "nintendo.com", "apps.apple.com", "play.google.com",
        "wikipedia.org", "wikimedia.org", "wiktionary.org", "wikidata.org", "wikiquote.org", "imdb.com", "metacritic.com",
        "opencritic.com", "duckduckgo.com", "bing.com"
    ];
    // The farms' own pages (their front pages and community hubs), not one of their wikis.
    private static readonly HashSet<string> RejectedHosts = ["fandom.com", "www.fandom.com", "wiki.gg", "www.wiki.gg", "www.google.com",
        "google.com"];
    private static readonly string[] RejectedLabels = ["amazon", "ebay", "aliexpress", "walmart", "bestbuy", "gamestop"];
    private static readonly string[] ForumHosts = ["forum", "forums.", "community.", "discussions.", "answers.", "boards."];
    private static readonly string[] RejectedPaths = ["/forum", "/threads/", "/thread/", "/boards/", "/discussions", "/r/", "/f/", "/t/",
        "/questions/", "/watch", "/video/", "/videos/", "/store/", "/shop/", "/buy", "/cart", "/product/"];
    private static readonly string[] HelpHosts = ["help.", "helpx.", "support.", "docs.", "learn.", "manual.", "documentation.", "kb.",
        "knowledge.", "guide.", "guides."];
    private static readonly string[] HelpPaths = ["/help", "/docs", "/manual", "/guide", "/user-guide", "/support", "/documentation",
        "/learn", "/tutorial", "/kb/"];
    private static readonly string[] HelpWords = ["wiki", "help", "guide", "manual", "documentation", "docs", "tutorial", "handbook"];

    /// <summary>The searches tried, in order: a wiki first, then help and guide pages (for apps).</summary>
    public static IReadOnlyList<string> Queries(string name) => [name + " wiki", name + " help", name + " guide"];

    /// <summary>The app's name as wiki farm site names: run together ("eldenring"), with hyphens ("elden-ring") and without a
    /// leading "the".</summary>
    public static IReadOnlyList<string> Slugs(string name)
    {
        var words = Words(name);
        var slugs = new List<string>();
        Add(string.Concat(words));
        if (words.Count > 1) Add(string.Join('-', words));
        if (words.Count > 1 && words[0] == "the") Add(string.Concat(words.Skip(1)));
        return slugs;

        void Add(string slug)
        {
            if (slug.Length is > 0 and <= 63 && !slugs.Contains(slug)) slugs.Add(slug);
        }
    }

    /// <summary>Whether the texts (a title, a site's name or address) are about the app: they hold its whole name, or more than
    /// half of its significant words.</summary>
    public static bool IsAbout(string name, params string?[] texts)
    {
        var haystack = string.Concat(Words(string.Join(' ', texts.Where(t => t is not null))));
        if (haystack.Length == 0) return false;
        var words = Words(name);
        var whole = string.Concat(words.SkipWhile(w => w == "the"));
        if (whole.Length > 0 && haystack.Contains(whole, StringComparison.Ordinal)) return true;
        var significant = words.Where(w => !StopWords.Contains(w) && (w.Length > 1 || char.IsAsciiDigit(w[0]))).Distinct().ToList();
        if (significant.Count == 0) return false;
        var found = significant.Count(w => haystack.Contains(w, StringComparison.Ordinal));
        return found * 2 > significant.Count;
    }

    /// <summary>Whether a host is a site on a wiki farm (not the farm's own pages).</summary>
    public static bool IsFarm(string host)
    {
        host = host.ToLowerInvariant();
        return Farms.Any(farm => host.EndsWith("." + farm, StringComparison.Ordinal)) && !host.StartsWith("www.", StringComparison.Ordinal) &&
            !host.StartsWith("community.", StringComparison.Ordinal);
    }

    /// <summary>A wiki farm page's api.php, keeping a language path ("/de/wiki/..." → "/de/api.php").</summary>
    public static Uri FarmApi(Uri page)
    {
        var segments = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var language = segments.Length >= 2 && segments[1] == "wiki" && IsLanguage(segments[0]) ? "/" + segments[0] : "";
        return new Uri($"{page.Scheme}://{page.Authority}{language}/api.php");

        static bool IsLanguage(string segment) => segment.Length is >= 2 and <= 8 && segment.All(c => char.IsAsciiLetterLower(c) || c == '-');
    }

    /// <summary>The usable search results, best first, one per site.</summary>
    public static IReadOnlyList<Uri> Rank(IEnumerable<WebSearchResult> results, string name)
    {
        var ranked = results.Select((result, index) => (Result: result, Index: index, Score: Score(result, name)))
            .Where(x => x.Score >= MinimumScore).OrderByDescending(x => x.Score).ThenBy(x => x.Index);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<Uri>();
        foreach (var (result, _, _) in ranked)
            if (Uri.TryCreate(result.Url, UriKind.Absolute, out var url) && hosts.Add(url.Host)) urls.Add(url);
        return urls;
    }

    /// <summary>How likely a search result is the app's wiki or help pages; below zero when it's left out (not about the app, or
    /// a shop, video, social, forum or encyclopedia site).</summary>
    public static int Score(WebSearchResult result, string name)
    {
        if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var url) || !WebAccess.IsWeb(url)) return -1;
        var host = url.Host.ToLowerInvariant();
        var path = url.AbsolutePath.ToLowerInvariant();
        if (Rejected(host, path) || !IsAbout(name, result.Title, host)) return -1;
        var score = 0;
        if (IsFarm(host) || Is(host, "fextralife.com")) score += 4;
        else if (host.Contains("wiki", StringComparison.Ordinal)) score += 3;
        if (path.StartsWith("/wiki/", StringComparison.Ordinal) || path.StartsWith("/w/", StringComparison.Ordinal) ||
            path.Contains("/wikis/", StringComparison.Ordinal))
            score += 2;
        if (HelpHosts.Any(h => host.StartsWith(h, StringComparison.Ordinal))) score += 3;
        if (HelpPaths.Any(p => path.Contains(p, StringComparison.Ordinal))) score += 1;
        var title = result.Title.ToLowerInvariant();
        if (HelpWords.Any(w => title.Contains(w, StringComparison.Ordinal))) score += 1;
        return score;
    }

    private static bool Rejected(string host, string path) =>
        RejectedHosts.Contains(host) || RejectedSites.Any(site => Is(host, site)) || host.Split('.').Any(label => RejectedLabels.Contains(label)) ||
        ForumHosts.Any(f => host.StartsWith(f, StringComparison.Ordinal)) || RejectedPaths.Any(p => path.Contains(p, StringComparison.Ordinal));

    private static bool Is(string host, string site) => host == site || host.EndsWith("." + site, StringComparison.Ordinal);

    /// <summary>A name's words: lower case, accents and apostrophes dropped, split at anything but a letter or digit.</summary>
    internal static IReadOnlyList<string> Words(string? text)
    {
        var plain = new StringBuilder();
        foreach (var c in (text ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’') continue;
            plain.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}

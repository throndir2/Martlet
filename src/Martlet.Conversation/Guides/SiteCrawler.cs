using System.Text.RegularExpressions;

namespace Martlet.Conversation.Guides;

/// <summary>Reads a site that isn't read through a wiki API: breadth first from the start page, through links on the same site
/// and under the start page's folder (widened when that folder has few links), each page once by its canonical address. Links
/// with a query, and edit, talk, user, file, special, media, login, search and shop pages, are left out.</summary>
internal static class SiteCrawler
{
    /// <summary>Readable text shorter than this isn't kept as a page.</summary>
    public const int MinimumText = 200;
    /// <summary>The most addresses waiting to be read.</summary>
    public const int MaxQueue = 2_000;
    /// <summary>A start folder with fewer links than this is widened to its parent.</summary>
    public const int FewLinks = 5;

    private static readonly Regex NamespaceLink = new(
        "^(special|file|image|media|user|user[_ ]talk|talk|[a-z_ ]+[_ ]talk|template|help|category|mediawiki|module|portal|property|widget|" +
        "forum|thread|message[_ ]wall|board|blog|gadget|gadget[_ ]definition|draft|translations|tag|timedtext):",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> SkippedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "talk", "user", "users", "login", "signin", "sign-in", "register", "signup", "sign-up", "logout", "search", "cart",
        "checkout", "account", "tag", "tags", "feed", "rss", "print", "share", "comments", "wp-admin", "wp-login.php", "cdn-cgi", "special",
        "media", "file", "files", "download", "downloads", "attachment", "attachments", "members", "profile", "profiles"
    };
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico", ".pdf", ".zip", ".rar", ".7z", ".exe", ".msi", ".dmg", ".mp4",
        ".webm", ".mp3", ".ogg", ".wav", ".css", ".js", ".json", ".xml", ".rss", ".txt", ".csv", ".woff", ".woff2", ".ttf", ".apk"
    };

    /// <summary>Reads the site from <paramref name="start"/> into the run (the start page's document when it was read already)
    /// and returns how many pages it added.</summary>
    public static async Task<int> ReadAsync(GuideRun run, Uri start, WebDocument? first)
    {
        var before = run.Pages.Count;
        var host = start.Host;
        var seen = new HashSet<string>(StringComparer.Ordinal) { Key(start) };
        var queue = new Queue<Uri>([start]);
        string? folder = null;
        while (queue.Count > 0 && !run.Full)
        {
            var url = queue.Dequeue();
            var document = first;
            first = null;
            if (document is null)
            {
                run.Reading();
                try { document = await run.GetAsync(url).ConfigureAwait(false); }
                catch (WebResearchException error)
                {
                    run.Failed++;
                    if (error.Status is 429 or 503) break;
                    continue;
                }
                if (document is null) continue;
            }
            if (document.MediaType is not ("text/html" or "application/xhtml+xml") || !Uri.TryCreate(document.Url, UriKind.Absolute, out var final) ||
                !final.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                continue;
            seen.Add(Key(final));
            var root = HtmlParser.Parse(document.Body);
            var canonical = HtmlOutline.Canonical(root, final) is { } c && c.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ? c : final;
            if (run.Has(canonical.AbsoluteUri)) continue;
            seen.Add(Key(canonical));
            var content = HtmlOutline.Content(root);
            var title = HtmlOutline.Title(root, content) ?? final.Host;
            var text = HtmlOutline.Text(content, title);
            if (text.Length >= MinimumText) run.Add(new GuidePage(canonical.AbsoluteUri, title, text, document.Bytes));
            var links = HtmlOutline.Links(root, content, final).Select(l => l.Url)
                .Where(u => u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && Readable(u)).ToList();
            folder ??= Folder(final, links);
            foreach (var link in links)
                if (queue.Count < MaxQueue && link.AbsolutePath.StartsWith(folder, StringComparison.Ordinal) && seen.Add(Key(link)))
                    queue.Enqueue(link);
        }
        return run.Pages.Count - before;
    }

    // The start page's folder ("/photoshop/" for "/photoshop/using/layers.html"), widened while it holds few of the page's links.
    internal static string Folder(Uri start, IReadOnlyList<Uri> links)
    {
        var path = start.AbsolutePath;
        var folder = path[..(path.LastIndexOf('/') + 1)];
        if (folder.Length == 0) folder = "/";
        while (folder.Length > 1 && links.Count(l => l.AbsolutePath.StartsWith(folder, StringComparison.Ordinal)) < FewLinks)
        {
            var parent = folder[..(folder.TrimEnd('/').LastIndexOf('/') + 1)];
            folder = parent.Length == 0 || parent == folder ? "/" : parent;
        }
        return folder;
    }

    /// <summary>Whether a link may be a readable content page: no query, no file, and no edit, talk, user, special, media, login,
    /// search or shop page.</summary>
    internal static bool Readable(Uri url)
    {
        if (url.Query.Length > 0) return false;
        string path;
        try { path = Uri.UnescapeDataString(url.AbsolutePath); }
        catch (UriFormatException) { return false; }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(SkippedSegments.Contains)) return false;
        if (segments.Length > 0)
        {
            var last = segments[^1];
            var dot = last.LastIndexOf('.');
            if (dot > 0 && SkippedExtensions.Contains(last[dot..])) return false;
            try
            {
                if (NamespaceLink.IsMatch(last)) return false;
            }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return true;
    }

    // Addresses compare without their fragment, with the host in lower case and without a trailing slash.
    private static string Key(Uri url)
    {
        var path = url.AbsolutePath;
        if (path.Length > 1) path = path.TrimEnd('/');
        return url.Scheme + "://" + url.Authority.ToLowerInvariant() + path + url.Query;
    }
}

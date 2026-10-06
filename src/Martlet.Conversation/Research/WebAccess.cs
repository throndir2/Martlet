using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Conversation;

/// <summary>One web search result: its title, link and the search engine's snippet.</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>A page read for research: where it ended up (after redirects), its title, its readable text (at most
/// <see cref="WebAccess.MaxPageCharacters"/>) and how many bytes were downloaded.</summary>
public sealed record WebPage(string Url, string Title, string Text, long Bytes);

/// <summary>A web search: the seam for search providers (the built-in one is <see cref="WebAccess"/>, DuckDuckGo's HTML page;
/// a self-hosted SearXNG or a keyed search API can implement it; checks use fixtures).</summary>
public interface IWebSearch
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

/// <summary>Reads one web page.</summary>
public interface IWebFetch
{
    Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A search or page that didn't work out, in a few plain words for the research loop (never logged with a URL).</summary>
public sealed class WebResearchException(string problem) : Exception(problem);

/// <summary>Martlet's own small web client for research: DuckDuckGo's HTML search (no key, no account; unofficial, so it may
/// limit or refuse automated searches) and a page reader that keeps only readable text. It connects only to public internet
/// addresses (never this PC, the local network or link-local addresses, checked on every connection, redirects included), uses
/// no proxy and no cookies, follows at most 4 redirects, reads at most <see cref="MaxPageBytes"/> of a page and gives each
/// request <see cref="RequestTimeout"/>.</summary>
public sealed class WebAccess : IWebSearch, IWebFetch, IDisposable
{
    public const string DuckDuckGo = "https://html.duckduckgo.com/html/";
    /// <summary>The most of one page (or search) downloaded; the rest is never read.</summary>
    public const int MaxPageBytes = 400_000;
    /// <summary>The most readable text kept of one page.</summary>
    public const int MaxPageCharacters = 20_000;
    public const int MaxResults = 8;
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(15);
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Martlet/1.0 (web research)";
    private readonly HttpClient client;
    private readonly Uri searchEndpoint;

    /// <param name="searchEndpoint">The search page (DuckDuckGo's HTML search by default; a fixture in checks).</param>
    /// <param name="allowLoopback">Only for checks against fixtures on 127.0.0.1: also connect to this PC.</param>
    public WebAccess(string? searchEndpoint = null, bool allowLoopback = false)
    {
        this.searchEndpoint = new Uri(searchEndpoint ?? DuckDuckGo, UriKind.Absolute);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 4, AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = false, UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, token) => ConnectAsync(context.DnsEndPoint, allowLoopback, token)
        };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.8");
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var url = new UriBuilder(searchEndpoint) { Query = "q=" + Uri.EscapeDataString(query.Trim()) }.Uri;
        var (html, _, _, _) = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseResults(html);
    }

    public async Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsWeb(url)) throw new WebResearchException("it isn't a web link");
        var (body, mediaType, final, bytes) = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        var html = mediaType is "text/html" or "application/xhtml+xml";
        var text = html ? HtmlText.Of(body) : Collapse(body);
        if (text.Length > MaxPageCharacters) text = text[..MaxPageCharacters];
        var title = html ? HtmlText.Title(body) : null;
        return new(final.AbsoluteUri, string.IsNullOrWhiteSpace(title) ? final.Host : title, text, bytes);
    }

    private async Task<(string Body, string MediaType, Uri Final, long Bytes)> GetAsync(Uri url, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new WebResearchException($"it answered {(int)response.StatusCode}");
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/html";
            if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
                throw new WebResearchException("it isn't a web page");
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var buffer = new byte[MaxPageBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), limit.Token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            Encoding encoding;
            try { encoding = response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8; }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            return (encoding.GetString(buffer, 0, read), mediaType, response.RequestMessage?.RequestUri ?? url, read);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new WebResearchException("it took too long");
        }
        catch (HttpRequestException error)
        {
            throw new WebResearchException(error.InnerException is WebResearchException blocked ? blocked.Message : "it couldn't be reached");
        }
        catch (IOException)
        {
            throw new WebResearchException("the connection broke off");
        }
    }

    // Every connection (redirects included) goes only to a public internet address.
    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowLoopback, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, token).ConfigureAwait(false);
        var allowed = addresses.Where(address => IsPublic(address) || allowLoopback && IPAddress.IsLoopback(address)).ToArray();
        if (allowed.Length == 0) throw new WebResearchException("it isn't on the public internet");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether <paramref name="address"/> is on the public internet (not this PC, a private or shared network,
    /// link-local, multicast, documentation or reserved).</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254 ||
                b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 ||
                b[0] == 198 && b[1] is 18 or 19);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC ||
            address.IsIPv6Teredo || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }

    public static bool IsWeb(Uri url) => url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) &&
        !string.IsNullOrEmpty(url.Host) && string.IsNullOrEmpty(url.UserInfo);

    private static readonly Regex Anchor = new("<a\\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex Attribute = new("\\b(class|href)\\s*=\\s*(\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>The results on a DuckDuckGo HTML search page (its result__a links and result__snippet text), ads left out, at
    /// most <see cref="MaxResults"/>.</summary>
    public static IReadOnlyList<WebSearchResult> ParseResults(string html)
    {
        var results = new List<WebSearchResult>();
        try
        {
            foreach (Match anchor in Anchor.Matches(html))
            {
                string? css = null, href = null;
                foreach (Match attribute in Attribute.Matches(anchor.Groups[1].Value))
                {
                    var value = attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value;
                    if (attribute.Groups[1].Value.Equals("class", StringComparison.OrdinalIgnoreCase)) css = value;
                    else href = value;
                }
                if (css is null) continue;
                var classes = css.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Contains("result__a"))
                {
                    if (results.Count >= MaxResults) break;
                    if (Target(href) is { } url) results.Add(new(Line(HtmlText.Of(anchor.Groups[2].Value)), url, ""));
                }
                else if (classes.Contains("result__snippet") && results.Count > 0 && results[^1].Snippet.Length == 0)
                    results[^1] = results[^1] with { Snippet = Line(HtmlText.Of(anchor.Groups[2].Value)) };
            }
        }
        catch (RegexMatchTimeoutException) { }
        return results.Where(r => r.Title.Length > 0).ToArray();

        static string Line(string text) => text.Replace('\n', ' ');
    }

    // A result's link: DuckDuckGo's redirect (//duckduckgo.com/l/?uddg=...) unwrapped; ads (y.js) and anything not a web link left out.
    private static string? Target(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        href = WebUtility.HtmlDecode(href.Trim());
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var url)) return null;
        if (url.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (url.AbsolutePath.StartsWith("/y.js", StringComparison.Ordinal)) return null;
            var uddg = url.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "uddg")?[1];
            if (uddg is null || !Uri.TryCreate(Uri.UnescapeDataString(uddg), UriKind.Absolute, out url)) return null;
        }
        return IsWeb(url) ? url.AbsoluteUri : null;
    }

    internal static string Collapse(string text) =>
        string.Join('\n', text.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0));

    public void Dispose() => client.Dispose();
}

/// <summary>The readable text of an HTML page: scripts, styles, navigation and markup left out, blocks on lines of their own.</summary>
public static class HtmlText
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private static readonly Regex Hidden = new("<!--.*?-->|<(script|style|noscript|svg|template|head|iframe|nav|footer|form)\\b.*?</\\1\\s*>", Options, Limit);
    private static readonly Regex Block = new("<(br|/?p|/?div|/?li|/?h[1-6]|/?tr|/?section|/?article|/?ul|/?ol|/?table|/?blockquote|/?pre|hr)\\b[^>]*>", Options, Limit);
    private static readonly Regex Tag = new("<[^>]*>", Options, Limit);
    private static readonly Regex TitleTag = new("<title\\b[^>]*>(.*?)</title>", Options, Limit);
    private static readonly Regex Main = new("<(main|article)\\b[^>]*>(.*)</\\1\\s*>", Options, Limit);

    public static string Of(string html)
    {
        try
        {
            // The page's main content when it marks it (menus and sidebars around it left out).
            var source = html;
            try
            {
                var main = Main.Match(html);
                if (main.Success && main.Groups[2].Length > 500) source = main.Groups[2].Value;
            }
            catch (RegexMatchTimeoutException) { }
            var text = Hidden.Replace(source, " ");
            text = Block.Replace(text, "\n");
            text = Tag.Replace(text, " ");
            return WebAccess.Collapse(WebUtility.HtmlDecode(text));
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    public static string? Title(string html)
    {
        try
        {
            var match = TitleTag.Match(html);
            if (!match.Success) return null;
            var title = WebAccess.Collapse(WebUtility.HtmlDecode(Tag.Replace(match.Groups[1].Value, " "))).Replace('\n', ' ');
            return title.Length > 200 ? title[..200] : title;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}

using System.Globalization;

namespace Martlet.Conversation.Guides;

/// <summary>A site's robots.txt rules for Martlet (RFC 9309): the group that names Martlet, else the groups for every agent ("*"),
/// with the longest matching rule deciding (an Allow wins a tie), "*" and "$" in rules, the Crawl-delay and the content signals.
/// A site that signals <c>ai-input=no</c> doesn't want its pages given to an AI model, so nothing on it may be read.</summary>
public sealed class RobotsRules
{
    /// <summary>Martlet's product token in robots.txt (the first word of <see cref="WebAccess.GuideAgent"/>).</summary>
    public const string Agent = "martlet";
    /// <summary>The most of a robots.txt read (RFC 9309 asks crawlers to read at least 500 KiB).</summary>
    public const int MaxBytes = 512_000;

    private readonly (string Pattern, bool Allow)[] rules;

    private RobotsRules((string Pattern, bool Allow)[] rules, TimeSpan? crawlDelay, bool noAiInput)
    {
        this.rules = rules;
        CrawlDelay = crawlDelay;
        NoAiInput = noAiInput;
    }

    /// <summary>No rules (no robots.txt, or it couldn't be read because the site refused it): every page may be read.</summary>
    public static RobotsRules AllowAll { get; } = new([], null, false);
    /// <summary>The site couldn't be reached for its robots.txt: nothing may be read (RFC 9309).</summary>
    public static RobotsRules DisallowAll { get; } = new([("/", false)], null, false);

    /// <summary>How long the site asks crawlers to wait between two requests, or null.</summary>
    public TimeSpan? CrawlDelay { get; }
    /// <summary>The site's content signals say <c>ai-input=no</c>.</summary>
    public bool NoAiInput { get; }

    public static RobotsRules Parse(string text, string agent = Agent)
    {
        var groups = new List<Group>();
        Group? current = null;
        var globalNoAi = false;
        foreach (var raw in (text ?? "").TrimStart('\uFEFF').Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key is "user-agent" or "useragent")
            {
                if (current is null || current.HasRules) groups.Add(current = new());
                current.Agents.Add(Token(value));
                continue;
            }
            if (key == "content-signal" && current is null)
            {
                globalNoAi |= SaysNoAiInput(value);
                continue;
            }
            if (current is null) continue;
            switch (key)
            {
                case "allow" or "disallow":
                    current.HasRules = true;
                    if (value.Length > 0) current.Rules.Add((Normalize(value), key == "allow"));
                    break;
                case "crawl-delay":
                    current.HasRules = true;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds < 86_400)
                        current.CrawlDelay = TimeSpan.FromSeconds(seconds);
                    break;
                case "content-signal":
                    current.HasRules = true;
                    current.NoAiInput |= SaysNoAiInput(value);
                    break;
            }
        }
        var token = Token(agent);
        var chosen = groups.Where(g => g.Agents.Contains(token)).ToList();
        if (chosen.Count == 0) chosen = groups.Where(g => g.Agents.Contains("*")).ToList();
        var delays = chosen.Where(g => g.CrawlDelay is not null).Select(g => g.CrawlDelay!.Value).ToList();
        return new(chosen.SelectMany(g => g.Rules).ToArray(), delays.Count == 0 ? null : delays.Max(),
            globalNoAi || chosen.Any(g => g.NoAiInput));

        static string Token(string value)
        {
            var end = value.IndexOfAny(['/', ' ', '\t']);
            return (end > 0 ? value[..end] : value).Trim().ToLowerInvariant();
        }
    }

    /// <summary>Whether Martlet may read <paramref name="url"/> (its path and query against the rules).</summary>
    public bool Allows(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (NoAiInput) return false;
        var path = Normalize(url.PathAndQuery);
        if (path == "/robots.txt") return true;
        var best = -1;
        var allowed = true;
        foreach (var (pattern, allow) in rules)
        {
            if (pattern.Length < best || !Matches(pattern, path)) continue;
            if (pattern.Length > best || allow) allowed = allow;
            best = pattern.Length;
        }
        return allowed;
    }

    // Rules and paths are compared with their %-escapes decoded, so "/w/File:" and "/w/File%3A" are the same rule.
    private static string Normalize(string value)
    {
        try { value = Uri.UnescapeDataString(value); }
        catch (UriFormatException) { }
        return value.StartsWith('/') || value.StartsWith('*') ? value : "/" + value;
    }

    private static bool SaysNoAiInput(string value) =>
        value.Split(',').Select(part => part.Split('=', 2)).Any(pair => pair.Length == 2 &&
            pair[0].Trim().Equals("ai-input", StringComparison.OrdinalIgnoreCase) && pair[1].Trim().Equals("no", StringComparison.OrdinalIgnoreCase));

    // A rule matches the start of the path; "*" is any run of characters and a final "$" anchors the rule at the end.
    internal static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];
        else pattern += "*";
        int p = 0, s = 0, star = -1, mark = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && pattern[p] == path[s]) { p++; s++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = s; }
            else if (star >= 0) { p = star + 1; s = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private sealed class Group
    {
        public HashSet<string> Agents { get; } = [];
        public List<(string Pattern, bool Allow)> Rules { get; } = [];
        public bool HasRules { get; set; }
        public TimeSpan? CrawlDelay { get; set; }
        public bool NoAiInput { get; set; }
    }
}

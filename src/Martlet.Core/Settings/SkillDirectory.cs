using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Martlet.Core.Settings;

/// <summary>How the skill directory orders what it finds. <see cref="Popular"/> (most downloads) is the default.
/// <see cref="BestMatch"/> keeps the order of a search; without a search it is the same as <see cref="Popular"/>.</summary>
public enum SkillDirectorySort { Popular, Installs, Stars, Trending, Newest, Updated, BestMatch }

/// <summary>Where a skill comes from: ClawHub (OpenClaw's skill registry) or skills.sh (skills kept in GitHub repositories,
/// which ClawHub's search also finds).</summary>
public enum SkillDirectorySource { All, ClawHub, SkillsSh }

/// <summary>What to look for: an optional search, the order, and the filters.</summary>
public sealed record SkillDirectoryQuery(string Search = "", SkillDirectorySort Sort = SkillDirectorySort.Popular, bool HideFlagged = true,
    long MinimumDownloads = 0, SkillDirectorySource Source = SkillDirectorySource.All)
{
    public bool Searching => !string.IsNullOrWhiteSpace(Search);
}

/// <summary>One skill in the directory. <paramref name="Source"/> is "clawhub" or "skills-sh". <paramref name="Repository"/> is
/// the GitHub "owner/repo" of a skills.sh skill. <paramref name="Flagged"/> is null when the directory didn't say.</summary>
public sealed record SkillDirectoryEntry(string Source, string Owner, string Slug, string Name, string Summary, long Downloads,
    long? Installs, long? Stars, long? RecentInstalls, DateTimeOffset? Created, DateTimeOffset? Updated, bool Official, bool Featured,
    bool? Flagged, string? Repository, string? PageAddress, IReadOnlyList<string> Topics)
{
    public const string ClawHubSource = "clawhub", SkillsShSource = "skills-sh";

    public string Id => Source == SkillsShSource && Repository is not null ? $"{Source}:{Repository}/{Slug}" : $"{Source}:{Owner}/{Slug}";

    public string SourceName => Source == SkillsShSource ? "skills.sh" : "ClawHub";

    /// <summary>The skill's page on the web, to read it before adding it.</summary>
    public Uri? Page => PageAddress is { } address && Uri.TryCreate(address, UriKind.Absolute, out var page) ? page
        : Source == ClawHubSource ? new Uri($"https://clawhub.ai/{Uri.EscapeDataString(Owner)}/skills/{Uri.EscapeDataString(Slug)}") : null;
}

public sealed record SkillDirectoryPage(IReadOnlyList<SkillDirectoryEntry> Skills, string? NextCursor);

public sealed class SkillDirectoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads the public skill directory: ClawHub (https://clawhub.ai, the skill registry of OpenClaw), whose search also
/// finds skills.sh skills. Without a search it lists ClawHub in the chosen order (most downloads by default); a search returns up
/// to 100 matches, which are filtered and ordered here. <see cref="ToSkill"/> makes a skill's SKILL.md one of the user's own
/// skills (Companion › Skills). Sends only the search words and filters; never a key or anything about the user.</summary>
public sealed class SkillDirectoryClient(HttpClient http)
{
    public static readonly Uri ClawHub = new("https://clawhub.ai/api/v1/");
    public const int PageSize = 50, SearchSize = 100, MaximumSkillBytes = 1_048_576;

    public async Task<SkillDirectoryPage> SearchAsync(SkillDirectoryQuery query, string? cursor, CancellationToken cancellation)
    {
        if (query.Searching)
        {
            var address = $"search?q={Uri.EscapeDataString(query.Search.Trim())}&limit={SearchSize}" +
                (query.HideFlagged ? "&nonSuspiciousOnly=true" : "");
            using var document = await GetJsonAsync(new Uri(ClawHub, address), cancellation);
            return new(Arrange(ParseSearch(document.RootElement), query), null);
        }
        if (query.Source == SkillDirectorySource.SkillsSh) return new([], null);
        var list = $"skills?sort={ServerSort(query.Sort)}&limit={PageSize}" + (query.HideFlagged ? "&nonSuspiciousOnly=true" : "") +
            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var page = await GetJsonAsync(new Uri(ClawHub, list), cancellation);
        var next = page.RootElement.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } n ? n : null;
        return new(Arrange(ParseList(page.RootElement, query.HideFlagged), query), next);
    }

    /// <summary>The skill's SKILL.md text: from ClawHub, or for a skills.sh skill from its GitHub repository.</summary>
    public async Task<string> ReadSkillAsync(SkillDirectoryEntry entry, CancellationToken cancellation)
    {
        if (entry.Source == SkillDirectoryEntry.ClawHubSource)
            return await GetTextAsync(new Uri(ClawHub, $"skills/{Uri.EscapeDataString(entry.Slug)}/file?path=SKILL.md&owner={Uri.EscapeDataString(entry.Owner)}"),
                cancellation) ?? throw new SkillDirectoryException($"ClawHub has no SKILL.md for {entry.Name}.");
        if (entry.Repository is not { } repository) throw new SkillDirectoryException($"{entry.Name} doesn't say which GitHub repository holds it.");
        var raw = $"https://raw.githubusercontent.com/{repository}/HEAD/";
        foreach (var path in new[] { $"skills/{entry.Slug}/SKILL.md", $"{entry.Slug}/SKILL.md", "SKILL.md" })
            if (await GetTextAsync(new Uri(raw + path), cancellation) is { } text) return text;
        // Deeper folders, such as plugins/<name>/skills/<slug>/SKILL.md: look the file up in the repository's tree.
        using var tree = await GetJsonAsync(new Uri($"https://api.github.com/repos/{repository}/git/trees/HEAD?recursive=1"), cancellation);
        var found = tree.RootElement.TryGetProperty("tree", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(i => Str(i, "path")).OfType<string>()
                .Where(p => p.EndsWith("/" + entry.Slug + "/SKILL.md", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Length).FirstOrDefault()
            : null;
        return found is not null && await GetTextAsync(new Uri(raw + found), cancellation) is { } deep ? deep
            : throw new SkillDirectoryException($"Couldn't find the SKILL.md of {entry.Name} in github.com/{repository}.");
    }

    /// <summary><paramref name="markdown"/> (a SKILL.md) as one of the user's own skills: its name, its description as the
    /// use-when line, and its instructions (without the front matter) as the text, cut at a line to fit
    /// <see cref="PromptSettings.MaximumTextCharacters"/> (<paramref name="shortened"/> says so). Its key comes from the
    /// directory's name for it and is not in <paramref name="usedKeys"/>. It starts unpinned.</summary>
    public static Skill ToSkill(SkillDirectoryEntry entry, string markdown, IEnumerable<string> usedKeys, out bool shortened)
    {
        var (front, body) = SplitFrontMatter(Clean(markdown));
        var name = Cut(OneLine(entry.Name is { Length: > 0 } n ? n : front.GetValueOrDefault("name") ?? entry.Slug), SkillSettings.MaximumNameCharacters);
        var useWhen = Cut(OneLine(front.GetValueOrDefault("description") is { Length: > 0 } d ? d : entry.Summary), SkillSettings.MaximumUseWhenCharacters);
        body = body.Trim();
        if (body.Length == 0) body = OneLine(entry.Summary);
        shortened = body.Length > PromptSettings.MaximumTextCharacters;
        if (shortened)
        {
            var end = body.LastIndexOf('\n', PromptSettings.MaximumTextCharacters - 1);
            body = body[..(end > PromptSettings.MaximumTextCharacters / 2 ? end : PromptSettings.MaximumTextCharacters)].TrimEnd();
        }
        return new Skill { Key = NewKey(entry.Slug, usedKeys), Name = name.Length == 0 ? "Skill" : name, UseWhen = useWhen, Body = body };
    }

    /// <summary>A key made from <paramref name="slug"/> (lowercase letters, digits and hyphens, at most 40) that no skill uses yet.</summary>
    public static string NewKey(string slug, IEnumerable<string> usedKeys)
    {
        var used = usedKeys.ToHashSet(StringComparer.Ordinal);
        var key = new StringBuilder();
        foreach (var c in slug.ToLowerInvariant())
        {
            var next = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-';
            if (next == '-' && (key.Length == 0 || key[^1] == '-')) continue;
            key.Append(next);
        }
        var stem = key.ToString().Trim('-');
        if (stem.Length == 0) stem = "skill";
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? "" : "-" + n.ToString(CultureInfo.InvariantCulture);
            var candidate = stem[..Math.Min(stem.Length, SkillSettings.MaximumKeyCharacters - suffix.Length)].TrimEnd('-') + suffix;
            if (!used.Contains(candidate) && SkillSettings.IsKey(candidate)) return candidate;
        }
    }

    /// <summary>The found skills with the filters applied, in the chosen order. A list from ClawHub is already in its order, so
    /// only a search is ordered here.</summary>
    public static IReadOnlyList<SkillDirectoryEntry> Arrange(IEnumerable<SkillDirectoryEntry> found, SkillDirectoryQuery query)
    {
        var kept = found.Where(e => e.Downloads >= query.MinimumDownloads && !(query.HideFlagged && e.Flagged == true) && query.Source switch
        {
            SkillDirectorySource.ClawHub => e.Source == SkillDirectoryEntry.ClawHubSource,
            SkillDirectorySource.SkillsSh => e.Source == SkillDirectoryEntry.SkillsShSource,
            _ => true
        });
        if (!query.Searching) return [.. kept];
        return query.Sort switch
        {
            SkillDirectorySort.Popular => [.. kept.OrderByDescending(e => e.Downloads)],
            SkillDirectorySort.Installs => [.. kept.OrderByDescending(e => e.Installs ?? -1).ThenByDescending(e => e.Downloads)],
            SkillDirectorySort.Stars => [.. kept.OrderByDescending(e => e.Stars ?? -1).ThenByDescending(e => e.Downloads)],
            SkillDirectorySort.Trending => [.. kept.OrderByDescending(e => e.RecentInstalls ?? -1).ThenByDescending(e => e.Downloads)],
            SkillDirectorySort.Newest => [.. kept.OrderByDescending(e => e.Created ?? e.Updated ?? DateTimeOffset.MinValue)],
            SkillDirectorySort.Updated => [.. kept.OrderByDescending(e => e.Updated ?? e.Created ?? DateTimeOffset.MinValue)],
            _ => [.. kept]
        };
    }

    public static string SortName(SkillDirectorySort sort) => sort switch
    {
        SkillDirectorySort.Popular => "most popular (downloads)", SkillDirectorySort.Installs => "most installed",
        SkillDirectorySort.Stars => "most stars", SkillDirectorySort.Trending => "trending", SkillDirectorySort.Newest => "newest",
        SkillDirectorySort.Updated => "recently updated", _ => "best match"
    };

    private static string ServerSort(SkillDirectorySort sort) => sort switch
    {
        SkillDirectorySort.Installs => "installs", SkillDirectorySort.Stars => "stars", SkillDirectorySort.Trending => "trending",
        SkillDirectorySort.Newest => "newest", SkillDirectorySort.Updated => "updated", _ => "downloads"
    };

    /// <summary>The items of a ClawHub list (GET /api/v1/skills).</summary>
    public static IReadOnlyList<SkillDirectoryEntry> ParseList(JsonElement root, bool unflaggedOnly)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var found = new List<SkillDirectoryEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (Str(item, "slug") is not { Length: > 0 } slug || Str(item, "ownerHandle") is not { Length: > 0 } owner) continue;
            var stats = Obj(item, "stats");
            found.Add(new(SkillDirectoryEntry.ClawHubSource, owner, slug, Str(item, "displayName") ?? slug, Str(item, "summary") ?? "",
                Num(stats, "downloads") ?? 0, Num(stats, "installs"), Num(stats, "stars"), null, Time(item, "createdAt"), Time(item, "updatedAt"),
                false, false, unflaggedOnly ? false : null, null, null, Strings(item, "topics")));
        }
        return found;
    }

    /// <summary>The results of a ClawHub search (GET /api/v1/search): ClawHub and skills.sh skills.</summary>
    public static IReadOnlyList<SkillDirectoryEntry> ParseSearch(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var found = new List<SkillDirectoryEntry>();
        foreach (var item in results.EnumerateArray())
        {
            var source = Str(item, "source");
            if (source is not (SkillDirectoryEntry.ClawHubSource or SkillDirectoryEntry.SkillsShSource)) continue;
            if (Str(item, "slug") is not { Length: > 0 } slug || Str(item, "ownerHandle") is not { Length: > 0 } owner) continue;
            var skill = Obj(Obj(item, "native"), "skill");
            var stats = Obj(skill, "stats");
            var identity = Obj(item, "sourceIdentity");
            string? repository = Str(identity, "owner") is { Length: > 0 } o && Str(identity, "repo") is { Length: > 0 } r ? $"{o}/{r}" : null;
            bool? flagged = skill is { } native && native.TryGetProperty("isSuspicious", out var suspicious) && suspicious.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? suspicious.GetBoolean()
                : Obj(Obj(item, "trust"), "upstreamScanners") is { ValueKind: JsonValueKind.Object } scanners
                    ? scanners.EnumerateObject().Any(s => Str(s.Value, "status") == "fail") : null;
            var topics = Strings(skill, "topics").Concat(Strings(skill, "categories")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            found.Add(new(source, owner, slug, Str(item, "displayName") ?? slug, Str(item, "summary") ?? "", Num(item, "downloads") ?? Num(stats, "downloads") ?? 0,
                Num(stats, "installs"), Num(stats, "stars"), Num(Obj(item, "metrics"), "rolling60DayInstalls"),
                Time(skill, "createdAt"), Time(skill, "updatedAt") ?? Time(item, "updatedAt"), Bool(item, "official"), Bool(item, "featured"),
                flagged, source == SkillDirectoryEntry.SkillsShSource ? repository : null, Str(Obj(item, "links"), "source"), topics));
        }
        return found;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri address, CancellationToken cancellation)
    {
        var text = await GetTextAsync(address, cancellation) ?? throw new SkillDirectoryException($"{address.Host} didn't find that.");
        try { return JsonDocument.Parse(text); }
        catch (JsonException error) { throw new SkillDirectoryException($"{address.Host} sent an answer Martlet can't read.", error); }
    }

    /// <summary>The text at <paramref name="address"/>, or null when it isn't there (404).</summary>
    private async Task<string?> GetTextAsync(Uri address, CancellationToken cancellation)
    {
        try
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new SkillDirectoryException($"{address.Host} is busy right now. Try again in a minute.");
            if (!response.IsSuccessStatusCode)
                throw new SkillDirectoryException($"{address.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            if (response.Content.Headers.ContentLength > MaximumSkillBytes * 8L)
                throw new SkillDirectoryException($"{address.Host} sent too much.");
            return await response.Content.ReadAsStringAsync(cancellation);
        }
        catch (HttpRequestException error)
        {
            throw new SkillDirectoryException($"Couldn't reach {address.Host}: {error.Message}", error);
        }
        catch (TaskCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new SkillDirectoryException($"{address.Host} took too long to answer.", error);
        }
    }

    private static string Clean(string text)
    {
        if (text.Length > MaximumSkillBytes) text = text[..MaximumSkillBytes];
        var clean = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
            if (!char.IsControl(c) || c is '\n' or '\t') clean.Append(c);
        return clean.ToString().TrimStart('\uFEFF');
    }

    /// <summary>The simple "key: value" lines of a SKILL.md's YAML front matter (folded "&gt;" and "|" values joined), and the rest.</summary>
    private static (Dictionary<string, string> Front, string Body) SplitFrontMatter(string text)
    {
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (front, text);
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (front, text);
        var lines = text[4..end].Split('\n');
        var after = text.IndexOf('\n', end + 4);
        var body = after < 0 ? "" : text[(after + 1)..];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.IndexOf(':') is not (> 0 and var colon)) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is "" or ">" or "|" or ">-" or "|-")
            {
                var folded = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0]))) folded.Add(lines[++i].Trim());
                value = string.Join(" ", folded.Where(f => f.Length > 0));
            }
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            front.TryAdd(key, value);
        }
        return (front, body);
    }

    private static string OneLine(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cut(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var space = text.LastIndexOf(' ', maximum - 2);
        return text[..(space > maximum / 2 ? space : maximum - 1)].TrimEnd(' ', ',', ';', ':', '.') + "\u2026";
    }

    private static JsonElement? Obj(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Str(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Num(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble() : null;

    private static bool Bool(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement? element, string name) =>
        Num(element, name) is { } ms and > 0 and < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static IReadOnlyList<string> Strings(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(v => v.Length > 0)]
            : [];
}

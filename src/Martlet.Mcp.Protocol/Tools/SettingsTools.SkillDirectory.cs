using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>skill_directory_search and skill_directory_add: Companion › Skills › Find skills without the window
/// (<see cref="SkillDirectoryClient"/>).</summary>
internal sealed partial class McpServer
{
    private static readonly Lazy<HttpClient> SkillDirectoryHttp = new(() =>
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 3, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("Martlet.Mcp");
        return http;
    });

    private static async Task<object> SkillDirectorySearchAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var query = new SkillDirectoryQuery(OptionalString(arguments, "search") ?? "",
            OptionalString(arguments, "sort") switch
            {
                null or "popular" => SkillDirectorySort.Popular, "installs" => SkillDirectorySort.Installs, "stars" => SkillDirectorySort.Stars,
                "trending" => SkillDirectorySort.Trending, "newest" => SkillDirectorySort.Newest, "updated" => SkillDirectorySort.Updated,
                "best_match" => SkillDirectorySort.BestMatch, var other => throw new ArgumentException($"Unknown sort '{other}'.")
            },
            OptionalBool(arguments, "hideFlagged") ?? true,
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("minimumDownloads", out var minimum) && minimum.TryGetInt64(out var m) ? Math.Max(0, m) : 0,
            OptionalString(arguments, "source") switch
            {
                null or "all" => SkillDirectorySource.All, "clawhub" => SkillDirectorySource.ClawHub, "skills-sh" => SkillDirectorySource.SkillsSh,
                var other => throw new ArgumentException($"Unknown source '{other}'.")
            });
        var page = await new SkillDirectoryClient(SkillDirectoryHttp.Value).SearchAsync(query, OptionalString(arguments, "cursor"), cancellation);
        var limit = Math.Clamp(OptionalInt(arguments, "limit") ?? 20, 1, 100);
        return new
        {
            search = query.Search, sort = SkillDirectoryClient.SortName(query.Sort), found = page.Skills.Count, nextCursor = page.NextCursor,
            note = !query.Searching && query.Source == SkillDirectorySource.SkillsSh ? "skills.sh skills can only be searched: give search." : null,
            skills = page.Skills.Take(limit).Select(e => new
            {
                id = e.Id, source = e.Source, owner = e.Owner, slug = e.Slug, name = e.Name, summary = e.Summary, downloads = e.Downloads,
                installs = e.Installs, stars = e.Stars, recentInstalls = e.RecentInstalls, created = e.Created, updated = e.Updated,
                official = e.Official, featured = e.Featured, flagged = e.Flagged, repository = e.Repository, page = e.Page?.AbsoluteUri
            }).ToArray()
        };
    }

    private static async Task<object> SkillDirectoryAddAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var source = OptionalString(arguments, "source") ?? SkillDirectoryEntry.ClawHubSource;
        var owner = RequiredString(arguments, "owner");
        var slug = RequiredString(arguments, "slug");
        var repository = OptionalString(arguments, "repository");
        if (source == SkillDirectoryEntry.SkillsShSource && repository is null) throw new ArgumentException("A skills-sh skill needs its repository (owner/repo).");
        var client = new SkillDirectoryClient(SkillDirectoryHttp.Value);
        // The directory's own facts (its name and summary) when a search finds it; otherwise the SKILL.md's.
        var found = await client.SearchAsync(new SkillDirectoryQuery(slug, SkillDirectorySort.BestMatch, HideFlagged: false), null, cancellation);
        var entry = found.Skills.FirstOrDefault(e => e.Source == source && string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase) &&
                (repository is null || string.Equals(e.Repository, repository, StringComparison.OrdinalIgnoreCase)))
            ?? new SkillDirectoryEntry(source, owner, slug, "", "", 0, null, null, null, null, null, false, false, null, repository, null, []);
        var markdown = await client.ReadSkillAsync(entry, cancellation);
        var directory = DataDirectory(arguments);
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var all = SkillSettings.All(loaded.Settings?.Skills);
        var skill = SkillDirectoryClient.ToSkill(entry, markdown, all.Select(s => s.Key), out var shortened);
        var list = new JsonArray([.. all.Append(skill).Select(s => (JsonNode)new JsonObject
        {
            ["key"] = s.Key, ["name"] = s.Name, ["use_when"] = s.UseWhen, ["body"] = s.Body, ["pinned"] = s.Pinned
        })]);
        using var value = JsonDocument.Parse(new JsonObject { ["skills"] = list }.ToJsonString());
        await SettingsConfiguration.SetAsync(directory, "skills", value.RootElement, loaded.Revision, cancellation);
        return new
        {
            added = new { key = skill.Key, name = skill.Name, useWhen = skill.UseWhen, body = skill.Body, pinned = skill.Pinned }, shortened, from = entry.Page?.AbsoluteUri,
            flagged = entry.Flagged,
            note = "A running Martlet follows this by itself; an open conversation uses it before its next reply."
        };
    }
}

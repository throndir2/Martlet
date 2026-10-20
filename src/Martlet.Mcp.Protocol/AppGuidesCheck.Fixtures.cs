using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Mcp;

internal static partial class AppGuidesCheck
{
    private sealed class Words(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class MovedClock : TimeProvider
    {
        internal TimeSpan Ahead { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Ahead;
    }

    // Reads nothing: the latency check keeps a guide it made itself.
    private sealed class NoBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([], [], 0, 0, "not in this check"));
    }

    /// <summary>The web client with every fetch outside loopback refused before it is sent (and counted), so nothing leaves this PC
    /// whatever the wiki reader tries.</summary>
    private sealed class LoopbackOnly(WebAccess web) : IWebSearch, IWebFetch, IWebDocuments
    {
        private int refused;
        internal int Refused => Volatile.Read(ref refused);

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            web.SearchAsync(query, cancellationToken);

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            if (url.IsLoopback) return web.FetchAsync(url, cancellationToken);
            Interlocked.Increment(ref refused);
            throw new WebResearchException("not on this PC's fixture (app_guides_check reads loopback only)");
        }

        public Task<WebDocument> ReadAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
        {
            if (url.IsLoopback) return web.ReadAsync(url, maxBytes, cancellationToken);
            Interlocked.Increment(ref refused);
            throw new WebResearchException("not on this PC's fixture (app_guides_check reads loopback only)");
        }
    }

    /// <summary>One HTTP server on 127.0.0.1 (NOT real sites): /search answers like DuckDuckGo's HTML search, /wiki/* is a tiny
    /// wiki about a made-up game (sections about iron ore and a sword, links to more pages, navigation to leave out), /w/abyss/* a
    /// second wiki found only through the search, and /robots.txt allows everything.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHttpServer server;
        private readonly List<string> paths = [];
        private readonly List<string> queries = [];

        internal Fixture() => server = new LoopbackHttpServer(HandleAsync, concurrent: true);

        internal string BaseUrl => server.Origin;
        internal IReadOnlyList<string> Queries { get { lock (paths) return [.. queries]; } }
        internal IReadOnlyList<string> Paths { get { lock (paths) return [.. paths]; } }
        /// <summary>The series wiki's pages asked for (action=parse), in order.</summary>
        internal IReadOnlyList<string> SeriesParsed { get { lock (paths) return [.. seriesParsed]; } }
        private readonly List<string> seriesParsed = [];

        /// <summary>The series wiki's main page: it names its api.php (EditURI), like a MediaWiki page.</summary>
        internal const string SeriesStart = "/series/wiki/Starfall_Saga_Wiki";
        private const string SeriesMain = "Starfall Saga Wiki";
        private static readonly string[] SeriesLinks =
            ["Moonstone Sword (Starfall Valley 3)", "Orla", "Moonstone Sword (Starfall Valley 2)", "Starfall Valley 3", "Ember Mines"];
        private static readonly Dictionary<string, (string Text, string[] Links)> SeriesPages = new(StringComparer.Ordinal)
        {
            ["Moonstone Sword (Starfall Valley 3)"] = ("In the third game the Moonstone Sword is forged from starglass at the observatory.", []),
            ["Orla"] = ("Orla is the blacksmith of the valley; she forges swords and mends pickaxes in her smithy by the river.", []),
            ["Moonstone Sword (Starfall Valley 2)"] = ("In Starfall Valley 2 the Moonstone Sword takes three moonstones that crystal golems drop.",
                ["Frost Caves", "Voice cast"]),
            ["Starfall Valley 3"] = ("Starfall Valley 3 is the third game of the series, set on a floating island far to the north.", []),
            ["Ember Mines"] = ("The Ember Mines hold iron and gold ore below level forty; bring a steel pickaxe and some torches.", []),
            ["Frost Caves"] = ("The Frost Caves are icy caves where crystal golems guard the moonstones deep below the glacier.", ["Crystal Golem"]),
            ["Voice cast"] = ("The voice actors of the series recorded their lines in two studios over many long months.", []),
            ["Crystal Golem"] = ("Crystal golems are slow but hit hard; strike the glowing core on their back with a heavy hammer.", []),
        };

        // A MediaWiki api.php answering siteinfo, the main page's links, Mostlinked (none), page info, every page and parse.
        private string SeriesApi(string target)
        {
            var query = (target.Split('?', 2).ElementAtOrDefault(1) ?? "").Split('&').Select(p => p.Split('=', 2))
                .GroupBy(p => Uri.UnescapeDataString(p[0])).ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1]) : "");
            static object Info(string title) => SeriesPages.ContainsKey(title) ? new { ns = 0, title, length = 5_000 } : (object)new { ns = 0, title, missing = true };
            object body = query.GetValueOrDefault("action") switch
            {
                "parse" when SeriesPages.TryGetValue(query.GetValueOrDefault("page") ?? "", out var page) => Parsed(query["page"], page),
                "query" when query.ContainsKey("meta") => new
                {
                    query = new
                    {
                        general = new { mainpage = SeriesMain, sitename = SeriesMain, generator = "MediaWiki 1.43.0", server = BaseUrl, articlepath = "/series/wiki/$1" },
                        namespaces = new Dictionary<string, object>
                        {
                            ["0"] = new { id = 0, name = "", content = true }, ["2"] = new { id = 2, name = "User", canonical = "User" },
                            ["6"] = new { id = 6, name = "File", canonical = "File" }
                        },
                        statistics = new { articles = SeriesPages.Count }
                    }
                },
                "query" when query.GetValueOrDefault("generator") == "links" => new { query = new { pages = SeriesLinks.Select(Info).ToArray() } },
                "query" when query.GetValueOrDefault("generator") == "allpages" => new { batchcomplete = true, query = new { pages = SeriesPages.Keys.Select(Info).ToArray() } },
                "query" when query.GetValueOrDefault("list") == "querypage" => new { query = new { querypage = new { results = Array.Empty<object>() } } },
                "query" when query.GetValueOrDefault("list") == "search" => new
                {
                    query = new { search = new[] { "Moonstone Sword (Starfall Valley 2)", "Ember Mines" }.Select(t => new { ns = 0, title = t }).ToArray() }
                },
                "query" when query.ContainsKey("titles") => new { query = new { pages = query["titles"].Split('|').Select(Info).ToArray() } },
                _ => new { error = new { code = "missingtitle" } }
            };
            return JsonSerializer.Serialize(body);

            object Parsed(string title, (string Text, string[] Links) page)
            {
                lock (paths) seriesParsed.Add(title);
                var links = string.Concat(page.Links.Select(l => $"<a href=\"/series/wiki/{l.Replace(' ', '_')}\">{l}</a> "));
                var text = $"<div class=\"mw-parser-output\"><p>{page.Text} {page.Text} {page.Text}</p><p>{links}</p></div>";
                return new { parse = new { title, pageid = 1, text } };
            }
        }

        private async Task HandleAsync(LoopbackExchange exchange)
        {
            var target = exchange.Request.Target;
            var path = target.Split('?')[0];
            var query = path == "/search"
                ? Uri.UnescapeDataString((target.Split("q=", 2).ElementAtOrDefault(1)?.Split('&')[0] ?? "").Replace('+', ' ')) : "";
            lock (paths)
            {
                paths.Add(path);
                if (path == "/search") queries.Add(query);
            }
            var (type, body) = path switch
            {
                "/robots.txt" => ("text/plain", "User-agent: *\nAllow: /\n"),
                "/search" => ("text/html", SearchPage(query)),
                "/series/api.php" => ("application/json", SeriesApi(target)),
                SeriesStart => ("text/html", "<html><head><link rel=\"EditURI\" type=\"application/rsd+xml\" href=\"" + BaseUrl +
                    "/series/api.php?action=rsd\"/></head><body><p>The Starfall Saga Wiki is about every Starfall game (FIXTURE).</p></body></html>"),
                // Every other page of the series wiki names its api.php too (Read more finds the wiki again from a page).
                _ when path.StartsWith("/series/wiki/", StringComparison.Ordinal) => ("text/html", "<html><head><link rel=\"EditURI\" " +
                    "type=\"application/rsd+xml\" href=\"" + BaseUrl + "/series/api.php?action=rsd\"/></head><body><p>A page of the Starfall " +
                    "Saga Wiki (FIXTURE).</p></body></html>"),
                "/wiki/Sunken_Crypt" => ("text/html", Page("Sunken Crypt",
                    "<p>The Sunken Crypt is a flooded dungeon under the old chapel in Starfall Valley (FIXTURE).</p><h2>Getting in</h2>" +
                    "<p>Drain the Sunken Crypt by turning the three bronze valves in the chapel cellar; the water drops one floor with " +
                    "each valve.</p><h2>Dangers</h2><p>Drowned knights guard the lowest floor of the Sunken Crypt. They are weak to " +
                    "fire, so bring torches, and leave before midnight, when the water rises again.</p>")),
                "/wiki/Main_Page" or "/wiki/" or "/wiki" => ("text/html", Page("Starfall Valley Wiki",
                    "<p>Welcome to the Starfall Valley Wiki, the fan wiki about the farming game Starfall Valley (FIXTURE).</p>" +
                    "<h2>Mining</h2><h3>Iron ore</h3><p>Iron ore is found in the Ember Mines below level 40. Bring a steel pickaxe; " +
                    "each iron node gives two or three ore. Smelt five iron ore in a furnace to make one iron bar.</p>" +
                    "<h2>Weapons</h2><h3>Moonstone sword</h3><p>The Moonstone sword is forged by the blacksmith Orla from three " +
                    "moonstones and one iron bar. Moonstones drop from crystal golems in the Frost Caves.</p>" +
                    "<h2>Seasons</h2><p>Each season lasts twenty-eight days. Crops die when the season changes unless they grow in " +
                    "the greenhouse.</p>" +
                    "<ul><li><a href=\"/wiki/Iron_Ore\">Iron Ore</a></li><li><a href=\"/wiki/Moonstone_Sword\">Moonstone Sword</a></li>" +
                    "<li><a href=\"/wiki/Special:Random\">Random page</a></li><li><a href=\"/wiki/User:Bob\">Bob</a></li></ul>")),
                "/wiki/Iron_Ore" => ("text/html", Page("Iron Ore",
                    "<p>Iron Ore is a common mineral in Starfall Valley (FIXTURE).</p><h2>Where to find</h2><p>Iron ore nodes appear " +
                    "in the Ember Mines from level 40 to level 80, and rarely in the Quarry after rain.</p><h2>Uses</h2><p>Five iron " +
                    "ore smelt into one iron bar, used for tools, sprinklers and the Moonstone sword.</p>")),
                "/wiki/Moonstone_Sword" => ("text/html", Page("Moonstone Sword",
                    "<p>The Moonstone Sword is a late-game weapon in Starfall Valley (FIXTURE).</p><h2>How to get it</h2><p>Bring " +
                    "three moonstones and one iron bar to Orla the blacksmith; she forges it overnight. It deals more damage at night " +
                    "and glows near hidden doors, so many players carry it into the Frost Caves to find the secret rooms.</p>")),
                "/w/abyss/Main_Page" => ("text/html", Page("Moonlit Abyss Wiki",
                    "<p>The Moonlit Abyss Wiki is about the diving game Moonlit Abyss (FIXTURE).</p><h2>Oxygen</h2><p>Your tank " +
                    "holds ninety seconds of air; blue coral refills it by ten seconds. A bigger tank from the harbour shop holds three " +
                    "minutes, and the rebreather perk makes each breath last a little longer.</p><h2>Getting started</h2><p>Your first " +
                    "dives are in the shallow reef, where the currents are gentle and the fish are friendly.</p>" +
                    "<ul><li><a href=\"/w/abyss/Trenches\">Trenches</a></li></ul>", "/w/abyss/Main_Page")),
                "/w/abyss/Trenches" => ("text/html", Page("Trenches",
                    "<p>Trenches in Moonlit Abyss are the deepest dives (FIXTURE).</p><h2>Pressure</h2><p>Below two hundred " +
                    "metres the hull creaks; upgrade it at the dock before diving. Lanterns attract the anglerfish, so dim them in the " +
                    "Black Trench and follow the glowing kelp back to the surface when your air runs low.</p>", "/w/abyss/Main_Page")),
                _ => ("", "")
            };
            if (type.Length == 0) await exchange.RespondAsync(404, "", "");
            else await exchange.RespondAsync(200, body, type + "; charset=utf-8");
        }

        private string Link(string path) => Away(BaseUrl + path);

        private static string Away(string url) => "//duckduckgo.com/l/?uddg=" + Uri.EscapeDataString(url) + "&amp;rut=fixture";

        // A search about the Sunken Crypt (area research) finds its wiki page, a Reddit thread (whose page Martlet never reads: its
        // snippet only) and a video (never read); every other search finds the second wiki.
        private string SearchPage(string query) =>
            "<html><body><div class=\"results\">" +
            "<div class=\"result result--ad\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/y.js?ad=1\">Sponsored</a></div>" +
            (query.Contains("Sunken Crypt", StringComparison.OrdinalIgnoreCase)
                ? Result("/wiki/Sunken_Crypt", "Sunken Crypt - Starfall Valley Wiki", "The Sunken Crypt is a flooded dungeon in Starfall Valley.") +
                  Result(Away(RedditThread), "Sunken Crypt is impossible : r/StarfallValley",
                      "Starfall Valley tip: in the Sunken Crypt, turn all three valves first and bring torches for the drowned knights.", away: true) +
                  Result(Away("https://www.youtube.com/watch?v=fixture"), "Starfall Valley Sunken Crypt walkthrough",
                      "A video walkthrough of the Sunken Crypt in Starfall Valley, all floors.", away: true)
                : Result("/w/abyss/Main_Page", "Moonlit Abyss Wiki", "The fan wiki about Moonlit Abyss.")) +
            "</div></body></html>";

        internal const string RedditThread = "https://www.reddit.com/r/StarfallValley/comments/fixture/sunken_crypt_is_impossible/";

        private string Result(string path, string title, string snippet, bool away = false) =>
            $"<div class=\"result\"><h2 class=\"result__title\"><a rel=\"nofollow\" class=\"result__a\" href=\"{(away ? path : Link(path))}\">{title}</a></h2>" +
            $"<a class=\"result__snippet\" href=\"{(away ? path : Link(path))}\">{snippet}</a></div>";

        private static string Page(string title, string content, string home = "/wiki/Main_Page") =>
            $"<html><head><title>{title} - Fixture Wiki</title><script>var tracking = 1;</script></head><body>" +
            $"<nav><a href=\"{home}\">Home</a> | <a href=\"/wiki/Special:RecentChanges\">Recent changes</a></nav>" +
            $"<main><article><h1>{title}</h1>{content}</article></main><footer>Fixture wiki for app_guides_check.</footer></body></html>";

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}

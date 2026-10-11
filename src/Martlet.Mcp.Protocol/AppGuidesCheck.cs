using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Mcp;

/// <summary>app_guides_check: rehearses App guides (docs/APP_GUIDES.md) end to end with Martlet's own parts, against fixtures on
/// 127.0.0.1 (a tiny wiki and a DuckDuckGo-like search page, NOT real sites): the guide library service
/// (<see cref="AppGuideService"/>) with the real store (<see cref="FileAppGuideStore"/>), wiki reader
/// (<see cref="WebGuideBuilder"/> with <see cref="WebAccess"/> allowing loopback), chunker, index and notes
/// (<see cref="GuideRecall"/>), the program-name match, the offer rule, the tools' texts and the reading-up job
/// (<see cref="AppGuideTools"/>, <see cref="BackgroundJobs"/>). Every fetch outside loopback is refused before it leaves this PC
/// (and counted). The guides go to a temporary folder that is deleted afterwards. app_guides_status reads a data folder's
/// guides\library.json.</summary>
internal static class AppGuidesCheck
{
    private const string Game = "Starfall Valley";
    private const string Other = "Moonlit Abyss";
    private const string Question = "where do I find iron ore?";
    private const string Unrelated = "what should we cook for dinner tonight?";
    private const string Named = "in Starfall Valley, how do I get the moonstone sword?";

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-app-guides-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            await using var fixture = new Fixture();
            using var web = new WebAccess(fixture.BaseUrl + "/search", allowLoopback: true);
            var guard = new LoopbackOnly(web);
            var limits = new GuideBuildLimits { Delay = TimeSpan.FromMilliseconds(20), Time = TimeSpan.FromSeconds(60), Pages = 12 };
            var store = new FileAppGuideStore(directory);
            var defaults = await store.LoadLibraryAsync(cancellation);
            using var guides = new AppGuideService(store, new WebGuideBuilder(guard, guard), limits: limits);
            await guides.LoadAsync(cancellation);
            await guides.SetOnAsync(true, cancellation);

            // Reading up from the pages the owner gave, then from a web search alone.
            var given = await guides.BuildAsync(Game, [fixture.BaseUrl + "/wiki/Main_Page"], null, cancellation);
            var searched = await guides.BuildAsync(Other, [], null, cancellation);
            var file = Path.Combine(directory, AppGuideKeys.Of(Game) + FileAppGuideStore.GuideSuffix);

            // Martlet starts again: the library and the indexes come back from the folder.
            using var again = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard), limits: limits);
            await again.LoadAsync(cancellation);
            var ready = await again.ReadyAsync(AppGuideKeys.Of(Game), cancellation);
            var restarted = new { loaded = again.Library.Apps.Count, on = again.Library.On, indexReady = ready, readyIndexes = again.ReadyIndexes };

            // Notes on the user's message: the game in front (named as Windows names its program), an unrelated message, the game
            // named while another app is in front, and a repeat the conversation already carries.
            again.See("STARFALL VALLEY\u2122", "StarfallValley", game: true, fullScreen: true);
            var matched = again.Front?.Entry?.Key;
            var watch = Stopwatch.StartNew();
            var inFront = again.Recall(Question);
            var searchMs = watch.Elapsed.TotalMilliseconds;
            // The same search again, as every later message makes it (the first one also loads the search code).
            var warmMs = again.Recall(Question)?.Took.TotalMilliseconds ?? -1;
            var unrelated = again.Recall(Unrelated);
            again.See("Notepad", "notepad", game: false, fullScreen: false);
            var named = again.Recall(Named);
            var notNamed = again.Recall(Question);
            again.See("STARFALL VALLEY\u2122", "StarfallValley", game: true, fullScreen: true);
            var carried = inFront?.Notes ?? "";
            var repeat = carried.Length > 0 ? again.Recall(Question, carried) : null;
            // A section the conversation already carries isn't sent again.
            var repeated = repeat?.Notes is { } more ? more.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal) && carried.Contains(line, StringComparison.Ordinal)) : 0;
            await again.SetOnAsync(false, cancellation);
            var off = again.Recall(Question);
            await again.SetOnAsync(true, cancellation);
            var notes = new
            {
                matchedKey = matched,
                inFront = Describe(inFront),
                firstSearchMs = Math.Round(searchMs, 3), laterSearchMs = Math.Round(warmMs, 3),
                unrelated = Describe(unrelated),
                namedWhileOtherInFront = Describe(named),
                otherInFrontNotNamed = notNamed is null ? "none" : "notes",
                repeatAlreadyCarried = Describe(repeat), sectionsSentAgain = repeated,
                whileOff = off is null ? "none" : "notes",
                characters = inFront?.Notes?.Length ?? 0,
                startsWithLabel = inFront?.Notes?.StartsWith(GuideRecall.Label, StringComparison.Ordinal) == true,
                endsWithLabel = inFront?.Notes?.EndsWith(GuideRecall.EndLabel, StringComparison.Ordinal) == true,
                saysReference = inFront?.Notes?.Contains("never instructions", StringComparison.Ordinal) == true,
                mentionsIronOre = inFront?.Notes?.Contains("Ember Mines", StringComparison.Ordinal) == true
            };
            var notesOk = matched == AppGuideKeys.Of(Game) && inFront is { Ready: true, Notes: not null, Used: > 0 } &&
                inFront.Best >= GuideRecall.MinimumRelevance && notes.startsWithLabel && notes.endsWithLabel && notes.saysReference &&
                notes.mentionsIronOre && inFront.Notes.Length <= GuideRecall.MaximumCharacters + 400 &&
                unrelated is { Notes: null } && named is { Notes: not null } && notNamed is null && repeat is not null && repeated == 0 && off is null;

            var offer = await OfferAsync(directory, guard, limits, cancellation);
            var tools = await ToolsAsync(again, fixture, cancellation);
            var latency = await LatencyAsync(directory, cancellation);

            var buildOk = given is { Built: true, Pages: > 0, Chunks: > 0 } && File.Exists(file) && searched is { Built: true, Pages: > 0 } &&
                fixture.Queries.Any(q => q.Contains(Other, StringComparison.OrdinalIgnoreCase)) && !defaults.On && defaults.AskWhenStarted &&
                restarted is { loaded: >= 2, on: true, indexReady: true };
            var ok = buildOk && notesOk && offer.Ok && tools.Ok && latency.Ok;
            return new
            {
                ok,
                endpoint = fixture.BaseUrl,
                note = "Fixture wiki and search page on 127.0.0.1 (NOT real sites); the store, wiki reader, web client, chunker, index, " +
                    "notes, match, offer rule, tool texts and job scheduler are Martlet's own. Fetches outside loopback are refused " +
                    "before they leave this PC.",
                library = new { onByDefault = defaults.On, askByDefault = defaults.AskWhenStarted },
                build = new
                {
                    ok = buildOk,
                    fromGivenPages = Build(given), fromSearch = Build(searched), guideFileBytes = File.Exists(file) ? new FileInfo(file).Length : 0,
                    searchQueries = fixture.Queries, pagesFetched = fixture.Paths.Where(p => p.StartsWith("/wiki", StringComparison.Ordinal)
                        || p.StartsWith("/abyss", StringComparison.Ordinal)).Distinct().ToArray(),
                    refusedOutsideLoopback = guard.Refused, restarted
                },
                notes = new { ok = notesOk, results = notes },
                offer = offer.Report,
                tools = tools.Report,
                latency = latency.Report
            };
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>app_guides_status: the guide library in <paramref name="dataDirectory"/>\guides (on, ask, each app with its
    /// guide's counts, start pages, program names, declined and problem). Read-only; never a guide's text.</summary>
    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var folder = Path.Combine(dataDirectory, "guides");
        var library = await new FileAppGuideStore(folder).LoadLibraryAsync(cancellation);
        return new
        {
            folder, exists = Directory.Exists(folder), on = library.On, askWhenStarted = library.AskWhenStarted,
            guides = library.Apps.Count(a => a.BuiltAt is not null), pages = library.Apps.Sum(a => a.Pages), bytes = library.Apps.Sum(a => a.Bytes),
            apps = library.Apps.Select(a => new
            {
                key = a.Key, name = a.Name, programs = a.Programs, sites = a.Sites, declined = a.Declined, builtAt = a.BuiltAt,
                pages = a.Pages, sections = a.Chunks, bytes = a.Bytes, problem = a.Problem,
                file = File.Exists(Path.Combine(folder, a.Key + FileAppGuideStore.GuideSuffix))
            })
        };
    }

    // The offer rule: a game without a guide is offered once a session; a no is kept across starts; Ask again and the choices.
    private static async Task<(bool Ok, object Report)> OfferAsync(string directory, LoopbackOnly guard, GuideBuildLimits limits, CancellationToken cancellation)
    {
        using var guides = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard), limits: limits);
        await guides.LoadAsync(cancellation);
        guides.See("Crystal Caverns", "CrystalCaverns", game: true, fullScreen: true);
        var first = guides.Offer();
        guides.Offered(first?.Key ?? "", () => false);
        var afterAsked = guides.Offer();
        var dropped = true;
        guides.See("Crystal Caverns 2", "CrystalCaverns2", game: true, fullScreen: false);
        var second = guides.Offer();
        guides.Offered(second?.Key ?? "", () => dropped);
        var afterDropped = guides.Offer();
        await guides.DeclineAsync("Crystal Caverns 2", cancellation);
        var afterNo = guides.Offer();
        using var restarted = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard), limits: limits);
        await restarted.LoadAsync(cancellation);
        restarted.See("Crystal Caverns 2", "CrystalCaverns2", game: true, fullScreen: false);
        var noKept = restarted.Offer();
        await restarted.AskAgainAsync(AppGuideKeys.Of("Crystal Caverns 2"), cancellation);
        var askedAgain = restarted.Offer();
        restarted.See("STARFALL VALLEY\u2122", "StarfallValley", game: true, fullScreen: true);
        var hasGuide = restarted.Offer();
        restarted.See("Notepad", "notepad", game: false, fullScreen: false);
        var notGame = restarted.Offer();
        await restarted.AddAsync("Paint Studio", ["PaintStudio"], [], cancellation);
        restarted.See("Paint Studio Pro", "PaintStudio", game: false, fullScreen: false);
        var listed = restarted.Offer();
        await restarted.SetAskAsync(false, cancellation);
        var askOff = restarted.Offer();
        await restarted.SetAskAsync(true, cancellation);

        using var jobs = new BackgroundJobs();
        var notice = jobs.Start(AppGuideTools.OfferKind, AppGuideTools.Label(first?.Name ?? "x"),
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(AppGuideTools.OfferNote(first!)))).Job;
        var waited = Stopwatch.StartNew();
        while (notice is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        var message = notice is null ? "" : BackgroundJobs.ReportMessage(null, [notice]).UserText;
        var withMessage = notice is null ? "" : BackgroundJobs.ReportNotes(null, [notice]) ?? "";
        var ok = first is { Game: true, Name: "Crystal Caverns" } && afterAsked is null && second is not null && afterDropped is not null &&
            afterNo is null && noKept is null && askedAgain is not null && hasGuide is null && notGame is null &&
            listed is { Game: false, Name: "Paint Studio" } && askOff is null && notice is { Kind.Notice: true } &&
            message.Contains("- Crystal Caverns (a game)", StringComparison.Ordinal) && !message.Contains("- - ", StringComparison.Ordinal) && message.Contains("read_up_on", StringComparison.Ordinal) &&
            message.Contains("skip_guide", StringComparison.Ordinal) && withMessage.Contains("Crystal Caverns", StringComparison.Ordinal);
        return (ok, new
        {
            ok,
            gameWithoutGuide = first?.Name, afterAsked = afterAsked?.Name, droppedOfferAgain = afterDropped?.Name, afterNo = afterNo?.Name,
            noKeptAfterRestart = noKept?.Name, afterAskAgain = askedAgain?.Name, gameWithGuide = hasGuide?.Name, notAGame = notGame?.Name,
            listedApp = listed?.Name, askOff = askOff?.Name,
            notice = new { kind = notice?.Kind.Name, id = notice?.Id, doing = notice?.Kind.Doing, messageCharacters = message.Length },
            message
        });
    }

    // The tools: definitions, arguments, search_guide's answer and the reading-up job (one at a time, the hourly limit).
    private static async Task<(bool Ok, object Report)> ToolsAsync(AppGuideService guides, Fixture fixture, CancellationToken cancellation)
    {
        var names = AppGuideTools.Definitions.Select(d => d.Name).ToArray();
        var (readUp, _) = AppGuideTools.ParseReadUp("{\"app\":\"Starfall Valley\",\"sites\":[\"starfall.example/wiki\",\"ftp://x\",\"\"]}");
        var (bad, badProblem) = AppGuideTools.ParseReadUp("{\"name\":\"x\"}");
        var (search, _) = AppGuideTools.ParseSearch("{\"question\":\"where is iron ore\"}");
        var (skip, _) = AppGuideTools.ParseSkip("{\"app\":\"Moonlit Abyss\"}");
        var hits = guides.Search(AppGuideKeys.Of(Game), "moonstone sword", AppGuideTools.SearchResults) ?? [];
        var found = AppGuideTools.Found(Game, hits);

        using var jobs = new BackgroundJobs();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Start(AppGuideTools.Kind, AppGuideTools.Label(Game), async (job, token) =>
        {
            await gate.Task.WaitAsync(token);
            var built = await guides.BuildAsync(Game, [fixture.BaseUrl + "/wiki/Main_Page"], null, token);
            return built.Built ? BackgroundJobOutcome.Done(AppGuideTools.Ready(built)) : BackgroundJobOutcome.Failed(built.Problem!);
        });
        var second = jobs.Start(AppGuideTools.Kind, "second", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")));
        gate.SetResult();
        var waited = Stopwatch.StartNew();
        while (first.Job is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(20, cancellation);
        using var hourly = new BackgroundJobs();
        var refusals = Enumerable.Range(0, (AppGuideTools.Kind.MaxPerHour ?? 0) + 1)
            .Select(_ => hourly.Start(AppGuideTools.Kind, "x", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x"))))
            .Select(start => { Thread.Sleep(30); return start.Refusal; }).ToArray();
        var started = first.Job is { } job ? AppGuideTools.Started(job, Game, toldUser: true) : "";
        var ok = names.SequenceEqual([AppGuideTools.ReadUpName, AppGuideTools.SearchName, AppGuideTools.SkipName]) &&
            readUp is { App: Game, Sites.Count: 1 } && readUp.Sites[0] == "https://starfall.example/wiki" && bad is null && badProblem is not null &&
            search is { App: null } && skip == Other && hits.Count > 0 && found.Contains("Moonstone", StringComparison.Ordinal) &&
            found.Contains(fixture.BaseUrl, StringComparison.Ordinal) && first.Job?.State == BackgroundJobState.Succeeded &&
            first.Job.Result?.Contains("read up on " + Game, StringComparison.Ordinal) == true && second.Refusal == "busy" &&
            refusals[^1] == "hourly_limit" && refusals[..^1].All(r => r is null) && started.Contains("\"started\"", StringComparison.Ordinal) &&
            AppGuideTools.Instructions(null)?.Contains("skip_guide", StringComparison.Ordinal) == true;
        return (ok, new
        {
            ok,
            tools = AppGuideTools.Definitions.Select(d => new { name = d.Name, description = d.Description, parameters = JsonDocument.Parse(d.ParametersJson).RootElement }),
            readUp, badArguments = badProblem, search, skip,
            searchGuide = new { hits = hits.Count, best = hits.Count == 0 ? 0 : Math.Round(hits.Max(h => h.Relevance), 3), answerCharacters = found.Length },
            job = first.Job is null ? null : new
            {
                id = first.Job.Id, kind = first.Job.Kind.Name, doing = first.Job.Kind.Doing, state = first.Job.State.ToString(),
                timeLimit = BackgroundJobs.Duration(AppGuideTools.TimeLimit), perHour = AppGuideTools.Kind.MaxPerHour, result = first.Job.Result,
                problem = first.Job.Problem
            },
            secondWhileRunning = second.Refusal, hourly = refusals, prompt = AppGuideTools.Instructions(null)
        });
    }

    // What App guides add to a reply's path (docs/APP_GUIDES.md, AGENTS.md "Never add conversation latency"): the notes step's
    // wall-clock time on a full-size guide (2,000 sections, as 60 long wiki pages make), for a message about the game in front, an
    // unrelated message and while off, median and worst of 200 runs after a warm-up; and the bytes the tools and prompt add to
    // every request while App guides are on (the same every time, so prompt caches keep them).
    private static async Task<(bool Ok, object Report)> LatencyAsync(string directory, CancellationToken cancellation)
    {
        var folder = Path.Combine(directory, "latency");
        var store = new FileAppGuideStore(folder);
        var words = ("iron ore copper gold silver crystal moonstone sword shield bow arrow potion herb mushroom berry fish crab " +
            "cave mine forest river desert mountain castle village farm barn tower dungeon boss dragon golem wolf bear spider " +
            "quest reward recipe forge smelt craft brew cook trade sell buy merchant blacksmith wizard knight guard festival " +
            "winter spring summer autumn rain snow night day level skill armor helmet boots ring amulet key map chest door").Split(' ');
        var random = new Random(7);
        var chunks = Enumerable.Range(0, 2_000).Select(i => new GuideChunk($"https://fixture.example/wiki/Page_{i / 30}", $"Page {i / 30}",
            $"Section {i % 30}", string.Join(' ', Enumerable.Range(0, 140).Select(_ => words[random.Next(words.Length)])) + ".")).ToArray();
        var key = AppGuideKeys.Of(Game);
        await store.SaveGuideAsync(new AppGuide(key, Game, ["fixture.example"], DateTimeOffset.UtcNow,
            [new GuideSource("https://fixture.example/wiki/Main_Page", "Main Page", 1)], chunks), cancellation);
        using var guides = new AppGuideService(store, new NoBuilder());
        await guides.LoadAsync(cancellation);
        await guides.SetOnAsync(true, cancellation);
        var indexWatch = Stopwatch.StartNew();
        var ready = await guides.ReadyAsync(key, cancellation);
        var indexMs = indexWatch.Elapsed.TotalMilliseconds;
        guides.See(Game, "StarfallValley", game: true, fullScreen: true);
        (double Median, double Worst) Time(string message)
        {
            for (var i = 0; i < 20; i++) guides.Recall(message);
            var times = new double[200];
            for (var i = 0; i < times.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                guides.Recall(message);
                times[i] = watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            return (Math.Round(times[times.Length / 2], 3), Math.Round(times[^1], 3));
        }
        var about = Time("where do I find moonstone in the crystal cave?");
        var unrelated = Time("what should we cook for dinner tonight?");
        await guides.SetOnAsync(false, cancellation);
        var off = Time("where do I find moonstone in the crystal cave?");
        var toolBytes = AppGuideTools.Definitions.Sum(d => Encoding.UTF8.GetByteCount(d.Name + d.Description + d.ParametersJson));
        var promptBytes = Encoding.UTF8.GetByteCount(AppGuideTools.Instructions(null) ?? "");
        var ok = ready && about.Median < 10 && unrelated.Median < 10 && off.Median < 1;
        return (ok, new
        {
            ok, sections = chunks.Length, indexBuiltOffThePathMs = Math.Round(indexMs, 1),
            notesStepMs = new
            {
                messageAboutTheGameInFront = new { median = about.Median, worst = about.Worst },
                unrelatedMessage = new { median = unrelated.Median, worst = unrelated.Worst },
                appGuidesOff = new { median = off.Median, worst = off.Worst }
            },
            everyRequestWhileOn = new { toolDefinitionBytes = toolBytes, promptBytes },
            messageAboutTheAppAtMost = new { notesCharacters = GuideRecall.MaximumCharacters, sections = GuideRecall.MaximumChunks }
        });
    }

    private static object? Describe(AppGuideRecall? found) => found is null ? null : new
    {
        app = found.App, ready = found.Ready, matched = found.Matched, used = found.Used, best = Math.Round(found.Best, 3),
        notes = found.Notes is not null, ms = Math.Round(found.Took.TotalMilliseconds, 3)
    };

    private static object Build(AppGuideBuild built) => new
    {
        key = built.Key, built = built.Built, pages = built.Pages, sections = built.Chunks, bytes = built.Bytes, sites = built.Sites,
        unreadable = built.Failed, downloaded = built.Downloaded, ms = (long)built.Took.TotalMilliseconds, problem = built.Problem
    };

    // Reads nothing: the latency check keeps a guide it made itself.
    private sealed class NoBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([], [], 0, 0, "not in this check"));
    }

    /// <summary>The web client with every fetch outside loopback refused before it is sent (and counted), so nothing leaves this PC
    /// whatever the wiki reader tries.</summary>
    private sealed class LoopbackOnly(WebAccess web) : IWebSearch, IWebFetch
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
    }

    /// <summary>One HTTP server on 127.0.0.1 (NOT real sites): /search answers like DuckDuckGo's HTML search, /wiki/* is a tiny
    /// wiki about a made-up game (sections about iron ore and a sword, links to more pages, navigation to leave out), /abyss/* a
    /// second wiki found only through the search, and /robots.txt allows everything.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<string> paths = [];
        private readonly List<string> queries = [];
        private readonly Task accepting;

        internal Fixture()
        {
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync();
        }

        internal string BaseUrl { get; }
        internal IReadOnlyList<string> Queries { get { lock (paths) return [.. queries]; } }
        internal IReadOnlyList<string> Paths { get { lock (paths) return [.. paths]; } }

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                await using var stream = client.GetStream();
                var target = await ReadAsync(stream);
                var path = target.Split('?')[0];
                lock (paths)
                {
                    paths.Add(path);
                    if (path == "/search")
                        queries.Add(Uri.UnescapeDataString(target.Split("q=", 2).ElementAtOrDefault(1)?.Split('&')[0] ?? "").Replace('+', ' '));
                }
                var (type, body) = path switch
                {
                    "/robots.txt" => ("text/plain", "User-agent: *\nAllow: /\n"),
                    "/search" => ("text/html", SearchPage()),
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
                        "three moonstones and one iron bar to Orla the blacksmith; she forges it overnight.</p>")),
                    "/abyss/Main_Page" or "/abyss/" => ("text/html", Page("Moonlit Abyss Wiki",
                        "<p>The Moonlit Abyss Wiki is about the diving game Moonlit Abyss (FIXTURE).</p><h2>Oxygen</h2><p>Your tank " +
                        "holds ninety seconds of air; blue coral refills it by ten seconds.</p>")),
                    _ => ("", "")
                };
                if (type.Length == 0) await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                else
                {
                    var bytes = Encoding.UTF8.GetByteCount(body);
                    await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n" + body);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }

        private string Link(string path) => "//duckduckgo.com/l/?uddg=" + Uri.EscapeDataString(BaseUrl + path) + "&amp;rut=fixture";

        private string SearchPage() =>
            "<html><body><div class=\"results\">" +
            "<div class=\"result result--ad\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/y.js?ad=1\">Sponsored</a></div>" +
            Result("/abyss/Main_Page", "Moonlit Abyss Wiki", "The fan wiki about Moonlit Abyss.") +
            "</div></body></html>";

        private string Result(string path, string title, string snippet) =>
            $"<div class=\"result\"><h2 class=\"result__title\"><a rel=\"nofollow\" class=\"result__a\" href=\"{Link(path)}\">{title}</a></h2>" +
            $"<a class=\"result__snippet\" href=\"{Link(path)}\">{snippet}</a></div>";

        private static string Page(string title, string content) =>
            $"<html><head><title>{title} - Fixture Wiki</title><script>var tracking = 1;</script></head><body>" +
            "<nav><a href=\"/wiki/Main_Page\">Home</a> | <a href=\"/wiki/Special:RecentChanges\">Recent changes</a></nav>" +
            $"<main><article><h1>{title}</h1>{content}</article></main><footer>Fixture wiki for app_guides_check.</footer></body></html>";

        private static async Task<string> ReadAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (buffer.Count < 65_536)
            {
                if (await stream.ReadAsync(one) == 0) break;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^1] == '\n' && buffer[^2] == '\r' && buffer[^3] == '\n' && buffer[^4] == '\r') break;
            }
            var line = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n")[0].Split(' ');
            return line.Length > 1 ? line[1] : "/";
        }

        private async Task WriteAsync(NetworkStream stream, string text)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), stop.Token);
            await stream.FlushAsync(stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await accepting; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            stop.Dispose();
        }
    }
}

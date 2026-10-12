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
    private const string Question = "hey martlet, where tf do I find iron ore?";
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
            var migration = await MigrationAsync(cancellation);
            using var guides = new AppGuideService(store, new WebGuideBuilder(guard, guard), limits: limits);
            await guides.LoadAsync(cancellation);
            await guides.SetOnAsync(true, cancellation);

            // Reading up from the pages the owner gave, then from a web search alone.
            var given = await guides.BuildAsync(Game, [fixture.BaseUrl + "/wiki/Main_Page"], null, cancellation);
            var searched = await guides.BuildAsync(Other, [], null, cancellation);
            var file = Path.Combine(directory, AppGuideKeys.Of(Game) + FileAppGuideStore.GuideSuffix);

            // Martlet starts again: the library comes back from the folder; an index is built only when it can be used.
            using var again = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard), limits: limits);
            await again.LoadAsync(cancellation);
            var indexesAtStart = again.ReadyIndexes;
            var ready = await again.ReadyAsync(AppGuideKeys.Of(Game), cancellation);
            var restarted = new { loaded = again.Library.Apps.Count, on = again.Library.On, indexReady = ready, indexesAtStart, readyIndexes = again.ReadyIndexes };

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
            var onItsOwn = await OnItsOwnAsync(Path.Combine(directory, "on-its-own"), guard, limits, cancellation);
            var reading = await ReadingAsync(directory, guard, fixture.BaseUrl, cancellation);
            var loading = await IndexesAsync(directory, cancellation);
            var tools = await ToolsAsync(again, fixture, cancellation);
            var latency = await LatencyAsync(directory, cancellation);
            var research = await ResearchAsync(Path.Combine(directory, "research"), guard, fixture, limits, cancellation);
            var series = await SeriesAsync(Path.Combine(directory, "series"), guard, fixture, cancellation);

            var buildOk = given is { Built: true, Pages: >= 2, Chunks: > 0 } && File.Exists(file) && searched is { Built: true, Pages: > 0 } &&
                fixture.Queries.Any(q => q.Contains(Other, StringComparison.OrdinalIgnoreCase)) && defaults.On && defaults.ReadUpWhenStarted &&
                migration.Ok && restarted is { loaded: >= 2, on: true, indexReady: true, indexesAtStart: 0 };
            var ok = buildOk && notesOk && offer.Ok && onItsOwn.Ok && reading.Ok && loading.Ok && tools.Ok && latency.Ok && research.Ok && series.Ok;
            return new
            {
                ok,
                endpoint = fixture.BaseUrl,
                note = "Fixture wiki and search page on 127.0.0.1 (NOT real sites); the store, wiki reader, web client, chunker, index, " +
                    "notes, match, the rule for reading up on its own, tool texts and job scheduler are Martlet's own. Fetches outside " +
                    "loopback are refused before they leave this PC.",
                library = new { onByDefault = defaults.On, readUpWhenStartedByDefault = defaults.ReadUpWhenStarted, saved = migration.Report },
                build = new
                {
                    ok = buildOk,
                    fromGivenPages = Build(given), fromSearch = Build(searched), guideFileBytes = File.Exists(file) ? new FileInfo(file).Length : 0,
                    searchQueries = fixture.Queries, pagesFetched = fixture.Paths.Where(p => p.StartsWith("/wiki", StringComparison.Ordinal)
                        || p.StartsWith("/w/", StringComparison.Ordinal) || p == "/robots.txt").Distinct().ToArray(),
                    refusedOutsideLoopback = guard.Refused, restarted
                },
                notes = new { ok = notesOk, results = notes },
                offer = offer.Report,
                onItsOwn = onItsOwn.Report,
                reading = reading.Report,
                indexes = loading.Report,
                tools = tools.Report,
                latency = latency.Report,
                research = research.Report,
                series = series.Report
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
            folder, exists = Directory.Exists(folder), saved = File.Exists(Path.Combine(folder, FileAppGuideStore.LibraryFile)),
            on = library.On, readUpWhenStarted = library.ReadUpWhenStarted,
            guides = library.Apps.Count(a => a.BuiltAt is not null), pages = library.Apps.Sum(a => a.Pages), bytes = library.Apps.Sum(a => a.Bytes),
            apps = library.Apps.Select(a => new
            {
                key = a.Key, name = a.Name, programs = a.Programs, sites = a.Sites, declined = a.Declined, builtAt = a.BuiltAt,
                pages = a.Pages, sections = a.Chunks, bytes = a.Bytes, problem = a.Problem,
                file = File.Exists(Path.Combine(folder, a.Key + FileAppGuideStore.GuideSuffix)),
                brief = a.Brief is not null, briefAt = a.BriefAt, briefCharacters = a.Brief?.Length ?? 0,
                areas = a.Areas.Count, researchedAreas = a.Areas.Count(r => r.Sections > 0), researchedSections = a.Areas.Sum(r => r.Sections),
                lastAreaAt = a.Areas.Count == 0 ? (DateTimeOffset?)null : a.Areas.Max(r => r.At)
            })
        };
    }

    // Libraries saved before App guides were on by default: a saved "on": false (the owner turned them off, or used the page while
    // they were off) stays off, also after another change; one without "on" is on; one Martlet can't read starts off.
    private static async Task<(bool Ok, object Report)> MigrationAsync(CancellationToken cancellation)
    {
        async Task<bool> OnAfterLoadAsync(string? saved, bool addApp = false)
        {
            var folder = Path.Combine(Path.GetTempPath(), "martlet-app-guides-saved-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(folder);
                if (saved is not null) await File.WriteAllTextAsync(Path.Combine(folder, FileAppGuideStore.LibraryFile), saved, cancellation);
                using var guides = new AppGuideService(new FileAppGuideStore(folder), new NoBuilder());
                var before = guides.On;
                await guides.LoadAsync(cancellation);
                if (addApp) await guides.AddAsync("Paint Studio", [], [], cancellation);
                return !before && guides.On;
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        var none = await OnAfterLoadAsync(null);
        var savedOff = await OnAfterLoadAsync("{\"formatVersion\":1,\"on\":false,\"askWhenStarted\":true,\"apps\":[]}");
        var savedOffThenAdded = await OnAfterLoadAsync("{\"formatVersion\":1,\"on\":false,\"askWhenStarted\":true,\"apps\":[]}", addApp: true);
        var savedOn = await OnAfterLoadAsync("{\"formatVersion\":1,\"on\":true,\"askWhenStarted\":true,\"apps\":[]}");
        var withoutOn = await OnAfterLoadAsync("{\"formatVersion\":1,\"apps\":[]}");
        var unreadable = await OnAfterLoadAsync("{ not json");
        var ok = none && !savedOff && !savedOffThenAdded && savedOn && withoutOn && !unreadable;
        return (ok, new { ok, noLibrary = none, savedOff, savedOffThenAdded, savedOn, withoutOn, unreadable });
    }

    // Reading up with the default caps (docs/APP_GUIDES.md, "Caps"): at most 200 pages, 25 MB and 15 minutes, so the 3-page
    // fixture wiki is read whole; what was read is kept while reading goes on; Stop reading ends it and keeps what was read.
    private static async Task<(bool Ok, object Report)> ReadingAsync(string directory, LoopbackOnly guard, string baseUrl, CancellationToken cancellation)
    {
        var defaults = new GuideBuildLimits();
        var limits = defaults with { Delay = TimeSpan.FromMilliseconds(20) };
        var start = baseUrl + "/wiki/Main_Page";
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(directory, "reading")), new WebGuideBuilder(guard, guard),
            limits: limits) { KeepEvery = TimeSpan.Zero };
        await guides.LoadAsync(cancellation);
        await guides.SetOnAsync(true, cancellation);
        var words = new List<string>();
        var keptWhileReading = 0;
        var whole = await guides.BuildAsync(Game, [start], new Words(text =>
        {
            lock (words) words.Add(text);
            if (text.StartsWith("Reading page 3 ", StringComparison.Ordinal)) keptWhileReading = guides.Library.Apps.FirstOrDefault()?.Pages ?? 0;
        }), cancellation);
        var stopped = false;
        var stoppedBuild = await guides.BuildAsync(Game, [start], new Words(text =>
        {
            if (text.StartsWith("Reading page 2 ", StringComparison.Ordinal)) stopped = guides.StopReading();
        }), cancellation);
        var nothingToStop = guides.StopReading();
        string[] said;
        lock (words) said = [.. words];
        var ok = defaults is { Pages: 200, Bytes: 25_000_000, Sites: 2 } && defaults.Time == TimeSpan.FromMinutes(15) &&
            GuideBuildLimits.More is { Pages: 300, Sites: 1 } && whole is { Built: true, Pages: 3 } &&
            said.Contains("Reading page 1 of at most 200") &&
            said.Contains("Saving what it read so far") && keptWhileReading > 0 && stopped && stoppedBuild is { Built: true, Pages: 1 } &&
            !nothingToStop && AppGuideTools.Kind.TimeLimit == TimeSpan.FromMinutes(20);
        return (ok, new
        {
            ok,
            defaultCaps = new
            {
                pages = defaults.Pages?.ToString() ?? "none", bytes = defaults.Bytes?.ToString() ?? "none",
                time = defaults.Time is { } time ? BackgroundJobs.Duration(time) : "none", sites = defaults.Sites,
                delayMs = defaults.Delay.TotalMilliseconds, jobTimeLimit = AppGuideTools.Kind.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "none",
                keepEveryMinutes = AppGuideService.DefaultKeepEvery.TotalMinutes
            },
            readMoreCaps = new
            {
                pages = GuideBuildLimits.More.Pages, bytes = GuideBuildLimits.More.Bytes,
                time = GuideBuildLimits.More.Time is { } more ? BackgroundJobs.Duration(more) : "none", sites = GuideBuildLimits.More.Sites
            },
            storeBounds = new
            {
                pages = FileAppGuideStore.MaximumSources, sections = FileAppGuideStore.MaximumChunks,
                guideFileMegabytes = FileAppGuideStore.MaximumGuideBytes / 1_000_000
            },
            firstRead = Build(whole), pagesKeptWhileReading = keptWhileReading, progress = said.Distinct().ToArray(),
            stopReading = new { stoppedAtPage2 = stopped, build = Build(stoppedBuild), stopWhenIdle = nothingToStop }
        });
    }

    // Reading up on a game of a series (docs/APP_GUIDES.md, "Choosing the pages") on a MediaWiki fixture about the Starfall
    // series: the first read reads this game's pages first (its titles, then the wiki's search for its name, then their links)
    // and never another game's, up to its cap, with no model; then, as the desktop does it after a read, a stand-in Thinking pool
    // member writes the brief and chooses more pages among the titles left, with notes on what the user does now, the chosen
    // pages join the guide and the brief is written again.
    private static async Task<(bool Ok, object Report)> SeriesAsync(string directory, LoopbackOnly guard, Fixture fixture, CancellationToken cancellation)
    {
        const string sequel = "Starfall Valley 2";
        const string doing = "Screen: the player walks into an icy cave full of crystal golems.";
        var key = AppGuideKeys.Of(sequel);
        var thinker = new CannedThinker();
        var log = new List<string>();
        var pause = TimeSpan.FromMilliseconds(20);
        using var guides = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard),
            limits: new GuideBuildLimits { Delay = pause, Time = TimeSpan.FromSeconds(60), Pages = 3 })
        {
            MoreLimits = GuideBuildLimits.More with { Delay = pause, Time = TimeSpan.FromSeconds(60) }
        };
        await guides.LoadAsync(cancellation);
        await guides.SetOnAsync(true, cancellation);
        using var research = new AppGuideResearcher(guides, thinker, guard, guard, wait: (_, token) => Task.Delay(20, token))
        {
            Log = line => { lock (log) log.Add(line); }, Context = () => doing
        };

        var built = await guides.BuildAsync(sequel, [fixture.BaseUrl + Fixture.SeriesStart], null, cancellation);
        var waited = Stopwatch.StartNew();
        while ((research.Mores == 0 || research.Briefs < 2 || research.Doing is not null || guides.Building is not null) &&
            waited.Elapsed < TimeSpan.FromSeconds(60))
            await Task.Delay(20, cancellation);
        var guide = await guides.Store.LoadGuideAsync(key, cancellation);
        var titles = guide?.Sources.Select(s => s.Title).ToArray() ?? [];
        var parsed = fixture.SeriesParsed;
        // Reading more starts only once the first read let go: the first read's pages are the first ones asked for.
        var firstRead = parsed.Take(built.Pages).ToArray();
        var pageAsks = thinker.Asks.Where(a => a.Label == GuidePagePicker.Label).ToArray();
        var entry = guides.Library.Apps.FirstOrDefault(a => a.Key == key);
        string[] lines;
        lock (log) lines = [.. log];
        var private_ = new[] { "icy", "golem", "Frost", "Orla", "127.0.0.1", "http", "Starfall" };
        var logPrivate = lines.Any(line => private_.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase)));
        string[] otherGames = ["Moonstone Sword (Starfall Valley 3)", "Starfall Valley 3"];
        var ok = built is { Built: true, Pages: 3 } && built.Choice == new GuidePageChoice(8, 2, 6, 0) && built.More is { Count: 3 } &&
            firstRead.SequenceEqual(["Moonstone Sword (Starfall Valley 2)", "Ember Mines", "Frost Caves"]) &&
            !parsed.Any(p => otherGames.Contains(p) || p == "Voice cast") &&
            titles.Contains("Frost Caves") && titles.Contains("Crystal Golem") && titles.Length == 5 && entry is { Pages: 5 } &&
            pageAsks.Length >= 1 && pageAsks.All(a => a.Instructions == GuidePagePicker.Instructions) &&
            pageAsks[0].Text.Contains("What the user does now:\n" + doing, StringComparison.Ordinal) &&
            pageAsks[0].Text.Contains("Voice cast", StringComparison.Ordinal) &&
            !pageAsks.Any(a => otherGames.Any(t => a.Text.Contains(t, StringComparison.Ordinal))) &&
            research.Mores == 1 && research.Briefs >= 2 && guides.Building is null &&
            lines.Any(l => l.Contains("read more of a wiki", StringComparison.Ordinal)) && !logPrivate;
        return (ok, new
        {
            ok,
            note = "A MediaWiki fixture on 127.0.0.1 (NOT a real wiki) and a stand-in Thinking pool member with canned answers (NOT a " +
                "model); the reader, choice by titles, research job, store and index are Martlet's own.",
            app = sequel,
            firstRead = new { build = Build(built), choice = built.Choice, readInOrder = firstRead, leftToReadMore = built.More?.Count ?? 0 },
            readMore = new
            {
                pagesInTheGuide = titles, asks = pageAsks.Length, firstAskCharacters = pageAsks.FirstOrDefault()?.Text.Length ?? 0,
                firstAskHadWhatTheUserDoes = pageAsks.FirstOrDefault()?.Text.Contains(doing, StringComparison.Ordinal) == true,
                sameInstructionsEveryTime = pageAsks.All(a => a.Instructions == GuidePagePicker.Instructions),
                otherGamesNeverReadOrAsked = !parsed.Any(otherGames.Contains) && !pageAsks.Any(a => otherGames.Any(t => a.Text.Contains(t, StringComparison.Ordinal))),
                leftOutByTheMember = !parsed.Contains("Voice cast")
            },
            pagesRead = parsed,
            status = new { research.Mores, research.Briefs, research.Sections },
            log = new { lines, holdsTitlesOrLinks = logPrivate }
        });
    }

    private sealed class Words(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    // Only the indexes that can be used now stay in memory (docs/APP_GUIDES.md, "On the desktop"): none at start, the app in
    // front's always, the most recently used others up to the limit, none of them after the idle time, and none once App guides
    // stop watching. A clock moved forward stands in for the idle time.
    private static async Task<(bool Ok, object Report)> IndexesAsync(string directory, CancellationToken cancellation)
    {
        var folder = Path.Combine(directory, "indexes");
        var store = new FileAppGuideStore(folder);
        string[] apps = ["Alpha Quest", "Beta Farm", "Gamma Dive"];
        foreach (var app in apps)
        {
            var key = AppGuideKeys.Of(app);
            await store.SaveGuideAsync(new AppGuide(key, app, ["fixture.example"], DateTimeOffset.UtcNow,
                [new GuideSource($"https://fixture.example/{key}", app, 1)],
                [new GuideChunk($"https://fixture.example/{key}", app, "", $"{app} is a made-up game for app_guides_check; its iron ore is in the caves.")]),
                cancellation);
        }
        var clock = new MovedClock();
        using var guides = new AppGuideService(store, new NoBuilder(), clock: clock);
        await guides.LoadAsync(cancellation);
        var counts = new List<int> { guides.ReadyIndexes };
        guides.See(apps[0], "alphaquest", game: true, fullScreen: true);
        foreach (var app in apps) await guides.ReadyAsync(AppGuideKeys.Of(app), cancellation);
        counts.Add(guides.ReadyIndexes);
        guides.See("Notepad", "notepad", game: false, fullScreen: false);
        counts.Add(guides.ReadyIndexes);
        var keptAfterLeaving = apps.Where(a => guides.IsReady(AppGuideKeys.Of(a))).ToArray();
        guides.See(apps[0], "alphaquest", game: true, fullScreen: true);
        await guides.ReadyAsync(AppGuideKeys.Of(apps[0]), cancellation);
        counts.Add(guides.ReadyIndexes);
        clock.Ahead = AppGuideService.IndexIdleTime + TimeSpan.FromMinutes(1);
        guides.See(apps[0], "alphaquest", game: true, fullScreen: true);
        counts.Add(guides.ReadyIndexes);
        var keptWhenIdle = apps.Where(a => guides.IsReady(AppGuideKeys.Of(a))).ToArray();
        guides.Forget();
        counts.Add(guides.ReadyIndexes);
        var ok = counts.SequenceEqual([0, 3, 2, 3, 1, 0]) && keptAfterLeaving.SequenceEqual([apps[1], apps[2]]) &&
            keptWhenIdle.SequenceEqual([apps[0]]);
        return (ok, new
        {
            ok, idleMinutes = AppGuideService.IndexIdleTime.TotalMinutes, otherIndexesAtMost = AppGuideService.MaximumOtherIndexes,
            atStart = counts[0], allThreeUsed = counts[1], frontLeft = counts[2], keptAfterLeaving, frontBack = counts[3],
            afterIdleTime = counts[4], keptWhenIdle, afterWatchStopped = counts[5]
        });
    }

    private sealed class MovedClock : TimeProvider
    {
        internal TimeSpan Ahead { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Ahead;
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
        await restarted.SetReadUpWhenStartedAsync(false, cancellation);
        var askOff = restarted.Offer();
        await restarted.SetReadUpWhenStartedAsync(true, cancellation);

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
            message.Contains("- Crystal Caverns (a game)", StringComparison.Ordinal) && !message.Contains("- - ", StringComparison.Ordinal) &&
            message.Contains("you've started reading up on it", StringComparison.Ordinal) && !message.Contains("read_up_on", StringComparison.Ordinal) &&
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

    // A game without a guide comes to the front, named as Windows names an Unreal game's program: Martlet reads up on it on its own
    // (what the desktop's job does, with no model asked), under a readable name, with the program's names on its entry, so it is
    // on the list with its guide, the guide goes with a question while the game is in front, and nothing more is read for it.
    private static async Task<(bool Ok, object Report)> OnItsOwnAsync(string directory, LoopbackOnly guard, GuideBuildLimits limits,
        CancellationToken cancellation)
    {
        using var guides = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard), limits: limits);
        await guides.LoadAsync(cancellation);
        await guides.SetOnAsync(true, cancellation);
        guides.See("MoonlitAbyss", "MoonlitAbyss-Win64-Shipping", game: true, fullScreen: true);
        var due = guides.Offer();
        guides.Offered(due?.Key ?? "");
        var built = due is null ? null : await guides.BuildAsync(due.Name, [], null, cancellation, due.Programs);
        var entry = guides.Library.Apps.FirstOrDefault(a => a.Key == due?.Key);
        await guides.ReadyAsync(due?.Key ?? "", cancellation);
        var inFront = guides.Recall("how much air does my oxygen tank hold?");
        var afterRead = guides.Offer();
        // A program name with build words of its own, and a name in one word.
        string[] names = ["AbioticFactor-Win64-Shipping", "StardewValley", "ELDEN RING\u2122", "Half-Life 2", "DOOMEternal", "TESV"];
        var readable = names.ToDictionary(n => n, AppGuideService.Readable);
        var ok = due is { Name: "Moonlit Abyss", Key: "moonlit-abyss", Game: true } && built is { Built: true, Pages: > 0 } &&
            entry is { BuiltAt: not null } && entry.Programs.Contains("MoonlitAbyss-Win64-Shipping") &&
            guides.Front?.Entry?.Key == "moonlit-abyss" && inFront is { Ready: true, Notes: not null } &&
            inFront.Notes.Contains("ninety seconds", StringComparison.Ordinal) && afterRead is null &&
            readable["AbioticFactor-Win64-Shipping"] == "Abiotic Factor" && readable["StardewValley"] == "Stardew Valley" &&
            readable["ELDEN RING\u2122"] == "ELDEN RING\u2122" && readable["Half-Life 2"] == "Half-Life 2" &&
            readable["DOOMEternal"] == "DOOM Eternal" && readable["TESV"] == "TESV";
        return (ok, new
        {
            ok,
            inFront = new { program = "MoonlitAbyss", file = "MoonlitAbyss-Win64-Shipping", game = true },
            readsUpOn = due?.Name, key = due?.Key, built = built is null ? null : Build(built),
            onTheList = entry is null ? null : new { name = entry.Name, programs = entry.Programs, builtAt = entry.BuiltAt, pages = entry.Pages },
            matchedInFront = guides.Front?.Entry?.Key, notesWhileInFront = Describe(inFront), readsAgainAfterward = afterRead?.Name,
            readableNames = readable
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
        var ok = names.SequenceEqual([AppGuideTools.ReadUpName, AppGuideTools.SearchName, AppGuideTools.SkipName, AppGuideTools.BriefName]) &&
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
                timeLimit = AppGuideTools.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "none", perHour = AppGuideTools.Kind.MaxPerHour, result = first.Job.Result,
                problem = first.Job.Problem
            },
            secondWhileRunning = second.Refusal, hourly = refusals, prompt = AppGuideTools.Instructions(null)
        });
    }

    // Deeper research (docs/APP_GUIDES.md, "The brief" and "Researching where you are") with a stand-in Thinking pool member
    // (canned answers, NOT a model): the brief after a read, the brief with the user's message once, an area the guide already
    // covers, an area researched on the web (the fixture's wiki page and search snippets; Reddit's and a video's pages never
    // read), research kept when the wiki is read again, update_guide_brief's arguments and refusals, and a log without text.
    private static async Task<(bool Ok, object Report)> ResearchAsync(string directory, LoopbackOnly guard, Fixture fixture, GuideBuildLimits limits,
        CancellationToken cancellation)
    {
        var key = AppGuideKeys.Of(Game);
        var thinker = new CannedThinker();
        var log = new List<string>();
        using var guides = new AppGuideService(new FileAppGuideStore(directory), new WebGuideBuilder(guard, guard), limits: limits);
        await guides.LoadAsync(cancellation);
        await guides.SetOnAsync(true, cancellation);
        using var research = new AppGuideResearcher(guides, thinker, guard, guard, wait: (_, token) => Task.Delay(20, token))
        {
            Log = line => { lock (log) log.Add(line); }
        };

        async Task<AppGuideEntry?> WaitForAsync(Func<AppGuideEntry, bool> done)
        {
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (guides.Library.Apps.FirstOrDefault(a => a.Key == key) is { } entry && done(entry) && research.Doing is null) return entry;
                await Task.Delay(20, cancellation);
            }
            return guides.Library.Apps.FirstOrDefault(a => a.Key == key);
        }

        // A read writes the brief on its own, in the background.
        var built = await guides.BuildAsync(Game, [fixture.BaseUrl + "/wiki/Main_Page"], null, cancellation);
        var afterRead = await WaitForAsync(e => e.Brief is not null);
        var briefAsks = thinker.Asks.Count(a => a.Label == "App guide brief");

        // The brief goes with a message about the app, once per conversation.
        guides.See("STARFALL VALLEY\u2122", "StarfallValley", game: true, fullScreen: true);
        await guides.ReadyAsync(key, cancellation);
        var first = guides.Recall(Question);
        var briefNotes = GuideRecall.BriefNotes(first?.App ?? Game, first?.Brief, null);
        var again = guides.Recall(Question, briefNotes);

        // The screen shows an area the guide covers (no web), then one it doesn't (the web).
        var refusedBefore = guard.Refused;
        var searchesBefore = fixture.Queries.Count;
        var covered = await research.ResearchScreenAsync(key, "The user fights crystal golems for moonstones in an icy cave.", cancellation);
        var webArea = await research.ResearchScreenAsync(key, "A dark flooded crypt under a chapel; a drowned knight blocks the stairs.", cancellation);
        var nothing = await research.ResearchScreenAsync(key, "The title screen with a Start button.", cancellation);
        var areaQueries = fixture.Queries.Skip(searchesBefore).ToArray();
        var researched = guides.Search(key, "how do I drain the Sunken Crypt", 5) ?? [];
        var fromResearch = researched.Count(h => GuideAreaResearch.IsResearched(h.Chunk));
        var snippetKept = researched.Any(h => h.Chunk.Section == "What people say on the web" && h.Chunk.Text.Contains("Reddit", StringComparison.Ordinal));
        var redditRead = fixture.Paths.Any(p => p.Contains("reddit", StringComparison.OrdinalIgnoreCase)) || guard.Refused != refusedBefore;

        // Read again: the wiki's sections are new, the research stays, and a new brief is written.
        var briefsBefore = research.Briefs;
        var reread = await guides.BuildAsync(Game, [fixture.BaseUrl + "/wiki/Main_Page"], null, cancellation);
        var afterReread = await WaitForAsync(_ => research.Briefs > briefsBefore);
        var keptAfterReread = (guides.Search(key, "how do I drain the Sunken Crypt", 5) ?? []).Count(h => GuideAreaResearch.IsResearched(h.Chunk));

        // update_guide_brief: its arguments, the same area again within the hour, and no pool member.
        var (app, focus, problem) = AppGuideTools.ParseBrief("{\"app\":\"Starfall Valley\",\"focus\":\"Sunken  Crypt\\n\"}");
        var (_, _, badProblem) = AppGuideTools.ParseBrief("[\"Sunken Crypt\"]");
        var refreshed = await research.RefreshAsync(key, "Sunken Crypt", cancellation);
        thinker.CanThink = false;
        var noMember = research.CantStart(key);
        var notStarted = research.Start(key, null);
        thinker.CanThink = true;
        var entry = guides.Library.Apps.FirstOrDefault(a => a.Key == key);
        string[] lines;
        lock (log) lines = [.. log];
        var private_ = new[] { "Sunken", "Crypt", "Frost", "moonstone", "drowned", "127.0.0.1", "http", "reddit" };
        var logPrivate = lines.Any(line => private_.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase)));
        var asks = thinker.Asks;
        var stableStart = asks.Where(a => a.Label == "App guide brief").All(a => a.Instructions == GuideBrief.Instructions) &&
            asks.Where(a => a.Label == "App guide area").All(a => a.Instructions == GuideAreaResearch.ClueInstructions) &&
            asks.Where(a => a.Label == "App guide area notes").All(a => a.Instructions == GuideAreaResearch.SummaryInstructions);

        var ok = built.Built && afterRead?.Brief?.StartsWith("What it is:", StringComparison.Ordinal) == true && afterRead.BriefAt is not null &&
            briefAsks >= 1 && first?.Brief is not null && briefNotes?.StartsWith(GuideBrief.Label, StringComparison.Ordinal) == true &&
            again?.Brief is null &&
            covered is { Area: "Frost Caves", Web: false, Sections: 0, Problem: null } &&
            webArea is { Area: "Sunken Crypt", Web: true, Sections: > 0, Problem: null } && nothing.Area is null &&
            areaQueries.Any(q => q.Contains("reddit", StringComparison.OrdinalIgnoreCase)) && areaQueries.Length <= GuideAreaResearch.Queries(Game, "x").Count &&
            fromResearch > 0 && snippetKept && !redditRead &&
            reread.Built && afterReread?.BriefAt > afterRead.BriefAt && keptAfterReread > 0 &&
            app == Game && focus == "Sunken Crypt" && problem is null && badProblem is not null &&
            refreshed is { Web: false, Brief: true } && noMember is not null && notStarted == noMember &&
            entry is { Areas.Count: 2 } && entry.Areas.Any(a => a.Name == "Sunken Crypt" && a.Sections > 0) &&
            !logPrivate && stableStart && asks.All(a => a.Text.Length <= Math.Max(GuideBrief.InputCharacters, GuideAreaResearch.SummaryInputCharacters) + 200);
        return (ok, new
        {
            ok,
            note = "A stand-in Thinking pool member gives canned answers (NOT a model); the guide, web reading, research, store and " +
                "notes are Martlet's own, against the 127.0.0.1 fixture.",
            briefAfterRead = new { written = afterRead?.Brief is not null, characters = afterRead?.Brief?.Length ?? 0, briefAt = afterRead?.BriefAt, requests = briefAsks },
            briefWithMessage = new { first = first?.Brief is not null, notesCharacters = briefNotes?.Length ?? 0, sentAgain = again?.Brief is not null },
            screen = new
            {
                areaTheGuideCovers = Result(covered), areaOnTheWeb = Result(webArea), noArea = Result(nothing),
                searches = areaQueries, sectionsFoundForTheArea = fromResearch, redditSnippetKept = snippetKept, redditOrVideoPageRead = redditRead
            },
            readAgain = new { built = reread.Built, researchSectionsKept = keptAfterReread, newBrief = afterReread?.BriefAt > afterRead?.BriefAt },
            updateGuideBrief = new { app, focus, problem, badArguments = badProblem, sameAreaWithinTheHour = Result(refreshed), noPoolMember = noMember, startWithoutMember = notStarted },
            saved = entry is null ? null : new { briefCharacters = entry.Brief?.Length ?? 0, areas = entry.Areas.Select(a => new { a.Name, a.Sections }) },
            status = new { research.Briefs, research.Areas, research.WebAreas, research.Sections, research.Screens, research.Mores },
            thinkingRequests = asks.GroupBy(a => a.Label).Select(g => new { label = g.Key, count = g.Count(), maxInputCharacters = g.Max(a => a.Text.Length) }),
            sameInstructionsEveryTime = stableStart,
            log = new { lines, holdsAreaOrLinks = logPrivate },
            caps = new
            {
                AppGuideResearcher.BriefsPerHour, AppGuideResearcher.AreasPerHour, screenEveryMinutes = AppGuideResearcher.DefaultScreenEvery.TotalMinutes,
                areaAgainHours = AppGuideResearcher.AreaAgain.TotalHours, GuideAreaResearch.MaximumPages, GuideAreaResearch.MaximumSections,
                briefCharacters = GuideBrief.MaximumCharacters
            }
        });
    }

    private static object Result(GuideResearchResult result) => new
    {
        area = result.Area, web = result.Web, sections = result.Sections, brief = result.Brief, problem = result.Problem,
        ms = (long)result.Took.TotalMilliseconds
    };

    /// <summary>A stand-in Thinking pool member with canned answers (NOT a model): a brief, the area a screen note shows, and notes
    /// on what was read about an area.</summary>
    private sealed class CannedThinker : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink { get; set; } = true;
        internal IReadOnlyList<GuideThinkingAsk> Asks { get { lock (asks) return [.. asks]; } }

        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
        {
            lock (asks) asks.Add(ask);
            string answer = ask.Label switch
            {
                "App guide brief" => "What it is: Starfall Valley is a farming game with mines and caves.\nCore loop: Farm by day, mine " +
                    "ore and fight in the caves.\nMain mechanics: Seasons last 28 days; smelt ore into bars.\nEarly tips: Bring a " +
                    "steel pickaxe to the Ember Mines.\nCommon mistakes: Crops die when the season changes." +
                    (ask.Text.Contains("Focus:", StringComparison.Ordinal) ? "\nAbout the focus: see the research notes." : ""),
                "App guide area" when ask.Text.Contains("crypt", StringComparison.OrdinalIgnoreCase) => "AREA: Sunken Crypt",
                "App guide area" when ask.Text.Contains("golem", StringComparison.OrdinalIgnoreCase) => "AREA: **Frost Caves**",
                "App guide area" => "AREA: NONE",
                "App guide area notes" when ask.Text.Contains("valve", StringComparison.OrdinalIgnoreCase) =>
                    "The Sunken Crypt is a flooded dungeon under the chapel.\nTurn the three bronze valves to drain it.\nDrowned knights " +
                    "are weak to fire: bring torches.\nLeave before midnight.",
                // Every page but the voice cast, the pages about what the user does now (an icy cave) first.
                GuidePagePicker.Label => string.Join(", ", ask.Text.Split('\n').Select(line => line.Split(". ", 2))
                    .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && !p[1].Contains("Voice", StringComparison.Ordinal))
                    .OrderByDescending(p => ask.Text.Contains("icy cave", StringComparison.Ordinal) && p[1].Contains("Frost", StringComparison.Ordinal))
                    .Select(p => p[0])) is { Length: > 0 } numbers ? numbers : "NONE",
                _ => "NONE"
            };
            return Task.FromResult<(string?, string?)>((answer, null));
        }
    }

    // What App guides add to a reply's path (docs/APP_GUIDES.md, AGENTS.md "Never add conversation latency"): the notes step's
    // wall-clock time on a guide of 2,000 sections (60 long wiki pages) and on one of 25,000 (a whole wiki of a few thousand
    // pages), for a message about the game in front, an unrelated message and while off, median and worst of 200 runs after a
    // warm-up; and the bytes the tools and prompt add to every request while App guides are on (the same every time, so prompt
    // caches keep them).
    private static async Task<(bool Ok, object Report)> LatencyAsync(string directory, CancellationToken cancellation)
    {
        var small = await NotesTimeAsync(Path.Combine(directory, "latency"), 2_000, cancellation);
        var whole = await NotesTimeAsync(Path.Combine(directory, "latency-whole"), 25_000, cancellation);
        var toolBytes = AppGuideTools.Definitions.Sum(d => Encoding.UTF8.GetByteCount(d.Name + d.Description + d.ParametersJson));
        var promptBytes = Encoding.UTF8.GetByteCount(AppGuideTools.Instructions(null) ?? "");
        var ok = small.Ok && whole.Ok;
        return (ok, new
        {
            ok, sections = small.Report, wholeWiki = whole.Report,
            everyRequestWhileOn = new { toolDefinitionBytes = toolBytes, promptBytes },
            messageAboutTheAppAtMost = new { notesCharacters = GuideRecall.MaximumCharacters, sections = GuideRecall.MaximumChunks }
        });
    }

    private static async Task<(bool Ok, object Report)> NotesTimeAsync(string folder, int sections, CancellationToken cancellation)
    {
        var store = new FileAppGuideStore(folder);
        var words = ("iron ore copper gold silver crystal moonstone sword shield bow arrow potion herb mushroom berry fish crab " +
            "cave mine forest river desert mountain castle village farm barn tower dungeon boss dragon golem wolf bear spider " +
            "quest reward recipe forge smelt craft brew cook trade sell buy merchant blacksmith wizard knight guard festival " +
            "winter spring summer autumn rain snow night day level skill armor helmet boots ring amulet key map chest door").Split(' ');
        var random = new Random(7);
        var chunks = Enumerable.Range(0, sections).Select(i => new GuideChunk($"https://fixture.example/wiki/Page_{i / 30}", $"Page {i / 30}",
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
        var ok = ready && about.Median < 10 && unrelated.Median < 10 && off.Median < 1;
        return (ok, new
        {
            ok, sections = chunks.Length, indexBuiltOffThePathMs = Math.Round(indexMs, 1),
            notesStepMs = new
            {
                messageAboutTheGameInFront = new { median = about.Median, worst = about.Worst },
                unrelatedMessage = new { median = unrelated.Median, worst = unrelated.Worst },
                appGuidesOff = new { median = off.Median, worst = off.Worst }
            }
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
                if (type.Length == 0) await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                else
                {
                    var bytes = Encoding.UTF8.GetByteCount(body);
                    await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n" + body);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
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

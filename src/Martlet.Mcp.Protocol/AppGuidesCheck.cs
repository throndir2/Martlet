using System.Diagnostics;
using System.IO;
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
internal static partial class AppGuidesCheck
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
            var readMore = await ReadMoreAsync(Path.Combine(directory, "more"), guard, fixture, cancellation);

            var buildOk = given is { Built: true, Pages: >= 2, Chunks: > 0 } && File.Exists(file) && searched is { Built: true, Pages: > 0 } &&
                fixture.Queries.Any(q => q.Contains(Other, StringComparison.OrdinalIgnoreCase)) && defaults.On && defaults.ReadUpWhenStarted &&
                migration.Ok && restarted is { loaded: >= 2, on: true, indexReady: true, indexesAtStart: 0 };
            var ok = buildOk && notesOk && offer.Ok && onItsOwn.Ok && reading.Ok && loading.Ok && tools.Ok && latency.Ok && research.Ok && series.Ok && readMore.Ok;
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
                series = series.Report,
                readMore = readMore.Report
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
            said.Contains("Saving what it read so far") && keptWhileReading > 0 && stopped && stoppedBuild is { Built: true, Pages: 3 } &&
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
            // Stopped at page 2 of Read again: the page read again and the guide's 2 other pages are kept (3 pages).
            stopReading = new { stoppedAtPage2 = stopped, build = Build(stoppedBuild), stopWhenIdle = nothingToStop }
        });
    }

    // Read more (docs/APP_GUIDES.md, "Reading more"), as the Read more buttons and asking Martlet do it, with no Thinking pool
    // member: after Martlet starts again it reads more pages of the same wiki from what the guide file kept, each time adding
    // pages the guide doesn't have; a guide kept before Martlet saved that finds its wiki again from its pages; Read again keeps
    // the pages Read more added; and the user's words that ask to read more are told apart from others.
    private static async Task<(bool Ok, object Report)> ReadMoreAsync(string directory, LoopbackOnly guard, Fixture fixture, CancellationToken cancellation)
    {
        const string sequel = "Starfall Valley 2";
        var key = AppGuideKeys.Of(sequel);
        var pause = TimeSpan.FromMilliseconds(20);
        var start = fixture.BaseUrl + Fixture.SeriesStart;
        AppGuideService Service(string folder) => new(new FileAppGuideStore(folder), new WebGuideBuilder(guard, guard),
            limits: new GuideBuildLimits { Delay = pause, Time = TimeSpan.FromSeconds(60), Pages = 2 })
        {
            MoreLimits = GuideBuildLimits.More with { Delay = pause, Time = TimeSpan.FromSeconds(60), Pages = 2 }
        };
        async Task<string[]> PagesAsync(AppGuideService guides) =>
            (await guides.Store.LoadGuideAsync(key, cancellation))?.Sources.Select(s => s.Url).ToArray() ?? [];

        AppGuideBuild first;
        bool savedState;
        using (var guides = Service(directory))
        {
            await guides.LoadAsync(cancellation);
            await guides.SetOnAsync(true, cancellation);
            first = await guides.BuildAsync(sequel, [start], null, cancellation);
            savedState = (await guides.Store.LoadGuideAsync(key, cancellation))?.More is { Related.Count: > 0 };
        }
        // Martlet starts again: no GuideMore in memory, no Thinking pool member.
        using var again = Service(directory);
        await again.LoadAsync(cancellation);
        var once = await again.ReadMoreAsync(key, cancellationToken: cancellation);
        var afterOnce = await PagesAsync(again);
        var twice = await again.ReadMoreAsync(key, cancellationToken: cancellation);
        var afterTwice = await PagesAsync(again);
        var readAgain = await again.BuildAsync(sequel, [start], null, cancellation);
        var afterReadAgain = await PagesAsync(again);

        // A guide kept before Martlet saved what is left to read more: the wiki is found again from its pages.
        var oldFolder = Path.Combine(directory, "old");
        using var old = Service(oldFolder);
        await old.LoadAsync(cancellation);
        await old.SetOnAsync(true, cancellation);
        await old.BuildAsync(sequel, [start], null, cancellation);
        if (await old.Store.LoadGuideAsync(key, cancellation) is { } kept) await old.Store.SaveGuideAsync(kept with { More = null }, cancellation);
        var fromPages = await old.ReadMoreAsync(key, cancellationToken: cancellation);
        var oldPages = await PagesAsync(old);

        var words = new Dictionary<string, string?>
        {
            ["can you read more about it?"] = AppGuideTools.AskedToReadMore("can you read more about it?"),
            ["learn more about Starfall Valley 2 please"] = AppGuideTools.AskedToReadMore("learn more about Starfall Valley 2 please"),
            ["keep reading up on this game"] = AppGuideTools.AskedToReadMore("keep reading up on this game"),
            ["don't read more about it"] = AppGuideTools.AskedToReadMore("don't read more about it"),
            ["I read more books this year"] = AppGuideTools.AskedToReadMore("I read more books this year")
        };
        var shownWords = words.ToDictionary(w => w.Key, w => w.Value == AppGuideTools.InFront ? "the app in front" : w.Value);
        var ready = AppGuideTools.MoreReady(once, afterOnce.Length);
        var wordsOk = words["can you read more about it?"] == AppGuideTools.InFront && words["learn more about Starfall Valley 2 please"] == sequel &&
            words["keep reading up on this game"] == AppGuideTools.InFront && words["don't read more about it"] is null &&
            words["I read more books this year"] is null;
        var ok = first is { Built: true, Pages: 2 } && savedState &&
            once is { Built: true, Pages: > 0 } && afterOnce.Length == 2 + once.Pages && afterOnce.Distinct().Count() == afterOnce.Length &&
            twice is { Built: true, Pages: > 0 } && afterTwice.Length == afterOnce.Length + twice.Pages && afterTwice.Distinct().Count() == afterTwice.Length &&
            readAgain.Built && afterReadAgain.Length >= afterTwice.Length && afterTwice.All(afterReadAgain.Contains) &&
            fromPages is { Built: true, Pages: > 0 } && oldPages.Length == 2 + fromPages.Pages &&
            ready.Contains("more page", StringComparison.Ordinal) && wordsOk && again.Building is null;
        return (ok, new
        {
            ok,
            note = "A MediaWiki fixture on 127.0.0.1 (NOT a real wiki) and no Thinking pool member: the most linked titles are read " +
                "with no model. The reader, store and service are Martlet's own.",
            firstRead = Build(first), savedWhatIsLeft = savedState,
            afterRestart = new
            {
                readMore = Build(once), guidePages = afterOnce.Length, readMoreAgain = Build(twice), guidePagesAgain = afterTwice.Length,
                noPageTwice = afterTwice.Distinct().Count() == afterTwice.Length
            },
            readAgain = new { build = Build(readAgain), guidePages = afterReadAgain.Length, keptReadMorePages = afterTwice.All(afterReadAgain.Contains) },
            guideKeptWithoutState = new { readMore = Build(fromPages), guidePages = oldPages.Length },
            words = shownWords,
            toolResult = ready
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
}

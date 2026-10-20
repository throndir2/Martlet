using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Mcp;

internal static partial class AppGuidesCheck
{
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
}

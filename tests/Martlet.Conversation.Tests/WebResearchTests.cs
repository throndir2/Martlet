using System.Net;
using System.Text;
using Martlet.Conversation;
using Martlet.Core.Creations;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class WebResearchTests
{
    private const string SearchHtml =
        "<div><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/y.js?ad=1\">Ad</a>" +
        "<h2 class=\"result__title\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fen.wikipedia.org%2Fwiki%2FMartlet&amp;rut=abc\">Martlet - <b>Wikipedia</b></a></h2>" +
        "<a class=\"result__snippet\" href=\"//duckduckgo.com/l/?uddg=x\">A martlet is a <b>heraldic</b> bird.</a>" +
        "<a class=\"result__a\" href=\"javascript:alert(1)\">Bad</a>" +
        "<a class=\"result__a\" href=\"https://example.org/swift\">Swifts</a></div>";

    [Fact]
    public void SearchResultsSkipAdsUnwrapRedirectsAndKeepSnippets()
    {
        var results = WebAccess.ParseResults(SearchHtml);
        Assert.Equal(2, results.Count);
        Assert.Equal("https://en.wikipedia.org/wiki/Martlet", results[0].Url);
        Assert.Equal("Martlet - Wikipedia", results[0].Title);
        Assert.Equal("A martlet is a heraldic bird.", results[0].Snippet);
        Assert.Equal("https://example.org/swift", results[1].Url);
    }

    [Fact]
    public void PageTextLeavesOutScriptsNavigationAndMarkup()
    {
        const string html = "<html><head><title>Cats &amp; toys</title><script>var x=1;</script></head><body><nav>Menu</nav>" +
            "<h1>Wand toys</h1><p>Cats <b>love</b> them.</p><style>p{}</style><footer>(c)</footer></body></html>";
        Assert.Equal("Wand toys\nCats love them.", HtmlText.Of(html));
        Assert.Equal("Cats & toys", HtmlText.Title(html));
        var article = "<body><div class=\"menu\">Menu items</div><main><p>" + new string('w', 600) + "</p></main><aside>Sidebar</aside></body>";
        Assert.Equal(new string('w', 600), HtmlText.Of(article));
    }

    [Theory]
    [InlineData("93.184.215.14", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.8", false)]
    [InlineData("172.31.255.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd12::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void OnlyPublicAddressesAreReachable(string address, bool expected) =>
        Assert.Equal(expected, WebAccess.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void StepsAreReadAsSearchReadOrReport()
    {
        var more = WebResearch.ParseStep("SEARCH: cat toys safety\nREAD: https://a.example/1\nREAD: <https://b.example/2>");
        Assert.Equal("cat toys safety", more.Search);
        Assert.Equal(["https://a.example/1", "https://b.example/2"], more.Read);
        Assert.Null(more.Report);
        Assert.Null(more.Notes);
        var done = WebResearch.ParseStep("**TITLE:** Cat toys\nSUMMARY: Wands win.\nREPORT:\n## Findings\nWands [1].");
        Assert.Equal("Cat toys", done.Title);
        Assert.Equal("Wands win.", done.Summary);
        Assert.Equal("## Findings\nWands [1].", done.Report);
        Assert.Equal("Just an answer.", WebResearch.ParseStep("Just an answer.").Report);
    }

    [Fact]
    public void NotesRunUntilTheNextFieldInCapitals()
    {
        var step = WebResearch.ParseStep("**NOTES:**\n- Wands mimic prey [1]\n- Title: The Cat Book is a guide [2]\n\n- search: lasers too [3]\n" +
            "SEARCH: laser toys\nREAD: https://a.example/1");
        Assert.Equal("- Wands mimic prey [1]\n- Title: The Cat Book is a guide [2]\n\n- search: lasers too [3]", step.Notes);
        Assert.Equal("laser toys", step.Search);
        Assert.Equal(["https://a.example/1"], step.Read);
        Assert.Null(step.Report);
        var report = WebResearch.ParseStep("NOTES:\n- A [1]\nTITLE: T\nSUMMARY: S.\nREPORT:\nBody [1].");
        Assert.Equal("- A [1]", report.Notes);
        Assert.Equal("Body [1].", report.Report);
        var onlyNotes = WebResearch.ParseStep("NOTES:\n- A [1]");
        Assert.Equal("- A [1]", onlyNotes.Notes);
        Assert.Null(onlyNotes.Report);
        Assert.Null(onlyNotes.Search);
    }

    [Fact]
    public void ArgumentsNeedATopicAndDefaultWhatToFindToIt()
    {
        Assert.NotNull(WebResearch.Parse("{}").Problem);
        Assert.NotNull(WebResearch.Parse("not json").Problem);
        var (arguments, _) = WebResearch.Parse("{\"topic\":\"  rust\\u0007 async \"}");
        Assert.Equal("rust  async", arguments!.Topic);
        Assert.Equal(arguments.Topic, arguments.Find);
    }

    [Fact]
    public void WebResearchIsOnByDefaultAndNeedsThinkingLonger()
    {
        Assert.True(new ThinkLongerSettings().Researches);
        Assert.False(new ThinkLongerSettings { WebResearch = false }.Researches);
        Assert.False(new ThinkLongerSettings { Enabled = false }.Researches);
        Assert.Null(ThinkLongerSettings.Normalize(new ThinkLongerSettings { WebResearch = true }));
        Assert.False(ThinkLongerSettings.Normalize(new ThinkLongerSettings { WebResearch = false })!.WebResearch);
    }

    [Fact]
    public void AJobResearchesLikeACarefulPersonWithNoTimeOrHourlyLimit()
    {
        Assert.Equal(1, WebResearch.Kind.MaxActive);
        Assert.Null(WebResearch.Kind.MaxPerHour);
        Assert.Null(WebResearch.Kind.TimeLimit);
        Assert.Equal(60, WebResearch.Budget.Pages);
        Assert.Equal(30, WebResearch.Budget.Searches);
        Assert.Equal(40, WebResearch.Budget.Steps);
        WebResearch.Kind.Validate();
    }

    [Fact]
    public async Task TheLoopSearchesReadsAsksAgainAndWritesTheReportWithSources()
    {
        var web = new FakeWeb();
        var tasks = new List<string>();
        var answers = new Queue<string>(["SEARCH: second query\nREAD: https://pages.example/extra", "TITLE: Found it\nSUMMARY: Yes.\nREPORT:\nIt is so [1]."]);
        var (outcome, run, job) = await RunAsync(web, task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(answers.Dequeue());
        });
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        var report = outcome.Report!;
        Assert.Equal("Found it", report.Title);
        Assert.Equal("Yes.", report.Summary);
        Assert.Contains("## Sources\n1. [Page a](https://pages.example/a)", report.Markdown, StringComparison.Ordinal);
        Assert.Equal(["topic words", "second query"], web.Queries);
        Assert.Equal(2, run.Searches);
        Assert.Equal(2, run.Steps);
        // The first search's top 3 (one blocked), then the page asked for, then 2 untried results of the second search.
        Assert.Equal(1, run.Failures);
        Assert.Equal(5, run.Pages);
        Assert.Contains("https://pages.example/extra", web.Fetched);
        Assert.Contains("[1] Page a", tasks[0], StringComparison.Ordinal);
        Assert.Contains("What they want to find out: what to find", tasks[0], StringComparison.Ordinal);
        Assert.DoesNotContain("This is the last step", tasks[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotesCarryOverAndOnlyPagesReadSinceAreNew()
    {
        var web = new FakeWeb();
        var tasks = new List<string>();
        var answers = new Queue<string>(["NOTES:\n- Fact from a [1]\n- Fact from c [2]\nSEARCH: second query", "TITLE: T\nSUMMARY: S.\nREPORT:\nSo [1] [3]."]);
        var (outcome, run, _) = await RunAsync(web, task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(answers.Dequeue());
        });
        Assert.NotNull(outcome.Report);
        Assert.Contains("Your notes so far:\nNone yet.", tasks[0], StringComparison.Ordinal);
        Assert.Contains("[2] Page c <https://pages.example/c>", tasks[0], StringComparison.Ordinal);
        Assert.Contains("Your notes so far:\n- Fact from a [1]\n- Fact from c [2]", tasks[1], StringComparison.Ordinal);
        Assert.DoesNotContain("[1] Page a <", tasks[1], StringComparison.Ordinal);
        Assert.DoesNotContain("[2] Page c <", tasks[1], StringComparison.Ordinal);
        Assert.Contains("[3] Page 1-0 <https://pages.example/1-0>", tasks[1], StringComparison.Ordinal);
        Assert.Contains("[5] Page 1-2 <https://pages.example/1-2>", tasks[1], StringComparison.Ordinal);
        Assert.Contains("- Result 1-3 <https://pages.example/1-3>", tasks[1], StringComparison.Ordinal);
        Assert.Contains("Searches so far (2 of at most 30): topic words | second query", tasks[1], StringComparison.Ordinal);
        Assert.Equal("- Fact from a [1]\n- Fact from c [2]".Length, run.NotesCharacters);
    }

    [Fact]
    public async Task NotesMuchShorterThanBeforeAreAddedToThemAndAPageNotNotedIsKept()
    {
        var web = new FakeWeb();
        var tasks = new List<string>();
        var earlier = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"- Earlier fact number {i} from the first pages [1]"));
        var answers = new Queue<string>([$"NOTES:\n{earlier}\nSEARCH: second query", "NOTES:\n- New fact [3]\nSEARCH: third query",
            "SEARCH: fourth query", "REPORT:\nDone."]);
        var (outcome, _, _) = await RunAsync(web, task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(answers.Dequeue());
        });
        Assert.NotNull(outcome.Report);
        Assert.Contains("- Earlier fact number 30 from the first pages [1]\n- New fact [3]", tasks[2], StringComparison.Ordinal);
        // The third step wrote no notes: the pages it saw go into them as their title and first words.
        Assert.Contains("- New fact [3]\n- [6] Page 2-0: Readable text of the page", tasks[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatStopsWorkingKeepsItsNotesAsTheReport()
    {
        var calls = 0;
        var (outcome, run, job) = await RunAsync(new FakeWeb(), _ => ++calls == 1
            ? BackgroundJobOutcome.Done("NOTES:\n- Fact from a [1]\nSEARCH: second query")
            : BackgroundJobOutcome.Failed("the Thinking model couldn't do it right now"));
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        Assert.Equal(3, run.Steps);
        var report = outcome.Report!;
        Assert.Contains("- Fact from a [1]", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("couldn't be written", report.Markdown, StringComparison.Ordinal);
        Assert.Equal("Only the notes from 5 pages: the full report couldn't be written.", report.Summary);
        Assert.Contains("5. [Page 1-2](https://pages.example/1-2)", report.Markdown, StringComparison.Ordinal);
        // Without notes a model that stops working fails the job, as before.
        (outcome, _, job) = await RunAsync(new FakeWeb(), _ => BackgroundJobOutcome.Failed("the Thinking model couldn't do it right now"));
        Assert.Equal(BackgroundJobState.Failed, job.State);
        Assert.Equal("the Thinking model couldn't do it right now", outcome.Problem);
    }

    [Fact]
    public async Task AJobCanReadItsWholeBudgetAndEachTaskStaysUnderItsBound()
    {
        var web = new FakeWeb { Text = new string('猫', 20_000) };
        var tasks = new List<string>();
        var (outcome, run, _) = await RunAsync(web, task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(task.Contains("This is the last step", StringComparison.Ordinal) ? "REPORT:\nDone." : $"SEARCH: again {tasks.Count}");
        });
        Assert.NotNull(outcome.Report);
        Assert.Equal(WebResearch.Budget.Pages, run.Pages);
        Assert.Equal(WebResearch.Budget.Pages, outcome.Report!.Sources.Count);
        Assert.True(run.Searches <= WebResearch.Budget.Searches);
        Assert.True(run.Steps <= WebResearch.Budget.Steps);
        Assert.True(run.NotesCharacters <= WebResearch.Budget.NotesCharacters);
        // Once no more pages can be read, the model is told to write the report, and the next step is the last.
        Assert.Contains("no more can be read", tasks[^2], StringComparison.Ordinal);
        Assert.Contains("This is the last step", tasks[^1], StringComparison.Ordinal);
        Assert.All(tasks, task => Assert.True(Encoding.UTF8.GetByteCount(task) <= WebResearch.MaxTaskBytes));
    }

    [Fact]
    public async Task ARepeatedSearchFindsNothingNewSoTheNextStepIsTheLast()
    {
        var tasks = new List<string>();
        var (outcome, run, _) = await RunAsync(new FakeWeb(), task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(task.Contains("This is the last step", StringComparison.Ordinal) ? "REPORT:\nDone." : "SEARCH: again");
        });
        Assert.NotNull(outcome.Report);
        Assert.Equal(3, run.Steps);
        Assert.Equal(2, run.Searches);
    }

    [Fact]
    public async Task AFailedFirstSearchOrNoResultsFailsTheJob()
    {
        var (outcome, _, _) = await RunAsync(new FakeWeb { Broken = true }, _ => BackgroundJobOutcome.Done("REPORT: x"));
        Assert.Contains("web search didn't work", outcome.Problem, StringComparison.Ordinal);
        (outcome, _, _) = await RunAsync(new FakeWeb { Results = 0 }, _ => BackgroundJobOutcome.Done("REPORT: x"));
        Assert.Contains("found nothing", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ResearchKeepsTenTimesMoreAndSaysWhereTextWasCut()
    {
        Assert.Equal((80, 4_000_000, 200_000), (WebAccess.MaxResults, WebAccess.MaxPageBytes, WebAccess.MaxPageCharacters));
        Assert.Equal((3_000, 10_000, 400_000), (WebResearch.MaxTopicCharacters, WebResearch.MaxFindCharacters, WebResearch.MaxReportCharacters));
        Assert.Equal(1_310_720, ResearchReports.MaximumReportBytes);
        // Each step shows about three times more of the pages and notes, as much as the place it thinks on takes.
        Assert.Equal((15_000, 15_000, 10_000), (WebResearch.Budget.NotesCharacters, WebResearch.Budget.FreshCharacters, WebResearch.Budget.ExcerptCharacters));
        Assert.Equal((WebResearch.MinTaskBytes, 15_360, WebResearch.MaxTaskBytes),
            (WebResearch.TaskBytes(1_000), WebResearch.TaskBytes(6_144), WebResearch.TaskBytes(16_384)));
        var pages = new List<WebPage>
        {
            new("https://a.example/", "A", new string('a', 5_000), 4_000_000, Cut: true), new("https://b.example/", "B", "Short page.", 11)
        };
        // A page shown only in part says so to the model.
        var sources = WebResearch.Sources(["q"], [], pages, 0, [], WebResearch.Budget, 2_000);
        Assert.Contains("a…" + WebResearch.ExcerptCut, sources, StringComparison.Ordinal);
        Assert.EndsWith("[2] B <https://b.example/>\nShort page.", sources, StringComparison.Ordinal);
        // The report says where it was cut and which pages were read only in part.
        var report = WebResearch.Report(WebResearch.ParseStep("REPORT:\n" + new string('x', WebResearch.MaxReportCharacters + 10)), new("t", "f"), pages);
        Assert.Contains(WebResearch.ReportCut, report.Markdown, StringComparison.Ordinal);
        Assert.Contains("1. [A](https://a.example/)" + WebResearch.SourceCut + "\n", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("2. [B](https://b.example/)\n", report.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportPageLinksOnlyWebAddressesAndEscapesTheRest()
    {
        var html = ResearchReports.Html("A <title>", "# Heading\n- **bold** [site](https://a.example/x)\n[bad](javascript:alert(1))\n<script>x</script> see https://b.example/y.");
        Assert.Contains("<h1>Heading</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://a.example/x\" rel=\"noreferrer noopener\">site</a>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://b.example/y\"", html, StringComparison.Ordinal);
        Assert.Contains("</a>.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:alert(1)\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("A &lt;title&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportIsKeptAsACreationAndShownAsAPage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-research-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var registry = new CreationRegistry();
            registry.Register(ResearchReports.Kind);
            var pages = new List<WebPage> { new("https://a.example/", "A", "text", 4) };
            var report = WebResearch.Report(WebResearch.ParseStep("TITLE: T\nSUMMARY: S.\nREPORT:\n" + new string('x', 20_000)), new("t", "f"), pages);
            var creation = await CreationStore.AddAsync(directory, ResearchReports.Draft(report, new CreationAuthor { Device = "test" }), registry,
                DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(Encoding.UTF8.GetByteCount(creation.Text!) <= CreationLibrary.MaximumTextUtf8Bytes);
            string? opened = null;
            using var none = System.Text.Json.JsonDocument.Parse("{}");
            var shown = await ResearchReports.Handler(directory, path => opened = path)
                .PerformAsync(new(creation, none.RootElement.Clone(), CreationStore.Assets(directory, creation)), CancellationToken.None);
            Assert.False(shown.IsError);
            Assert.Contains("<a href=\"https://a.example/\"", await File.ReadAllTextAsync(opened!), StringComparison.Ordinal);
            var ready = WebResearch.Ready(report, creation.Key);
            Assert.Contains(creation.Key, ready, StringComparison.Ordinal);
            Assert.Contains("perform_creation", ready, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task EachTaskFitsThePlaceItThinksOn()
    {
        var tasks = new List<string>();
        var (outcome, _, _) = await RunAsync(new FakeWeb { Text = new string('猫', 20_000) }, task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(tasks.Count >= 4 ? "REPORT:\nDone." : $"SEARCH: again {tasks.Count}");
        }, () => WebResearch.TaskBytes(6_144));
        Assert.NotNull(outcome.Report);
        Assert.All(tasks, task => Assert.True(Encoding.UTF8.GetByteCount(task) <= 15_360));
        Assert.Contains(WebResearch.ExcerptCut, tasks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolTakesADepthAndFullIsTheDefault()
    {
        Assert.Equal(ResearchDepth.Full, WebResearch.Parse("""{"topic":"t","what_to_find":"f"}""").Arguments!.Depth);
        Assert.Equal(ResearchDepth.Quick, WebResearch.Parse("""{"topic":"t","what_to_find":"f","depth":"quick"}""").Arguments!.Depth);
        Assert.Equal(ResearchDepth.Deep, WebResearch.Parse("""{"topic":"t","what_to_find":"f","depth":" Deep "}""").Arguments!.Depth);
        Assert.Equal(ResearchDepth.Full, WebResearch.Parse("""{"topic":"t","what_to_find":"f","depth":"whatever"}""").Arguments!.Depth);
        using var parameters = System.Text.Json.JsonDocument.Parse(WebResearch.ParametersJson);
        var depth = parameters.RootElement.GetProperty("properties").GetProperty("depth");
        Assert.Equal(["quick", "full", "deep"], depth.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("depth", parameters.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        // The reply's prompt says when to answer from what Martlet knows, search a guide, look up quickly or research.
        var prompt = WebResearch.Instructions(null)!;
        Assert.Contains("From what you know", prompt, StringComparison.Ordinal);
        Assert.Contains("search_guide", prompt, StringComparison.Ordinal);
        Assert.Contains("depth quick", prompt, StringComparison.Ordinal);
        Assert.Contains("depth full", prompt, StringComparison.Ordinal);
        Assert.Contains("depth deep", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EachDepthHasItsBudgetAndJobKind()
    {
        Assert.Same(WebResearch.Budget, WebResearch.BudgetFor(ResearchDepth.Full));
        Assert.True(WebResearch.QuickBudget.Pages < WebResearch.Budget.Pages && WebResearch.QuickBudget.Steps <= 4);
        Assert.True(WebResearch.DeepBudget.Pages > WebResearch.Budget.Pages && WebResearch.DeepBudget.Steps > WebResearch.Budget.Steps);
        Assert.Same(WebResearch.LookupKind, WebResearch.KindFor(ResearchDepth.Quick));
        Assert.Same(WebResearch.Kind, WebResearch.KindFor(ResearchDepth.Deep));
        // A look-up the user waits for runs beside full research and goes before long work in the Thinking pool's line.
        Assert.Equal((2, false, ThinkingPriority.ThinkLonger, ThinkingJobKind.Research),
            (WebResearch.LookupKind.MaxActive, WebResearch.LookupKind.Offer, WebResearch.LookupKind.PoolPriority, WebResearch.LookupKind.PoolKind));
        BackgroundPlace[] pool = [new("diva", "diva", 1)];
        Assert.Equal((int)ThinkingPriority.ThinkLonger, WebResearch.LookupKind.Demand(pool)!.Priority);
        Assert.Equal((int)ThinkingPriority.Research, WebResearch.Kind.Demand(pool)!.Priority);
        Assert.True(WebResearch.FindingKind.Notice);
        Assert.True(SourceCheck.Kind.Quiet);
        WebResearch.LookupKind.Validate();
        WebResearch.FindingKind.Validate();
        SourceCheck.Kind.Validate();
        // The early finding is work the user asked for: it never drops for its age like a check-in.
        Assert.Equal(UnpromptedKind.FinishedWork, UnpromptedSpeech.Of(WebResearch.FindingKind));
    }

    [Fact]
    public void FoundRunsUntilTheNextFieldAndIsNeverTheReport()
    {
        var step = WebResearch.ParseStep("NOTES:\n- Iron ore is mined [1]\nFOUND: Mine iron ore with a stone pickaxe [1].\nIt is in caves [2].\n" +
            "SEARCH: iron ore depth");
        Assert.Equal("Mine iron ore with a stone pickaxe [1].\nIt is in caves [2].", step.Found);
        Assert.Equal("- Iron ore is mined [1]", step.Notes);
        Assert.Equal("iron ore depth", step.Search);
        var only = WebResearch.ParseStep("FOUND: It's 12 [3].");
        Assert.Equal("It's 12 [3].", only.Found);
        Assert.Null(only.Report);
        var withReport = WebResearch.ParseStep("FOUND: Yes [1].\nTITLE: T\nSUMMARY: S.\nREPORT:\nBody [1].");
        Assert.Equal(("Yes [1].", "Body [1]."), (withReport.Found, withReport.Report));
    }

    [Fact]
    public void TheWatcherTakesTheFirstAnswerThatCitesAPageReadOnce()
    {
        var watcher = new ResearchWatcher();
        var raised = new List<ResearchFinding>();
        watcher.Found += raised.Add;
        WebPage[] pages = [new("https://a.example/", "A", "text", 4), new("https://b.example/", "B", "text", 4)];
        Assert.Null(watcher.See(new(null, [], null, null, null, Found: "Not yet."), pages, 1));
        Assert.Null(watcher.See(new(null, [], null, null, null, Found: "None"), pages, 1));
        Assert.Null(watcher.See(new(null, [], null, null, null, Found: "Probably a stone pickaxe."), pages, 1));
        Assert.Null(watcher.See(new(null, [], null, null, null, Found: "A stone pickaxe [7]."), pages, 1));
        var found = watcher.See(new(null, [], null, null, null, Found: "Mine it with a stone pickaxe [2, 1] [2]."), pages, 2);
        Assert.NotNull(found);
        Assert.Equal(["https://b.example/", "https://a.example/"], found.Sources.Select(p => p.Url));
        Assert.Equal(2, found.Step);
        Assert.Null(watcher.See(new(null, [], null, null, null, Found: "Something else [1]."), pages, 3));
        Assert.Same(found, Assert.Single(raised));
        Assert.Equal("Mine it with a stone pickaxe.", WebResearch.Spoken(found.Answer));
    }

    [Fact]
    public async Task AQuickLookUpEndsAsSoonAsTheAnswerIsFound()
    {
        var tasks = new List<string>();
        var (outcome, run, job) = await RunAsync(new FakeWeb(), task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done("NOTES:\n- Pickaxe [1]\nFOUND: Mine iron ore with a stone pickaxe [1].\nSEARCH: more about it");
        }, depth: ResearchDepth.Quick);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        Assert.Null(outcome.Report);
        Assert.Equal("Mine iron ore with a stone pickaxe [1].", outcome.Answer!.Answer);
        Assert.Equal((1, 1), (run.Steps, run.Searches));
        Assert.Contains("This is a quick look-up", tasks[0], StringComparison.Ordinal);
        Assert.Equal(WebResearch.QuickBudget, run.Limits);
        var note = WebResearch.Answered(outcome.Answer);
        Assert.Contains("\"status\":\"found\"", note, StringComparison.Ordinal);
        Assert.Contains("What you found: Mine iron ore with a stone pickaxe.", note, StringComparison.Ordinal);
        Assert.Contains("Page a <https://pages.example/a>", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuickLookUpGivesItsBestGuessOnItsLastStep()
    {
        var tasks = new List<string>();
        var (outcome, run, _) = await RunAsync(new FakeWeb(), task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(task.Contains("This is the last step", StringComparison.Ordinal)
                ? "FOUND: Most likely a stone pickaxe, but no page says so." : $"SEARCH: again {tasks.Count}");
        }, depth: ResearchDepth.Quick);
        Assert.False(outcome.Answer!.Settled);
        Assert.True(run.Steps <= WebResearch.QuickBudget.Steps);
        Assert.Contains("give FOUND with the best answer", tasks[^1], StringComparison.Ordinal);
        Assert.Contains("\"status\":\"unsure\"", WebResearch.Answered(outcome.Answer), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullResearchTellsTheWatcherEarlyAndGoesOnToTheReport()
    {
        var events = new List<string>();
        var watcher = new ResearchWatcher();
        watcher.Found += finding => events.Add("found at step " + finding.Step);
        var (outcome, run, _) = await RunAsync(new FakeWeb(), task =>
        {
            events.Add("step");
            return BackgroundJobOutcome.Done(events.Count(e => e == "step") == 1
                ? "NOTES:\n- Pickaxe [1]\nFOUND: A stone pickaxe mines it [2].\nSEARCH: second query"
                : "TITLE: Iron ore\nSUMMARY: Mine it with a stone pickaxe.\nREPORT:\nA stone pickaxe mines iron ore [2].");
        }, watcher: watcher);
        Assert.Equal(["step", "found at step 1", "step"], events);
        Assert.NotNull(outcome.Report);
        Assert.Equal(1, outcome.Answer!.Step);
        Assert.Equal(2, run.Steps);
        // The report's note says the user already heard the short answer only when they did.
        var report = outcome.Report!;
        Assert.Contains("don't repeat it", WebResearch.Ready(report, "abc", toldEarly: true), StringComparison.Ordinal);
        Assert.DoesNotContain("don't repeat it", WebResearch.Ready(report, "abc"), StringComparison.Ordinal);
        var notice = WebResearch.FindingNote("research-1", new("iron ore", "how to get iron ore"), outcome.Answer);
        Assert.Contains("research-1, still going", notice, StringComparison.Ordinal);
        Assert.Contains("A stone pickaxe mines it.", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("[2]", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongPageShowsItsStartAndThePartsThatMatch()
    {
        var text = "Iron ore guide intro.\n" + string.Join('\n', Enumerable.Range(0, 200).Select(i => $"Filler line {i} about other things entirely."))
            + "\nTo get iron ore, mine it with a stone pickaxe or better.\n" + string.Join('\n', Enumerable.Range(0, 100).Select(i => $"More filler {i}."));
        var terms = WebResearch.Terms(new ResearchArguments("minecraft iron ore", "how to get iron ore with a pickaxe"));
        Assert.Contains("pickaxe", terms);
        Assert.DoesNotContain("how", terms);
        var (excerpt, parts) = WebResearch.Excerpt(text, 2_000, terms);
        Assert.True(parts);
        Assert.True(excerpt.Length <= 2_000);
        Assert.StartsWith("Iron ore guide intro.", excerpt, StringComparison.Ordinal);
        Assert.Contains("mine it with a stone pickaxe", excerpt, StringComparison.Ordinal);
        Assert.Contains("\n…\n", excerpt, StringComparison.Ordinal);
        var sources = WebResearch.Sources(["q"], [], [new("https://a.example/", "A", text, text.Length)], 0, [], WebResearch.Budget, 2_000,
            terms: terms);
        Assert.Contains("stone pickaxe", sources, StringComparison.Ordinal);
        Assert.EndsWith(WebResearch.PartsCut.TrimEnd(), sources, StringComparison.Ordinal);
        // Nothing matches: only the start, as before.
        Assert.False(WebResearch.Excerpt(text, 2_000, ["zebra"]).Parts);
    }

    [Fact]
    public async Task PagesAreReadAtOnceAndKeepTheirOrder()
    {
        var web = new FakeWeb { Slow = true };
        var (outcome, run, _) = await RunAsync(web, _ => BackgroundJobOutcome.Done("REPORT:\nDone."));
        Assert.NotNull(outcome.Report);
        Assert.True(web.MostAtOnce >= 2);
        Assert.Equal(["https://pages.example/a", "https://pages.example/c"], outcome.Report!.Sources.Select(p => p.Url));
        Assert.Equal(1, run.Failures);
    }

    [Fact]
    public void TheSourceCheckReadsTheCitedSentencesWithTheirBestParts()
    {
        var page = "Intro.\n" + string.Join('\n', Enumerable.Range(0, 100).Select(i => $"Unrelated line {i}.")) + "\nIron ore needs a stone pickaxe.";
        WebPage[] pages = [new("https://a.example/", "Wiki", page, page.Length), new("https://b.example/", "Blog", "Iron ore is found in caves.", 27)];
        var report = WebResearch.Report(WebResearch.ParseStep("TITLE: Iron ore\nSUMMARY: Use a stone pickaxe.\nREPORT:\n## How\n" +
            "You need a stone pickaxe [1]. It is found in caves [2]. An uncited sentence.\n- Diamonds need iron [1][2]."), new("t", "f"), pages);
        var claims = SourceCheck.Claims(report);
        Assert.Equal(3, claims.Count);
        Assert.Equal("You need a stone pickaxe [1].", claims[0].Sentence);
        Assert.Equal([1], claims[0].Pages);
        Assert.Contains("Iron ore needs a stone pickaxe.", claims[0].Passages, StringComparison.Ordinal);
        Assert.Equal([1, 2], claims[2].Pages);
        var task = SourceCheck.Task(null, report, claims);
        Assert.Contains("1. Claim: You need a stone pickaxe [1].", task, StringComparison.Ordinal);
        Assert.Contains("Cites: [1] Wiki", task, StringComparison.Ordinal);
        Assert.Contains("NOT SUPPORTED", task, StringComparison.Ordinal);
        var verdicts = SourceCheck.Parse("1: SUPPORTED\n**2.** PARTLY: only says caves\n3 - NOT SUPPORTED: no page says diamonds\n4: maybe");
        Assert.Equal((SourceVerdict.Supported, (string?)null), verdicts[1]);
        Assert.Equal((SourceVerdict.Partly, "only says caves"), verdicts[2]);
        Assert.Equal((SourceVerdict.NotSupported, "no page says diamonds"), verdicts[3]);
        Assert.False(verdicts.ContainsKey(4));
    }

    [Fact]
    public async Task TheSourceCheckAddsItsSectionBeforeTheSourcesAndSaysWhatIsWrong()
    {
        WebPage[] pages = [new("https://a.example/", "Wiki", "Iron ore needs a stone pickaxe.", 31)];
        var body = string.Join(' ', Enumerable.Range(1, 8).Select(i => $"Claim number {i} about iron [1]."));
        var report = WebResearch.Report(WebResearch.ParseStep("TITLE: Iron ore\nSUMMARY: S.\nREPORT:\n" + body), new("t", "f"), pages);
        var tasks = new List<string>();
        var result = await SourceCheck.RunAsync(report, (task, _) =>
        {
            tasks.Add(task);
            return Task.FromResult(tasks.Count == 1
                ? BackgroundJobOutcome.Done("1: SUPPORTED\n2: SUPPORTED\n3: NOT SUPPORTED: wrong\n4: PARTLY\n5: SUPPORTED\n6: SUPPORTED")
                : BackgroundJobOutcome.Failed("the model couldn't do it"));
        }, null, CancellationToken.None);
        Assert.Equal(2, tasks.Count);
        Assert.Equal((8, 4, 1, 1, 2), (result.Claims.Count, result.Supported, result.Partly, result.NotSupported, result.Unchecked));
        var revised = SourceCheck.Revised(report.Markdown, result);
        var check = revised.IndexOf(SourceCheck.SectionHeading, StringComparison.Ordinal);
        Assert.True(check > 0 && check < revised.IndexOf("## Sources", StringComparison.Ordinal));
        Assert.Contains("Not supported: \"Claim number 3 about iron.\" ([1]): wrong", revised, StringComparison.Ordinal);
        Assert.Contains("4 supported, 1 only partly, 1 not supported, 2 not checked", revised, StringComparison.Ordinal);
        // A second check replaces the first, and the claims never include the check itself.
        Assert.Equal(1, CountOf(SourceCheck.Revised(revised, result), SourceCheck.SectionHeading));
        Assert.Equal(8, SourceCheck.Claims(report with { Markdown = revised }).Count);
        var note = SourceCheck.Note(result, "Iron ore", "abc123");
        Assert.Contains("(abc123) after it was ready", note, StringComparison.Ordinal);
        Assert.Contains("\"Claim number 3 about iron.\": not supported (wrong)", note, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    private static async Task<(ResearchOutcome Outcome, WebResearchRun Run, BackgroundJob Job)> RunAsync(FakeWeb web,
        Func<string, BackgroundJobOutcome> think, Func<int>? taskBytes = null, ResearchDepth depth = ResearchDepth.Full,
        ResearchWatcher? watcher = null)
    {
        using var jobs = new BackgroundJobs();
        ResearchOutcome? outcome = null;
        var run = new WebResearchRun(web, web, (task, _) => Task.FromResult(think(task)), null, taskBytes: taskBytes, watcher: watcher);
        var job = jobs.Start(WebResearch.KindFor(depth), "label", async (job, token) =>
        {
            outcome = await run.RunAsync(job, new("topic words", "what to find", depth), token);
            return outcome.Report is null && outcome.Answer is null ? BackgroundJobOutcome.Failed(outcome.Problem!) : BackgroundJobOutcome.Done("ok");
        }).Job!;
        for (var i = 0; i < 500 && !job.Finished; i++) await Task.Delay(10);
        Assert.True(job.Finished);
        return (outcome!, run, job);
    }

    private sealed class FakeWeb : IWebSearch, IWebFetch
    {
        private int searches;
        private int reading, mostAtOnce;
        public bool Broken { get; init; }
        public int Results { get; init; } = 4;
        public string Text { get; init; } = "Readable text of the page, long enough to count as a page.";
        /// <summary>Each page takes longer the earlier it is in a search's results, so pages read at once finish in reverse.</summary>
        public bool Slow { get; init; }
        public List<string> Queries { get; } = [];
        public List<string> Fetched { get; } = [];
        public int MostAtOnce => Volatile.Read(ref mostAtOnce);

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            if (Broken) throw new WebResearchException("it answered 503");
            Queries.Add(query);
            var round = searches++;
            IReadOnlyList<WebSearchResult> found = [.. Enumerable.Range(0, Results).Select(i =>
                new WebSearchResult($"Result {round}-{i}", round == 0 ? $"https://pages.example/{(char)('a' + i)}" : $"https://pages.example/{round}-{i}", "snippet"))];
            return Task.FromResult(found);
        }

        public async Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            lock (Fetched) Fetched.Add(url.AbsoluteUri);
            if (url.AbsolutePath == "/b") throw new WebResearchException("it isn't on the public internet");
            if (Slow)
            {
                var now = Interlocked.Increment(ref reading);
                int most;
                while ((most = Volatile.Read(ref mostAtOnce)) < now && Interlocked.CompareExchange(ref mostAtOnce, now, most) != most) { }
                await Task.Delay(TimeSpan.FromMilliseconds(30 * ('e' - url.AbsolutePath[^1])), cancellationToken);
                Interlocked.Decrement(ref reading);
            }
            return new WebPage(url.AbsoluteUri, "Page " + url.AbsolutePath.Trim('/'), Text, Text.Length);
        }
    }
}

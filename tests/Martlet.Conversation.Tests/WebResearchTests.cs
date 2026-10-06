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
        var done = WebResearch.ParseStep("**TITLE:** Cat toys\nSUMMARY: Wands win.\nREPORT:\n## Findings\nWands [1].");
        Assert.Equal("Cat toys", done.Title);
        Assert.Equal("Wands win.", done.Summary);
        Assert.Equal("## Findings\nWands [1].", done.Report);
        Assert.Equal("Just an answer.", WebResearch.ParseStep("Just an answer.").Report);
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
    public void WebResearchIsOffByDefaultAndNeedsThinkingLonger()
    {
        Assert.False(new ThinkLongerSettings().Researches);
        Assert.True(new ThinkLongerSettings { WebResearch = true }.Researches);
        Assert.False(new ThinkLongerSettings { WebResearch = true, Enabled = false }.Researches);
        Assert.Null(ThinkLongerSettings.Normalize(new ThinkLongerSettings { WebResearch = false }));
        Assert.True(ThinkLongerSettings.Normalize(new ThinkLongerSettings { WebResearch = true })!.WebResearch);
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
        // The first search's top 3 (one blocked), then 2 untried results of the second search, then the page asked for.
        Assert.Equal(1, run.Failures);
        Assert.Equal(5, run.Pages);
        Assert.Contains("https://pages.example/extra", web.Fetched);
        Assert.Contains("[1] Page a", tasks[0], StringComparison.Ordinal);
        Assert.Contains("What they want to find out: what to find", tasks[0], StringComparison.Ordinal);
        Assert.DoesNotContain("This is the last step", tasks[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastStepAlwaysAsksForTheReportAndEachTaskStaysUnderItsBound()
    {
        var web = new FakeWeb { Text = new string('猫', 20_000) };
        var tasks = new List<string>();
        var (outcome, run, _) = await RunAsync(web, task =>
        {
            tasks.Add(task);
            return BackgroundJobOutcome.Done(task.Contains("This is the last step", StringComparison.Ordinal) ? "REPORT:\nDone." : "SEARCH: again");
        });
        Assert.NotNull(outcome.Report);
        Assert.Equal(4, run.Steps);
        Assert.True(run.Searches <= 3);
        Assert.All(tasks, task => Assert.True(Encoding.UTF8.GetByteCount(task) <= WebResearch.MaxTaskBytes));
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

    private static async Task<(ResearchOutcome Outcome, WebResearchRun Run, BackgroundJob Job)> RunAsync(FakeWeb web,
        Func<string, BackgroundJobOutcome> think)
    {
        using var jobs = new BackgroundJobs();
        ResearchOutcome? outcome = null;
        var run = new WebResearchRun(web, web, (task, _) => Task.FromResult(think(task)), null);
        var job = jobs.Start(WebResearch.Kind, "label", async (job, token) =>
        {
            outcome = await run.RunAsync(job, new("topic words", "what to find"), token);
            return outcome.Report is null ? BackgroundJobOutcome.Failed(outcome.Problem!) : BackgroundJobOutcome.Done("ok");
        }).Job!;
        for (var i = 0; i < 500 && !job.Finished; i++) await Task.Delay(10);
        Assert.True(job.Finished);
        return (outcome!, run, job);
    }

    private sealed class FakeWeb : IWebSearch, IWebFetch
    {
        private int searches;
        public bool Broken { get; init; }
        public int Results { get; init; } = 4;
        public string Text { get; init; } = "Readable text of the page, long enough to count as a page.";
        public List<string> Queries { get; } = [];
        public List<string> Fetched { get; } = [];

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            if (Broken) throw new WebResearchException("it answered 503");
            Queries.Add(query);
            var round = searches++;
            IReadOnlyList<WebSearchResult> found = [.. Enumerable.Range(0, Results).Select(i =>
                new WebSearchResult($"Result {round}-{i}", round == 0 ? $"https://pages.example/{(char)('a' + i)}" : $"https://pages.example/{round}-{i}", "snippet"))];
            return Task.FromResult(found);
        }

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            Fetched.Add(url.AbsoluteUri);
            if (url.AbsolutePath == "/b") throw new WebResearchException("it isn't on the public internet");
            return Task.FromResult(new WebPage(url.AbsoluteUri, "Page " + url.AbsolutePath.Trim('/'), Text, Text.Length));
        }
    }
}

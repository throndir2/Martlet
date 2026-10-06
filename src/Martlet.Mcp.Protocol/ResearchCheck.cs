using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>research_check: rehearses web research end to end with Martlet's own parts: the research tool's texts and job kind
/// (<see cref="WebResearch"/>), the background-job scheduler (<see cref="BackgroundJobs"/>), the web client
/// (<see cref="WebAccess"/>: the DuckDuckGo results parser, the page reader and its public-address guard), the research loop
/// (<see cref="WebResearchRun"/>) with each step a background think (<see cref="BackgroundThink"/>) through the conversation
/// runtime and Chat Completions adapter, the report creation and its page (<see cref="ResearchReports"/>), against fixtures on
/// 127.0.0.1: a search page, web pages and a model with canned answers (NOT AI). Nothing leaves loopback; no credentials are
/// read; the report is kept in a temporary data folder that is deleted afterwards.</summary>
internal static class ResearchCheck
{
    private const string Model = "fixture-model";
    private const string Persona = "You are Martlet, a cheerful companion who talks with the user.";
    private const string Asked = "Can you look up what toys cats like best?";
    private const string Acknowledged = "Ooh, good question! Let me look into that for you.";
    private const string Topic = "best toys for cats";
    private const string Find = "which toys most cats like and why";
    private const string Summary = "Most cats love wand toys and crinkle balls because they imitate prey (FIXTURE - NOT AI).";

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-research-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            await using var fixture = new Fixture();
            var settings = Settings();
            var guard = Guard();
            var flow = await FlowAsync(fixture, directory, cancellation);
            var limits = await LimitsAsync(cancellation);
            return new
            {
                ok = settings.Ok && guard.Ok && flow.Ok && limits.Ok,
                endpoint = fixture.BaseUrl,
                note = "Fixture search page, web pages and model on 127.0.0.1 with canned answers (NOT AI); the tool texts, job kind, " +
                    "scheduler, web client, research loop, runtime, adapter and report creation are Martlet's own. No real web search " +
                    "or model was used.",
                settings = settings.Report, guard = guard.Report, flow = flow.Report, limits = limits.Report
            };
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Off by default; on only with Thinking longer on; saved lean.
    private static (bool Ok, object Report) Settings()
    {
        var byDefault = new ThinkLongerSettings();
        var on = new ThinkLongerSettings { WebResearch = true };
        var deepOff = new ThinkLongerSettings { WebResearch = true, Enabled = false };
        var saved = JsonSerializer.Serialize(ThinkLongerSettings.Normalize(on));
        var ok = !byDefault.Researches && on.Researches && !deepOff.Researches && ThinkLongerSettings.Normalize(on with { WebResearch = false }) is null &&
            saved.Contains("true", StringComparison.Ordinal);
        return (ok, new { ok, byDefault = byDefault.Researches, turnedOn = on.Researches, deepThinkingOff = deepOff.Researches, saved });
    }

    // Only public internet addresses are reachable (redirects included).
    private static (bool Ok, object Report) Guard()
    {
        var cases = new (string Address, bool Public)[]
        {
            ("93.184.215.14", true), ("2606:4700::1111", true), ("127.0.0.1", false), ("10.1.2.3", false), ("192.168.1.1", false),
            ("172.20.0.5", false), ("169.254.169.254", false), ("100.100.1.1", false), ("::1", false), ("fd00::1", false), ("fe80::1", false),
            ("::ffff:192.168.0.1", false), ("0.0.0.0", false), ("224.0.0.251", false)
        };
        var results = cases.Select(c => (c.Address, c.Public, Got: WebAccess.IsPublic(IPAddress.Parse(c.Address)))).ToArray();
        var ok = results.All(r => r.Public == r.Got);
        return (ok, new { ok, addresses = results.Select(r => new { address = r.Address, @public = r.Got, expected = r.Public }) });
    }

    private static async Task<(bool Ok, object Report)> FlowAsync(Fixture fixture, string directory, CancellationToken cancellation)
    {
        var registry = new CreationRegistry();
        registry.Register(ResearchReports.Kind);
        string? opened = null;
        using var attached = registry.Handle(ResearchReports.KindName, ResearchReports.Handler(directory, path => opened = path));
        var thinkSettings = new ThinkLongerSettings { WebResearch = true };
        using var jobs = new BackgroundJobs();
        await using var replies = ConversationRuntime.Create(new NoCredentials());
        await using var thinking = ConversationRuntime.Create(new NoCredentials());
        var permissions = new Permissions(ChatCompletionsSetup.BaseUri(fixture.BaseUrl + "/v1"));
        var instructions = string.Join("\n\n", Persona, PromptSettings.Fill(null, PromptCatalog.Tools), ThinkLonger.Instructions(thinkSettings, null),
            WebResearch.Instructions(null));
        var input = new BoundedTextInput(Asked, instructions, [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey!")],
            tools: [.. ThinkLonger.Definitions(thinkSettings), WebResearch.Definition]);
        var clock = Stopwatch.StartNew();
        long toolReturnedMs = -1;
        ConversationTurn? reply = null;
        BackgroundJobStart? started = null;
        WebResearchRun? run = null;
        string? key = null;
        var host = new Tools(call =>
        {
            if (call.Name != WebResearch.Name) return new("Not in this check.", true);
            var (arguments, problem) = WebResearch.Parse(call.ArgumentsJson);
            if (arguments is null) return new(problem!, true);
            started = jobs.Start(WebResearch.Kind, WebResearch.Label(arguments.Topic), async (job, token) =>
            {
                using var web = new WebAccess(fixture.BaseUrl + "/search", allowLoopback: true);
                run = new WebResearchRun(web, web, (task, stepToken) => new BackgroundThink(thinking, left =>
                    (StepRequest(fixture, input, reply!.Content.Text, task, thinkSettings, left), permissions)) { Doing = job.Progress }
                    .RunAsync(job, stepToken), null);
                var found = await run.RunAsync(job, arguments, token);
                if (found.Report is not { } report) return BackgroundJobOutcome.Failed(found.Problem ?? "nothing");
                var creation = await CreationStore.AddAsync(directory, ResearchReports.Draft(report, new CreationAuthor { Device = "fixture-device" }),
                    registry, DateTimeOffset.UtcNow, token);
                key = creation.Key;
                return BackgroundJobOutcome.Done(WebResearch.Ready(report, key));
            });
            toolReturnedMs = clock.ElapsedMilliseconds;
            return started.Job is { } job ? new(WebResearch.Started(job, !string.IsNullOrWhiteSpace(reply!.Content.Text))) : new(WebResearch.Refused(started), true);
        });
        reply = replies.Start(new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ReplyLimits,
            new ConversationLimits { TurnTimeout = TimeSpan.FromSeconds(60), MaxToolRounds = 4 }, chat: new ChatCompletionsTarget(fixture.BaseUrl + "/v1", true),
            generation: new GenerationSettings { Reasoning = false }, tools: host), permissions, cancellation);
        var replied = await reply.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        await reply.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        var replyMs = clock.ElapsedMilliseconds;
        var job = started?.Job;
        var runningWhenReplyEnded = job is { Finished: false };
        var waited = Stopwatch.StartNew();
        while (job is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(20, cancellation);

        // The steps the model got, the note the conversation gets and the report shown on a yes.
        var steps = fixture.Bodies("step").Select(Last).ToArray();
        var firstStep = steps.FirstOrDefault() ?? "";
        var results = job is null ? "" : BackgroundJobs.Results([job]);
        var library = CreationStore.View(directory);
        var creation = key is null ? null : library.Live.FirstOrDefault(c => c.Key == key);
        CreationActionResult? shown = null;
        string html = "";
        if (creation is not null && registry.HandlerFor(creation.Kind) is { } handler)
        {
            using var none = JsonDocument.Parse("{}");
            shown = await handler.PerformAsync(new(creation, none.RootElement.Clone(), CreationStore.Assets(directory, creation)), cancellation);
            if (opened is not null && File.Exists(opened)) html = await File.ReadAllTextAsync(opened, cancellation);
        }
        var searches = fixture.Queries;
        var ok = replied.State == ConversationState.Completed && toolReturnedMs >= 0 && toolReturnedMs < replyMs && runningWhenReplyEnded &&
            job?.State == BackgroundJobState.Succeeded && job.Kind.Offer && run is { Searches: 2, Steps: 2 } && run.Pages >= 3 &&
            run.Failures >= 2 && searches.Count == 2 && searches[0] == Topic &&
            steps.Length == 2 && firstStep.Contains("A background task from Martlet", StringComparison.Ordinal) &&
            firstStep.Contains(Find, StringComparison.Ordinal) && firstStep.Contains("Wand toys", StringComparison.Ordinal) &&
            !firstStep.Contains("trackingScript", StringComparison.Ordinal) && !firstStep.Contains("Sponsored", StringComparison.Ordinal) &&
            steps.All(s => Encoding.UTF8.GetByteCount(s) < BoundedTextInput.HardMaxUtf8Bytes) &&
            results.Contains("go-ahead", StringComparison.Ordinal) && results.Contains("perform_creation", StringComparison.Ordinal) &&
            results.Contains(Summary, StringComparison.Ordinal) &&
            creation is { Kind: ResearchReports.KindName } && creation.Text?.Contains("## Sources", StringComparison.Ordinal) == true &&
            shown is { IsError: false } && html.Contains($"<a href=\"{fixture.BaseUrl}/page/1\"", StringComparison.Ordinal) &&
            !html.Contains("<script", StringComparison.OrdinalIgnoreCase) && fixture.Fetched("/page/5") == 0;
        return (ok, new
        {
            ok,
            reply = new { state = replied.State.ToString(), toolReturnedMs, replyMs, researchStillRunning = runningWhenReplyEnded },
            job = job is null ? null : new
            {
                id = job.Id, kind = job.Kind.Name, state = job.State.ToString(), offer = job.Kind.Offer, doing = job.Kind.Doing,
                timeLimit = BackgroundJobs.Duration(job.Kind.TimeLimit), perHour = job.Kind.MaxPerHour, elapsedMs = (long)job.Elapsed.TotalMilliseconds,
                problem = job.Problem
            },
            run = run is null ? null : new { searches = run.Searches, pagesRead = run.Pages, unreadable = run.Failures, bytes = run.Bytes, modelSteps = run.Steps },
            searchQueries = searches,
            pagesFetched = fixture.Paths.Where(p => p.StartsWith("/page/", StringComparison.Ordinal)).ToArray(),
            privateRedirectFollowed = fixture.Fetched("/page/5") > 0,
            steps = steps.Select(s => new { bytes = Encoding.UTF8.GetByteCount(s), hasSources = s.Contains("[1]", StringComparison.Ordinal),
                last = s.Contains("This is the last step", StringComparison.Ordinal) }),
            note = results,
            creation = creation is null ? null : new { key = creation.Key, kind = creation.Kind, title = creation.Title, textCharacters = creation.Text?.Length, bytes = creation.Bytes },
            shown = shown?.Text, page = new { written = opened is not null, characters = html.Length, links = CountOf(html, "<a href=") },
            tool = new { name = WebResearch.Definition.Name, description = WebResearch.Definition.Description, parameters = JsonNode.Parse(WebResearch.ParametersJson) },
            prompt = WebResearch.Instructions(null)
        });
    }

    // One research at a time (a think may run beside it), the hourly limit, Cancel and a failed first search.
    private static async Task<(bool Ok, object Report)> LimitsAsync(CancellationToken cancellation)
    {
        using var jobs = new BackgroundJobs();
        var gate = new TaskCompletionSource<BackgroundJobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Start(WebResearch.Kind, "first", (_, token) => gate.Task.WaitAsync(token));
        var second = jobs.Start(WebResearch.Kind, "second", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")));
        var think = jobs.Start(ThinkLonger.Kind(new()), "beside", (_, token) => gate.Task.WaitAsync(token));
        var canceled = jobs.Cancel(first.Job?.Id, BackgroundJob.CanceledByYou);
        var waited = Stopwatch.StartNew();
        while (first.Job is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        gate.TrySetResult(BackgroundJobOutcome.Done("done"));
        waited.Restart();
        while (jobs.Active.Count > 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        using var hourly = new BackgroundJobs();
        var refusals = Enumerable.Range(0, WebResearch.Kind.MaxPerHour + 1)
            .Select(_ => hourly.Start(WebResearch.Kind, "x", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x"))))
            .Select(start => { Thread.Sleep(30); return start.Refusal; }).ToArray();
        using var broken = new BackgroundJobs();
        var failing = broken.Start(WebResearch.Kind, "x", async (job, token) =>
        {
            var found = await new WebResearchRun(new BrokenSearch(), new BrokenSearch(), (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")), null)
                .RunAsync(job, new(Topic, Find), token);
            return found.Report is null ? BackgroundJobOutcome.Failed(found.Problem!) : BackgroundJobOutcome.Done("x");
        }).Job!;
        waited.Restart();
        while (!failing.Finished && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        var ok = first.Started && second.Refusal == "busy" && think.Started && canceled is not null && first.Job!.State == BackgroundJobState.Canceled &&
            refusals[^1] == "hourly_limit" && refusals[..^1].All(r => r is null) &&
            failing.State == BackgroundJobState.Failed && failing.Problem?.Contains("web search", StringComparison.Ordinal) == true;
        return (ok, new
        {
            ok, secondRefused = second.Refusal, secondTold = second.Started ? null : WebResearch.Refused(second), thinkBeside = think.Started,
            canceled = first.Job?.State.ToString(), hourly = refusals, failedSearch = failing.Problem
        });
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Last(string body) =>
        JsonNode.Parse(body)?["messages"]?.AsArray().LastOrDefault()?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static readonly TextGenerationLimits ReplyLimits = new()
    {
        MaxInputBytes = BoundedTextInput.HardMaxInputUtf8Bytes, MaxInputTokens = 181_968, MaxOutputTokens = GenerationSettings.ChatReplyTokens,
        MaxContextTokens = 181_968 + GenerationSettings.ChatReplyTokens, MaxHistoryMessages = BoundedTextInput.HardMaxHistoryMessages,
        MaxEvents = 4094, MaxStreamBytes = 4_194_304, MaxRequestTime = TimeSpan.FromSeconds(45)
    };

    // A step's request as the desktop builds it with the Thinking model (LiveConversationConfiguration.ThinkRequest): the reply's
    // request continued, then the step's task.
    private static ConversationRequest StepRequest(Fixture fixture, BoundedTextInput conversation, string reply, string task,
        ThinkLongerSettings settings, TimeSpan left)
    {
        var input = ThinkLonger.Input(conversation, reply, task, null, null);
        return new(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ThinkLonger.Limits(ReplyLimits, settings.HowHard, left),
            ThinkLonger.TurnLimits(left), chat: new ChatCompletionsTarget(fixture.BaseUrl + "/v1", true),
            generation: ThinkLonger.Generation(new GenerationSettings { Reasoning = false }, settings.HowHard, false),
            tools: input.Tools.Count > 0 ? ThinkLonger.NoTools : null);
    }

    private sealed class BrokenSearch : IWebSearch, IWebFetch
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new WebResearchException("it answered 503");

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken) => throw new WebResearchException("it answered 503");
    }

    private sealed class Tools(Func<TextToolCall, ConversationToolResult> call) : IConversationToolHost
    {
        public ValueTask<ConversationToolResult> CallAsync(TextToolCall textCall, CancellationToken cancellationToken) =>
            ValueTask.FromResult(call(textCall));
    }

    private sealed class NoCredentials : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BoundProviderCredential?>(null);
    }

    private sealed class Permissions(Uri baseUri) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, action.Context.Deadline, true, true), new(action.Budget, action.Context.Deadline)));

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
    }

    /// <summary>One HTTP server on 127.0.0.1 (NOT AI, canned): /search answers like DuckDuckGo's HTML search (an ad, results
    /// behind its redirect links), /page/N are web pages (one with scripts and navigation, one a PDF, one redirecting to a
    /// private address, one never linked), and /v1/chat/completions is the model: the reply says it'll look into it and calls
    /// research; a research step asks for another search and a page first, then writes the report.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<(string Kind, string Body)> bodies = [];
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
        internal int Fetched(string path) { lock (paths) return paths.Count(p => p == path); }
        internal IReadOnlyList<string> Bodies(string kind) { lock (paths) return [.. bodies.Where(b => b.Kind == kind).Select(b => b.Body)]; }

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
                var (method, target, body) = await ReadAsync(stream);
                var path = target.Split('?')[0];
                lock (paths)
                {
                    paths.Add(path);
                    if (path == "/search")
                        queries.Add(Uri.UnescapeDataString(target.Split("q=", 2).ElementAtOrDefault(1)?.Split('&')[0] ?? "").Replace('+', ' '));
                }
                if (method == "POST" && path == "/v1/chat/completions") await ChatAsync(stream, body);
                else if (path == "/search") await PageAsync(stream, "text/html", SearchPage());
                else if (path == "/page/1") await PageAsync(stream, "text/html", Page("Wand toys - Cat Care", "Wand toys let cats stalk and pounce like they hunt prey. Most cats play with them daily.", script: true));
                else if (path == "/page/2") await PageAsync(stream, "application/pdf", "%PDF-1.4");
                else if (path == "/page/3") await WriteAsync(stream, "HTTP/1.1 302 Found\r\nLocation: http://192.168.1.1/page/5\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                else if (path == "/page/4") await PageAsync(stream, "text/html", Page("Crinkle balls", "Crinkle balls make a sound cats find exciting; they bat them around for hours."));
                else if (path == "/page/6") await PageAsync(stream, "text/html", Page("Laser pointers, carefully", "Laser pointers should end on a real toy so the cat can catch something."));
                else await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException or JsonException) { }
        }

        private string Link(string path) => "//duckduckgo.com/l/?uddg=" + Uri.EscapeDataString(BaseUrl + path) + "&amp;rut=fixture";

        private string SearchPage() =>
            "<html><body><div class=\"results\">" +
            "<div class=\"result result--ad\"><a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/y.js?ad=1\">Sponsored cat stuff</a></div>" +
            Result("/page/1", "Wand toys - Cat Care", "Wand <b>toys</b> imitate prey.") + Result("/page/2", "Cat toy guide (PDF)", "A PDF guide.") +
            Result("/page/3", "Toy reviews", "Reviews of cat toys.") + Result("/page/4", "Crinkle balls", "Cats love the sound.") +
            "</div></body></html>";

        private string Result(string path, string title, string snippet) =>
            $"<div class=\"result\"><h2 class=\"result__title\"><a rel=\"nofollow\" class=\"result__a\" href=\"{Link(path)}\">{title}</a></h2>" +
            $"<a class=\"result__snippet\" href=\"{Link(path)}\">{snippet}</a></div>";

        private static string Page(string title, string text, bool script = false) =>
            $"<html><head><title>{title}</title><script>var trackingScript = 1;</script></head><body><nav>Home | About</nav>" +
            $"<article><h1>{title}</h1><p>{text}</p><p>FIXTURE - NOT AI web page for research_check.</p></article>" +
            (script ? "<script>trackingScript()</script>" : "") + "<footer>Copyright</footer></body></html>";

        private async Task ChatAsync(NetworkStream stream, string body)
        {
            var last = Last(body);
            var role = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().LastOrDefault()?["role"];
            var kind = role == "tool" ? "after-tool" : last.Contains("A background task from Martlet", StringComparison.Ordinal) ? "step" : "reply";
            lock (paths) bodies.Add((kind, body));
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            switch (kind)
            {
                case "reply":
                    await ChunkAsync(stream, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(Acknowledged) + "}");
                    var arguments = JsonSerializer.Serialize(new { topic = Topic, what_to_find = Find });
                    await ChunkAsync(stream, "{\"tool_calls\":[{\"index\":0,\"id\":\"call_research\",\"type\":\"function\",\"function\":" +
                        "{\"name\":\"research\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}");
                    await FinishAsync(stream, "tool_calls");
                    break;
                case "after-tool":
                    await FinishAsync(stream, "stop");
                    break;
                default:
                    var answer = last.Contains("step 1 of", StringComparison.Ordinal)
                        ? $"SEARCH: laser pointer cats safe\nREAD: {BaseUrl}/page/6"
                        : "TITLE: Toys cats like best\nSUMMARY: " + Summary + "\nREPORT:\nMost cats prefer toys that move like prey: wand toys [1] " +
                          "and crinkle balls [2]. Laser pointers are fine if the game ends on a toy they can catch [3].";
                    await ChunkAsync(stream, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(answer) + "}");
                    await FinishAsync(stream, "stop");
                    break;
            }
        }

        private static async Task<(string Method, string Target, string Body)> ReadAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (buffer.Count < 65_536)
            {
                if (await stream.ReadAsync(one) == 0) break;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^1] == '\n' && buffer[^2] == '\r' && buffer[^3] == '\n' && buffer[^4] == '\r') break;
            }
            var head = Encoding.ASCII.GetString(buffer.ToArray());
            var line = head.Split("\r\n")[0].Split(' ');
            var length = head.Split("\r\n").Select(h => h.Split(':', 2)).Where(h => h.Length == 2 && h[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(h => int.Parse(h[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            return (line[0], line.Length > 1 ? line[1] : "/", Encoding.UTF8.GetString(body, 0, read));
        }

        private Task PageAsync(NetworkStream stream, string type, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n" + content);
        }

        private Task ChunkAsync(NetworkStream stream, string delta) =>
            WriteAsync(stream, "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0," +
                "\"delta\":" + delta + ",\"finish_reason\":null}]}\n\n");

        private Task FinishAsync(NetworkStream stream, string reason) =>
            WriteAsync(stream, "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0," +
                "\"delta\":{},\"finish_reason\":\"" + reason + "\"}]}\n\ndata: [DONE]\n\n");

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

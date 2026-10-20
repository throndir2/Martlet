using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Mcp;

internal static partial class ThinkLongerCheck
{
    // The owner's example with the production broker and scheduler (fixture runners, no network): a companion PC and three hosts,
    // one general, one kept for image generation and one that sings. Thinks go to the general computer first, then the others,
    // but a long job never takes the pool's last free slot while the pool has two or more slots: two run, the third and fourth
    // wait in line, and a quick job (an end-of-turn judge) takes the free slot at once. A song holds the singing computer; the
    // first computer to free up takes the next in line, and the last in line runs once two slots are free again.
    private static async Task<(bool Ok, object Report)> BrokerAsync(CancellationToken cancellation)
    {
        using var jobs = new BackgroundJobs();
        var general = new BackgroundPlace("host:general", "general");
        var images = new BackgroundPlace("host:images", "images") { Duties = ["image generation"] };
        var singer = new BackgroundPlace("host:singer", "singer") { Duties = ["singing"] };
        IReadOnlyList<BackgroundPlace> pool = [singer, images, general];
        var kind = ThinkLonger.Kind(new ThinkLongerSettings(), ThinkLonger.Slots(pool));
        var gates = new Dictionary<string, TaskCompletionSource>();
        Func<BackgroundJob, CancellationToken, Task<BackgroundJobOutcome>> Run(string name)
        {
            var gate = gates[name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return async (job, token) =>
            {
                await gate.Task.WaitAsync(token);
                return BackgroundJobOutcome.Done($"{name} on {job.Place?.Name}");
            };
        }
        async Task Until(Func<bool> done)
        {
            var waited = Stopwatch.StartNew();
            while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, cancellation);
        }
        var timer = Stopwatch.StartNew();
        var starts = new[] { "a", "b", "c", "d" }.Select(name => jobs.Start(kind, name, Run(name), pool, wait: true)).ToArray();
        var decidedMs = timer.Elapsed.TotalMilliseconds;
        var placed = starts.Select(s => s.Job?.Place?.Name).ToArray();
        var queued = starts[2].Queued;
        await Until(() => jobs.Places.Line.Count == 2);
        var line = jobs.Places.Line;
        // A quick job takes the slot the thinks leave free, at once, while two thinks wait.
        var judge = jobs.Places.TryAcquire(pool, "end-of-turn-judge-1", demand: ThinkingDemand.For(ThinkingJobKind.EndOfTurnJudge, pool));
        var judgeOn = judge?.Place.Name;
        var waitedForJudge = starts[2].Job!.Place is null && starts[3].Job!.Place is null;
        judge?.Dispose();
        // A song starts on the singing computer (its think carries on), then that think finishes: the song still holds the
        // singing computer, so the ones in line keep waiting.
        var song = jobs.Places.Hold(singer, "song-1");
        gates["b"].SetResult();
        await Until(() => starts[1].Job!.Finished);
        await Task.Delay(100, cancellation);
        var waitingDuringSong = starts[2].Job!.Place is null && starts[3].Job!.Place is null;
        // The general computer frees up: the first in line goes there. Only one slot is free then, so the last in line waits.
        gates["a"].SetResult();
        await Until(() => starts[2].Job!.Place is not null);
        var nextOn = starts[2].Job!.Place?.Name;
        await Task.Delay(100, cancellation);
        var lastWaited = starts[3].Job!.Place is null;
        // The song ends: two slots are free, so the last in line runs on the singing computer.
        song.Dispose();
        await Until(() => starts[3].Job!.Place is not null);
        var lastOn = starts[3].Job!.Place?.Name;
        foreach (var gate in gates.Values) gate.TrySetResult();
        await Until(() => starts.All(s => s.Job!.Finished));
        var ok = placed is ["general", "singer", null, null] && queued == "think-1 on general and think-2 on singer" &&
            starts[3].Queued == queued && line is ["think-3", "think-4"] && judgeOn == "images" && waitedForJudge &&
            waitingDuringSong && nextOn == "general" && lastWaited && lastOn == "singer" &&
            starts.All(s => s.Job!.State == BackgroundJobState.Succeeded) &&
            jobs.Places.Leases.Count == 0 && jobs.Places.Line.Count == 0 && decidedMs < 50;
        return (ok, new
        {
            ok,
            places = pool.Select(p => new { p.Name, p.Rank, p.Slots, p.Duties, p.Standing }),
            maxActive = kind.MaxActive,
            placed,
            queuedBehind = queued,
            inLine = line,
            quickJob = new { holder = "end-of-turn-judge-1", on = judgeOn, thinksStillWaiting = waitedForJudge },
            waitedWhileTheSongHeldTheSinger = waitingDuringSong,
            nextInLineRanOn = nextOn,
            lastInLineWaitedForTwoFreeSlots = lastWaited,
            lastInLineRanOn = lastOn,
            decidedMs = Math.Round(decidedMs, 2),
            note = "Deterministic: no model is asked; every choice is the production BackgroundPlaces order and its rule that a " +
                "long job leaves the pool's last free slot for quick jobs."
        });
    }

    private static SetupRoute Chat(SetupRole role, string origin, string model) => new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = role, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin, ModelId = model,
        ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static SetupRoute Gateway(SetupRole role, SetupRouteType type, string hostId, string origin) => new()
    {
        RouteType = type, Role = role, ProviderAlias = SelfHostSetup.Gateway(type).Alias, Origin = origin, ModelId = "fixture",
        ConfigurationRevision = Guid.NewGuid(), Enabled = true,
        Gateway = new() { SchemaVersion = 1, Origin = origin, HostId = hostId, SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
    };

    private static DeepThinkingSettings Host(string hostId) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "gemma4:27b", HostId = hostId, HostOrigin = $"https://{hostId}.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "fixture-device", HostCredentialId = Guid.NewGuid()
    };

    // The production plan (DeepThinkingPlan) for the setups that matter: a think always runs alongside the conversation, so it
    // can run only where it has a model of its own; a second model in the same Ollama on this PC is checked to fit first.
    private static (bool Ok, object Report) Plans()
    {
        const string ollama = GenerationSupport.LocalOllamaChatBaseUrl, openRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
        var localThinking = Chat(SetupRole.Llm, ollama, "gemma4:e4b");
        var cloudThinking = Chat(SetupRole.Llm, openRouter, "x-ai/grok-4.3");
        var cases = new (string Name, DeepThinkingSettings Deep, SetupRoute[] Routes, bool Available, bool ChecksFit)[]
        {
            ("Same as Thinking, Thinking in Ollama on this PC", new(), [localThinking], false, false),
            ("Same as Thinking, Thinking on OpenRouter", new(), [cloudThinking], true, false),
            ("Same as Thinking, Thinking on diva's Ollama", new(), [Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443")],
                false, false),
            ("OpenRouter, Thinking in Ollama on this PC", new() { Place = DeepThinkingPlace.Endpoint, Origin = openRouter, ModelId = "x-ai/grok-4.3" },
                [localThinking], true, false),
            ("Ollama on this PC (another model), Thinking in Ollama on this PC",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:12b" }, [localThinking], true, true),
            ("Ollama on this PC (Thinking's own model), Thinking in Ollama on this PC",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:e4b" }, [localThinking], false, false),
            ("Ollama on this PC, Thinking on OpenRouter and the voice on diva",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:12b" },
                [cloudThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "diva", "https://diva.local:9443")], true, false),
            ("Ollama on this PC, Thinking on OpenRouter and the voice in this PC's host service",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:12b" },
                [cloudThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "this-pc", "https://127.0.0.1:9443")], true, false),
            ("diva, Thinking in Ollama on this PC and the voice on imouto", Host("diva"),
                [localThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "imouto", "https://imouto.local:9443")], true, false),
            ("diva, the voice on diva too", Host("diva"),
                [localThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "diva", "https://diva.local:9443")], true, false),
            ("diva, Thinking on diva too", Host("diva"),
                [Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443")], false, false),
            // diva's Deep thinking role is an Ollama server of its own, so it thinks beside diva's Thinking model.
            ("diva's Deep thinking role, Thinking on diva too", Host("diva") with { HostRouteId = SelfHostSetup.DeepThinkingRouteId },
                [Gateway(SetupRole.Llm, SetupRouteType.GatewayOllama, "diva", "https://diva.local:9443")], true, false),
            ("diva's Deep thinking role, Thinking in Ollama on this PC", Host("diva") with { HostRouteId = SelfHostSetup.DeepThinkingRouteId },
                [localThinking], true, false)
        };
        var results = cases.Select(c => (c, Plan: DeepThinkingPlan.For(c.Deep, c.Routes))).ToArray();
        var ok = results.All(r => r.Plan.Available == r.c.Available && r.Plan.ChecksFit == r.c.ChecksFit);
        return (ok, new
        {
            ok,
            cases = results.Select(r => new { name = r.c.Name, where = r.c.Deep.Separate ? r.c.Deep.Describe() : "the Thinking model",
                available = r.Plan.Available, expected = r.c.Available, checksFit = r.Plan.ChecksFit, why = r.Plan.Why })
        });
    }

    // A think on a destination of its own: it runs on its own endpoint (a second fixture, standing in for the other machine)
    // while three replies go to the conversation's, and is never stopped. Its request is fitted to the destination: the
    // conversation and the task, no tools, Thinking steps on.
    private static async Task<(bool Ok, object Report)> ParallelAsync(Fixture conversation, Fixture other, CancellationToken cancellation)
    {
        var settings = new ThinkLongerSettings();
        var plan = DeepThinkingPlan.For(new() { Place = DeepThinkingPlace.Endpoint, Origin = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl,
            ModelId = "x-ai/grok-4.3" }, [Chat(SetupRole.Llm, GenerationSupport.LocalOllamaChatBaseUrl, "gemma4:e4b")]);
        using var jobs = new BackgroundJobs();
        await using var thinking = ConversationRuntime.Create(new NoCredentials());
        await using var replies = ConversationRuntime.Create(new NoCredentials());
        var conversationInput = new BoundedTextInput(Asked, Persona, [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey!")],
            tools: ThinkLonger.Definitions(settings));
        var bounds = new ThinkBounds(BoundedTextInput.HardMaxInputUtf8Bytes, BoundedTextInput.HardMaxHistoryMessages, 93_904, Tools: false);
        var think = new BackgroundThink(thinking, left =>
        {
            var input = ThinkLonger.Fit(ThinkLonger.Input(conversationInput, Acknowledged, TaskText, null, null), bounds);
            return (new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model),
                ThinkLonger.Limits(ReplyLimits, settings.HowHard, left), ThinkLonger.TurnLimits(left), chat: new ChatCompletionsTarget(other.BaseUrl, true),
                generation: new GenerationSettings { Reasoning = true, ReasoningEffort = GenerationSupport.ReasoningEffortOn }),
                new Permissions(ChatCompletionsSetup.BaseUri(other.BaseUrl)));
        });
        var before = other.Count("think");
        var job = jobs.Start(ThinkLonger.Kind(settings), ThinkLonger.Label(TaskText), think.RunAsync).Job!;
        var waited = Stopwatch.StartNew();
        while (other.Count("think", inFlight: true) == 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        // Three replies to the conversation's own endpoint while the think works on the other.
        var firstWords = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var reply = replies.Start(new ConversationRequest(new BoundedTextInput($"Quick question {i + 1}?", Persona),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), ReplyLimits, new ConversationLimits(),
                chat: new ChatCompletionsTarget(conversation.BaseUrl, true), generation: new GenerationSettings { Reasoning = false }),
                new Permissions(ChatCompletionsSetup.BaseUri(conversation.BaseUrl)), cancellation);
            var done = await reply.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await reply.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            firstWords.Add((long)(done.FirstTextAfter?.TotalMilliseconds ?? -1));
        }
        var thinkingDuringReplies = other.Count("think", inFlight: true) == 1;
        waited.Restart();
        while (!job.Finished && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20, cancellation);
        var body = other.Bodies("think").Skip(before).FirstOrDefault();
        var sent = body is null ? null : JsonNode.Parse(body)!.AsObject();
        var messages = body is null ? [] : Messages(body);
        var last = messages.LastOrDefault()?["content"]?.GetValue<string>() ?? "";
        var ok = plan.Available && thinkingDuringReplies && firstWords.All(ms => ms is >= 0 and < 1000) && job.State == BackgroundJobState.Succeeded &&
            job.Result == Lyrics && think.Attempts == 1 && other.Served("think").Skip(before).All(s => !s.Aborted) &&
            sent?["tools"] is null && sent?["reasoning_effort"] is null && sent?["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == true &&
            messages.Count == 6 && last.Contains(TaskText, StringComparison.Ordinal);
        return (ok, new
        {
            ok, plan = new { available = plan.Available, why = plan.Why },
            thinkingWhileReplying = thinkingDuringReplies, replyFirstWordsMs = firstWords,
            think = new { state = job.State.ToString(), attempts = think.Attempts, finishedAfterMs = (long)job.Elapsed.TotalMilliseconds },
            request = new { tools = sent?["tools"] is not null, thinking = sent?["chat_template_kwargs"]?.ToJsonString(), messages = messages.Count,
                taskLast = last.Contains(TaskText, StringComparison.Ordinal) }
        });
    }

    // The production pool (DeepThinkingPool, ThinkLonger.Places) of three paired computers' Deep thinking roles: diva and
    // ripley do none of the conversation's jobs, imouto also speaks. The production job list places each think as the desktop
    // starts it (BackgroundJobs.Start with the pool, waiting in line when no place may take it): think-1 on diva, think-2 on
    // ripley (both working at once, each on its own fixture endpoint standing in for that computer, through a runtime of its
    // own as the desktop's slots do). A long job never takes the pool's last free slot while the pool has two or more slots,
    // so think-3 waits in line and imouto's slot stays free: a quick job (a screen summary through the production
    // ThinkingJobBoard on the same broker, a simulated member, NOT a model) takes it at once. Think-3 then starts on the first
    // of diva and ripley to free up, never on imouto; once they finish every place is free and the next goes to diva.
    private static async Task<(bool Ok, object Report)> PoolAsync(TimeSpan reasoning, CancellationToken cancellation)
    {
        var settings = new ThinkLongerSettings();
        var localThinking = Chat(SetupRole.Llm, GenerationSupport.LocalOllamaChatBaseUrl, "gemma4:e4b");
        var routes = new[] { localThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "imouto", "https://imouto.local:9443") };
        static DeepThinkingSettings Role(string id) => Host(id) with { HostRouteId = SelfHostSetup.DeepThinkingRouteId };
        var deep = Role("diva").WithPool([Role("imouto"), Role("ripley")]);
        var pool = DeepThinkingPool.For(deep, routes);
        var places = ThinkLonger.Places(pool);
        var kind = ThinkLonger.Kind(settings, ThinkLonger.Slots(places));
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        foreach (var place in places) fixtures[place.Id] = new Fixture(reasoning);
        var runtimes = new List<ConversationRuntime>();
        try
        {
            using var jobs = new BackgroundJobs();
            var conversationInput = new BoundedTextInput(Asked, Persona, [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey!")],
                tools: ThinkLonger.Definitions(settings, ThinkLonger.Slots(places)));
            BackgroundJobStart Start() => jobs.Start(kind, ThinkLonger.Label(TaskText), (job, token) =>
            {
                var fixture = fixtures[job.Place!.Id];
                var runtime = ConversationRuntime.Create(new NoCredentials());
                lock (runtimes) runtimes.Add(runtime);
                var bounds = new ThinkBounds(BoundedTextInput.HardMaxInputUtf8Bytes, BoundedTextInput.HardMaxHistoryMessages, 24_576, Tools: false);
                return new BackgroundThink(runtime, left =>
                {
                    var input = ThinkLonger.Fit(ThinkLonger.Input(conversationInput, Acknowledged, TaskText, null, null), bounds);
                    return (new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model),
                        ThinkLonger.Limits(ReplyLimits, settings.HowHard, left), ThinkLonger.TurnLimits(left),
                        chat: new ChatCompletionsTarget(fixture.BaseUrl, true),
                        generation: new GenerationSettings { Reasoning = true, ReasoningEffort = GenerationSupport.ReasoningEffortOn }),
                        new Permissions(ChatCompletionsSetup.BaseUri(fixture.BaseUrl)));
                }).RunAsync(job, token);
            }, places, wait: true);
            var first = Start();
            var second = Start();
            var waited = Stopwatch.StartNew();
            bool BothThinking() => first.Job?.Place is { } a && second.Job?.Place is { } b &&
                fixtures[a.Id].Count("think", inFlight: true) == 1 && fixtures[b.Id].Count("think", inFlight: true) == 1;
            while (!BothThinking() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, cancellation);
            var together = BothThinking();
            // The only free slot (imouto's) is the pool's last one, so the third think waits in line for a free computer.
            var third = Start();
            waited.Restart();
            while (third.Job is { } inLine && !(jobs.Places.Line.Contains(inLine.Id) && inLine.Progress is not null) &&
                waited.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10, cancellation);
            var heldWhileBusy = jobs.Places.Leases.Select(lease => new { place = lease.Place.Name, by = lease.Holder }).ToArray();
            var thirdWaiting = third.Job is { Place: null, State: BackgroundJobState.Waiting } && jobs.Places.Line.Count == 1;
            var thirdProgress = third.Job?.Progress;
            // A quick job on the same broker takes the slot the thinks leave free, at once, while the third think still waits.
            object[] heldDuringQuick = [];
            var board = new ThinkingJobBoard(jobs.Places, () => places, (member, _, _) =>
            {
                heldDuringQuick = [.. jobs.Places.Leases.Select(lease => new { place = lease.Place.Name, by = lease.Holder })];
                return Task.FromResult(ThinkingAnswer.Done(member.Name));
            });
            var quickTimer = Stopwatch.StartNew();
            var quick = await board.RunAsync(new ThinkingJob
            {
                Kind = ThinkingJobKind.Digest, Instructions = "Say what is on the screen in one line.", Text = "fixture",
                Timeout = TimeSpan.FromSeconds(5), DropWhenStale = true
            }, cancellation);
            var quickMs = quickTimer.Elapsed.TotalMilliseconds;
            var thirdWaitedForQuick = third.Job is { Place: null };
            BackgroundJob[] started = [.. new[] { first, second, third }.Where(s => s.Started).Select(s => s.Job!)];
            waited.Restart();
            while (started.Any(job => !job.Finished) && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20, cancellation);
            var freed = jobs.Places.Leases.Count == 0 && jobs.Places.Line.Count == 0;
            // Each think's request on its own computer's fixture (none hung up on); the first two overlapping in time.
            waited.Restart();
            while (fixtures.Values.Any(fixture => fixture.Count("think", inFlight: true) > 0) && waited.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10, cancellation);
            var spans = fixtures.Values.SelectMany(fixture => fixture.Served("think")).ToArray();
            var overlapped = started.Length >= 2 && started[1].StartedUtc < started[0].FinishedUtc && started[0].StartedUtc < started[1].FinishedUtc;
            var again = Start();
            var againPlace = again.Job?.Place?.Name;
            if (again.Job is { } fourth) jobs.Cancel(fourth.Id, BackgroundJob.CanceledByMartlet);
            var thirdOn = third.Job?.Place?.Name;
            var tool = ThinkLonger.Description(settings, ThinkLonger.Slots(places));
            var ok = pool.Usable.Count == 3 && places.Count == 3 && ThinkLonger.Slots(places) == 3 && kind.MaxActive == 6 &&
                ThinkLonger.AtOnce(places) == 2 && tool.EndsWith("Up to 2 at once; more wait in line.", StringComparison.Ordinal) &&
                pool.Plan.Why.StartsWith("Up to 2 thinks run at once", StringComparison.Ordinal) &&
                started.Length == 3 && started[0].Place?.Name == "diva" && started[1].Place?.Name == "ripley" &&
                together && overlapped && started.All(job => job.State == BackgroundJobState.Succeeded && job.Result == Lyrics) &&
                heldWhileBusy.Length == 2 && thirdWaiting && third.Queued == "think-1 on diva and think-2 on ripley" &&
                quick is { Succeeded: true, Member: "imouto" } && quickMs < 1000 && heldDuringQuick.Length == 3 && thirdWaitedForQuick &&
                thirdOn is "diva" or "ripley" && freed && againPlace == "diva" && spans.Length == 3 && spans.All(s => s is { Aborted: false });
            return (ok, new
            {
                ok,
                configured = pool.Spots.Select(spot => new
                {
                    computer = spot.Computer, where = spot.Settings.Describe(), available = spot.Plan.Available, rank = spot.Plan.Rank, why = spot.Plan.Why
                }),
                maxThinks = ThinkLonger.Slots(places), atOnce = ThinkLonger.AtOnce(places), plan = pool.Plan.Why, tool,
                placed = started.Select(job => new
                {
                    id = job.Id, place = job.Place?.Name, state = job.State.ToString(), finishedAfterMs = (long)job.Elapsed.TotalMilliseconds
                }),
                thinkingAtOnce = together, overlapped, heldWhileBusy,
                waitedInLine = new
                {
                    id = third.Job?.Id, waiting = thirdWaiting, progress = thirdProgress, behind = third.Queued, startedOn = thirdOn,
                    why = "A long job never takes the pool's last free slot while the pool has two or more slots."
                },
                quickJob = new
                {
                    kind = ThinkingJobKinds.Name(ThinkingJobKind.Digest), outcome = quick.Outcome.ToString(), on = quick.Member,
                    tookMs = Math.Round(quickMs, 2), held = heldDuringQuick, thinkStillWaiting = thirdWaitedForQuick
                },
                freedAfter = freed, nextPlacedOn = againPlace
            });
        }
        finally
        {
            foreach (var runtime in runtimes) await runtime.DisposeAsync();
            foreach (var fixture in fixtures.Values) await fixture.DisposeAsync();
        }
    }

    // A think on a paired computer: a long conversation fitted into its Deep thinking window (32,768 tokens less the think's
    // output; the gateway takes the whole conversation that fits), the newest kept, the task last, no tools, as the production
    // fit does.
    private static (bool Ok, object Report) HostFit()
    {
        var settings = new ThinkLongerSettings();
        var history = Enumerable.Range(0, 80).SelectMany(i => new TextHistoryMessage[]
        {
            new(TextHistoryRole.User, $"Message {i}: " + new string('a', 300)), new(TextHistoryRole.Assistant, $"Answer {i}: " + new string('b', 300))
        }).ToArray();
        var conversation = new BoundedTextInput(Asked, Persona, history, tools: ThinkLonger.Definitions(settings));
        var bounds = ThinkLonger.HostBounds(settings.HowHard);
        var fitted = ThinkLonger.Fit(ThinkLonger.Input(conversation, Acknowledged, TaskText, null, null), bounds);
        var newest = fitted.History.Count >= 2 && fitted.History[^1].Text.StartsWith(Acknowledged, StringComparison.Ordinal);
        var ok = fitted.Utf8Bytes <= bounds.MaxInputBytes && fitted.History.Count <= bounds.MaxHistoryMessages && fitted.Tools.Count == 0 &&
            fitted.Personality == Persona && newest && fitted.UserText.Contains(TaskText, StringComparison.Ordinal);
        return (ok, new
        {
            ok, bytes = fitted.Utf8Bytes, maxBytes = bounds.MaxInputBytes, messages = fitted.History.Count, maxMessages = bounds.MaxHistoryMessages,
            leftOut = conversation.History.Count + 2 - fitted.History.Count, tools = fitted.Tools.Count, persona = fitted.Personality is not null,
            newestKept = newest, inputTokens = bounds.MaxInputTokens, outputTokens = ThinkLonger.OutputTokens(settings.HowHard)
        });
    }

    // A second model in Ollama on this PC, beside Thinking's: the production side-by-side check (OllamaSideBySide) reads a fixture
    // Ollama's /api/ps and /api/tags on 127.0.0.1 and decides for graphics cards of several sizes; after the think's model loaded,
    // it stops the think when Thinking's was pushed off the card.
    private static async Task<(bool Ok, object Report)> SideBySideAsync(CancellationToken cancellation)
    {
        const long gib = 1L << 30;
        const string thinking = "gemma4:e4b", large = "gemma4:12b", small = "qwen3:1.7b";
        var ps = JsonSerializer.Serialize(new { models = new[] { new { name = thinking, model = thinking, size = 7 * gib, size_vram = 7 * gib } } });
        var tags = JsonSerializer.Serialize(new
        {
            models = new[]
            {
                new { name = thinking, size = 6_583_656_505L }, new { name = large, size = 8_021_618_941L }, new { name = small, size = 1_400_000_000L }
            }
        });
        var answers = new Dictionary<string, string> { ["/api/ps"] = ps, ["/api/tags"] = tags };
        // A minimal Ollama on 127.0.0.1: each path answers with its canned JSON.
        var server = LoopbackHttpServer.Answering(request => answers.TryGetValue(request.Target, out var json)
            ? (200, "application/json", json) : (404, "application/json", "{\"error\":\"not found\"}"));
        IReadOnlyList<OllamaLoadedModel>? loaded;
        IReadOnlyDictionary<string, long>? downloads;
        try
        {
            using var client = new HttpClient();
            var origin = new Uri(server.Origin + "/");
            loaded = await OllamaSideBySide.LoadedAsync(client, origin, cancellation);
            downloads = await OllamaSideBySide.DownloadsAsync(client, origin, cancellation);
        }
        finally
        {
            await server.DisposeAsync();
        }
        var read = loaded is [{ Name: thinking, OnGraphicsCard: true }] && downloads is { Count: 3 };
        loaded ??= [];
        downloads ??= new Dictionary<string, long>();
        var cases = new (string Name, string Deep, IReadOnlyList<OllamaLoadedModel> Loaded, GraphicsMemory? Memory, bool Fits)[]
        {
            ("gemma4:12b on a 24 GB card", large, loaded, new(24 * gib, 15 * gib / 2), true),
            ("gemma4:12b on a 12 GB card", large, loaded, new(12 * gib, 15 * gib / 2), false),
            ("a small model on a 12 GB card", small, loaded, new(12 * gib, 15 * gib / 2), true),
            ("a small model on a 12 GB card a game fills", small, loaded, new(12 * gib, 11 * gib), false),
            ("a small model, only Windows' total known", small, loaded, new(12 * gib, null), true),
            ("graphics memory unknown", small, loaded, null, false),
            ("Thinking's own model", thinking, loaded, new(24 * gib, 15 * gib / 2), false),
            ("a model that isn't downloaded", "llama3.3:70b", loaded, new(24 * gib, 15 * gib / 2), false),
            ("both already loaded on the card", large, [.. loaded, new(large, 9 * gib, 9 * gib)], new(12 * gib, 11 * gib), true),
            ("Thinking's already partly on the processor", small, [new(thinking, 7 * gib, 3 * gib)], new(48 * gib, 3 * gib), false)
        };
        var decided = cases.Select(c => (c, Fit: OllamaSideBySide.Decide(thinking, c.Deep, c.Loaded, downloads, c.Memory))).ToArray();
        var watched = new (string Name, OllamaLoadedModel[] Now, bool Stops)[]
        {
            ("still loading", [new(thinking, 7 * gib, 7 * gib)], false),
            ("both on the card", [new(thinking, 7 * gib, 7 * gib), new(large, 9 * gib, 9 * gib)], false),
            ("Thinking's unloaded for it", [new(large, 9 * gib, 9 * gib)], true),
            ("Thinking's pushed partly off the card", [new(thinking, 7 * gib, 4 * gib), new(large, 9 * gib, 9 * gib)], true)
        };
        var stops = watched.Select(w => (w, Why: OllamaSideBySide.PushedOut(thinking, large, w.Now))).ToArray();
        var ok = read && decided.All(d => d.Fit.Fits == d.c.Fits) && stops.All(s => s.Why is not null == s.w.Stops);
        return (ok, new
        {
            ok, readFromOllama = read, loaded = loaded.Select(m => new { m.Name, m.Size, m.SizeVram }), downloads,
            decisions = decided.Select(d => new
            {
                name = d.c.Name, fits = d.Fit.Fits, expected = d.c.Fits, needGb = Gb(d.Fit.NeedBytes), roomGb = Gb(d.Fit.RoomBytes), why = d.Fit.Why
            }),
            afterLoading = stops.Select(s => new { name = s.w.Name, stops = s.Why is not null, expected = s.w.Stops, why = s.Why })
        });

        static double? Gb(long? bytes) => bytes is { } b ? Math.Round(b / (double)gib, 1) : null;
    }
}

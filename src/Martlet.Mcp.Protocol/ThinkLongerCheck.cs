using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>think_longer_status and think_longer_check: Companion › Replies › Thinking longer. The status reads the saved
/// settings, what the Thinking model is offered and the desktop's background-jobs.json. The check rehearses the production
/// background-job scheduler (<see cref="BackgroundJobs"/>), the think runner (<see cref="BackgroundThink"/>), the tool texts and
/// request layout (<see cref="ThinkLonger"/>), the conversation runtime and the Chat Completions adapter against a fixture
/// endpoint on 127.0.0.1 that answers with canned text (NOT AI). Nothing leaves loopback; no credentials are read.</summary>
internal static class ThinkLongerCheck
{
    private const string Model = "fixture-model";
    private const string Persona = "You are Martlet, a cheerful companion who talks with the user.";
    private const string Asked = "Can you write me lyrics for a short song about my cat Biscuit?";
    private const string Acknowledged = "Ooh, a song for Biscuit! Let me think about that one, give me a bit.";
    private const string TaskText = "Write lyrics for a short, cheerful song about the user's cat Biscuit: two verses and a chorus.";
    private const string Lyrics = "Verse 1: Biscuit on the windowsill, sunbeams in her fur.\nChorus: Oh Biscuit, Biscuit, purr purr purr.\n" +
        "Verse 2: Chasing string across the floor, napping by the door.";
    private const string Report = "Guess what, I finished that song for Biscuit! Want to hear it?";

    // ---------- think_longer_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var generation = loaded.Settings?.Generation;
        var settings = ThinkLongerSettings.Of(generation);
        var route = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var supportsTools = route?.RouteType is SetupRouteType.OpenAi or SetupRouteType.ChatCompletions;
        var local = route is not null && (ContextBudget.IsLocalOllama(route.RouteType, route.Origin) ||
            route.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(route.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback);
        var rejected = route is not null && ToolsRejected(dataDirectory, $"{route.RouteType}|{route.Origin}|{route.ModelId}");
        var effort = settings.HowHard == ThinkEffort.High ? GenerationSupport.ReasoningEffortHigh : GenerationSupport.ReasoningEffortOn;
        var (deep, deepState) = DeepThinkingSettings.Read(dataDirectory);
        var plan = DeepThinkingPlan.For(deep, loaded.Settings?.Setup?.Routes ?? []);
        (SetupRouteType? Type, string? Origin) deepRoute = deep.Place switch
        {
            DeepThinkingPlace.Host => (SetupRouteType.GatewayOllama, null),
            DeepThinkingPlace.Endpoint => (SetupRouteType.ChatCompletions, deep.Origin),
            _ => (route?.RouteType, route?.Origin)
        };
        return new
        {
            settings = loaded.State switch { SettingsLoadState.Loaded => "loaded", SettingsLoadState.FirstRun => "none", _ => "unreadable" },
            thinkLonger = new
            {
                enabled = settings.On, effort = settings.HowHard.ToString(), minutes = (int)settings.TimeLimit.TotalMinutes,
                perHour = settings.Hourly, delivery = settings.When.ToString(), chosen = generation?.ThinkLonger is not null
            },
            thinking = route is null ? null : new
            {
                routeType = route.RouteType?.ToString() ?? "OpenAi", model = route.ModelId, supportsTools, toolsRejected = rejected,
                offered = settings.On && supportsTools && !rejected,
                onThisPc = local
            },
            deepThinking = new
            {
                file = deepState, place = deep.Place.ToString(), where = deep.Separate ? deep.Describe() : route?.ModelId,
                model = deep.Separate ? deep.ModelId : route?.ModelId, hostId = deep.HostId,
                origin = deep.Place == DeepThinkingPlace.Endpoint ? deep.Origin : null,
                ownKey = deep.CredentialId is not null, usesThinkingKey = deep.UsesThinkingKey(route),
                parallel = plan.Parallel, waitsForQuiet = !plan.Parallel, why = plan.Why,
                thinkingSteps = deepRoute.Type is null ? "Unused" : GenerationSupport.Use(deepRoute.Type, deepRoute.Origin, GenerationSetting.Reasoning).ToString(),
                sends = deepRoute.Type == SetupRouteType.GatewayOllama ? "{\"think\":true}"
                    : GenerationSupport.ReasoningJson(deepRoute.Type, deepRoute.Origin, true, effort),
                outputTokens = ThinkLonger.OutputTokens(settings.HowHard),
                carriesTools = !deep.Separate
            },
            tools = ThinkLonger.Definitions(settings).Select(tool => new
            {
                name = tool.Name, description = tool.Description, parameters = JsonNode.Parse(tool.ParametersJson)
            }).ToArray(),
            prompt = ThinkLonger.Instructions(settings, loaded.Settings?.Prompts),
            jobs = Jobs(dataDirectory)
        };
    }

    // What the desktop wrote last (kinds, states and times only), or why there is nothing.
    private static object Jobs(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "background-jobs.json");
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't run a conversation with this data directory." };
            if (new FileInfo(path).Length > 262_144) return new { state = "unreadable", why = "background-jobs.json is too large." };
            return new { state = "loaded", file = JsonNode.Parse(File.ReadAllText(path)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
    }

    private static bool ToolsRejected(string dataDirectory, string key)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "tools-unsupported.json");
            return File.Exists(path) && new FileInfo(path).Length <= 65_536 &&
                JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) is { } rejected &&
                rejected.TryGetValue(key, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(7);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    // ---------- think_longer_check ----------

    internal static async Task<object> RunAsync(int? reasoningMs, CancellationToken cancellation)
    {
        var reasoning = TimeSpan.FromMilliseconds(reasoningMs ?? 1200);
        if (reasoning < TimeSpan.FromMilliseconds(200) || reasoning > TimeSpan.FromSeconds(3))
            throw new ArgumentException("'reasoningMs' must be 200 through 3000.");
        await using var fixture = new Fixture(reasoning);
        var flow = await FlowAsync(fixture, cancellation);
        var limits = await LimitsAsync(cancellation);
        var local = await LocalAsync(fixture, cancellation);
        var plans = Plans();
        await using var other = new Fixture(reasoning * 3);
        var parallel = await ParallelAsync(fixture, other, cancellation);
        var host = HostFit();
        return new
        {
            ok = flow.Ok && limits.Ok && local.Ok && plans.Ok && parallel.Ok && host.Ok,
            endpoint = fixture.BaseUrl,
            note = "Fixture endpoints on 127.0.0.1 with canned replies (NOT AI); the scheduler, runner, tool texts, request layout, " +
                "Deep thinking plan, runtime and adapter are Martlet's own.",
            flow = flow.Report, limits = limits.Report, local = local.Report, plans = plans.Report, parallel = parallel.Report,
            hostFit = host.Report
        };
    }

    // ---------- Deep thinking: where a think runs and whether it waits ----------

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

    // The production plan (DeepThinkingPlan) for the setups that matter: a think waits for quiet moments only when it shares
    // the conversation's hardware.
    private static (bool Ok, object Report) Plans()
    {
        const string ollama = GenerationSupport.LocalOllamaChatBaseUrl, openRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
        var localThinking = Chat(SetupRole.Llm, ollama, "gemma4:e4b");
        var cloudThinking = Chat(SetupRole.Llm, openRouter, "x-ai/grok-4.3");
        var cases = new (string Name, DeepThinkingSettings Deep, SetupRoute[] Routes, bool Parallel)[]
        {
            ("Same as Thinking, Thinking in Ollama on this PC", new(), [localThinking], false),
            ("Same as Thinking, Thinking on OpenRouter", new(), [cloudThinking], true),
            ("OpenRouter, Thinking in Ollama on this PC", new() { Place = DeepThinkingPlace.Endpoint, Origin = openRouter, ModelId = "x-ai/grok-4.3" },
                [localThinking], true),
            ("Ollama on this PC (another model), Thinking in Ollama on this PC",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:12b" }, [localThinking], false),
            ("Ollama on this PC, Thinking on OpenRouter and the voice on diva",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:12b" },
                [cloudThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "diva", "https://diva.local:9443")], true),
            ("Ollama on this PC, Thinking on OpenRouter and the voice in this PC's host service",
                new() { Place = DeepThinkingPlace.Endpoint, Origin = ollama, ModelId = "gemma4:12b" },
                [cloudThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "this-pc", "https://127.0.0.1:9443")], false),
            ("diva, Thinking in Ollama on this PC and the voice on imouto", Host("diva"),
                [localThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "imouto", "https://imouto.local:9443")], true),
            ("diva, the voice on diva too", Host("diva"),
                [localThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "diva", "https://diva.local:9443")], false)
        };
        var results = cases.Select(c => (c, Plan: DeepThinkingPlan.For(c.Deep, c.Routes))).ToArray();
        var ok = results.All(r => r.Plan.Parallel == r.c.Parallel);
        return (ok, new
        {
            ok,
            cases = results.Select(r => new { name = r.c.Name, where = r.c.Deep.Separate ? r.c.Deep.Describe() : "the Thinking model",
                parallel = r.Plan.Parallel, expected = r.c.Parallel, why = r.Plan.Why })
        });
    }

    // A think on a destination of its own (the plan says parallel): it runs on its own endpoint (a second fixture, standing in for
    // the other machine) while three replies go to the conversation's, and is never stopped. Its request is fitted to the
    // destination: the conversation and the task, no tools, Thinking steps on.
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
        }, plan.Parallel ? null : () => true);
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
        var ok = plan.Parallel && thinkingDuringReplies && firstWords.All(ms => ms is >= 0 and < 1000) && job.State == BackgroundJobState.Succeeded &&
            job.Result == Lyrics && think.Attempts == 1 && think.Pauses == 0 && other.Served("think").Skip(before).All(s => !s.Aborted) &&
            sent?["tools"] is null && sent?["reasoning_effort"] is null && sent?["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == true &&
            messages.Count == 6 && last.Contains(TaskText, StringComparison.Ordinal);
        return (ok, new
        {
            ok, plan = new { parallel = plan.Parallel, why = plan.Why },
            thinkingWhileReplying = thinkingDuringReplies, replyFirstWordsMs = firstWords,
            think = new { state = job.State.ToString(), attempts = think.Attempts, pauses = think.Pauses, finishedAfterMs = (long)job.Elapsed.TotalMilliseconds },
            request = new { tools = sent?["tools"] is not null, thinking = sent?["chat_template_kwargs"]?.ToJsonString(), messages = messages.Count,
                taskLast = last.Contains(TaskText, StringComparison.Ordinal) }
        });
    }

    // A think on a paired computer: a long conversation fitted into its gateway's 16 KiB and 16 messages (the newest kept, the
    // task last, no tools), as the production fit does.
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

    // A reply that calls think_longer, the background request, and bringing the result up as a message at the end.
    private static async Task<(bool Ok, object Report)> FlowAsync(Fixture fixture, CancellationToken cancellation)
    {
        var settings = new ThinkLongerSettings();
        using var jobs = new BackgroundJobs();
        await using var replies = ConversationRuntime.Create(new NoCredentials());
        await using var thinking = ConversationRuntime.Create(new NoCredentials());
        var permissions = new Permissions(ChatCompletionsSetup.BaseUri(fixture.BaseUrl));
        var instructions = string.Join("\n\n", Persona, PromptSettings.Fill(null, PromptCatalog.Tools), ThinkLonger.Instructions(settings, null));
        TextHistoryMessage[] history = [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey, you're back!")];
        var input = new BoundedTextInput(Asked, instructions, history, tools: ThinkLonger.Definitions(settings));
        var clock = Stopwatch.StartNew();
        long toolCalledMs = -1, toolReturnedMs = -1;
        ConversationTurn? reply = null;
        BackgroundJobStart? started = null;
        var host = new Tools(call =>
        {
            toolCalledMs = clock.ElapsedMilliseconds;
            var (task, reason, problem) = ThinkLonger.Parse(call.ArgumentsJson);
            if (problem is not null) return new(problem, true);
            var think = new BackgroundThink(thinking, left => (ThinkRequest(fixture, input, reply!.Content.Text, task!, reason, settings, left),
                permissions), busy: null);
            started = jobs.Start(ThinkLonger.Kind(settings), ThinkLonger.Label(task!), think.RunAsync);
            var told = !string.IsNullOrWhiteSpace(reply!.Content.Text);
            toolReturnedMs = clock.ElapsedMilliseconds;
            return started.Job is { } job ? new(ThinkLonger.Started(job, told)) : new(ThinkLonger.Refused(started), true);
        });
        var request = new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ReplyLimits,
            new ConversationLimits { TurnTimeout = TimeSpan.FromSeconds(140), MaxToolRounds = 4 }, chat: new ChatCompletionsTarget(fixture.BaseUrl, true),
            generation: new GenerationSettings { Reasoning = false }, tools: host);
        reply = replies.Start(request, permissions, cancellation);
        var replied = await reply.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        await reply.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        var replyMs = clock.ElapsedMilliseconds;
        var replyBody = fixture.Bodies("reply").FirstOrDefault();

        // The think runs on its own; wait for it to finish.
        var job = started?.Job;
        var waited = Stopwatch.StartNew();
        while (job is { Finished: false } && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50, cancellation);
        var thinkBody = fixture.Bodies("think").FirstOrDefault();
        var layout = Layout(replyBody, thinkBody);

        // Delivery: as soon as Martlet is free, a reply of its own whose message is Martlet's note with the result.
        var news = jobs.HasNews;
        var delivery = jobs.Take(onItsOwn: true);
        object? report = null;
        var reportOk = false;
        if (delivery is not null)
        {
            var message = BackgroundJobs.ReportMessage(null, delivery.Jobs);
            // The conversation as the desktop keeps it: the reply's message as sent, and what the reply said.
            var kept = history.Append(new(TextHistoryRole.User, input.SentUserText)).Append(new(TextHistoryRole.Assistant, reply.Content.Text));
            var reportInput = new BoundedTextInput(message.UserText, instructions, kept, tools: ThinkLonger.Definitions(settings));
            var reportTurn = replies.Start(new ConversationRequest(reportInput, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ReplyLimits,
                new ConversationLimits { TurnTimeout = TimeSpan.FromSeconds(140), MaxToolRounds = 4 }, chat: new ChatCompletionsTarget(fixture.BaseUrl, true),
                generation: new GenerationSettings { Reasoning = false }, tools: host), permissions, cancellation);
            var reported = await reportTurn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await reportTurn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            if (reported.State == ConversationState.Completed) delivery.Complete();
            else delivery.Return();
            var reportBody = fixture.Bodies("report").FirstOrDefault();
            var sameStart = replyBody is not null && reportBody is not null && SameStart(Messages(replyBody), Messages(reportBody), Messages(replyBody).Count);
            reportOk = reported.State == ConversationState.Completed && reportTurn.Content.Text == Report && sameStart &&
                message.UserText.Contains(Lyrics, StringComparison.Ordinal) && job?.Delivery == BackgroundDeliveryState.Delivered && !jobs.HasNews;
            report = new
            {
                ok = reportOk, state = reported.State.ToString(), said = reportTurn.Content.Text, messageCarriesResult = message.UserText.Contains(Lyrics),
                startsLikeTheReply = sameStart, delivery = job?.Delivery.ToString(), newsAfter = jobs.HasNews,
                message = message.UserText.Length > 400 ? message.UserText[..400] + "…" : message.UserText
            };
        }
        // The notes of the next message when Martlet shares results when you talk next.
        var notes = job is null ? null : BackgroundJobs.ReportNotes(null, [job]);

        var replyOk = replied.State == ConversationState.Completed && reply.Content.Text.Trim() == Acknowledged && replied.ToolCalls == 1 &&
            toolReturnedMs >= 0 && toolReturnedMs - toolCalledMs < 100 && replied.FirstTextAfter?.TotalMilliseconds < toolCalledMs + 1;
        var thinkOk = job is { State: BackgroundJobState.Succeeded } && job.Result == Lyrics && layout.Ok;
        var ok = replyOk && thinkOk && news && reportOk && notes?.Contains(Lyrics, StringComparison.Ordinal) == true;
        return (ok, new
        {
            ok,
            reply = new
            {
                ok = replyOk, state = replied.State.ToString(), said = reply.Content.Text, toolCalls = replied.ToolCalls,
                firstWordsMs = (long?)replied.FirstTextAfter?.TotalMilliseconds, toolCalledMs, toolReturnedMs,
                toolTookMs = toolReturnedMs - toolCalledMs, replyDoneMs = replyMs,
                toolsOffered = replyBody is null ? null : ToolNames(replyBody), tookToolResult = started?.Job is not null
            },
            think = new
            {
                ok = thinkOk, id = job?.Id, state = job?.State.ToString(), result = job?.Result, finishedAfterMs = (long?)job?.Elapsed.TotalMilliseconds,
                layout = layout.Report
            },
            delivery = new { hadNews = news, report, notesCarryResult = notes?.Contains(Lyrics, StringComparison.Ordinal) }
        });
    }

    // The background request continues the reply's: the same instructions, tools and messages, then what the reply said and the
    // task; Thinking steps on, and the Medium output budget.
    private static (bool Ok, object Report) Layout(string? replyBody, string? thinkBody)
    {
        if (replyBody is null || thinkBody is null) return (false, new { ok = false, why = "The fixture didn't get both requests." });
        var reply = JsonNode.Parse(replyBody)!.AsObject();
        var think = JsonNode.Parse(thinkBody)!.AsObject();
        var replyMessages = Messages(replyBody);
        var thinkMessages = Messages(thinkBody);
        var sameStart = SameStart(replyMessages, thinkMessages, replyMessages.Count);
        var then = thinkMessages.Skip(replyMessages.Count).Select(m => (string?)m["role"]).ToArray();
        var last = thinkMessages.LastOrDefault()?["content"]?.GetValue<string>() ?? "";
        var sameTools = JsonNode.DeepEquals(reply["tools"], think["tools"]);
        var thinkingOn = think["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == true;
        var replyThinkingOff = reply["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false;
        var budget = think["max_tokens"]?.GetValue<int>();
        var ok = sameStart && then is ["assistant", "user"] && last.Contains(TaskText, StringComparison.Ordinal) && sameTools && thinkingOn &&
            replyThinkingOff && budget == ThinkLonger.MediumOutputTokens;
        return (ok, new
        {
            ok, replyMessages = replyMessages.Count, sameStart, then, taskLast = last.Contains(TaskText, StringComparison.Ordinal), sameTools,
            replyThinking = reply["chat_template_kwargs"]?.ToJsonString(), thinkThinking = think["chat_template_kwargs"]?.ToJsonString(),
            replyBudget = reply["max_tokens"]?.GetValue<int>(), thinkBudget = budget
        });
    }

    // The limits, cancellation and delivery rules of the production scheduler, with runners that wait on the check (no network).
    private static async Task<(bool Ok, object Report)> LimitsAsync(CancellationToken cancellation)
    {
        using var jobs = new BackgroundJobs();
        var kind = new BackgroundJobKind(ThinkLonger.KindName, 1, 2, TimeSpan.FromSeconds(1), Doing: "Thinking about");
        var song = new BackgroundJobKind("song", 1, 3, TimeSpan.FromMinutes(15), Offer: true, Doing: "Making a song");
        static async Task<BackgroundJobOutcome> Forever(BackgroundJob job, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return BackgroundJobOutcome.Failed("unreachable");
        }
        async Task Until(Func<bool> done)
        {
            var waited = Stopwatch.StartNew();
            while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, cancellation);
        }
        var first = jobs.Start(kind, "first", Forever);
        var second = jobs.Start(kind, "second", Forever);
        var songJob = jobs.Start(song, "a song", Forever);
        var together = jobs.Active.Count;
        // The user's Cancel: stopped, only mentioned with the next message (never brought up on Martlet's own).
        jobs.Cancel(first.Job!.Id, BackgroundJob.CanceledByYou);
        await Until(() => first.Job.Finished);
        var proactive = jobs.Take(onItsOwn: true);
        var withMessage = jobs.Take(onItsOwn: false);
        var notes = withMessage is null ? null : BackgroundJobs.ReportNotes(null, withMessage.Jobs);
        withMessage?.Return();
        var returned = first.Job.Delivery;
        jobs.Take(onItsOwn: false)?.Complete();
        // The time limit.
        var timed = jobs.Start(kind, "timed", Forever);
        await Until(() => timed.Job?.Finished == true);
        var hourly = jobs.Start(kind, "third", Forever);
        // Martlet's own cancel: nothing to bring up later.
        jobs.Cancel(null, BackgroundJob.CanceledByMartlet, "song");
        await Until(() => songJob.Job!.Finished);
        var lateSong = jobs.Start(song, "another song", Forever);
        // The conversation ending stops everything and drops what wasn't brought up.
        jobs.CancelAll();
        await Until(() => lateSong.Job!.Finished);
        var timedOut = timed.Job;
        var songRun = songJob.Job!;
        var lateRun = lateSong.Job!;
        var ok = first.Started && second.Refusal == "busy" && songJob.Started && together == 2 &&
            first.Job.State == BackgroundJobState.Canceled && first.Job.CanceledBy == BackgroundJob.CanceledByYou && proactive is null &&
            withMessage is not null && notes?.Contains("canceled", StringComparison.Ordinal) == true && returned == BackgroundDeliveryState.Pending &&
            first.Job.Delivery == BackgroundDeliveryState.Delivered &&
            timedOut is { State: BackgroundJobState.TimedOut } && hourly.Refusal == "hourly_limit" &&
            songRun.State == BackgroundJobState.Canceled && songRun.Delivery == BackgroundDeliveryState.Delivered &&
            lateRun.State == BackgroundJobState.Canceled && lateRun.Delivery == BackgroundDeliveryState.Dropped && !jobs.HasNews;
        return (ok, new
        {
            ok,
            oneAtATime = new { first = first.Job.Id, second = second.Refusal, told = ThinkLonger.Refused(second) },
            besideASong = new { song = songRun.Id, activeTogether = together, offer = song.Offer },
            youCanceled = new
            {
                state = first.Job.State.ToString(), by = first.Job.CanceledBy, broughtUpOnItsOwn = proactive is not null,
                withYourNextMessage = withMessage is not null, notes, afterReturn = returned.ToString(), afterNextMessage = first.Job.Delivery.ToString()
            },
            timeLimit = new { state = timedOut?.State.ToString(), problem = timedOut?.Problem, afterMs = (long?)timedOut?.Elapsed.TotalMilliseconds },
            hourlyLimit = new { refusal = hourly.Refusal, message = hourly.Message, startedThisHour = jobs.StartedWithinHour(ThinkLonger.KindName) },
            martletCanceled = new { state = songRun.State.ToString(), delivery = songRun.Delivery.ToString() },
            conversationEnded = new { state = lateRun.State.ToString(), delivery = lateRun.Delivery.ToString() }
        });
    }

    // A model on this PC: the think waits for a quiet moment, stops at once when the conversation needs the model and starts
    // again from the same request once it's quiet.
    private static async Task<(bool Ok, object Report)> LocalAsync(Fixture fixture, CancellationToken cancellation)
    {
        var settings = new ThinkLongerSettings();
        using var jobs = new BackgroundJobs();
        await using var thinking = ConversationRuntime.Create(new NoCredentials());
        var permissions = new Permissions(ChatCompletionsSetup.BaseUri(fixture.BaseUrl));
        var conversation = new BoundedTextInput(Asked, Persona, tools: ThinkLonger.Definitions(settings));
        var busy = 1;
        var think = new BackgroundThink(thinking, left => (ThinkRequest(fixture, conversation, Acknowledged, TaskText, null, settings, left), permissions),
            () => Volatile.Read(ref busy) != 0);
        var before = fixture.Count("think");
        var started = jobs.Start(ThinkLonger.Kind(settings), ThinkLonger.Label(TaskText), think.RunAsync);
        var job = started.Job!;
        await Task.Delay(400, cancellation);
        var waiting = (State: job.State, job.Progress, Sent: fixture.Count("think") - before);
        // Quiet: it starts. Then the user talks a moment later: it stops at once.
        Volatile.Write(ref busy, 0);
        var waited = Stopwatch.StartNew();
        while (fixture.Count("think", inFlight: true) == 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        await Task.Delay(250, cancellation);
        var busyAt = fixture.Now;
        Volatile.Write(ref busy, 1);
        think.Yield();
        waited.Restart();
        while (fixture.Count("think", inFlight: true) > 0 && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5, cancellation);
        var stoppedAfterMs = fixture.Now - busyAt;
        await Task.Delay(500, cancellation);
        var paused = (State: job.State, job.Progress, Sent: fixture.Count("think") - before);
        Volatile.Write(ref busy, 0);
        waited.Restart();
        while (!job.Finished && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20, cancellation);
        var bodies = fixture.Bodies("think").Skip(before).ToArray();
        var aborted = fixture.Served("think").Skip(before).Count(s => s.Aborted);
        var sameRequest = bodies.Length == 2 && JsonNode.DeepEquals(JsonNode.Parse(bodies[0]), JsonNode.Parse(bodies[1]));
        var ok = waiting.State == BackgroundJobState.Waiting && waiting.Sent == 0 && stoppedAfterMs < 300 && paused.Sent == 1 &&
            paused.State == BackgroundJobState.Paused && job.State == BackgroundJobState.Succeeded && job.Result == Lyrics &&
            think.Attempts == 2 && think.Pauses == 1 && aborted == 1 && sameRequest;
        return (ok, new
        {
            ok,
            waitedForQuiet = new { state = waiting.State.ToString(), progress = waiting.Progress, requestsSent = waiting.Sent },
            stoppedForTheConversation = new { stoppedAfterMs, whilePaused = new { state = paused.State.ToString(), progress = paused.Progress, requestsSent = paused.Sent } },
            resumed = new { state = job.State.ToString(), attempts = think.Attempts, pauses = think.Pauses, abortedRequests = aborted, sameRequestAgain = sameRequest }
        });
    }

    private static readonly TextGenerationLimits ReplyLimits = new()
    {
        MaxInputBytes = BoundedTextInput.HardMaxInputUtf8Bytes, MaxInputTokens = 181_968, MaxOutputTokens = GenerationSettings.ChatReplyTokens,
        MaxContextTokens = 181_968 + GenerationSettings.ChatReplyTokens, MaxHistoryMessages = BoundedTextInput.HardMaxHistoryMessages,
        MaxEvents = 4094, MaxStreamBytes = 4_194_304, MaxRequestTime = TimeSpan.FromSeconds(45)
    };

    // The background request exactly as the desktop builds it (LiveConversationConfiguration.ThinkRequest).
    private static ConversationRequest ThinkRequest(Fixture fixture, BoundedTextInput conversation, string reply, string task, string? reason,
        ThinkLongerSettings settings, TimeSpan left)
    {
        var input = ThinkLonger.Input(conversation, reply, task, reason, null);
        return new(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ThinkLonger.Limits(ReplyLimits, settings.HowHard, left),
            ThinkLonger.TurnLimits(left), chat: new ChatCompletionsTarget(fixture.BaseUrl, true),
            generation: ThinkLonger.Generation(new GenerationSettings { Reasoning = false }, settings.HowHard, false),
            tools: input.Tools.Count > 0 ? ThinkLonger.NoTools : null);
    }

    private static List<JsonNode> Messages(string body) =>
        JsonNode.Parse(body)?["messages"]?.AsArray().Select(node => node!).ToList() ?? [];

    private static bool SameStart(IReadOnlyList<JsonNode> first, IReadOnlyList<JsonNode> second, int count) =>
        first.Count >= count && second.Count >= count && Enumerable.Range(0, count).All(i => JsonNode.DeepEquals(first[i], second[i]));

    private static string[] ToolNames(string body) =>
        JsonNode.Parse(body)?["tools"]?.AsArray().Select(tool => (string?)tool?["function"]?["name"] ?? "").ToArray() ?? [];

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

    // Allows exactly what was asked, bound to the fixture endpoint, as the desktop's own authorization does.
    private sealed class Permissions(Uri baseUri) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, action.Context.Deadline, true, true), new(action.Budget, action.Context.Deadline)));

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
    }

    private sealed record ServedRequest(string Kind, string Body, long StartMs, long EndMs, bool Aborted, bool Done);

    /// <summary>A Chat Completions endpoint on 127.0.0.1 (canned replies, NOT AI), one connection at a time each on its own task:
    /// the reply says it'll think it over and calls think_longer, then has nothing to add; the background think streams hidden
    /// reasoning for the given time, then the lyrics; Martlet's report says it finished. It notes each request and whether the
    /// client hung up on it.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<ServedRequest> served = [];
        private readonly Dictionary<int, (string Kind, string Body, long StartMs)> open = [];
        private readonly TimeSpan reasoning;
        private readonly Task accepting;
        private int next;

        internal Fixture(TimeSpan reasoning)
        {
            this.reasoning = reasoning;
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            accepting = AcceptAsync();
        }

        internal string BaseUrl { get; }
        internal long Now => clock.ElapsedMilliseconds;

        internal IReadOnlyList<ServedRequest> Served(string kind) { lock (served) return [.. served.Where(s => s.Kind == kind)]; }
        internal IReadOnlyList<string> Bodies(string kind)
        {
            lock (served)
                return [.. served.Where(s => s.Kind == kind).Select(s => (s.StartMs, s.Body))
                    .Concat(open.Values.Where(o => o.Kind == kind).Select(o => (o.StartMs, o.Body))).OrderBy(s => s.StartMs).Select(s => s.Body)];
        }

        internal int Count(string kind, bool inFlight = false)
        {
            lock (served) return open.Values.Count(o => o.Kind == kind) + (inFlight ? 0 : served.Count(s => s.Kind == kind));
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
            var number = Interlocked.Increment(ref next);
            string kind = "?", body = "";
            long start = Now;
            bool aborted = false, done = false;
            try
            {
                await using var stream = client.GetStream();
                body = Encoding.UTF8.GetString(await HearingCheck.ReadRequestAsync(stream, stop.Token));
                kind = Classify(body);
                start = Now;
                lock (served) open[number] = (kind, body, start);
                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                switch (kind)
                {
                    case "reply":
                        await ChunkAsync(stream, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(Acknowledged) + "}");
                        var arguments = JsonSerializer.Serialize(new { task = TaskText, reason = "Song lyrics need real writing." });
                        await ChunkAsync(stream, "{\"tool_calls\":[{\"index\":0,\"id\":\"call_think\",\"type\":\"function\",\"function\":" +
                            "{\"name\":\"think_longer\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}");
                        await FinishAsync(stream, "tool_calls");
                        break;
                    case "after-tool":
                        await FinishAsync(stream, "stop");
                        break;
                    case "think":
                        // Hidden reasoning a little at a time (it shows the model is working), then the lyrics.
                        var thought = Stopwatch.StartNew();
                        await ChunkAsync(stream, "{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":\"Thinking about rhymes.\"}");
                        while (thought.Elapsed < reasoning)
                        {
                            await Task.Delay(50, stop.Token);
                            await ChunkAsync(stream, "{\"reasoning\":\" more.\"}");
                        }
                        foreach (var line in Lyrics.Split('\n'))
                            await ChunkAsync(stream, "{\"content\":" + JsonSerializer.Serialize(line == Lyrics.Split('\n')[^1] ? line : line + "\n") + "}");
                        await FinishAsync(stream, "stop");
                        break;
                    case "chat":
                        await ChunkAsync(stream, "{\"role\":\"assistant\",\"content\":\"Sure thing, here you go.\"}");
                        await FinishAsync(stream, "stop");
                        break;
                    default:
                        await ChunkAsync(stream, "{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(Report) + "}");
                        await FinishAsync(stream, "stop");
                        break;
                }
                done = true;
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                aborted = !stop.IsCancellationRequested;
            }
            finally
            {
                lock (served)
                {
                    open.Remove(number);
                    if (kind != "?") served.Add(new(kind, body, start, Now, aborted, done));
                }
            }
        }

        // What the request is: the reply (it ends with what the user asked), its next round (after the tool result), the
        // background think (its last message is the task) or Martlet's report (its last message is Martlet's note).
        private static string Classify(string body)
        {
            var last = JsonNode.Parse(body)?["messages"]?.AsArray().LastOrDefault();
            var role = (string?)last?["role"];
            var content = last?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            return role == "tool" ? "after-tool"
                : content.Contains("A background task from Martlet", StringComparison.Ordinal) ? "think"
                : content.Contains("Martlet's note", StringComparison.Ordinal) ? "report"
                : JsonNode.Parse(body)?["tools"] is null ? "chat" : "reply";
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

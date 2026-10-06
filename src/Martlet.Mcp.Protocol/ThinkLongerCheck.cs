using System.Diagnostics;
using System.IO;
using System.Net;
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

/// <summary>think_longer_status and think_longer_check: Companion › Deep thinking. The status reads the saved settings, where
/// Deep thinking thinks on this PC and whether it can run there, what the Thinking model is offered and the desktop's
/// background-jobs.json. The check rehearses the production background-job scheduler (<see cref="BackgroundJobs"/>), the think
/// runner (<see cref="BackgroundThink"/>), the tool texts and request layout (<see cref="ThinkLonger"/>), the Deep thinking plan,
/// the side-by-side fit check (<see cref="OllamaSideBySide"/>), the conversation runtime and the Chat Completions adapter against
/// fixture endpoints on 127.0.0.1 that answer with canned text (NOT AI). Nothing leaves loopback; no credentials are read.</summary>
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
        var pool = DeepThinkingPool.For(deep, loaded.Settings?.Setup?.Routes ?? []);
        var places = ThinkLonger.Places(pool);
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
                enabled = settings.On, effort = settings.HowHard.ToString(), timeLimit = "none", hourlyLimit = "none",
                delivery = settings.When.ToString(), chosen = generation?.ThinkLonger is not null
            },
            thinking = route is null ? null : new
            {
                routeType = route.RouteType?.ToString() ?? "OpenAi", model = route.ModelId, supportsTools, toolsRejected = rejected,
                offered = settings.On && supportsTools && !rejected && pool.Plan.Available,
                onThisPc = local
            },
            deepThinking = new
            {
                file = deepState, place = deep.Place.ToString(), where = deep.Separate ? deep.Describe() : route?.ModelId,
                model = deep.Separate ? deep.ModelId : route?.ModelId, hostId = deep.HostId,
                // The paired computer's route a think goes to: its Deep thinking role's own Ollama, or its Ollama role's.
                hostRoute = deep.Place == DeepThinkingPlace.Host ? deep.HostRoute : null, hostRole = deep.OnHostRole,
                origin = deep.Place == DeepThinkingPlace.Endpoint ? deep.Origin : null,
                ownKey = deep.CredentialId is not null, usesThinkingKey = deep.UsesThinkingKey(route),
                available = plan.Available, parallel = plan.Available, checksFit = plan.ChecksFit, why = plan.Why,
                thinkingSteps = deepRoute.Type is null ? "Unused" : GenerationSupport.Use(deepRoute.Type, deepRoute.Origin, GenerationSetting.Reasoning).ToString(),
                sends = deepRoute.Type == SetupRouteType.GatewayOllama ? "{\"think\":true}"
                    : GenerationSupport.ReasoningJson(deepRoute.Type, deepRoute.Origin, true, effort),
                outputTokens = ThinkLonger.OutputTokens(settings.HowHard),
                carriesTools = !deep.Separate,
                // Every place it thinks on (the one chosen first, then each computer ticked Think here too): several thinks run at
                // once, one on each usable place, the one sharing least with the conversation (lowest rank) first.
                pool = new
                {
                    places = pool.Spots.Select(spot => new
                    {
                        computer = spot.Computer, where = spot.Settings.Separate ? spot.Settings.Describe() : route?.ModelId,
                        place = spot.Settings.Place.ToString(), hostRole = spot.Settings.OnHostRole, available = spot.Plan.Available,
                        rank = spot.Plan.Rank, checksFit = spot.Plan.ChecksFit, why = spot.Plan.Why
                    }),
                    usable = places.Count, maxThinks = ThinkLonger.Kind(settings, places.Count).MaxActive,
                    available = pool.Plan.Available, why = pool.Plan.Why
                }
            },
            tools = ThinkLonger.Definitions(settings, places.Count).Select(tool => new
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
        var plans = Plans();
        await using var other = new Fixture(reasoning * 3);
        var parallel = await ParallelAsync(fixture, other, cancellation);
        var sideBySide = await SideBySideAsync(cancellation);
        var host = HostFit();
        var pool = await PoolAsync(reasoning, cancellation);
        var moment = await MomentAsync(fixture, cancellation);
        return new
        {
            ok = flow.Ok && limits.Ok && plans.Ok && parallel.Ok && sideBySide.Ok && host.Ok && pool.Ok && moment.Ok,
            endpoint = fixture.BaseUrl,
            note = "Fixture endpoints on 127.0.0.1 with canned replies (NOT AI) and a fixture Ollama model list; the scheduler, runner, " +
                "tool texts, request layout, Deep thinking plan, side-by-side fit, moment plan, runtime and adapter are Martlet's own.",
            flow = flow.Report, limits = limits.Report, plans = plans.Report, parallel = parallel.Report,
            sideBySide = sideBySide.Report, hostFit = host.Report, pool = pool.Report, moment = moment.Report
        };
    }

    // ---------- one moment: whatever starts a reply takes everything else that waits ----------

    // The owner's example: while they play, a song and a report finish and the PC plays the game's sounds; a look comes due. The
    // production plan (MomentTurn) makes it one reply that takes the PC's lines, the picture and both finished jobs (the song
    // marked to offer), sent to the fixture with the One moment instruction at the same place as a plain reply's, so the start of
    // every request stays the same. Then what each trigger takes when something else waits.
    private static async Task<(bool Ok, object Report)> MomentAsync(Fixture fixture, CancellationToken cancellation)
    {
        using var jobs = new BackgroundJobs();
        var song = new BackgroundJobKind("song", 1, 3, TimeSpan.FromMinutes(15), Offer: true, Doing: "Making a song");
        var think = new BackgroundJobKind(ThinkLonger.KindName, 1, 10, TimeSpan.FromMinutes(5), Doing: "Thinking about");
        var songJob = jobs.Start(song, "victory song", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("song-1: Monster Slayer (0:48)."))).Job;
        var reportJob = jobs.Start(think, "dragon lore report", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("Dragons hoard gold; three facts."))).Job;
        var waited = Stopwatch.StartNew();
        while ((songJob?.Finished != true || reportJob?.Finished != true) && waited.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, cancellation);
        var cases = new (string Name, MomentTrigger Trigger, bool Pc, bool Jobs, bool Look, MomentRoute Route, bool TakesPc, bool TakesJobs, bool TakesLook)[]
        {
            ("A look comes due while the PC played and work finished", MomentTrigger.Look, true, true, true, MomentRoute.Reply, true, true, true),
            ("A look comes due while only work finished", MomentTrigger.Look, false, true, true, MomentRoute.Report, false, true, true),
            ("A look and nothing else", MomentTrigger.Look, false, false, true, MomentRoute.Glance, false, false, true),
            ("Finished work comes up while the PC played", MomentTrigger.Report, true, true, false, MomentRoute.Reply, true, true, false),
            ("Finished work comes up while a look is due", MomentTrigger.Report, false, true, true, MomentRoute.Report, false, true, true),
            ("The PC's pace came up while work finished", MomentTrigger.PcAudio, true, true, false, MomentRoute.Reply, true, true, false),
            ("The PC's pace came up while you held the work (Esc)", MomentTrigger.PcAudio, true, false, false, MomentRoute.Reply, true, false, false),
            ("You talk while all of it waits", MomentTrigger.User, true, false, true, MomentRoute.Reply, true, true, true)
        };
        var planned = cases.Select(c => (c, Plan: MomentTurn.Plan(c.Trigger, c.Pc, c.Jobs, c.Look))).ToArray();
        var plansOk = planned.All(p => p.Plan.Route == p.c.Route && p.Plan.PcAudio == p.c.TakesPc && p.Plan.Jobs == p.c.TakesJobs &&
            p.Plan.Look == p.c.TakesLook);

        // The plain reply and the combined one, as the desktop asks: the persona, then the One moment instruction first among the
        // reply's own; the combined message carries the PC's marked lines and the finished work in its notes.
        var moment = PromptSettings.Fill(null, PromptCatalog.Moment, ("silent", StayQuiet.Marker))!;
        var pcPrompt = PromptSettings.Fill(null, PromptCatalog.PcAudio, ("marker", "[PC audio]"), ("silent", StayQuiet.Marker))!;
        var permissions = new Permissions(ChatCompletionsSetup.BaseUri(fixture.BaseUrl));
        await using var replies = ConversationRuntime.Create(new NoCredentials());
        async Task<(ConversationSnapshot Done, string? Body)> AskAsync(BoundedTextInput input)
        {
            var before = fixture.Bodies("chat").Count;
            var turn = replies.Start(new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, Model), ReplyLimits,
                new ConversationLimits { TurnTimeout = TimeSpan.FromSeconds(60) }, chat: new ChatCompletionsTarget(fixture.BaseUrl, true),
                generation: new GenerationSettings { Reasoning = false }), permissions, cancellation);
            var done = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            return (done, fixture.Bodies("chat").Skip(before).FirstOrDefault());
        }
        TextHistoryMessage[] history = [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey, good luck with the boss!")];
        var plain = await AskAsync(new BoundedTextInput("How am I doing?", string.Join("\n\n", Persona, moment), history));
        var plan = planned[0].Plan;
        var delivery = plan.Jobs ? jobs.Take(onItsOwn: true) : null;
        string[] played = ["[PC audio] The monster roars and falls.", "[PC audio] Quest complete!"];
        var notes = delivery is null ? null : BackgroundJobs.ReportNotes(null, delivery.Jobs);
        var combined = await AskAsync(new BoundedTextInput(string.Join("\n", played), string.Join("\n\n", Persona, moment, pcPrompt), history,
            notes: notes));
        if (combined.Done.State == ConversationState.Completed) delivery?.Complete();
        else delivery?.Return();
        string System(string? body) => body is null ? "" : (string?)Messages(body).FirstOrDefault(m => (string?)m["role"] == "system")?["content"] ?? "";
        string User(string? body) => body is null ? "" : string.Join("\n", Messages(body).Where(m => (string?)m["role"] == "user").TakeLast(1)
            .Select(m => m["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : m["content"]?.ToJsonString() ?? ""));
        var plainSystem = System(plain.Body);
        var combinedSystem = System(combined.Body);
        var through = plainSystem.IndexOf(moment, StringComparison.Ordinal) + moment.Length;
        var stableStart = through > moment.Length && combinedSystem.Length >= through && combinedSystem[..through] == plainSystem[..through];
        var message = User(combined.Body);
        var carries = new
        {
            pcLines = played.All(line => message.Contains(line, StringComparison.Ordinal)),
            song = message.Contains("Monster Slayer", StringComparison.Ordinal),
            songOffered = message.Contains("offer it and ask", StringComparison.Ordinal),
            report = message.Contains("Dragons hoard gold", StringComparison.Ordinal)
        };
        // The fixture request carries no image, so what it took is told without the picture the desktop would add.
        var took = MomentTurn.Describe(false, played.Length, false, null, delivery?.Jobs.Count ?? 0);
        var requestOk = plain.Done.State == ConversationState.Completed && combined.Done.State == ConversationState.Completed && stableStart &&
            carries.pcLines && carries.song && carries.songOffered && carries.report && delivery?.Jobs.Count == 2 && !jobs.HasNews &&
            songJob?.Delivery == BackgroundDeliveryState.Delivered && reportJob?.Delivery == BackgroundDeliveryState.Delivered;
        var ok = plansOk && requestOk;
        return (ok, new
        {
            ok,
            plans = new
            {
                ok = plansOk,
                cases = planned.Select(p => new
                {
                    name = p.c.Name, trigger = p.c.Trigger.ToString(), route = p.Plan.Route.ToString(), takesPcAudio = p.Plan.PcAudio,
                    takesFinishedWork = p.Plan.Jobs, takesTheLook = p.Plan.Look, combined = p.Plan.Combined
                })
            },
            combinedTurn = new
            {
                ok = requestOk, took, state = combined.Done.State.ToString(),
                carries, jobsTaken = delivery?.Jobs.Count, newsAfter = jobs.HasNews, delivery = new { song = songJob?.Delivery.ToString(), report = reportJob?.Delivery.ToString() },
                momentInstruction = moment, sameStartAsAPlainReply = stableStart, sharedStartCharacters = stableStart ? through : 0
            }
        });
    }

    // ---------- Deep thinking: whether a think can run where it is set to think ----------

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

    // ---------- Deep thinking on several computers at once ----------

    // The production pool (DeepThinkingPool, ThinkLonger.Places) of three paired computers' Deep thinking roles: diva and
    // ripley do none of the conversation's jobs, imouto also speaks. The production job list places each think on a free
    // place (BackgroundJobs.Start with the pool): think-1 on diva, think-2 on ripley (both working at once, each on its own
    // fixture endpoint standing in for that computer, through a runtime of its own as the desktop's slots do), think-3 on
    // imouto, and a fourth is refused as busy naming each place; once they finish every place is free and the next goes to diva.
    private static async Task<(bool Ok, object Report)> PoolAsync(TimeSpan reasoning, CancellationToken cancellation)
    {
        var settings = new ThinkLongerSettings();
        var localThinking = Chat(SetupRole.Llm, GenerationSupport.LocalOllamaChatBaseUrl, "gemma4:e4b");
        var routes = new[] { localThinking, Gateway(SetupRole.Tts, SetupRouteType.GatewayF5, "imouto", "https://imouto.local:9443") };
        static DeepThinkingSettings Role(string id) => Host(id) with { HostRouteId = SelfHostSetup.DeepThinkingRouteId };
        var deep = Role("diva").WithPool([Role("imouto"), Role("ripley")]);
        var pool = DeepThinkingPool.For(deep, routes);
        var places = ThinkLonger.Places(pool);
        var kind = ThinkLonger.Kind(settings, places.Count);
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        foreach (var place in places) fixtures[place.Id] = new Fixture(reasoning);
        var runtimes = new List<ConversationRuntime>();
        try
        {
            using var jobs = new BackgroundJobs();
            var conversationInput = new BoundedTextInput(Asked, Persona, [new(TextHistoryRole.User, "Hi!"), new(TextHistoryRole.Assistant, "Hey!")],
                tools: ThinkLonger.Definitions(settings, places.Count));
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
            }, places);
            var first = Start();
            var second = Start();
            var waited = Stopwatch.StartNew();
            bool BothThinking() => first.Job?.Place is { } a && second.Job?.Place is { } b &&
                fixtures[a.Id].Count("think", inFlight: true) == 1 && fixtures[b.Id].Count("think", inFlight: true) == 1;
            while (!BothThinking() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, cancellation);
            var together = BothThinking();
            var third = Start();
            var fourth = Start();
            var heldWhileBusy = jobs.Places.Leases.Select(lease => new { place = lease.Place.Name, by = lease.Holder }).ToArray();
            BackgroundJob[] started = [.. new[] { first, second, third }.Where(s => s.Started).Select(s => s.Job!)];
            waited.Restart();
            while (started.Any(job => !job.Finished) && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20, cancellation);
            var freed = jobs.Places.Leases.Count == 0;
            var again = Start();
            var againPlace = again.Job?.Place?.Name;
            if (again.Job is { } fifth) jobs.Cancel(fifth.Id, BackgroundJob.CanceledByMartlet);
            // Each started think's request on its own computer's fixture; the first two overlapping in time.
            var spans = started.Select(job => fixtures[job.Place!.Id].Served("think").FirstOrDefault()).ToArray();
            var overlapped = started.Length >= 2 && started[1].StartedUtc < started[0].FinishedUtc && started[0].StartedUtc < started[1].FinishedUtc;
            var expected = new[] { "diva", "ripley", "imouto" };
            var ok = pool.Usable.Count == 3 && places.Count == 3 && kind.MaxActive == 3 &&
                started.Length == 3 && started.Select(job => job.Place!.Name).SequenceEqual(expected) &&
                together && overlapped && started.All(job => job.State == BackgroundJobState.Succeeded && job.Result == Lyrics) &&
                fourth.Refusal == "busy" && expected.All(name => fourth.Message?.Contains("on " + name, StringComparison.Ordinal) == true) &&
                heldWhileBusy.Length == 3 && freed && againPlace == "diva" && spans.All(s => s is { Aborted: false });
            return (ok, new
            {
                ok,
                configured = pool.Spots.Select(spot => new
                {
                    computer = spot.Computer, where = spot.Settings.Describe(), available = spot.Plan.Available, rank = spot.Plan.Rank, why = spot.Plan.Why
                }),
                maxThinks = kind.MaxActive, plan = pool.Plan.Why,
                tool = ThinkLonger.Description(settings, places.Count),
                placed = started.Select(job => new
                {
                    id = job.Id, place = job.Place!.Name, state = job.State.ToString(), finishedAfterMs = (long)job.Elapsed.TotalMilliseconds
                }),
                thinkingAtOnce = together, overlapped,
                heldWhileBusy, refused = new { refusal = fourth.Refusal, message = fourth.Message, toldModel = ThinkLonger.Refused(fourth) },
                freedAfter = freed, nextPlacedOn = againPlace
            });
        }
        finally
        {
            foreach (var runtime in runtimes) await runtime.DisposeAsync();
            foreach (var fixture in fixtures.Values) await fixture.DisposeAsync();
        }
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
                permissions));
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
        // Deep thinking's own kind has no time limit and no hourly limit: far more thinks than any old hourly limit start one after
        // another, and one keeps running past the fixture kind's time limit until it is canceled.
        using var unlimitedJobs = new BackgroundJobs();
        var production = ThinkLonger.Kind(new ThinkLongerSettings());
        var unlimitedStarted = 0;
        for (var i = 0; i < 20; i++)
        {
            var next = unlimitedJobs.Start(production, $"think {i}", Forever);
            if (!next.Started) break;
            unlimitedStarted++;
            unlimitedJobs.Cancel(next.Job!.Id, BackgroundJob.CanceledByYou);
            await Until(() => next.Job.Finished);
        }
        var longRun = unlimitedJobs.Start(production, "a long think", Forever);
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellation);
        var stillRunning = longRun.Job is { Finished: false };
        unlimitedJobs.CancelAll();
        var unlimited = production.TimeLimit is null && production.MaxPerHour is null && unlimitedStarted == 20 && stillRunning;
        var timedOut = timed.Job;
        var songRun = songJob.Job!;
        var lateRun = lateSong.Job!;
        var ok = first.Started && second.Refusal == "busy" && songJob.Started && together == 2 &&
            first.Job.State == BackgroundJobState.Canceled && first.Job.CanceledBy == BackgroundJob.CanceledByYou && proactive is null &&
            withMessage is not null && notes?.Contains("canceled", StringComparison.Ordinal) == true && returned == BackgroundDeliveryState.Pending &&
            first.Job.Delivery == BackgroundDeliveryState.Delivered &&
            timedOut is { State: BackgroundJobState.TimedOut } && hourly.Refusal == "hourly_limit" &&
            songRun.State == BackgroundJobState.Canceled && songRun.Delivery == BackgroundDeliveryState.Delivered &&
            lateRun.State == BackgroundJobState.Canceled && lateRun.Delivery == BackgroundDeliveryState.Dropped && !jobs.HasNews && unlimited;
        return (ok, new
        {
            ok,
            deepThinkingUnlimited = new
            {
                ok = unlimited, timeLimit = production.TimeLimit?.ToString() ?? "none", hourlyLimit = production.MaxPerHour?.ToString() ?? "none",
                startedInARow = unlimitedStarted, runningAfterFixtureLimit = stillRunning, requestTimeHours = ThinkLonger.RequestTime.TotalHours
            },
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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeOllamaAsync(listener, new Dictionary<string, string> { ["/api/ps"] = ps, ["/api/tags"] = tags }, stop.Token);
        IReadOnlyList<OllamaLoadedModel>? loaded;
        IReadOnlyDictionary<string, long>? downloads;
        try
        {
            using var client = new HttpClient();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            loaded = await OllamaSideBySide.LoadedAsync(client, origin, cancellation);
            downloads = await OllamaSideBySide.DownloadsAsync(client, origin, cancellation);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    // A minimal Ollama on 127.0.0.1: each GET path answers with its canned JSON.
    private static async Task ServeOllamaAsync(TcpListener listener, IReadOnlyDictionary<string, string> answers, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellation) ?? "";
                while (await reader.ReadLineAsync(cancellation) is { Length: > 0 }) { }
                var path = line.Split(' ') is [_, var target, ..] ? target : "";
                var payload = Encoding.UTF8.GetBytes(answers.TryGetValue(path, out var json) ? json : "{\"error\":\"not found\"}");
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(json is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, cancellation);
                await stream.WriteAsync(payload, cancellation);
                await stream.FlushAsync(cancellation);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
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

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

/// <summary>think_longer_status and think_longer_check: Companion › Deep thinking. The status reads the saved settings, where
/// Deep thinking thinks on this PC and whether it can run there, what the Thinking model is offered and the desktop's
/// background-jobs.json. The check rehearses the production background-job scheduler (<see cref="BackgroundJobs"/>), the think
/// runner (<see cref="BackgroundThink"/>), the tool texts and request layout (<see cref="ThinkLonger"/>), the Deep thinking plan,
/// the side-by-side fit check (<see cref="OllamaSideBySide"/>), the conversation runtime and the Chat Completions adapter against
/// fixture endpoints on 127.0.0.1 that answer with canned text (NOT AI). Nothing leaves loopback; no credentials are read.</summary>
internal static partial class ThinkLongerCheck
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
        var route = loaded.Settings.ThinkingRoute();
        var supportsTools = route?.RouteType is SetupRouteType.OpenAi or SetupRouteType.ChatCompletions;
        var local = route is not null && (ContextBudget.IsLocalOllama(route.RouteType, route.Origin) ||
            route.RouteType == SetupRouteType.ChatCompletions && Uri.TryCreate(route.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback);
        var rejected = route is not null && ToolsRejected(dataDirectory, $"{route.RouteType}|{route.Origin}|{route.ModelId}");
        var effort = settings.HowHard == ThinkEffort.High ? GenerationSupport.ReasoningEffortHigh : GenerationSupport.ReasoningEffortOn;
        var (poolSettings, deepState) = ThinkingPoolSettings.Read(dataDirectory, save: false);
        var deep = poolSettings.Places;
        var pool = poolSettings.Plan(loaded.Settings?.Setup?.Routes ?? []);
        var plan = pool.Spots.Count == 1 ? pool.Spots[0].Plan : DeepThinkingPlan.For(deep, loaded.Settings?.Setup?.Routes ?? []);
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
                delivery = settings.When.ToString(), chosen = generation?.ThinkLonger is not null,
                webResearch = settings.WebResearchOn, researches = settings.Researches
            },
            thinking = route is null ? null : new
            {
                routeType = route.RouteType?.ToString() ?? "OpenAi", model = route.ModelId, supportsTools, toolsRejected = rejected,
                offered = settings.On && supportsTools && !rejected && pool.Plan.Available,
                researchOffered = settings.Researches && supportsTools && !rejected && pool.Plan.Available,
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
                        rank = spot.Plan.Rank, slots = spot.Settings.ThinksAtOnce, checksFit = spot.Plan.ChecksFit, why = spot.Plan.Why
                    }),
                    usable = places.Count, maxThinks = ThinkLonger.Slots(places), atOnce = ThinkLonger.AtOnce(places),
                    available = pool.Plan.Available, why = pool.Plan.Why
                }
            },
            tools = ThinkLonger.Definitions(settings, ThinkLonger.Slots(places)).Concat(settings.Researches ? [WebResearch.Definition] : []).Select(tool => new
            {
                name = tool.Name, description = tool.Description, parameters = JsonNode.Parse(tool.ParametersJson)
            }).ToArray(),
            prompt = ThinkLonger.Instructions(settings, loaded.Settings?.Prompts),
            researchPrompt = settings.Researches ? WebResearch.Instructions(loaded.Settings?.Prompts) : null,
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

    internal static bool ToolsRejected(string dataDirectory, string key)
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
        var broker = await BrokerAsync(cancellation);
        return new
        {
            ok = flow.Ok && limits.Ok && plans.Ok && parallel.Ok && sideBySide.Ok && host.Ok && pool.Ok && moment.Ok && broker.Ok,
            endpoint = fixture.BaseUrl,
            note = "Fixture endpoints on 127.0.0.1 with canned replies (NOT AI) and a fixture Ollama model list; the scheduler, runner, " +
                "tool texts, request layout, Deep thinking plan, side-by-side fit, moment plan, runtime and adapter are Martlet's own.",
            flow = flow.Report, limits = limits.Report, plans = plans.Report, parallel = parallel.Report,
            sideBySide = sideBySide.Report, hostFit = host.Report, pool = pool.Report, moment = moment.Report, broker = broker.Report
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

    // ---------- the background broker: which computer a think goes to, and the line ----------

    // ---------- Deep thinking: whether a think can run where it is set to think ----------

    // ---------- Deep thinking on several computers at once ----------

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
        // A full line on Deep thinking's places: three one-slot places run two thinks at once (the last free slot stays free for
        // quick jobs) and four wait in line; a seventh is refused with those real numbers.
        using var lineJobs = new BackgroundJobs();
        IReadOnlyList<BackgroundPlace> three = [new("host:diva", "diva"), new("host:imouto", "imouto", 1), new("host:ripley", "ripley")];
        var lineKind = ThinkLonger.Kind(new ThinkLongerSettings(), ThinkLonger.Slots(three));
        var inLine = Enumerable.Range(0, lineKind.MaxActive).Select(i => lineJobs.Start(lineKind, $"think {i}", Forever, three, wait: true)).ToArray();
        var fullLine = lineJobs.Start(lineKind, "one more", Forever, three, wait: true);
        lineJobs.CancelAll();
        var lineFull = inLine.All(start => start.Started) && fullLine.Refusal == "busy" && fullLine.Message ==
            "Martlet already has 6 thinks running or waiting, and it runs up to 2 at once. Running now: think-1 on diva and think-2 on ripley. " +
            "Wait for one to finish.";
        // A job whose seating throws (a clock that fails, as at shutdown) ends Failed and is reported, so its kind isn't blocked;
        // finished jobs waiting to be brought up never grow past KeptWaiting.
        var breaking = new BreakingClock();
        using var faultJobs = new BackgroundJobs(breaking);
        string? reported = null;
        faultJobs.Faulted += (id, _) => reported = id;
        IReadOnlyList<BackgroundPlace> one = [new("host:diva", "diva")];
        var faultKind = new BackgroundJobKind("check", 2, null, TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = faultJobs.Start(faultKind, "holder", async (job, token) =>
        {
            entered.TrySetResult();
            return await Forever(job, token);
        }, one);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        breaking.Broken = true;
        var seating = faultJobs.Start(faultKind, "waits in line", Forever, one, wait: true);
        await Until(() => seating.Job?.Finished == true);
        breaking.Broken = false;
        faultJobs.CancelAll();
        var startFailure = holder.Started && seating.Job is { State: BackgroundJobState.Failed } failedStart && reported == failedStart.Id;
        using var many = new BackgroundJobs();
        var quick = new BackgroundJobKind("quick", 8, null, null);
        for (var i = 0; i < BackgroundJobs.KeptWaiting + 8; i++)
        {
            var next = many.Start(quick, $"quick {i}", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("ok")));
            await Until(() => next.Job?.Finished == true);
        }
        var trimmed = many.Recent.Count <= BackgroundJobs.KeptWaiting + 1 && many.Undelivered.Count <= BackgroundJobs.KeptWaiting + 1;
        var timedOut = timed.Job;
        var songRun = songJob.Job!;
        var lateRun = lateSong.Job!;
        var ok = first.Started && second.Refusal == "busy" && songJob.Started && together == 2 &&
            first.Job.State == BackgroundJobState.Canceled && first.Job.CanceledBy == BackgroundJob.CanceledByYou && proactive is null &&
            withMessage is not null && notes?.Contains("canceled", StringComparison.Ordinal) == true && returned == BackgroundDeliveryState.Pending &&
            first.Job.Delivery == BackgroundDeliveryState.Delivered &&
            timedOut is { State: BackgroundJobState.TimedOut } && hourly.Refusal == "hourly_limit" &&
            songRun.State == BackgroundJobState.Canceled && songRun.Delivery == BackgroundDeliveryState.Delivered &&
            lateRun.State == BackgroundJobState.Canceled && lateRun.Delivery == BackgroundDeliveryState.Dropped && !jobs.HasNews && unlimited && lineFull &&
            startFailure && trimmed;
        return (ok, new
        {
            ok,
            startFailure = new
            {
                ok = startFailure, state = seating.Job?.State.ToString(), problem = seating.Job?.Problem, reported = reported is not null
            },
            waitingTrimmed = new { ok = trimmed, kept = many.Recent.Count, waiting = many.Undelivered.Count, limit = BackgroundJobs.KeptWaiting },
            deepThinkingUnlimited = new
            {
                ok = unlimited, timeLimit = production.TimeLimit?.ToString() ?? "none", hourlyLimit = production.MaxPerHour?.ToString() ?? "none",
                startedInARow = unlimitedStarted, runningAfterFixtureLimit = stillRunning, requestTimeHours = ThinkLonger.RequestTime.TotalHours
            },
            fullLine = new
            {
                ok = lineFull, runningOrWaiting = inLine.Count(start => start.Started), maxActive = lineKind.MaxActive, atOnce = ThinkLonger.AtOnce(three),
                refusal = fullLine.Refusal, message = fullLine.Message, toldModel = ThinkLonger.Refused(fullLine)
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
}

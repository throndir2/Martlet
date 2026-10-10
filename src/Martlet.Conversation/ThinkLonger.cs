using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>think_longer: Martlet decides, sparingly, that a task needs real thinking and works it out in the background while
/// the conversation carries on (Companion › Replies › Thinking longer, on by default). The tool returns at once; the reply that
/// called it tells the user it'll take a while, and the background request (Thinking steps On at the chosen effort, whatever
/// the replies use) continues the reply's own request, so it reuses the prompt cache instead of evicting it. When it is done,
/// its result is brought into the conversation (<see cref="BackgroundJobs.Take"/>). cancel_thinking stops it.</summary>
public static class ThinkLonger
{
    public const string Name = "think_longer";
    public const string CancelName = "cancel_thinking";
    /// <summary>The background job kind: think-1, think-2...</summary>
    public const string KindName = "think";
    public const int MaxTaskCharacters = 4_000;
    public const int MaxReasonCharacters = 300;
    /// <summary>The background request's output budget (its hidden reasoning included where the route counts it): Medium and
    /// High. A reply's is at most 4,096.</summary>
    public const int MediumOutputTokens = 8_192, HighOutputTokens = 16_384;

    public static int OutputTokens(ThinkEffort effort) => effort == ThinkEffort.High ? HighOutputTokens : MediumOutputTokens;

    /// <summary>What a think on a paired computer's Ollama must fit: its gateway takes 16 KiB and 16 earlier messages, no tools,
    /// and loads at most <see cref="GenerationSettings.MaximumHostContextTokens"/> of context, which the output shares.</summary>
    public static ThinkBounds HostBounds(ThinkEffort effort) => new(BoundedTextInput.HardMaxUtf8Bytes,
        TextGenerationLimits.DefaultMaxHistoryMessages, GenerationSettings.MaximumHostContextTokens - OutputTokens(effort), Tools: false);
    /// <summary>The least time left worth starting a background request with.</summary>
    public static TimeSpan MinimumAttempt => TimeSpan.FromSeconds(5);

    public const string ParametersJson =
        """{"type":"object","properties":{"task":{"type":"string","description":"Complete and self-contained: what to work out and exactly what the result must contain."},"reason":{"type":"string","description":"Why it needs real thinking, in a few words."}},"required":["task"],"additionalProperties":false}""";

    public const string CancelParametersJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"Such as think-1; leave out for the running one."}},"additionalProperties":false}""";

    /// <summary>The think job kind: up to twice the slots of the places Deep thinking can use (<paramref name="slots"/>) running
    /// or waiting in line for the next free one (at most <see cref="MaxPlaces"/> in all; how many of them run at once is
    /// <see cref="AtOnce"/>), with no hourly limit and no time limit (it runs until it is done or canceled). It yields to the live
    /// conversation (<see cref="RunYieldingAsync"/>).</summary>
    public static BackgroundJobKind Kind(ThinkLongerSettings settings, int slots = 1) =>
        new(KindName, Math.Clamp(Math.Max(1, slots) * 2, 2, MaxPlaces), null, null, Doing: "Thinking about")
        {
            PoolKind = ThinkingJobKind.ThinkLonger, Yields = true
        };

    /// <summary>How long a think's request may take: the provider contracts' ceiling, so in practice it runs until it is done
    /// or canceled.</summary>
    public static TimeSpan RequestTime => TextGenerationLimits.HardMaxRequestTime;

    /// <summary>The most thinks that run or wait at once.</summary>
    public const int MaxPlaces = DeepThinkingSettings.MaxPlaces;

    /// <summary>The places a think (or another kind's work that thinks, such as a song's lyrics) can run on: each usable place
    /// of <paramref name="pool"/>, with its computer's name, how much it shares with the conversation, how many thinks it runs
    /// at once and the other work its computer is kept free for (<paramref name="duties"/>, by computer name: singing, image
    /// generation), so the broker places a think on a general computer first. <paramref name="can"/> says what each member can
    /// do (text only when null); each place also says which computer it runs on (<see cref="LiveResources.MachineOf(DeepThinkingSettings)"/>)
    /// and, when <paramref name="gpus"/> knows, which graphics cards, for the live floor. <paramref name="choices"/> (the Thinking
    /// pool's settings) says which members take quick jobs and long jobs (all of them when null).</summary>
    public static IReadOnlyList<BackgroundPlace> Places(DeepThinkingPool pool, IReadOnlyDictionary<string, IReadOnlyList<string>>? duties = null,
        Func<DeepThinkingSettings, ThinkingCapability>? can = null, Func<DeepThinkingSettings, IReadOnlyList<string>>? gpus = null,
        ThinkingPoolSettings? choices = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return [.. pool.Usable.Take(MaxPlaces).Select(spot => new BackgroundPlace(spot.Key,
            spot.Computer.Length <= 80 ? spot.Computer : spot.Computer[..80], Math.Clamp(spot.Plan.Rank, 0, BackgroundPlace.MaxRank))
        {
            Slots = Math.Clamp(spot.Settings.ThinksAtOnce, 1, BackgroundPlace.MaxSlots),
            Duties = duties?.GetValueOrDefault(spot.Computer) is { Count: > 0 } kept ? [.. kept.Take(8)] : [],
            Can = can?.Invoke(spot.Settings) ?? ThinkingCapability.Text,
            Model = spot.Settings.Separate ? spot.Settings.ModelId : null,
            Machine = LiveResources.MachineOf(spot.Settings),
            Gpus = gpus?.Invoke(spot.Settings) is { Count: > 0 } cards ? [.. cards.Take(16)] : [],
            QuickJobs = choices?.TakesQuickJobs(spot.Key) ?? true,
            LongJobs = choices?.TakesLongJobs(spot.Key) ?? true,
            // Without the pool's settings an external member gets no pictures or recordings, as with its box unticked.
            Media = (choices ?? new()).MayReceiveMedia(spot.Settings),
            Smarts = !spot.Settings.Separate ? ThinkingSmarts.Standard
                : choices?.SmartsOf(spot.Key, spot.Settings.ModelId) ?? ThinkingSmartsGuess.From(spot.Settings.ModelId)
        })];
    }

    /// <summary>The places a long job (thinking longer, research, a song's lyrics) may run on, as <paramref name="where"/> says:
    /// Smart only and These members keep only those members (<see cref="ThinkingRunsOnRules.Allows"/>). Prefer smart keeps every
    /// member; its kind takes the smartest free one first (<see cref="BackgroundJobKind.SmartFirst"/>). The conversation model,
    /// in the pool's place while no member can run, is not a member and stays. Empty when no member may take it.</summary>
    public static IReadOnlyList<BackgroundPlace> RunsOn(IReadOnlyList<BackgroundPlace> places, ThinkingRunsOn where)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(where);
        if (places.All(place => place.Id == "thinking")) return places;
        return [.. places.Where(place => place.Id != "thinking" && ThinkingRunsOnRules.Allows(place, where))];
    }

    /// <summary>Whether a think on <paramref name="place"/> can go on in place after the live floor stopped it: its server
    /// continues the assistant's unfinished message (Ollama, on its own port; for the conversation model, where Thinking's
    /// route runs). Elsewhere a stopped think starts again with what it wrote as context.</summary>
    public static bool ContinuesInPlace(DeepThinkingSettings place, SetupRoute? thinking)
    {
        ArgumentNullException.ThrowIfNull(place);
        var origin = place.Place switch
        {
            DeepThinkingPlace.Endpoint => place.Origin,
            DeepThinkingPlace.SameAsThinking when thinking?.RouteType == SetupRouteType.ChatCompletions => thinking.Origin,
            _ => null
        };
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Port == GenerationSupport.OllamaPort;
    }

    /// <summary>How many thinks <paramref name="places"/> have slots for in all.</summary>
    public static int Slots(IReadOnlyList<BackgroundPlace> places) => places.Sum(place => place.Slots);

    /// <summary>How many thinks run at once on <paramref name="places"/> (<see cref="DeepThinkingPool.AtOnce"/>): one fewer than
    /// their slots when there are two or more, because a long job never takes the Thinking pool's last free slot, which stays
    /// free for quick jobs; the others wait in line.</summary>
    public static int AtOnce(IReadOnlyList<BackgroundPlace> places) => DeepThinkingPool.AtOnce(Slots(places));

    /// <summary>The think_longer tool's description. <paramref name="slots"/> is the slots of Deep thinking's places in all; it
    /// says how many thinks really run at once (<see cref="DeepThinkingPool.AtOnce"/>), so the text changes only when the slots
    /// do.</summary>
    public static string Description(ThinkLongerSettings settings, int slots = 1)
    {
        var atOnce = DeepThinkingPool.AtOnce(slots);
        return "Think a task through in the background, step by step, while you keep talking. Use rarely: only for real multi-step " +
            "reasoning or long creative work (song lyrics, a story, a plan, tricky math or code), never for chat or quick answers. " +
            "Tell the user first that you'll think it over. Returns at once; " +
            $"the result comes back later in a note. {(atOnce > 1 ? $"Up to {atOnce} at once" : "One at a time")}; more wait in line.";
    }

    public const string CancelDescription = "Stop the background think (think_longer) that is running.";

    /// <summary>The tools a reply gets while Thinking longer is on, always the same two in the same order, so the start of every
    /// request stays the same for prompt caches (<paramref name="slots"/> is the slots of Deep thinking's places in all, from the
    /// settings only, never from what is busy).</summary>
    public static IReadOnlyList<TextToolDefinition> Definitions(ThinkLongerSettings settings, int slots = 1) =>
        [new(Name, Description(settings, slots), ParametersJson), new(CancelName, CancelDescription, CancelParametersJson)];

    /// <summary>The prompt added to a reply's instructions while think_longer is offered.</summary>
    public static string? Instructions(ThinkLongerSettings settings, PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.ThinkLonger);

    /// <summary>The task and reason a call passed, or what was wrong with it, in words for the model.</summary>
    public static (string? Task, string? Reason, string? Problem) Parse(string argumentsJson)
    {
        JsonObject? arguments = null;
        try { arguments = JsonNode.Parse(argumentsJson) as JsonObject; }
        catch (JsonException) { }
        string? Read(string name) => arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
        var task = Read("task");
        if (arguments is null || string.IsNullOrWhiteSpace(task))
            return (null, null, "Pass one JSON object with the task as a string, like {\"task\": \"Write lyrics for a short song about...\"}.");
        if (task.Length > MaxTaskCharacters) return (null, null, $"The task is too long; keep it under {MaxTaskCharacters} characters.");
        var reason = Read("reason");
        if (string.IsNullOrWhiteSpace(reason)) reason = null;
        else if (reason.Length > MaxReasonCharacters) reason = reason[..MaxReasonCharacters];
        return (Clean(task), reason is null ? null : Clean(reason), null);
    }

    /// <summary>The think a cancel_thinking call names, or null for the one running.</summary>
    public static string? CancelId(string argumentsJson)
    {
        try
        {
            return JsonNode.Parse(argumentsJson) is JsonObject arguments && arguments["id"] is JsonValue value &&
                value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Clean(string text) =>
        new string(text.Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? ' ' : c).ToArray()).Trim();

    /// <summary>A few words about the task for the talk window and the conversation: its first line, at most 60 characters.</summary>
    public static string Label(string task)
    {
        var line = string.Join(' ', task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }

    /// <summary>What the model is told when the think started (or, with <paramref name="queued"/>, waits in line behind that
    /// work for the next free computer, or with <paramref name="forConversation"/> until the conversation pauses): its id, and to
    /// tell the user now unless it already did.</summary>
    public static string Started(BackgroundJob job, bool toldUser, string? queued = null, bool forConversation = false) =>
        JsonSerializer.Serialize(new { status = queued is null ? "started" : "queued", id = job.Id, time_limit = job.Kind.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "none" }) + "\n" +
        (queued is null ? "" : forConversation
            ? "It starts as soon as the conversation pauses: the computers that think are kept free for it while the user talks with you. "
            : $"Every computer that thinks is busy ({queued}), so it starts as soon as one is free. ") +
        (toldUser
            ? "You're thinking about it in the background now. You already told the user, so add nothing more, or at most a few words."
            : "You're thinking about it in the background now. Tell the user now, in one short sentence in character, that you'll " +
              "think it over and get back to them, and that it may take a while.") +
        " Don't make up the result; it comes back in a note when it's done.";

    /// <summary>What the model is told when no think could start.</summary>
    public static string Refused(BackgroundJobStart start) => start.Refusal switch
    {
        "busy" => $"Not started: {start.Message} Tell the user you're still thinking about something else; try again after it's done, " +
                  "or stop it with cancel_thinking if they'd rather you think about this instead.",
        _ => $"Not started: {start.Message ?? "it isn't available right now."} Answer as well as you can right away instead."
    };

    public const string TurnedOff =
        "Not started: the user turned Deep thinking off (Companion › Deep thinking). Answer as well as you can right away instead.";

    /// <summary>What the model is told when Deep thinking can't run where it is set to think (<see cref="DeepThinkingPlan"/>).</summary>
    public static string Unavailable(string why) =>
        $"Not started: Deep thinking can't run right now. {why} Answer as well as you can right away instead.";

    /// <summary>What the model is told after cancel_thinking.</summary>
    public static string Canceled(BackgroundJob? job) => job is null
        ? "No background think is running, so there was nothing to stop."
        : JsonSerializer.Serialize(new { status = "stopped", id = job.Id }) + "\nIt won't come back. Tell the user briefly if they asked.";

    /// <summary>The background think's message: a reply's request exactly as it was sent (instructions, tools, earlier
    /// messages, the message with its notes) and what that reply said (the reply that called think_longer), then the task, so
    /// the model's prompt cache reuses the conversation instead of reading it again. Without a request (or when it can't be
    /// continued) the task goes alone with <paramref name="personality"/>. The picture or recording the message went with
    /// isn't sent again. <paramref name="resume"/>: what the think wrote before the live floor stopped it, sent as the assistant's
    /// unfinished message to continue in place, or with the task as context to start again.</summary>
    public static BoundedTextInput Input(BoundedTextInput? conversation, string? reply, string task, string? reason, PromptSettings? prompts,
        string? personality = null, ThinkResume? resume = null)
    {
        if (resume is { InPlace: false, Partial: { } before }) task = task + "\n\n" + ResumeNote(before);
        var continuation = resume is { InPlace: true } ? resume.Partial : null;
        var message = PromptSettings.Fill(prompts, PromptCatalog.BackgroundThink, ("task", task),
            ("reason", reason is null ? "" : "\nWhy it needs thinking: " + reason))!;
        if (conversation is not null)
        {
            try
            {
                var origin = conversation.Origin ?? conversation;
                List<TextHistoryMessage> history = [.. origin.History, new(TextHistoryRole.User, origin.SentUserText)];
                // Exactly as the conversation keeps it (the next reply's history), so their starts stay the same.
                if (!string.IsNullOrWhiteSpace(reply)) history.Add(new(TextHistoryRole.Assistant, reply));
                return new BoundedTextInput(message, origin.Personality, history, tools: origin.Tools, continuation: continuation);
            }
            catch (ContractException) { }
        }
        return new BoundedTextInput(message, personality, continuation: continuation);
    }

    /// <summary>The most of a stopped think's text that starts it again as context (UTF-8 bytes): the rest of a message's bound
    /// stays for the task.</summary>
    public const int MaxResumeBytes = 8_192;

    /// <summary>What a think that starts again is told about the text it wrote before the live conversation stopped it (at most
    /// <see cref="MaxResumeBytes"/> of it, from its start).</summary>
    public static string ResumeNote(string partial)
    {
        ArgumentNullException.ThrowIfNull(partial);
        var text = partial.Trim();
        var clipped = false;
        while (System.Text.Encoding.UTF8.GetByteCount(text) > MaxResumeBytes)
        {
            text = text[..(text.Length * 3 / 4)];
            clipped = true;
        }
        return "You started on this before and were stopped partway. What you had written so far" +
            (clipped ? " (its start)" : "") + ":\n\n" + text + "\n\nUse it, and write the complete result now, from the beginning.";
    }

    /// <summary>The think's message within a destination's <paramref name="bounds"/>: the tools are dropped when it doesn't take
    /// them (a destination other than the Thinking model has no prompt cache to share), then the oldest exchanges are left out
    /// two at a time until it fits, then the persona; the task always stays.</summary>
    public static BoundedTextInput Fit(BoundedTextInput full, ThinkBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(bounds);
        var history = full.History.ToList();
        var excess = history.Count - bounds.MaxHistoryMessages;
        if (excess > 0) history.RemoveRange(0, Math.Min(history.Count, excess + (excess & 1)));
        IReadOnlyList<TextToolDefinition> tools = bounds.Tools ? full.Tools : [];
        var personality = full.Personality;
        while (true)
        {
            try
            {
                var candidate = new BoundedTextInput(full.UserText, personality, history, tools: tools, continuation: full.Continuation);
                if (candidate.Utf8Bytes <= bounds.MaxInputBytes && candidate.History.Count <= bounds.MaxHistoryMessages &&
                    candidate.InputTokenReservation <= bounds.MaxInputTokens)
                    return candidate;
            }
            catch (ContractException) { }
            if (history.Count > 0) history.RemoveRange(0, Math.Min(2, history.Count));
            else if (personality is not null) personality = null;
            else if (tools.Count > 0) tools = [];
            else return new BoundedTextInput(full.UserText, continuation: full.Continuation);
        }
    }

    /// <summary>The background request's bounds: the reply's input bounds with the effort's output budget and the time left,
    /// and room for a reasoning model's long stream of hidden thinking.</summary>
    public static TextGenerationLimits Limits(TextGenerationLimits reply, ThinkEffort effort, TimeSpan time)
    {
        var output = effort == ThinkEffort.High ? HighOutputTokens : MediumOutputTokens;
        var limit = time > TextGenerationLimits.HardMaxRequestTime ? TextGenerationLimits.HardMaxRequestTime : time;
        return reply with
        {
            MaxOutputTokens = output, MaxContextTokens = reply.MaxInputTokens + output,
            MaxEvents = TextGenerationLimits.HardMaxEvents, MaxStreamBytes = TextGenerationLimits.HardMaxStreamBytes,
            MaxTextCharacters = ContractRules.MaxTextCharacters,
            FirstDeltaTimeout = limit, IdleTimeout = limit, MaxRequestTime = limit
        };
    }

    /// <summary>The background turn: the time left, and one tool round at most (tools are described only to keep the request's
    /// start the same; calls are declined).</summary>
    public static ConversationLimits TurnLimits(TimeSpan time) =>
        new() { TurnTimeout = time > TextGenerationLimits.HardMaxRequestTime ? TextGenerationLimits.HardMaxRequestTime : time, MaxToolRounds = 1 };

    /// <summary>The reply's generation settings with Thinking steps On at <paramref name="effort"/>, whatever the replies use;
    /// <paramref name="withoutReasoning"/> (the model refused the choice before) leaves the model's own default. A request
    /// <paramref name="continuing"/> an answer the live floor stopped has Thinking steps Off: the thinking came before the answer.</summary>
    public static GenerationSettings? Generation(GenerationSettings? reply, ThinkEffort effort, bool withoutReasoning, bool continuing = false)
    {
        if (continuing) return (reply ?? new()) with { Reasoning = false, ReasoningEffort = null };
        var on = (reply ?? new()) with
        {
            Reasoning = true,
            ReasoningEffort = effort == ThinkEffort.High ? GenerationSupport.ReasoningEffortHigh : GenerationSupport.ReasoningEffortOn
        };
        return withoutReasoning ? GenerationSettings.WithoutReasoning(on) : on;
    }

    /// <summary>The background request's tool host: tools are described only so the request starts like the reply's; every
    /// call is declined.</summary>
    public static IConversationToolHost NoTools { get; } = new DeclineTools();

    private sealed class DeclineTools : IConversationToolHost
    {
        public ValueTask<ConversationToolResult> CallAsync(TextToolCall call, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationToolResult(
                "No tools while thinking in the background: work the task out yourself and answer in text.", true));
    }

    /// <summary>The outcome of a finished background request, in words the conversation can use.</summary>
    public static BackgroundJobOutcome Outcome(ConversationSnapshot terminal, string text)
    {
        var answer = text.Trim();
        if (terminal.State == ConversationState.Completed && answer.Length > 0) return BackgroundJobOutcome.Done(answer);
        // Cut off by the output budget: what came is still usable.
        if (answer.Length > 0 && terminal.ProviderFailure == ProviderFailureCode.OutputTokenLimit) return BackgroundJobOutcome.Done(answer, cut: true);
        if (terminal.State == ConversationState.Refused) return BackgroundJobOutcome.Failed("the Thinking model declined it");
        return BackgroundJobOutcome.Failed(terminal.ProviderFailure switch
        {
            ProviderFailureCode.RateLimited => "the Thinking provider is limiting requests right now",
            ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout or ProviderFailureCode.DeadlineExceeded =>
                "the Thinking model didn't finish in time",
            ProviderFailureCode.InputLimit => "the conversation was too long to think about",
            ProviderFailureCode.OutputTokenLimit => "it ran out of room before it wrote anything",
            ProviderFailureCode.CredentialUnavailable => "the Thinking model's key couldn't be used",
            null when answer.Length == 0 && terminal.State == ConversationState.Completed => "it came back empty",
            null => "the Thinking model stopped before it finished",
            _ => "the Thinking model couldn't do it right now"
        });
    }
}

/// <summary>What a think's message must fit on its destination: its byte, message and estimated-token bounds, and whether it
/// carries the reply's tools (only on the Thinking model, to share its prompt cache).</summary>
public sealed record ThinkBounds(int MaxInputBytes, int MaxHistoryMessages, int MaxInputTokens, bool Tools);

/// <summary>One background think's request on its own runtime, beside the conversation. Deep thinking is parallel thinking: it
/// runs only where it has a model of its own (<see cref="DeepThinkingPlan"/>), so it simply runs alongside the conversation,
/// never waiting for it or stopping for it, until it is done, canceled or out of time.</summary>
public sealed class BackgroundThink
{
    /// <param name="prepare">Builds the request and its authorization with the time left.</param>
    public BackgroundThink(ConversationRuntime runtime, Func<TimeSpan, (ConversationRequest Request, IConversationAuthorizationSource Authorization)> prepare,
        TimeProvider? clock = null)
    {
        Runtime = runtime;
        Prepare = prepare;
        Clock = clock ?? TimeProvider.System;
    }

    public ConversationRuntime Runtime { get; }
    public Func<TimeSpan, (ConversationRequest Request, IConversationAuthorizationSource Authorization)> Prepare { get; }
    public TimeProvider Clock { get; }
    /// <summary>Raised after the request ended (for the desktop log's Thinking input line).</summary>
    public Action<ConversationSnapshot>? AttemptFinished { get; init; }
    /// <summary>What the job chip says while the request runs ("Writing the lyrics"); null says nothing.</summary>
    public string? Doing { get; init; }
    /// <summary>How many requests it sent (one, once it started).</summary>
    public int Attempts { get; private set; }
    /// <summary>The visible text the last request wrote, also when it was stopped (what a think the live floor stopped keeps).</summary>
    public string Partial { get; private set; } = "";

    /// <summary>Works <paramref name="job"/> out until <paramref name="token"/> is canceled (the job list's cancel, or the time
    /// limit of a kind that has one), with the time the job has left (anything done before it, such as checking a model fits,
    /// counts); without a time limit the request gets <see cref="ThinkLonger.RequestTime"/>.</summary>
    public async Task<BackgroundJobOutcome> RunAsync(BackgroundJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        token.ThrowIfCancellationRequested();
        var left = job.Kind.TimeLimit is { } limit ? limit - job.Elapsed : ThinkLonger.RequestTime;
        // Too little time left to be worth asking: the job's time limit ends it.
        if (left < ThinkLonger.MinimumAttempt) await Task.Delay(Timeout.InfiniteTimeSpan, Clock, token).ConfigureAwait(false);
        job.Report(BackgroundJobState.Running, Doing);
        var (request, authorization) = Prepare(left);
        var started = Runtime.Start(request, authorization, token, purpose: job.Id);
        Attempts++;
        var terminal = await started.Completion.ConfigureAwait(false);
        await started.OwnershipRelease.ConfigureAwait(false);
        Partial = started.Content.Text;
        AttemptFinished?.Invoke(terminal);
        token.ThrowIfCancellationRequested();
        return ThinkLonger.Outcome(terminal, started.Content.Text);
    }
}

/// <summary>What a background think wrote before the live floor stopped it (<see cref="Partial"/>), and how it goes on:
/// <see cref="InPlace"/>, its next request sends the text as the assistant's unfinished message and the server writes on from
/// there (<see cref="ThinkLonger.ContinuesInPlace"/>); otherwise it starts again with the text as context
/// (<see cref="ThinkLonger.ResumeNote"/>) and writes the whole result.</summary>
public sealed record ThinkResume(string Partial, bool InPlace)
{
    /// <summary>The most a continuation in place may carry (UTF-8 bytes): one message's bound, less room to spare.</summary>
    public const int MaxInPlaceBytes = BoundedTextInput.HardMaxUtf8Bytes - 1_024;

    /// <summary>The result once the next request is done: what was written before, then what it added (<paramref name="written"/>,
    /// its text as it came, when known), when it went on in place; otherwise what it wrote.</summary>
    public BackgroundJobOutcome Combine(BackgroundJobOutcome next, string? written = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        return InPlace && next.Result is { } added ? next with { Result = Join(Partial, written is { Length: > 0 } ? written : added).Trim() } : next;
    }

    /// <summary>The text written before the stop, then the text written after it in place. The spacing at the stop may be lost
    /// on either side, so a space goes back between a sentence's end and the next word; anything else joins as it came (a
    /// word may have been cut in two).</summary>
    public static string Join(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Length == 0 || after.Length == 0 || char.IsWhiteSpace(before[^1]) || char.IsWhiteSpace(after[0])) return before + after;
        return before[^1] is '.' or '!' or '?' or ',' or ';' or ':' && char.IsLetter(after[0]) ? before + " " + after : before + after;
    }

    public override string ToString() => $"{nameof(ThinkResume)} ({Partial.Length} characters, {(InPlace ? "in place" : "again")})";
}

/// <summary>Runs background thinks that yield to the live conversation (docs/CONVERSATION.md, Live floor).</summary>
public static class YieldingThink
{
    /// <summary>How long a think waits before it asks again on a computer that kept its graphics card for a live turn.</summary>
    public static TimeSpan HeldRetry { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The most times one think goes on on another place because its computer stopped answering.</summary>
    public const int MaxMoves = 3;

    /// <summary>Runs <paramref name="job"/> (a kind that <see cref="BackgroundJobKind.Yields"/>) on the place it holds: the think
    /// <paramref name="think"/> makes for that place and what it goes on from (null the first time). When the live floor needs the
    /// place (<see cref="BackgroundJob.Stopping"/>), the request stops, the job keeps what it wrote, waits for a place the floor
    /// allows (<see cref="BackgroundJobs.ReseatAsync"/>: Paused, waiting for the conversation) and goes on there: in place where
    /// <paramref name="inPlace"/> says the place's server continues an unfinished message, else again from the start with the
    /// text as context. When the place's computer refused or stopped it because another live turn holds its graphics card
    /// (<paramref name="held"/>, asked with the <see cref="System.Diagnostics.Stopwatch"/> time the request began), it waits
    /// <see cref="HeldRetry"/> and goes on the same way. When a request failed because the place's computer stopped answering
    /// (<paramref name="gone"/>), it keeps what it wrote and goes on on another place of its pool the same way (waiting in line
    /// for one that answers), at most <see cref="MaxMoves"/> times. <paramref name="stopped"/> hears about each stop (for the
    /// desktop log).</summary>
    public static async Task<BackgroundJobOutcome> RunAsync(BackgroundJobs jobs, BackgroundJob job,
        Func<BackgroundPlace, ThinkResume?, BackgroundThink> think, Func<BackgroundPlace, bool> inPlace, CancellationToken token,
        Action<BackgroundPlace, ThinkResume?>? stopped = null, Func<BackgroundPlace, long, bool>? held = null,
        Func<BackgroundPlace, bool>? gone = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(think);
        ArgumentNullException.ThrowIfNull(inPlace);
        ThinkResume? resume = null;
        var moves = 0;
        while (true)
        {
            var place = job.Place ?? throw new InvalidOperationException("The job holds no place.");
            var current = think(place, resume);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, job.Stopping);
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            BackgroundJobOutcome outcome;
            try { outcome = await current.RunAsync(job, attempt.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && job.Stopping.IsCancellationRequested)
            {
                var kept = Kept(resume, current);
                stopped?.Invoke(place, kept);
                await jobs.ReseatAsync(job, token).ConfigureAwait(false);
                resume = Next(kept, inPlace(job.Place!));
                continue;
            }
            if (outcome.Result is null && held?.Invoke(place, began) == true)
            {
                // Another companion PC's live turn holds that computer's graphics card: wait, then go on there.
                var kept = Kept(resume, current);
                stopped?.Invoke(place, kept);
                job.Report(BackgroundJobState.Paused, BackgroundJob.WaitingForConversation);
                await Task.Delay(HeldRetry, current.Clock, token).ConfigureAwait(false);
                job.Report(BackgroundJobState.Running);
                resume = Next(kept, inPlace(place));
                continue;
            }
            if (outcome.Result is null && moves < MaxMoves && job.Pool is { Count: > 1 } && gone?.Invoke(place) == true)
            {
                // Its computer stopped answering: what it wrote goes on on another place of its pool.
                moves++;
                var kept = Kept(resume, current);
                await jobs.ReseatAsync(job, token, BackgroundJob.ComputerLost).ConfigureAwait(false);
                resume = Next(kept, inPlace(job.Place!));
                continue;
            }
            return resume?.Combine(outcome, current.Partial) ?? outcome;
        }
    }

    // What it wrote so far: the text it went on from and what it added, or what it wrote again (when that is anything).
    private static ThinkResume? Kept(ThinkResume? resume, BackgroundThink current)
    {
        var written = resume is { InPlace: true } ? ThinkResume.Join(resume.Partial, current.Partial)
            : current.Partial.Trim().Length > 0 ? current.Partial : resume?.Partial ?? "";
        return written.Trim().Length == 0 ? null : new(written, false);
    }

    private static ThinkResume? Next(ThinkResume? kept, bool inPlace) => kept is null ? null
        : kept with { InPlace = inPlace && System.Text.Encoding.UTF8.GetByteCount(kept.Partial) <= ThinkResume.MaxInPlaceBytes };
}

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

    /// <summary>The think job kind for <paramref name="settings"/>: one at a time on each of the <paramref name="places"/> Deep
    /// thinking can use (at most <see cref="MaxPlaces"/>), the hourly limit and time limit chosen.</summary>
    public static BackgroundJobKind Kind(ThinkLongerSettings settings, int places = 1) =>
        new(KindName, Math.Clamp(places, 1, MaxPlaces), settings.Hourly, settings.TimeLimit, Doing: "Thinking about");

    /// <summary>The most thinks that run at once, one on each place Deep thinking can use.</summary>
    public const int MaxPlaces = DeepThinkingSettings.MaxPlaces;

    /// <summary>The places a think (or another kind's work that thinks, such as a song's lyrics) can run on: each usable place
    /// of <paramref name="pool"/>, with its computer's name and how much it shares with the conversation.</summary>
    public static IReadOnlyList<BackgroundPlace> Places(DeepThinkingPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return [.. pool.Usable.Take(MaxPlaces).Select(spot => new BackgroundPlace(spot.Key,
            spot.Computer.Length <= 80 ? spot.Computer : spot.Computer[..80], Math.Clamp(spot.Plan.Rank, 0, BackgroundPlace.MaxRank)))];
    }

    public static string Description(ThinkLongerSettings settings, int places = 1) =>
        "Think a task through in the background, step by step, while you keep talking. Use rarely: only for real multi-step " +
        "reasoning or long creative work (song lyrics, a story, a plan, tricky math or code), never for chat or quick answers. " +
        $"Tell the user first that you'll think it over (up to {BackgroundJobs.Duration(settings.TimeLimit)}). Returns at once; " +
        $"the result comes back later in a note. {(places > 1 ? $"Up to {Math.Min(places, MaxPlaces)} at once" : "One at a time")}, " +
        $"{settings.Hourly} an hour.";

    public const string CancelDescription = "Stop the background think (think_longer) that is running.";

    /// <summary>The tools a reply gets while Thinking longer is on, always the same two in the same order, so the start of every
    /// request stays the same for prompt caches (<paramref name="places"/> is how many places Deep thinking can use, from the
    /// settings only, never from what is busy).</summary>
    public static IReadOnlyList<TextToolDefinition> Definitions(ThinkLongerSettings settings, int places = 1) =>
        [new(Name, Description(settings, places), ParametersJson), new(CancelName, CancelDescription, CancelParametersJson)];

    /// <summary>The prompt added to a reply's instructions while think_longer is offered.</summary>
    public static string? Instructions(ThinkLongerSettings settings, PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.ThinkLonger, ("minutes", ((int)settings.TimeLimit.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture)));

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

    /// <summary>What the model is told when the think started: its id, and to tell the user now unless it already did.</summary>
    public static string Started(BackgroundJob job, bool toldUser) =>
        JsonSerializer.Serialize(new { status = "started", id = job.Id, time_limit = BackgroundJobs.Duration(job.Kind.TimeLimit) }) + "\n" +
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
        "hourly_limit" => $"Not started: {start.Message} Answer as well as you can right away instead, without thinking in the background.",
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
    /// isn't sent again.</summary>
    public static BoundedTextInput Input(BoundedTextInput? conversation, string? reply, string task, string? reason, PromptSettings? prompts,
        string? personality = null)
    {
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
                return new BoundedTextInput(message, origin.Personality, history, tools: origin.Tools);
            }
            catch (ContractException) { }
        }
        return new BoundedTextInput(message, personality);
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
                var candidate = new BoundedTextInput(full.UserText, personality, history, tools: tools);
                if (candidate.Utf8Bytes <= bounds.MaxInputBytes && candidate.History.Count <= bounds.MaxHistoryMessages &&
                    candidate.InputTokenReservation <= bounds.MaxInputTokens)
                    return candidate;
            }
            catch (ContractException) { }
            if (history.Count > 0) history.RemoveRange(0, Math.Min(2, history.Count));
            else if (personality is not null) personality = null;
            else if (tools.Count > 0) tools = [];
            else return new BoundedTextInput(full.UserText);
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
    /// <paramref name="withoutReasoning"/> (the model refused the choice before) leaves the model's own default.</summary>
    public static GenerationSettings? Generation(GenerationSettings? reply, ThinkEffort effort, bool withoutReasoning)
    {
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

    /// <summary>Works <paramref name="job"/> out until <paramref name="token"/> is canceled (the job list's cancel or time limit),
    /// with the time the job has left (anything done before it, such as checking a model fits, counts).</summary>
    public async Task<BackgroundJobOutcome> RunAsync(BackgroundJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        token.ThrowIfCancellationRequested();
        var left = job.Kind.TimeLimit - job.Elapsed;
        // Too little time left to be worth asking: the job's time limit ends it.
        if (left < ThinkLonger.MinimumAttempt) await Task.Delay(Timeout.InfiniteTimeSpan, Clock, token).ConfigureAwait(false);
        job.Report(BackgroundJobState.Running, Doing);
        var (request, authorization) = Prepare(left);
        var started = Runtime.Start(request, authorization, token);
        Attempts++;
        var terminal = await started.Completion.ConfigureAwait(false);
        await started.OwnershipRelease.ConfigureAwait(false);
        AttemptFinished?.Invoke(terminal);
        token.ThrowIfCancellationRequested();
        return ThinkLonger.Outcome(terminal, started.Content.Text);
    }
}

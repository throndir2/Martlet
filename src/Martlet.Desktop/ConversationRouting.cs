using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The live conversation's model and fallback choice. It keeps what the Thinking model was found to refuse this app
/// session (a recording, the Thinking steps choice, tools), which models answered HTTP 410 Gone (retired), which job's requests
/// failed lately (Home's health lines) and when Parakeet on this PC stands in for Listening's own route. It never sends a request
/// and never waits: the controller asks it before a request and tells it what came back after the request ended.</summary>
internal sealed class ConversationRouting
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly string? dataDirectory;
    private readonly McpToolService? tools;
    // The Parakeet model on this PC that hears an utterance when Listening's own route fails (LocalSpeechSetup.ListeningStandIn),
    // or null when this PC can't transcribe.
    private readonly Func<SetupRoute, string?>? standIn;
    // The conversation, the Thinking pool's members and the image and audio models' routes follow what was found out at once.
    private readonly Action<ModelAbilities> useAbilities;
    private readonly Dictionary<SetupRole, JobFailure> failures = [];
    // Thinking models that rejected a recording this app session; they get the transcript only until Martlet restarts.
    private readonly HashSet<string> deafModels = new(StringComparer.Ordinal);
    // Thinking models that refused the Thinking steps choice this app session; they get their own default until Martlet restarts.
    private readonly HashSet<string> reasoningRefused = new(StringComparer.Ordinal);
    // Listening's own route as it was when it last failed, and when (controller clock); null once it answers.
    private (SetupRoute Route, long At)? routeFailed;

    internal ConversationRouting(TimeProvider clock, string? dataDirectory, McpToolService? tools, Func<SetupRoute, string?>? standIn,
        Action<ModelAbilities> useAbilities)
    {
        this.clock = clock;
        this.dataDirectory = dataDirectory;
        this.tools = tools;
        this.standIn = standIn;
        this.useAbilities = useAbilities;
    }

    /// <summary>A request that failed since Martlet started and has not worked since: what it was, Martlet's own outcome codes
    /// (never provider text or anything said) and when.</summary>
    internal sealed record JobFailure(SetupRole Role, string What, string Outcome, DateTimeOffset At);

    /// <summary>Each job's latest failed request that no later request of that job has made good, in job order.</summary>
    internal IReadOnlyList<JobFailure> RecentFailures { get { lock (gate) return [.. failures.Values.OrderBy(f => f.Role)]; } }
    /// <summary>Raised off the dispatcher when <see cref="RecentFailures"/> changes.</summary>
    internal event Action? FailuresChanged;

    /// <summary>A reply found something out about a model and saved it (a model its server retired, say): Home proposes a
    /// replacement at once. Raised after the request ended, never on its path.</summary>
    internal event Action<ModelAbility>? AbilityRecorded;

    /// <summary>Saves what Martlet found out about a Thinking model (model-abilities.json) and lets the running conversation use
    /// it at once. Nothing is saved without a data directory (tests).</summary>
    internal void RecordAbility(ModelAbility ability)
    {
        if (dataDirectory is null) return;
        var abilities = ModelAbilities.Load(dataDirectory).With(ability);
        if (!abilities.Save(dataDirectory))
        {
            ErrorLog.Warn("Martlet couldn't save what it found out about the Thinking model (model-abilities.json).");
            return;
        }
        useAbilities(abilities);
        AbilityRecorded?.Invoke(ability);
    }

    /// <summary>Reads model-abilities.json again, after Martlet found out more or another computer shared it.</summary>
    internal void ReloadAbilities()
    {
        if (dataDirectory is not null) useAbilities(ModelAbilities.Load(dataDirectory));
    }

    /// <summary>The Thinking model hears and hasn't refused a recording this app session.</summary>
    internal bool Hears(LiveConversationConfiguration configured) =>
        configured.Hearing() == HearingSupport.Supported && !Deaf(configured);

    /// <summary>The Thinking model refused a recording this app session: it gets the transcript only.</summary>
    internal bool Deaf(LiveConversationConfiguration configured)
    {
        lock (gate) return deafModels.Contains(configured.ToolModelKey());
    }

    /// <summary>The Thinking model refused the Thinking steps choice this app session: it gets its own default.</summary>
    internal bool RefusesReasoning(LiveConversationConfiguration configured)
    {
        lock (gate) return reasoningRefused.Contains(configured.ToolModelKey());
    }

    /// <summary>What a finished reply found the Thinking model refuses, after the request ended: tools, the recording, the
    /// picture or the Thinking steps choice. Later requests are asked without it.</summary>
    internal void NoteRefusals(LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        // A model that rejected tools is asked without them from now on (for a week, on this PC).
        if (terminal.ToolsRejected)
        {
            tools?.MarkUnsupported(configured.ToolModelKey());
            ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the request with tools; " +
                "Martlet asked again without tools and stops offering them to it for a week.");
        }
        // A model that rejected the recording gets the transcript only from now on (this app session), and Martlet remembers
        // it can't hear (model-abilities.json, shared with the owner's other computers) until a check or test says otherwise.
        if (terminal.AudioRejected)
        {
            lock (gate) deafModels.Add(configured.ToolModelKey());
            var thinking = configured.Route(SetupRole.Llm);
            // A paired computer older than recordings refused it before it left this PC: the model may hear, so update that
            // computer instead of remembering the model as deaf.
            if (thinking.RouteType == SetupRouteType.GatewayOllama && HostAudio.IsOld(thinking.Gateway?.HostId))
                ErrorLog.Info($"Martlet on {thinking.Gateway?.HostId} is older than recordings, so the Thinking model " +
                    $"{thinking.ModelId} got the transcript only; update the Martlet host so Thinking hears your voice.");
            else
            {
                RecordAbility(new() { Origin = thinking.Origin, ModelId = thinking.ModelId, Hears = false, Source = "a refused recording",
                    CheckedAt = DateTimeOffset.UtcNow });
                ErrorLog.Info($"The Thinking model {thinking.ModelId} rejected the request with your recording; " +
                    "Martlet asked again with the transcript only and sends it only the transcript from now on.");
            }
        }
        if (terminal.ImageRejected)
            ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the picture of your screen sent " +
                "with your message; Martlet asked again with your words only.");
        // A model that always thinks refused Thinking steps Off (or its server refused the control): it gets its own default
        // from now on (this app session).
        if (terminal.ReasoningRejected)
        {
            lock (gate) reasoningRefused.Add(configured.ToolModelKey());
            ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} refused Thinking steps " +
                $"{(GenerationSettings.ThinkingSteps(configured.Generation) ? "On" : "Off")}; Martlet asked again with the model's own default and " +
                "uses it until it restarts. This model always thinks, so choose a model that can answer without thinking for the fastest replies.");
        }
    }

    internal static bool IsFailure(ConversationSnapshot terminal) =>
        terminal.State is ConversationState.Failed or ConversationState.Partial || terminal.ProviderFailure is not null ||
        terminal.SpeechFailed;

    // A reply whose voice failed is a Speaking failure: its text still completes, so Thinking answered and counts as working.
    // The voice stopping never cuts the reply short; a reply that itself failed is a Thinking failure too.
    internal void LogReplyFailure(string what, LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        if (terminal.SpeechFailed || terminal.FailedProvider == ProviderRole.Tts)
            LogFailure("Spoken " + what.ToLowerInvariant(), configured, SetupRole.Tts, Describe(terminal));
        if (terminal.State is ConversationState.Failed or ConversationState.Partial && terminal.FailedProvider != ProviderRole.Tts)
            LogFailure(what, configured, SetupRole.Llm, Describe(terminal));
        else if (terminal.TextComplete) Succeeded(SetupRole.Llm);
    }

    internal static string Describe(ConversationSnapshot terminal) =>
        $"state {terminal.State}, failure {terminal.Failure}" + TimeLimit(terminal.Failure) +
        (terminal.SpeechFailed ? $", voice stopped {terminal.SpeechFailure}" + TimeLimit(terminal.SpeechFailure) : "") +
        (terminal.ProviderFailure is { } provider ? $", provider {provider}" : "") +
        (terminal.SequenceFailure is { } sequence ? $", stream {sequence.Issue}" : "") +
        (terminal.Playback?.Error?.Code is { } audio ? $", audio {audio}" : "") +
        (terminal.ToolCalls > 0 ? $", {terminal.ToolCalls} tool call(s)" : "") +
        (terminal.ToolsRejected ? ", tools rejected" : "") +
        (terminal.FellBackAfter is { } after ? $", Thinking fallback asked after {after}" : "");

    // AuthorizationExpired and BudgetExpired are the request's own time window running out (a slow or loading model), never a
    // key, sign-in or pairing problem.
    internal static string TimeLimit(ConversationFailure? failure) => failure is ConversationFailure.AuthorizationExpired or
        ConversationFailure.BudgetExpired or ConversationFailure.DeadlineExceeded
        ? " (it ran out of time: the model took too long to answer; this is not a key or sign-in problem)" : "";

    // A reply the fallback answered: Thinking itself failed, so say so in the log (the provider's own explanation is
    // logged just before it by ProviderDiagnostics); the reply still counts as working. A model its server retired is noted too.
    internal void NoteFallback(string what, LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        NoteRetired(configured, terminal);
        if (terminal.FellBackAfter is not { } after || configured.Fallback is not { } fallback) return;
        var route = configured.Route(SetupRole.Llm);
        var answered = terminal.State is ConversationState.Completed or ConversationState.Refused;
        ErrorLog.Warn($"{what}: Thinking failed ({after}) on {route.Origin}, model {route.ModelId}; the Thinking fallback " +
            $"{fallback.Origin}, model {fallback.ModelId}, {(answered ? "answered instead" : "failed too")}.");
    }

    /// <summary>A Chat Completions model its server retired for good (HTTP 410 Gone: <see cref="ProviderFailureCode.ModelRetired"/>)
    /// is remembered as retired on that route (model-abilities.json, shared with the owner's other computers), so Companion,
    /// Health and the next conversation say so; a later reply from it clears that. It runs only after the request ended, from what
    /// the reply already found, and touches the file only when that changes.</summary>
    private void NoteRetired(LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        var route = configured.Route(SetupRole.Llm);
        if (route.RouteType != SetupRouteType.ChatCompletions) return;
        var known = configured.Abilities.Find(route.Origin, route.ModelId)?.Retired;
        var gone = terminal.FellBackAfter == nameof(ProviderFailureCode.ModelRetired) ||
            terminal.FellBackAfter is null && terminal.FailedProvider == ProviderRole.Llm && terminal.ProviderFailure == ProviderFailureCode.ModelRetired;
        if (gone && known is null)
        {
            var now = clock.GetUtcNow();
            ModelAbility ability = new() { Origin = route.Origin, ModelId = route.ModelId, Retired = now, Source = ModelAbility.GoneAnswer, CheckedAt = now };
            if (dataDirectory is not null) RecordAbility(ability);
            else configured.UseAbilities(configured.Abilities.With(ability));
            ErrorLog.Warn($"{route.Origin} answered that {route.ModelId} is gone (HTTP 410), so Martlet remembers it as retired there.");
        }
        else if (!gone && known is not null && terminal.FellBackAfter is null && terminal.State == ConversationState.Completed)
        {
            var cleared = (dataDirectory is null ? configured.Abilities : ModelAbilities.Load(dataDirectory)).Answered(route.Origin, route.ModelId);
            if (dataDirectory is not null && !cleared.Save(dataDirectory))
                ErrorLog.Warn("Martlet couldn't save what it found out about the Thinking model (model-abilities.json).");
            else useAbilities(cleared);
            ErrorLog.Info($"{route.ModelId} answered again, so Martlet no longer counts it retired on {route.Origin}.");
        }
    }

    // One local log line per failed request naming the route it used, never what was said. The provider's own
    // explanation (HTTP status and message) is logged just before it by ProviderDiagnostics.
    internal void LogFailure(string what, LiveConversationConfiguration configured, SetupRole role, string outcome)
    {
        var route = configured.Routes.SingleOrDefault(r => r.Role == role);
        ErrorLog.Warn($"{what} failed ({outcome}). {role} route: {route?.RouteType?.ToString() ?? "OpenAi"}, " +
            $"{route?.Origin ?? "no destination"}, model {route?.ModelId ?? "none"}.");
        lock (gate) failures[role] = new(role, what, outcome, clock.GetLocalNow());
        FailuresChanged?.Invoke();
    }

    internal void Succeeded(SetupRole role)
    {
        bool cleared;
        lock (gate) cleared = failures.Remove(role);
        if (cleared) FailuresChanged?.Invoke();
    }

    /// <summary>How long Listening's stand-in hears every turn at once after Listening's own route failed, before the route is
    /// asked again.</summary>
    internal static readonly TimeSpan StandInFor = TimeSpan.FromSeconds(60);

    /// <summary>How long ago <paramref name="stt"/> failed, while that is less than <see cref="StandInFor"/>; null otherwise.</summary>
    internal TimeSpan? RouteFailedAgo(SetupRoute stt)
    {
        lock (gate)
        {
            if (routeFailed is not { } failed || failed.Route != stt) return null;
            var ago = clock.GetElapsedTime(failed.At);
            return ago < StandInFor ? ago : null;
        }
    }

    /// <summary>The Parakeet model on this PC that hears an utterance in place of <paramref name="stt"/>, or null. Asked only once
    /// the route failed, so a route that answers costs nothing more.</summary>
    internal string? StandInModel(SetupRoute stt) => standIn?.Invoke(stt);

    /// <summary>Listening's own route failed at <paramref name="at"/> and the stand-in heard the turn: later turns skip the route
    /// while the stand-in hears them, until <see cref="StandInFor"/> has passed.</summary>
    internal void StoodIn(SetupRoute stt, long at)
    {
        lock (gate) routeFailed = (stt, at);
    }

    /// <summary>Listening's own route answered, or the stand-in failed too: turns ask the route first again.</summary>
    internal void AskRoute()
    {
        lock (gate) routeFailed = null;
    }
}

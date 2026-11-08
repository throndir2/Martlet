using System.Globalization;

namespace Martlet.Conversation;

/// <summary>What a reply does with the Thinking fallback (Companion › Thinking › If Thinking fails): how long the conversation's
/// own model may take to its first token before the fallback answers instead (<see cref="Wait"/>, null: the route's own limit),
/// and whether the reply asks the fallback first (<see cref="FallbackFirst"/>) because the model failed in the last replies.</summary>
public sealed record ThinkingFallbackPlan(TimeSpan? Wait, bool FallbackFirst, string Why)
{
    public override string ToString() => $"{nameof(ThinkingFallbackPlan)} {Why}";
}

/// <summary>When a cloud Thinking model (often a free one, which can be slow or down) gives way to the Thinking fallback. It owns
/// the first-token times of the conversation's Thinking model on its server and how often that model failed in a row.
/// <list type="bullet">
/// <item>Wait: <see cref="Factor"/> times the usual (middle) first-token time of the last <see cref="Window"/> replies, from
/// <see cref="Minimum"/> to <see cref="Maximum"/>; <see cref="Starting"/> until <see cref="MinimumSamples"/> are known. No public
/// source gives the first-token time of every provider's model (OpenRouter's needs the owner's key, NVIDIA Build gives none),
/// so Martlet measures it: model-speed.json seeds it (<see cref="Seed"/>), and each reply adds its own time.</item>
/// <item>Skip: after <see cref="FailuresToSkip"/> replies in a row where the model failed before its first words, replies ask the
/// fallback first for <see cref="SkipFor"/>; the model is asked only when the fallback fails before its first words. Then the
/// model is tried again, with the short wait.</item>
/// </list>
/// The desktop makes a plan before each reply (<see cref="Plan"/>) and notes each finished reply (<see cref="Noted"/>), after the
/// reply, never on its path. Tests: ThinkingFallbackTimerTests (Martlet.Conversation.Tests). Thread-safe.</summary>
public sealed class ThinkingFallbackTimer
{
    public const int Window = 9;
    public const int MinimumSamples = 3;
    public const int Factor = 3;
    public const int FailuresToSkip = 2;
    public static TimeSpan Minimum { get; } = TimeSpan.FromMilliseconds(2_500);
    public static TimeSpan Maximum { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan Starting { get; } = TimeSpan.FromSeconds(6);
    public static TimeSpan SkipFor { get; } = TimeSpan.FromMinutes(2);

    /// <summary>The reason a reply that asked the fallback first gives (<see cref="ConversationSnapshot.FellBackAfter"/>).</summary>
    public const string SkippedReason = "it failed in the last replies";

    private readonly object gate = new();
    private readonly Queue<int> samples = new();
    private string? model;
    private int failures;
    private DateTimeOffset? skipUntil;

    /// <summary>The wait for first-token times <paramref name="samplesMs"/> (milliseconds).</summary>
    public static TimeSpan WaitFor(IReadOnlyCollection<int> samplesMs)
    {
        ArgumentNullException.ThrowIfNull(samplesMs);
        if (samplesMs.Count < MinimumSamples) return Starting;
        var sorted = samplesMs.Order().ToArray();
        var middle = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
        var wait = TimeSpan.FromMilliseconds(middle * Factor);
        return wait < Minimum ? Minimum : wait > Maximum ? Maximum : wait;
    }

    /// <summary>The model the timer counts for now (origin and model), or null before the first plan.</summary>
    public string? Model { get { lock (gate) return model; } }

    /// <summary>The timer's key for the Thinking route <paramref name="thinking"/> (its server and model), or null when the timer
    /// doesn't apply: no If Thinking fails (<paramref name="hasFallback"/>), or Thinking runs on this PC or the home network, where
    /// the model may first load and keeps its own long limit.</summary>
    public static string? KeyFor(Martlet.Core.Settings.SetupRoute? thinking, bool hasFallback) =>
        !hasFallback || thinking is not { Enabled: true } route ||
        Martlet.Core.Settings.ContextBudget.IsInNetwork(route.RouteType, route.Origin)
            ? null : $"{Martlet.Core.Planning.MeasuredFirstWords.HostKey(route.Origin) ?? route.Origin} {route.ModelId}";

    /// <summary>How many replies in a row the model failed before its first words.</summary>
    public int Failures { get { lock (gate) return failures; } }

    /// <summary>The first-token times (milliseconds) the wait is made from, oldest first.</summary>
    public IReadOnlyList<int> Samples { get { lock (gate) return [.. samples]; } }

    /// <summary>Starts counting for <paramref name="key"/> (the Thinking route's origin and model) with its measured first-word
    /// times <paramref name="samplesMs"/> (model-speed.json), when the timer has none of its own for it yet.</summary>
    public void Seed(string key, IEnumerable<int> samplesMs)
    {
        ArgumentNullException.ThrowIfNull(samplesMs);
        lock (gate)
        {
            Use(key);
            if (samples.Count > 0) return;
            foreach (var ms in samplesMs.TakeLast(Window)) Add(ms);
        }
    }

    /// <summary>The plan for a reply on <paramref name="key"/> at <paramref name="now"/>.</summary>
    public ThinkingFallbackPlan Plan(string key, DateTimeOffset now)
    {
        lock (gate)
        {
            Use(key);
            var wait = WaitFor(samples);
            var known = samples.Count >= MinimumSamples
                ? string.Create(CultureInfo.InvariantCulture, $"{Factor} times its usual first token from {samples.Count} replies")
                : $"too few replies to know its first token ({samples.Count})";
            if (skipUntil is { } until && now < until)
                return new(wait, true, string.Create(CultureInfo.InvariantCulture,
                    $"the fallback first until {until:HH:mm:ss} UTC: the model failed {failures} replies in a row"));
            return new(wait, false, string.Create(CultureInfo.InvariantCulture, $"the fallback after {wait.TotalMilliseconds:0} ms: {known}"));
        }
    }

    /// <summary>Notes a finished reply on <paramref name="key"/>: its model's first-token time, or that the model failed.</summary>
    public void Noted(string key, ConversationSnapshot reply, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reply);
        lock (gate)
        {
            Use(key);
            if (FirstTokenMs(reply) is { } ms)
            {
                Add(ms);
                failures = 0;
                skipUntil = null;
                return;
            }
            // The model itself failed: the fallback answered for it, or it was the last asked and failed.
            var modelFailed = reply.FellBackAfter is { } after ? after != SkippedReason
                : reply.State == ConversationState.Failed && reply.FailedProvider is null or Martlet.Core.Contracts.ProviderRole.Llm &&
                  reply.Failure is ConversationFailure.DeadlineExceeded or ConversationFailure.InvalidStream or
                      ConversationFailure.ProviderFailed or ConversationFailure.DependencyFailed;
            if (!modelFailed) return;
            failures++;
            if (failures >= FailuresToSkip) skipUntil = now + SkipFor;
        }
    }

    /// <summary>The model's own first token (text or hidden reasoning) from its request, in milliseconds; null when the fallback
    /// or Backup Thinking answered, or nothing came.</summary>
    public static int? FirstTokenMs(ConversationSnapshot reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.FellBack || reply.Backup is { Outcome: ThinkingBackupOutcome.Won }) return null;
        var timings = reply.Timings ?? new ConversationTimings();
        var first = reply.FirstTextAfter is { } words && timings.FirstReasoningAfter is { } reasoning
            ? words < reasoning ? words : reasoning
            : reply.FirstTextAfter ?? timings.FirstReasoningAfter;
        if (first is not { } at) return null;
        var ms = (at - (timings.TextRequestAfter ?? TimeSpan.Zero)).TotalMilliseconds;
        return ms >= 1 ? (int)Math.Round(ms) : null;
    }

    // Another model starts counting again.
    private void Use(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (model == key) return;
        model = key;
        samples.Clear();
        failures = 0;
        skipUntil = null;
    }

    private void Add(int ms)
    {
        if (ms is <= 0 or > 120_000) return;
        samples.Enqueue(ms);
        while (samples.Count > Window) samples.Dequeue();
    }

    public override string ToString() => nameof(ThinkingFallbackTimer);
}

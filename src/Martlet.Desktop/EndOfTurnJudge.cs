using Martlet.Conversation;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Always listening's end-of-turn judge on this PC: Smart Turn v3.2 (<see cref="SmartTurnEngine"/>, bundled in Martlet's
/// turn-detection folder) hears the end of what was said and says whether the turn sounds finished. It loads once, in the
/// background, when always listening starts; a missing model or a failed load makes it unavailable (the plain pause rule decides,
/// and the log says why).</summary>
internal sealed class SmartTurnJudge : IEndOfTurnJudge, IDisposable
{
    private readonly SmartTurnEngine? engine;
    private readonly double threshold;
    private readonly object gate = new();
    private Task? warming;
    private volatile string? problem;

    internal SmartTurnJudge(SmartTurnEngine? engine, double threshold = 0.5, string? missing = null)
    {
        this.engine = engine;
        this.threshold = threshold;
        problem = engine is null ? missing ?? "the Smart Turn model or ONNX Runtime isn't in Martlet's folder" : null;
    }

    /// <summary>The judge for Martlet's own folder.</summary>
    internal static SmartTurnJudge Bundled() => new(SmartTurnEngine.Bundled());

    public string Name => SmartTurnEngine.Name;
    public bool Available => engine is not null && problem is null;
    /// <summary>Why the judge can't answer (null while it can).</summary>
    internal string? Problem => problem;
    /// <summary>How long loading took, once it has.</summary>
    internal TimeSpan? LoadTime { get; private set; }
    internal bool Loaded => engine?.Loaded == true;

    /// <summary>Loads the model in the background once; later calls return the same task.</summary>
    internal Task WarmAsync()
    {
        lock (gate)
            return warming ??= engine is null ? Task.CompletedTask : Task.Run(() =>
            {
                try
                {
                    LoadTime = engine.Warm();
                    ErrorLog.Info($"End-of-turn judge: {Name} loaded in {LoadTime.Value.TotalMilliseconds:0} ms.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    problem = "it couldn't load (" + error.Message + ")";
                    ErrorLog.Warn($"End-of-turn judge: {Name} {problem}; the plain pause decides when you finished talking.");
                }
            });
    }

    public Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken)
    {
        if (engine is null) throw new InvalidOperationException(problem);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probability = engine.Probability(Pcm.ToFloats(request.Pcm.Span));
            return new EndOfTurnJudgement(probability > threshold ? TurnVerdict.Complete : TurnVerdict.Incomplete, probability);
        }, cancellationToken);
    }

    public void Dispose() => engine?.Dispose();
}

/// <summary>The end-of-turn judge's fallback: a Thinking-pool member (<see cref="ThinkingJobKind.EndOfTurnJudge"/>, a fast job)
/// reads the quick transcript and answers COMPLETE or INCOMPLETE. Used only when Smart Turn on this PC is missing or fails
/// (<see cref="EndOfTurnJudges.WithFallback"/>). Without a quick transcript (Parakeet isn't Listening) or an answer within
/// <see cref="Budget"/> it fails, and the plain pause decides.</summary>
internal sealed class PoolTurnJudge(Func<ThinkingPool?> pool) : IEndOfTurnJudge
{
    /// <summary>The pool's whole time, from the quick transcript to its answer: well inside the plain pause after the judge is asked.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    internal const string Instructions = "You decide whether someone talking to a voice assistant has finished their turn. You get " +
        "what they said so far, transcribed (punctuation may be missing). Answer with one word: COMPLETE when it is a finished " +
        "thought, request or question they now expect an answer to; INCOMPLETE when they stopped mid-sentence or are trailing " +
        "off (\"and\", \"so\", \"I think that\", \"um\").";

    public string Name => "a Thinking-pool model";
    public bool Available => pool() is { } members && members.CanRun(ThinkingJobKind.EndOfTurnJudge, ThinkingCapability.Text);

    public async Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken)
    {
        var members = pool() ?? throw new InvalidOperationException("there is no Thinking pool");
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? words = null;
        if (request.Words is { } quick)
        {
            try { words = await quick.WaitAsync(Budget, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        if (string.IsNullOrWhiteSpace(words)) throw new InvalidOperationException("no quick transcript to judge in time");
        var left = Budget - started.Elapsed;
        if (left <= TimeSpan.FromMilliseconds(50)) throw new TimeoutException("the quick transcript came too late");
        var result = await members.RunAsync(new ThinkingJob
        {
            Kind = ThinkingJobKind.EndOfTurnJudge, Instructions = Instructions, Text = words, Timeout = left, DropWhenStale = true,
            MaxOutputTokens = 4, Reasoning = false
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidOperationException(result.Problem ?? result.Outcome.ToString());
        return Parse(result.Text) ?? throw new InvalidOperationException("the answer was neither COMPLETE nor INCOMPLETE");
    }

    /// <summary>The verdict in a pool member's answer, or null when it gave neither.</summary>
    internal static EndOfTurnJudgement? Parse(string? answer)
    {
        var word = answer?.Trim().TrimStart('*', '"', '\'').ToUpperInvariant() ?? "";
        if (word.StartsWith("INCOMPLETE", StringComparison.Ordinal) || word.StartsWith("UNFINISHED", StringComparison.Ordinal))
            return new(TurnVerdict.Incomplete);
        if (word.StartsWith("COMPLETE", StringComparison.Ordinal) || word.StartsWith("FINISHED", StringComparison.Ordinal))
            return new(TurnVerdict.Complete);
        return null;
    }
}

/// <summary>The end-of-turn judge's quick transcript of exactly the speech kept for one utterance (Parakeet on this PC, started
/// when the judge was asked): speech-to-text uses it instead of transcribing the same audio again.</summary>
internal sealed record QuickWords(string ModelId, byte[] Pcm, Task<LocalTranscript> Transcript, long StartedAt);

/// <summary>Speech-to-text that hands back the quick transcript (<see cref="QuickWords"/>) and transcribes again only when that
/// one failed. It goes through <see cref="LocalTranscriptionAdapter"/>, so the usual one-use authorization still applies.</summary>
internal sealed class ReusedWords(Task<LocalTranscript> quick, ILocalTranscriber again) : ILocalTranscriber
{
    internal bool Reused { get; private set; }

    public async Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
    {
        try
        {
            var heard = await quick.WaitAsync(cancellationToken).ConfigureAwait(false);
            Reused = true;
            return heard;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await again.TranscribeAsync(modelId, pcm16kMono, cancellationToken).ConfigureAwait(false);
        }
    }
}

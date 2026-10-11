using Martlet.Core.Contracts;
using System.Text.Json.Serialization;

namespace Martlet.Providers;

public sealed record TranscriptionLimits : IContract
{
    /// <summary>The largest recording Martlet holds or uploads at once: OpenAI's 25 MB upload limit (about 13 minutes of 16 kHz
    /// speech). A request may set less; talking longer than one request takes is split (<see cref="BoundedWaveAudio.Split"/>).</summary>
    public const int HardMaxAudioBytes = 25_000_000;
    public static TimeSpan HardMaxDuration => TimeSpan.FromMinutes(15);
    /// <summary>The most a conversation's one speech-to-text request hears (canonical 16 kHz PCM16 in a WAV: 800,044 bytes); a longer
    /// turn goes in pieces of at most this, each cut at a pause.</summary>
    public static TimeSpan ConversationPiece => TimeSpan.FromSeconds(25);
    public int MaxAudioBytes { get; init; } = HardMaxAudioBytes;
    public TimeSpan MaxAudioDuration { get; init; } = HardMaxDuration;
    public int MaxResponseBytes { get; init; } = ContractRules.MaxJsonBytes;
    public int MaxTextCharacters { get; init; } = ContractRules.MaxTextCharacters;
    public TimeSpan MaxRequestTime { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        ContractRules.Require(MaxAudioBytes is >= 46 and <= HardMaxAudioBytes &&
            MaxAudioDuration > TimeSpan.Zero && MaxAudioDuration <= HardMaxDuration &&
            MaxResponseBytes is > 0 and <= ContractRules.MaxJsonBytes &&
            MaxTextCharacters is > 0 and <= ContractRules.MaxTextCharacters &&
            MaxRequestTime > TimeSpan.Zero && MaxRequestTime <= TimeSpan.FromMinutes(2),
            "Transcription limits are out of range.");
    }
}

public sealed record ProviderRequestContext : IContract
{
    public required CorrelationIds Ids { get; init; }
    public required long Epoch { get; init; }
    public required DateTimeOffset Deadline { get; init; }

    public void Validate()
    {
        ContractRules.Require(Ids is not null, "Request correlation is required.");
        Ids!.Validate();
        ContractRules.Require(Epoch is >= 0 and < int.MaxValue, "The request epoch must leave room for local stop.");
    }
}

public sealed record ProviderCredentialBinding(Uri Origin, ProviderRole Role, string UpstreamModelId);

// Constructed ONLY by a calling path that has obtained data/cost permission.
// This library checks the scope, not the identity or authority of that caller.
public sealed class AudioUploadAuthorization(
    ProviderCredentialBinding binding,
    CorrelationIds ids,
    long epoch,
    TranscriptionLimits limits,
    DateTimeOffset expiresAt,
    bool allowAudioUpload,
    bool allowPotentialCharges)
{
    public ProviderCredentialBinding Binding { get; } = binding;
    public CorrelationIds Ids { get; } = ids;
    public long Epoch { get; } = epoch;
    public TranscriptionLimits Limits { get; } = limits;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public bool AllowAudioUpload { get; } = allowAudioUpload;
    public bool AllowPotentialCharges { get; } = allowPotentialCharges;
    private int consumed;

    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    public override string ToString() => nameof(AudioUploadAuthorization);
}

public enum TranscriptionOutcome { Completed, NoSpeech, Failed, Canceled, DeadlineExceeded }
public enum UsageKind { Unknown, Tokens, Duration }

public sealed record TranscriptionUsage(
    UsageKind Kind,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    double? Seconds = null)
{
    public static TranscriptionUsage Unknown { get; } = new(UsageKind.Unknown);
    public decimal? EstimatedCost => null;
}

/// <summary>What the speech-to-text engine itself said about one transcript, where it says anything: the model's own token
/// probabilities (Parakeet, whisper), whisper's no-speech probability and average log probability, and when the words were
/// heard. Uncalibrated engine output: <see cref="UtteranceFilter"/> weighs it to tell words from noise, but it is never the
/// calibrated <see cref="TranscriptionResult.Confidence"/> the participation policy needs. Null fields are unknown.</summary>
public sealed record TranscriptionEvidence
{
    /// <summary>Which engine said it ("parakeet", "whisper").</summary>
    public string Engine { get; init; } = "";
    /// <summary>The mean probability of the transcript's tokens (or words), 0-1.</summary>
    public double? MeanProbability { get; init; }
    /// <summary>The least likely token's (or word's) probability, 0-1.</summary>
    public double? MinimumProbability { get; init; }
    /// <summary>whisper: how likely the audio held no speech at all (the highest of its segments), 0-1.</summary>
    public double? NoSpeechProbability { get; init; }
    /// <summary>whisper: the average log probability of the decoded tokens (0 is certain; below -1 is a guess).</summary>
    public double? AverageLogProbability { get; init; }
    /// <summary>When the first and last words were heard, from the start of the audio.</summary>
    public TimeSpan? WordsStart { get; init; }
    public TimeSpan? WordsEnd { get; init; }
}

public sealed class TranscriptionResult
{
    public ProviderRequestContext Context { get; }
    public EvidenceProvenance Provenance { get; }
    public TranscriptionOutcome Outcome { get; }
    [JsonIgnore]
    public string? Text { get; }
    [JsonIgnore]
    public IReadOnlyList<string> Languages { get; }
    public double? Confidence => null;
    /// <summary>What the engine said about the transcript (local engines only today), or null.</summary>
    public TranscriptionEvidence? Evidence { get; }
    public TranscriptionUsage Usage { get; }
    public ProviderFailure? Failure { get; }

    internal TranscriptionResult(ProviderRequestContext context, EvidenceProvenance provenance,
        TranscriptionOutcome outcome, string? text = null, IReadOnlyList<string>? languages = null,
        TranscriptionUsage? usage = null, ProviderFailure? failure = null, TranscriptionEvidence? evidence = null)
    {
        Context = context;
        Provenance = provenance;
        Outcome = outcome;
        Text = text;
        Languages = languages ?? Array.Empty<string>();
        Usage = usage ?? TranscriptionUsage.Unknown;
        Failure = failure;
        Evidence = evidence;
    }

    /// <summary>The pieces of one long recording (<see cref="BoundedWaveAudio.Split"/>) heard in turn as one transcript: their
    /// words in order, Completed when any piece had words, otherwise NoSpeech. Every piece must have completed or heard no
    /// speech; <paramref name="offsets"/> are where each piece starts in the recording.</summary>
    public static TranscriptionResult Join(IReadOnlyList<TranscriptionResult> pieces, IReadOnlyList<TimeSpan> offsets)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(offsets);
        ContractRules.Require(pieces.Count > 0 && offsets.Count == pieces.Count &&
            pieces.All(p => p is { Outcome: TranscriptionOutcome.Completed or TranscriptionOutcome.NoSpeech }),
            "Only heard pieces of one recording can be joined.");
        if (pieces.Count == 1) return pieces[0];
        var words = pieces.Where(p => p.Outcome == TranscriptionOutcome.Completed && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text!.Trim()).ToArray();
        var outcome = words.Length > 0 ? TranscriptionOutcome.Completed : TranscriptionOutcome.NoSpeech;
        var usage = pieces.Select(p => p.Usage).Distinct().Count() == 1 ? pieces[0].Usage
            : pieces.All(p => p.Usage.Kind == UsageKind.Duration) ? new TranscriptionUsage(UsageKind.Duration, Seconds: pieces.Sum(p => p.Usage.Seconds ?? 0))
            : pieces.All(p => p.Usage.Kind == UsageKind.Tokens) ? new TranscriptionUsage(UsageKind.Tokens, pieces.Sum(p => p.Usage.InputTokens ?? 0),
                pieces.Sum(p => p.Usage.OutputTokens ?? 0), pieces.Sum(p => p.Usage.TotalTokens ?? 0))
            : TranscriptionUsage.Unknown;
        var heard = pieces.Select((p, i) => (p.Evidence, Offset: offsets[i])).Where(e => e.Evidence is not null).ToArray();
        TranscriptionEvidence? evidence = heard.Length == 0 ? null : new()
        {
            Engine = heard[0].Evidence!.Engine,
            MeanProbability = Average(heard.Select(e => e.Evidence!.MeanProbability)),
            MinimumProbability = heard.Select(e => e.Evidence!.MinimumProbability).Min(),
            // The whole recording held speech when any piece did.
            NoSpeechProbability = heard.Select(e => e.Evidence!.NoSpeechProbability).Min(),
            AverageLogProbability = Average(heard.Select(e => e.Evidence!.AverageLogProbability)),
            WordsStart = heard.Where(e => e.Evidence!.WordsStart is not null).Select(e => e.Offset + e.Evidence!.WordsStart).FirstOrDefault(),
            WordsEnd = heard.Where(e => e.Evidence!.WordsEnd is not null).Select(e => e.Offset + e.Evidence!.WordsEnd).LastOrDefault()
        };
        return new(pieces[0].Context, pieces[0].Provenance, outcome, words.Length > 0 ? string.Join(" ", words) : null,
            pieces.SelectMany(p => p.Languages).Distinct(StringComparer.Ordinal).ToArray(), usage, null, evidence);

        static double? Average(IEnumerable<double?> values) =>
            values.Where(v => v is not null).ToArray() is { Length: > 0 } known ? known.Average() : null;
    }

    public ProviderEvent ToTerminalEvent() => new()
    {
        Version = ContractVersion.Current,
        Ids = Context.Ids,
        Epoch = Context.Epoch,
        Sequence = 1,
        ProviderId = OpenAiTranscriptionCatalog.ProviderId,
        Provenance = Provenance,
        Kind = Outcome switch
        {
            TranscriptionOutcome.Completed => ProviderEventKind.Completed,
            TranscriptionOutcome.NoSpeech => ProviderEventKind.NoSpeech,
            TranscriptionOutcome.Canceled => ProviderEventKind.Canceled,
            _ => ProviderEventKind.Failed
        },
        Text = Text,
        Error = Failure?.Error
    };

    public override string ToString() => $"{nameof(TranscriptionResult)}: {Outcome}";
}

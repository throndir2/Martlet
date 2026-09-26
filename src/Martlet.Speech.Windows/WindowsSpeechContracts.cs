using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Speech.Windows;

public sealed record WindowsSpeechVoice(
    [property: JsonIgnore] string Id, [property: JsonIgnore] string Name, string Culture)
{
    public override string ToString() => nameof(WindowsSpeechVoice);
}

public enum WindowsSpeechFailure
{
    None, Busy, ConsentMissing, ConsentMismatch, ConsentExpired, ConsentConsumed,
    VoiceUnavailable, EngineUnavailable, EngineFailed, InputLimit, AudioLimit,
    InvalidAudio, FirstAudioTimeout, IdleTimeout, DeadlineExceeded, CleanupFailed, Quarantined
}

public sealed class WindowsSpeechException(WindowsSpeechFailure failure)
    : Exception($"Windows speech failed: {failure}.")
{
    public WindowsSpeechFailure Failure { get; } = failure;
}

// Issued by the host only after fresh local output/AI-voice disclosure consent.
// This is scope checking, not a mechanism for authenticating the calling host.
public sealed class WindowsSpeechAuthorization(
    ProviderRequestContext context, string voiceId, BoundedSpeechInput input,
    SpeechSynthesisLimits limits, DateTimeOffset expiresAt, bool allowGeneratedSpeech)
{
    [JsonIgnore] public ProviderRequestContext Context { get; } = context;
    [JsonIgnore] public string VoiceId { get; } = voiceId;
    [JsonIgnore] public SpeechSynthesisLimits Limits { get; } = limits;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public bool AllowGeneratedSpeech { get; } = allowGeneratedSpeech;
    private readonly BoundedSpeechInput input = input;
    private int consumed;

    internal bool Matches(ProviderRequestContext request, string voice, BoundedSpeechInput text,
        SpeechSynthesisLimits bounds) =>
        Context == request && VoiceId == voice && ReferenceEquals(input, text) && Limits == bounds;
    internal bool TryConsume() => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    public override string ToString() => nameof(WindowsSpeechAuthorization);
}

public sealed record WindowsSpeechResult(
    SpeechSynthesisOutcome Outcome, WindowsSpeechFailure Failure,
    EvidenceProvenance Provenance, WindowsSpeechAudio? Audio)
{
    // Returning from synthesis means its native owner has finished cleanup, not just cancellation.
    public bool OwnershipReleased => Failure != WindowsSpeechFailure.CleanupFailed;
    public override string ToString() => $"{nameof(WindowsSpeechResult)}: {Outcome}, {Failure}";
}

public sealed class WindowsSpeechAudio
{
    private readonly byte[] bytes;
    private readonly ProviderRequestContext context;
    private readonly Action ensureActive;
    private int enumerated;
    public PcmFormat Format => WindowsSpeechSynthesisAdapter.Format;
    public long SampleCount => bytes.Length / Format.BlockAlignment;
    public IAsyncEnumerable<PcmFrame> Frames => Enumerate();

    internal WindowsSpeechAudio(byte[] bytes, ProviderRequestContext context, Action ensureActive)
    {
        this.bytes = bytes;
        this.context = context;
        this.ensureActive = ensureActive;
    }

    private async IAsyncEnumerable<PcmFrame> Enumerate(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("Generated speech can only be enumerated once.");
        await Task.CompletedTask.ConfigureAwait(false);
        const int frameBytes = 960;
        for (int offset = 0, sequence = 0; offset < bytes.Length; offset += frameBytes, sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ensureActive();
            yield return new(context.Ids, context.Epoch, sequence, offset / 2, Format,
                bytes.AsSpan(offset, Math.Min(frameBytes, bytes.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ensureActive();
    }

    public override string ToString() => nameof(WindowsSpeechAudio);
}

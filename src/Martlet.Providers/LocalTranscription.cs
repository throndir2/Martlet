using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>What a speech-to-text model on this PC heard: the text (empty when nothing was said) and what the engine said about
/// it, when it says.</summary>
public sealed record LocalTranscript(string Text, TranscriptionEvidence? Evidence = null);

/// <summary>Transcribes one utterance with a speech-to-text model running inside Martlet on this PC (Parakeet). An empty
/// result means no speech was recognized; failures throw.</summary>
public interface ILocalTranscriber
{
    Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken);
}

/// <summary>One push-to-talk or hands-free utterance transcribed on this PC instead of by a cloud provider or host. It checks
/// the same one-use audio authorization as the other adapters, bound to the local origin and model, and returns the same
/// <see cref="TranscriptionResult"/>. Nothing is sent anywhere.</summary>
public sealed class LocalTranscriptionAdapter(ILocalTranscriber transcriber, TimeProvider? clock = null)
{
    public const string ProviderId = LocalSpeechSetup.ParakeetAlias;
    public const int SampleRate = 16_000;
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>The binding a caller authorizes for local speech-to-text: this PC's origin, the STT role and the model.</summary>
    public static ProviderCredentialBinding Binding(string modelId) => new(new Uri(SelfHostSetup.LocalOrigin), ProviderRole.Stt, modelId);

    public async Task<TranscriptionResult> TranscribeAsync(ProviderRequestContext context, string modelId, BoundedWaveAudio audio,
        TranscriptionLimits limits, AudioUploadAuthorization? authorization, CancellationToken cancellationToken,
        CancellationToken operationCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        if (cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested)
            return new(context, EvidenceProvenance.Live, TranscriptionOutcome.Canceled);
        var now = time.GetUtcNow();
        if (authorization is null || !authorization.AllowAudioUpload) return Failed(context, ProviderFailureCode.ConsentMissing);
        if (authorization.Binding != Binding(modelId) || authorization.Ids != context.Ids ||
            authorization.Epoch != context.Epoch || authorization.Limits != limits)
            return Failed(context, ProviderFailureCode.ConsentMismatch);
        if (authorization.ExpiresAt <= now) return Failed(context, ProviderFailureCode.ConsentExpired);
        if (audio.Format.SampleRate != SampleRate || audio.ByteLength > limits.MaxAudioBytes || audio.Duration > limits.MaxAudioDuration)
            return Failed(context, ProviderFailureCode.AudioLimit);
        if (!authorization.TryConsume()) return Failed(context, ProviderFailureCode.ConsentConsumed);
        var deadline = new[] { context.Deadline, authorization.ExpiresAt, now + limits.MaxRequestTime }.Min();
        if (deadline <= now) return Failed(context, ProviderFailureCode.DeadlineExceeded);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationCancellationToken);
        stop.CancelAfter(deadline - now);
        try
        {
            var heard = await transcriber.TranscribeAsync(modelId, audio.Pcm, stop.Token).ConfigureAwait(false);
            var text = heard.Text;
            if (text.Length > limits.MaxTextCharacters) return Failed(context, ProviderFailureCode.ResponseTooLarge);
            return string.IsNullOrWhiteSpace(text)
                ? new(context, EvidenceProvenance.Live, TranscriptionOutcome.NoSpeech, evidence: heard.Evidence)
                : new(context, EvidenceProvenance.Live, TranscriptionOutcome.Completed, text.Trim(), evidence: heard.Evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested)
        {
            return new(context, EvidenceProvenance.Live, TranscriptionOutcome.Canceled);
        }
        catch (OperationCanceledException) { return Failed(context, ProviderFailureCode.DeadlineExceeded); }
        // The model runs natively in this process; surface a failed turn, never a crash.
        catch (Exception) { return Failed(context, ProviderFailureCode.Server); }
    }

    private static TranscriptionResult Failed(ProviderRequestContext context, ProviderFailureCode code) =>
        new(context, EvidenceProvenance.Live, code == ProviderFailureCode.DeadlineExceeded
            ? TranscriptionOutcome.DeadlineExceeded : TranscriptionOutcome.Failed, failure: new(code));

    public override string ToString() => nameof(LocalTranscriptionAdapter);
}

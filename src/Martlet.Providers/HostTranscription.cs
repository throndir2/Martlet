using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>Transcribes one utterance with a paired host's own speech-to-text model (its stt role). Failures throw
/// <see cref="HostTextException"/> with the provider failure code; an empty result means no speech was recognized.</summary>
public interface IHostTranscriptionClient
{
    Task<string> TranscribeAsync(HostTextTarget target, string modelId, ReadOnlyMemory<byte> pcm16kMono,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken cancellationToken);
}

/// <summary>One push-to-talk or hands-free utterance transcribed by the user's own Martlet host instead of a cloud
/// provider. It checks the same one-use audio upload authorization as the cloud adapter, bound to the host's gateway
/// origin and model, and returns the same <see cref="TranscriptionResult"/>.</summary>
public sealed class HostTranscriptionAdapter(IHostTranscriptionClient client, TimeProvider? clock = null)
{
    public const string ProviderId = SelfHostSetup.GatewaySttAlias;
    public const int SampleRate = 16_000;
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>The binding a caller authorizes for this host: its gateway origin, the STT role and model.</summary>
    public static ProviderCredentialBinding Binding(HostTextTarget target, string modelId) =>
        new(new Uri(target.Origin), ProviderRole.Stt, modelId);

    public async Task<TranscriptionResult> TranscribeAsync(ProviderRequestContext context, HostTextTarget target, string modelId,
        BoundedWaveAudio audio, TranscriptionLimits limits, AudioUploadAuthorization? authorization,
        CancellationToken cancellationToken, CancellationToken operationCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        if (cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested)
            return new(context, EvidenceProvenance.Live, TranscriptionOutcome.Canceled);
        var now = time.GetUtcNow();
        if (authorization is null || !authorization.AllowAudioUpload) return Failed(context, ProviderFailureCode.ConsentMissing);
        if (authorization.Binding.Origin != new Uri(target.Origin)) return Failed(context, ProviderFailureCode.OriginRejected);
        if (authorization.Binding != Binding(target, modelId) || authorization.Ids != context.Ids ||
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
            var text = await client.TranscribeAsync(target, modelId, audio.Pcm, context.Ids, context.Epoch, deadline, stop.Token)
                .ConfigureAwait(false);
            if (text.Length > limits.MaxTextCharacters) return Failed(context, ProviderFailureCode.ResponseTooLarge);
            return string.IsNullOrWhiteSpace(text)
                ? new(context, EvidenceProvenance.Live, TranscriptionOutcome.NoSpeech)
                : new(context, EvidenceProvenance.Live, TranscriptionOutcome.Completed, text.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || operationCancellationToken.IsCancellationRequested)
        {
            return new(context, EvidenceProvenance.Live, TranscriptionOutcome.Canceled);
        }
        catch (OperationCanceledException) { return Failed(context, ProviderFailureCode.DeadlineExceeded); }
        catch (HostTextException error) { return Failed(context, error.Code); }
        // The host client crosses network, vault and JSON boundaries; surface a failed turn, never a crash.
        catch (Exception) { return Failed(context, ProviderFailureCode.Server); }
    }

    private static TranscriptionResult Failed(ProviderRequestContext context, ProviderFailureCode code) =>
        new(context, EvidenceProvenance.Live, code == ProviderFailureCode.DeadlineExceeded
            ? TranscriptionOutcome.DeadlineExceeded : TranscriptionOutcome.Failed, failure: new(code));

    public override string ToString() => nameof(HostTranscriptionAdapter);
}

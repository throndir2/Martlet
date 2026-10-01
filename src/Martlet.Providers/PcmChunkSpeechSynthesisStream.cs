using System.Runtime.CompilerServices;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

/// <summary>One reply segment spoken by a voice that hands back raw 24 kHz mono PCM16 chunks: a paired host's F5 voice or an
/// installed Windows voice. It checks the same one-use speech disclosure authorization as the cloud adapter, bound to the
/// voice's exact destination and selection, and re-frames the chunks into the 20 ms frames playback expects.</summary>
public abstract class PcmChunkSpeechSynthesisStream : ISpeechSynthesisStream
{
    private const int FrameBytes = 24_000 / 50 * 2;
    private readonly ProviderCredentialBinding binding;
    private readonly ProviderRequestContext context;
    private readonly SpeechSynthesisSelection selection;
    private readonly BoundedSpeechInput input;
    private readonly SpeechSynthesisLimits limits;
    private readonly SpeechDisclosureAuthorization? authorization;
    private readonly TimeProvider clock;
    private readonly CancellationToken callerToken;
    private readonly DateTimeOffset startedUtc;
    private int enumerated;

    private protected PcmChunkSpeechSynthesisStream(string providerId, CancellationCapability cancellation,
        ProviderCredentialBinding binding, ProviderRequestContext context, SpeechSynthesisSelection selection,
        BoundedSpeechInput input, SpeechSynthesisLimits limits, SpeechDisclosureAuthorization? authorization,
        TimeProvider? clock, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        ContractRules.Identifier(selection.ModelAlias);
        this.binding = binding;
        this.context = context;
        this.selection = selection;
        this.input = input;
        this.limits = limits;
        this.authorization = authorization;
        this.clock = clock ?? TimeProvider.System;
        this.callerToken = callerToken;
        startedUtc = this.clock.GetUtcNow();
        Capabilities = new()
        {
            Version = ContractVersion.Current, ProviderId = providerId, AdapterVersion = "1.0.0", ModelId = selection.ModelAlias,
            Role = ProviderRole.Tts, Provenance = EvidenceProvenance.Live, SttPartials = CapabilitySupport.Unsupported,
            LlmTextDeltas = CapabilitySupport.Unsupported, TtsAudioTransport = CapabilitySupport.Supported,
            TtsIncrementalSynthesis = CapabilitySupport.Unsupported, Cancellation = cancellation,
            MaxInputBytes = BoundedSpeechInput.HardMaxUtf8Bytes
        };
    }

    public SpeechSynthesisResult? Result { get; private set; }
    public ProviderCapabilities Capabilities { get; }
    public static PcmFormat Format => OpenAiSpeechSynthesisCatalog.PcmFormat;

    /// <summary>The voice's raw PCM16 chunks for <paramref name="input"/>; any length, re-framed by this stream.</summary>
    private protected abstract IAsyncEnumerable<byte[]> Chunks(ProviderRequestContext context, BoundedSpeechInput input,
        SpeechSynthesisLimits limits, DateTimeOffset deadline, CancellationToken cancellationToken);

    public IAsyncEnumerator<PcmFrame> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("A speech synthesis stream can only be enumerated once.");
        return Enumerate(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    private async IAsyncEnumerable<PcmFrame> Enumerate([EnumeratorCancellation] CancellationToken enumerationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(callerToken, enumerationToken);
        long samples = 0, sequence = 0;
        try
        {
            if (Authorize() is { } blocked)
            {
                Finish(SpeechSynthesisOutcome.Failed, samples, blocked);
                yield break;
            }
            var deadline = new[] { context.Deadline, authorization!.ExpiresAt, startedUtc + limits.MaxRequestTime }.Min();
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                Finish(SpeechSynthesisOutcome.DeadlineExceeded, samples, ProviderFailureCode.DeadlineExceeded);
                yield break;
            }
            using var timeout = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, timeout.Token);
            var chunks = Chunks(context, input, limits, deadline, linked.Token).GetAsyncEnumerator(linked.Token);
            var pending = new byte[FrameBytes];
            var filled = 0;
            try
            {
                while (true)
                {
                    var (moved, outcome, failure) = await MoveAsync(chunks, stop.Token, timeout.Token).ConfigureAwait(false);
                    if (outcome is { } ended)
                    {
                        Finish(ended, samples, failure);
                        yield break;
                    }
                    if (!moved) break;
                    var pcm = chunks.Current;
                    if (pcm.Length % 2 != 0)
                    {
                        Finish(SpeechSynthesisOutcome.Failed, samples, ProviderFailureCode.ResponseSchema);
                        yield break;
                    }
                    for (var offset = 0; offset < pcm.Length;)
                    {
                        var count = Math.Min(FrameBytes - filled, pcm.Length - offset);
                        pcm.AsSpan(offset, count).CopyTo(pending.AsSpan(filled));
                        filled += count;
                        offset += count;
                        if (filled < FrameBytes) continue;
                        if (samples + FrameBytes / 2 > limits.MaxSamples)
                        {
                            Finish(SpeechSynthesisOutcome.Failed, samples, ProviderFailureCode.OutputAudioLimit);
                            yield break;
                        }
                        var frame = new PcmFrame(context.Ids, context.Epoch, sequence++, samples, Format, pending);
                        samples += frame.SamplesPerChannel;
                        filled = 0;
                        yield return frame;
                    }
                }
            }
            finally
            {
                await chunks.DisposeAsync().ConfigureAwait(false);
            }
            if (filled > 0)
            {
                if (samples + filled / 2 > limits.MaxSamples)
                {
                    Finish(SpeechSynthesisOutcome.Failed, samples, ProviderFailureCode.OutputAudioLimit);
                    yield break;
                }
                var last = new PcmFrame(context.Ids, context.Epoch, sequence++, samples, Format, pending.AsSpan(0, filled));
                samples += last.SamplesPerChannel;
                yield return last;
            }
            Finish(samples > 0 ? SpeechSynthesisOutcome.Completed : SpeechSynthesisOutcome.Failed, samples,
                samples > 0 ? null : ProviderFailureCode.ResponseTruncated);
        }
        finally
        {
            stop.Cancel();
            Result ??= new(context, EvidenceProvenance.Live, SpeechSynthesisOutcome.Canceled, samples, null);
        }
    }

    private static async Task<(bool Moved, SpeechSynthesisOutcome? Outcome, ProviderFailureCode? Failure)> MoveAsync(
        IAsyncEnumerator<byte[]> chunks, CancellationToken stop, CancellationToken timeout)
    {
        try { return (await chunks.MoveNextAsync().ConfigureAwait(false), null, null); }
        catch (HostTextException error) { return (false, SpeechSynthesisOutcome.Failed, error.Code); }
        catch (WindowsVoiceException error) { return (false, SpeechSynthesisOutcome.Failed, error.Code); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return (false, SpeechSynthesisOutcome.Canceled, null); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (false, SpeechSynthesisOutcome.DeadlineExceeded, ProviderFailureCode.DeadlineExceeded);
        }
        // Voice clients cross network, vault, store, COM and JSON boundaries; surface a failed segment, never a crash.
        catch (Exception) { return (false, SpeechSynthesisOutcome.Failed, ProviderFailureCode.Server); }
    }

    private ProviderFailureCode? Authorize()
    {
        if (authorization is null || !authorization.AllowTextDisclosure || !authorization.AiGeneratedVoiceDisclosureConfirmed)
            return ProviderFailureCode.ConsentMissing;
        if (authorization.Binding is null || authorization.Binding.Origin != binding.Origin)
            return ProviderFailureCode.OriginRejected;
        if (authorization.Binding != binding || authorization.Selection != selection || !authorization.Matches(input) ||
            authorization.Ids != context.Ids || authorization.Epoch != context.Epoch || authorization.Limits != limits)
            return ProviderFailureCode.ConsentMismatch;
        if (authorization.ExpiresAt <= clock.GetUtcNow()) return ProviderFailureCode.ConsentExpired;
        if (input.Utf8Bytes > limits.MaxInputBytes) return ProviderFailureCode.InputLimit;
        return authorization.TryConsume() ? null : ProviderFailureCode.ConsentConsumed;
    }

    private void Finish(SpeechSynthesisOutcome outcome, long samples, ProviderFailureCode? code) =>
        Result = new(context, EvidenceProvenance.Live, outcome, samples, null,
            outcome is SpeechSynthesisOutcome.Completed or SpeechSynthesisOutcome.Canceled ? null
                : new ProviderFailure(code ?? ProviderFailureCode.Server, stage: Stage.Synthesis));
}

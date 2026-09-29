using System.Runtime.CompilerServices;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>The paired Martlet host whose F5 voice (its f5 role) speaks replies, reached through the host's pinned
/// gateway. <see cref="CredentialId"/> names the TTS route's reference to the host pairing kept in the OS vault;
/// <see cref="ModelId"/> is the host's advertised F5 model and <see cref="ReferenceRevision"/> the applied reference voice.</summary>
public sealed record HostSpeechTarget(string Origin, string HostId, string SpkiFingerprint, string DeviceId, Guid CredentialId,
    string ModelId, Guid PresetId, string ReferenceRevision)
{
    public override string ToString() => nameof(HostSpeechTarget);
}

/// <summary>Streams one reply segment's 24 kHz mono PCM16 bytes from a paired host's F5 voice.
/// Failures throw <see cref="HostTextException"/> with the provider failure code.</summary>
public interface IHostSpeechClient
{
    IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids, long epoch,
        DateTimeOffset deadline, CancellationToken cancellationToken);
}

/// <summary>One reply segment spoken by the user's own Martlet host instead of a cloud voice. It checks the same one-use
/// speech disclosure authorization as the cloud adapter, bound to the host's gateway origin and F5 model, and re-frames
/// the host's PCM into the 20 ms 24 kHz mono PCM16 frames playback expects (the F5 output already has that format).</summary>
public sealed class HostSpeechSynthesisStream : ISpeechSynthesisStream
{
    public const string ProviderId = SelfHostSetup.GatewayF5Alias;
    private const int FrameBytes = 24_000 / 50 * 2;
    private readonly IHostSpeechClient client;
    private readonly HostSpeechTarget target;
    private readonly ProviderRequestContext context;
    private readonly SpeechSynthesisSelection selection;
    private readonly BoundedSpeechInput input;
    private readonly SpeechSynthesisLimits limits;
    private readonly SpeechDisclosureAuthorization? authorization;
    private readonly TimeProvider clock;
    private readonly CancellationToken callerToken;
    private readonly DateTimeOffset startedUtc;
    private int enumerated;

    public HostSpeechSynthesisStream(IHostSpeechClient client, HostSpeechTarget target, ProviderRequestContext context,
        SpeechSynthesisSelection selection, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        SpeechDisclosureAuthorization? authorization, TimeProvider? clock = null, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        ContractRules.Identifier(selection.ModelAlias);
        this.client = client;
        this.target = target;
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
            Version = ContractVersion.Current, ProviderId = ProviderId, AdapterVersion = "1.0.0", ModelId = selection.ModelAlias,
            Role = ProviderRole.Tts, Provenance = EvidenceProvenance.Live, SttPartials = CapabilitySupport.Unsupported,
            LlmTextDeltas = CapabilitySupport.Unsupported, TtsAudioTransport = CapabilitySupport.Supported,
            TtsIncrementalSynthesis = CapabilitySupport.Unsupported, Cancellation = CancellationCapability.RequestAbort,
            MaxInputBytes = BoundedSpeechInput.HardMaxUtf8Bytes
        };
    }

    public SpeechSynthesisResult? Result { get; private set; }
    public ProviderCapabilities Capabilities { get; }
    public static PcmFormat Format => OpenAiSpeechSynthesisCatalog.PcmFormat;

    /// <summary>The credential binding a caller authorizes for this host: its gateway origin, the TTS role and F5 model.</summary>
    public static ProviderCredentialBinding Binding(HostSpeechTarget target) =>
        new(new Uri(target.Origin), ProviderRole.Tts, target.ModelId);

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
            var chunks = client.StreamAsync(target, input, context.Ids, context.Epoch, deadline, linked.Token)
                .GetAsyncEnumerator(linked.Token);
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
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return (false, SpeechSynthesisOutcome.Canceled, null); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (false, SpeechSynthesisOutcome.DeadlineExceeded, ProviderFailureCode.DeadlineExceeded);
        }
        // The host client crosses network, vault, store and JSON boundaries; surface a failed segment, never a crash.
        catch (Exception) { return (false, SpeechSynthesisOutcome.Failed, ProviderFailureCode.Server); }
    }

    private ProviderFailureCode? Authorize()
    {
        if (authorization is null || !authorization.AllowTextDisclosure || !authorization.AiGeneratedVoiceDisclosureConfirmed)
            return ProviderFailureCode.ConsentMissing;
        if (authorization.Binding is null || authorization.Binding.Origin != new Uri(target.Origin))
            return ProviderFailureCode.OriginRejected;
        if (authorization.Binding != Binding(target) || authorization.Selection != selection || !authorization.Matches(input) ||
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

    public override string ToString() => nameof(HostSpeechSynthesisStream);
}

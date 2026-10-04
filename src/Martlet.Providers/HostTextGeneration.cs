using System.Runtime.CompilerServices;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>The paired Martlet host whose own conversation model (its Ollama role) answers, reached through the host's
/// pinned gateway. <see cref="CredentialId"/> names the host pairing's device credential kept in the OS vault;
/// <see cref="RouteId"/> is the host route that answers: its conversation model's, or its Deep thinking role's
/// (<see cref="SelfHostSetup.DeepThinkingRouteId"/>).</summary>
public sealed record HostTextTarget(string Origin, string HostId, string SpkiFingerprint, string DeviceId, Guid CredentialId,
    string RouteId = SelfHostSetup.OllamaRouteId)
{
    public override string ToString() => nameof(HostTextTarget);
}

public sealed class HostTextException(ProviderFailureCode code) : Exception("The Martlet host could not generate the reply.")
{
    public ProviderFailureCode Code { get; } = code;
}

/// <summary>Streams reply text from a paired host's conversation model. Failures throw <see cref="HostTextException"/>.
/// <paramref name="generation"/> carries the optional sampling settings for the host's Ollama.</summary>
public interface IHostTextClient
{
    IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
        TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
        CancellationToken cancellationToken);
}

/// <summary>One LLM request answered by the user's own Martlet host instead of a cloud provider. It checks the same
/// one-use text disclosure authorization as the cloud adapters, bound to the host's gateway origin and model.</summary>
public sealed class HostTextGenerationStream : ITextGenerationStream
{
    public const string ProviderId = SelfHostSetup.GatewayOllamaAlias;
    public const double Temperature = GenerationSettings.DefaultHostTemperature;
    private readonly IHostTextClient client;
    private readonly HostTextTarget target;
    private readonly ProviderRequestContext context;
    private readonly TextModelSelection model;
    private readonly BoundedTextInput input;
    private readonly TextGenerationLimits limits;
    private readonly TextDisclosureAuthorization? authorization;
    private readonly GenerationSettings? generation;
    private readonly TimeProvider clock;
    private readonly CancellationToken callerToken;
    private readonly DateTimeOffset startedUtc;
    private int enumerated;

    public HostTextGenerationStream(IHostTextClient client, HostTextTarget target, ProviderRequestContext context,
        TextModelSelection model, BoundedTextInput input, TextGenerationLimits limits,
        TextDisclosureAuthorization? authorization, TimeProvider? clock = null, CancellationToken callerToken = default,
        GenerationSettings? generation = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        generation?.Validate();
        ContractRules.Identifier(model.ModelAlias);
        this.client = client;
        this.target = target;
        this.context = context;
        this.model = model;
        this.input = input;
        this.limits = limits;
        this.authorization = authorization;
        this.generation = generation;
        this.clock = clock ?? TimeProvider.System;
        this.callerToken = callerToken;
        startedUtc = this.clock.GetUtcNow();
        Capabilities = new()
        {
            Version = ContractVersion.Current, ProviderId = ProviderId, AdapterVersion = "1.0.0", ModelId = model.ModelAlias,
            Role = ProviderRole.Llm, Provenance = EvidenceProvenance.Live, SttPartials = CapabilitySupport.Unsupported,
            LlmTextDeltas = CapabilitySupport.Supported, TtsAudioTransport = CapabilitySupport.Unsupported,
            TtsIncrementalSynthesis = CapabilitySupport.Unsupported, Cancellation = CancellationCapability.RequestAbort,
            MaxInputBytes = BoundedTextInput.HardMaxUtf8Bytes
        };
    }

    public TextGenerationResult? Result { get; private set; }
    public ProviderCapabilities Capabilities { get; }

    /// <summary>The credential binding a caller authorizes for this host: its gateway origin, the LLM role and model.</summary>
    public static ProviderCredentialBinding Binding(HostTextTarget target, TextModelSelection model) =>
        new(new Uri(target.Origin), ProviderRole.Llm, model.UpstreamModelId);

    public IAsyncEnumerator<ProviderEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("A text generation stream can only be enumerated once.");
        return Enumerate(cancellationToken).GetAsyncEnumerator();
    }

    private async IAsyncEnumerable<ProviderEvent> Enumerate([EnumeratorCancellation] CancellationToken enumerationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(callerToken, enumerationToken);
        long sequence = 0;
        var started = false;
        // Started is announced with the first reply text (or the outcome), so a model that is still loading on the host
        // gets the whole first-event window instead of the shorter idle window.
        ProviderEvent Start()
        {
            started = true;
            return Event(ProviderEventKind.Started, sequence++);
        }
        try
        {
            if (Authorize() is { } blocked)
            {
                yield return Start();
                yield return Finish(TextGenerationOutcome.Failed, sequence, blocked);
                yield break;
            }
            var deadline = Earliest(context.Deadline, authorization!.ExpiresAt, startedUtc + limits.MaxRequestTime);
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                yield return Start();
                yield return Finish(TextGenerationOutcome.Failed, sequence, ProviderFailureCode.DeadlineExceeded);
                yield break;
            }
            using var timeout = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, timeout.Token);
            var deltas = client.StreamAsync(target, model, input, limits, context.Ids, context.Epoch, deadline, generation, linked.Token)
                .GetAsyncEnumerator(linked.Token);
            try
            {
                while (true)
                {
                    var (moved, outcome, failure) = await MoveAsync(deltas, stop.Token).ConfigureAwait(false);
                    if (outcome is { } ended)
                    {
                        if (!started) yield return Start();
                        yield return Finish(ended, sequence, failure);
                        yield break;
                    }
                    if (!moved) break;
                    if (string.IsNullOrEmpty(deltas.Current)) continue;
                    if (!started) yield return Start();
                    yield return Event(ProviderEventKind.TextDelta, sequence++, deltas.Current);
                }
            }
            finally
            {
                await deltas.DisposeAsync().ConfigureAwait(false);
            }
            if (!started) yield return Start();
            yield return Finish(TextGenerationOutcome.Completed, sequence, null);
        }
        finally
        {
            stop.Cancel();
            Result ??= new(context, EvidenceProvenance.Live, TextGenerationOutcome.Canceled);
        }
    }

    private static async Task<(bool Moved, TextGenerationOutcome? Outcome, ProviderFailureCode? Failure)> MoveAsync(
        IAsyncEnumerator<string> deltas, CancellationToken stop)
    {
        try { return (await deltas.MoveNextAsync().ConfigureAwait(false), null, null); }
        catch (HostTextException error) { return (false, TextGenerationOutcome.Failed, error.Code); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return (false, TextGenerationOutcome.Canceled, null); }
        catch (OperationCanceledException) { return (false, TextGenerationOutcome.Failed, ProviderFailureCode.DeadlineExceeded); }
        // The host client crosses network, vault and JSON boundaries; surface a failed turn, never a crash.
        catch (Exception) { return (false, TextGenerationOutcome.Failed, ProviderFailureCode.Server); }
    }

    private ProviderFailureCode? Authorize()
    {
        if (authorization is null || !authorization.AllowTextDisclosure ||
            input.Image is not null && !authorization.AllowImageDisclosure ||
            input.Audio is not null && !authorization.AllowAudioDisclosure) return ProviderFailureCode.ConsentMissing;
        // A host's Ollama takes no audio; the caller asks again with the transcript only.
        if (input.Audio is not null) return ProviderFailureCode.RequestRejected;
        if (authorization.Binding.Origin != new Uri(target.Origin)) return ProviderFailureCode.OriginRejected;
        if (authorization.Binding != Binding(target, model) || authorization.Model != model ||
            authorization.Ids != context.Ids || authorization.Epoch != context.Epoch || authorization.Limits != limits)
            return ProviderFailureCode.ConsentMismatch;
        if (authorization.ExpiresAt <= clock.GetUtcNow()) return ProviderFailureCode.ConsentExpired;
        if (input.Utf8Bytes > limits.MaxInputBytes || input.InputTokenReservation > limits.MaxInputTokens)
            return ProviderFailureCode.InputLimit;
        return authorization.TryConsume() ? null : ProviderFailureCode.ConsentConsumed;
    }

    private ProviderEvent Finish(TextGenerationOutcome outcome, long sequence, ProviderFailureCode? code)
    {
        var failure = outcome == TextGenerationOutcome.Failed
            ? new ProviderFailure(code ?? ProviderFailureCode.Server, stage: Stage.Generation) : null;
        Result = new(context, EvidenceProvenance.Live, outcome, failure: failure);
        return Event(outcome switch
        {
            TextGenerationOutcome.Completed => ProviderEventKind.Completed,
            TextGenerationOutcome.Canceled => ProviderEventKind.Canceled,
            _ => ProviderEventKind.Failed
        }, sequence, error: failure?.Error);
    }

    private ProviderEvent Event(ProviderEventKind kind, long sequence, string? text = null, MartletError? error = null) => new()
    {
        Version = ContractVersion.Current, Ids = context.Ids, Epoch = context.Epoch, Sequence = sequence,
        ProviderId = ProviderId, Provenance = EvidenceProvenance.Live, Kind = kind, Text = text, Error = error
    };

    private static DateTimeOffset Earliest(params DateTimeOffset[] values) => values.Min();

    public override string ToString() => nameof(HostTextGenerationStream);
}

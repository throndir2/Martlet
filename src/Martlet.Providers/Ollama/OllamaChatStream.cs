using System.Net;
using System.Net.Http.Headers;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Ollama;

public sealed class OllamaChatStream : IAsyncEnumerable<ProviderEvent>, IAsyncDisposable
{
    private readonly OllamaChatAdapter owner;
    private readonly OllamaChatAction action;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationToken caller, operation;
    private CancellationToken enumeration;
    private readonly CancellationToken ioToken;
    private readonly CancellationTokenSource stop = new();
    private readonly List<CancellationTokenRegistration> signals = new(6);
    private readonly object lifetime = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task callbacks = Task.CompletedTask;
    private Task? disposal;
    private int enumerated, moving, disposing, stopRequested;
    private bool started, initialized, ended, retiring, retired, firstText;
    private long sequence, lastProgress;
    private int emitted;
    private DateTimeOffset? permitExpiry;
    private CancellationTokenSource? progress;
    private ProviderRequestWindow? requestWindow, consentWindow;
    private IAsyncDisposable? dispatchLease;
    private HttpRequestMessage? request;
    private HttpResponseMessage? response;
    private Stream? body;
    private OllamaNdjsonReader? reader;
    private OllamaChatResponseParser? parser;
    private OllamaChatStep? pendingTerminal;
    private ProviderEvent current = null!;
    public ProviderCapabilities Capabilities { get; }
    public OllamaChatResult? Result { get; private set; }
    public Task OwnershipRelease => release.Task;

    internal OllamaChatStream(OllamaChatAdapter owner, OllamaChatAction action, TimeProvider clock,
        EvidenceProvenance provenance, CancellationToken caller, CancellationToken operation)
    {
        this.owner = owner;
        this.action = action;
        this.clock = clock;
        this.provenance = provenance;
        this.caller = caller;
        this.operation = operation;
        ioToken = stop.Token;
        lastProgress = action.StartedAt;
        Capabilities = OllamaChatAdapter.Capabilities(action.Selection, provenance);
    }

    public IAsyncEnumerator<ProviderEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposing) != 0, this);
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("An Ollama stream has one serialized consumer.");
        enumeration = cancellationToken;
        return new Enumerator(this);
    }

    public void RequestCancellation()
    {
        lock (lifetime)
        {
            if (retired) return;
            Interlocked.Exchange(ref stopRequested, 1);
            SignalCancellation();
        }
    }

    // All source/timer routes signal this owned token; none can execute its callbacks outside the observed task.
    private void SignalCancellation()
    {
        lock (lifetime)
        {
            if (!retiring && !retired && !stop.IsCancellationRequested)
                callbacks = stop.CancelAsync();
        }
    }

    private void Observe(CancellationToken token) =>
        signals.Add(token.UnsafeRegister(static state => ((OllamaChatStream)state!).SignalCancellation(), this));

    private bool IsStopped => Volatile.Read(ref stopRequested) != 0 || caller.IsCancellationRequested ||
        operation.IsCancellationRequested || enumeration.IsCancellationRequested || owner.IsStopping;

    private async ValueTask<bool> MoveNextAsync()
    {
        if (Interlocked.Exchange(ref moving, 1) != 0)
            throw new InvalidOperationException("Concurrent Ollama stream pulls are not supported.");
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ended || Volatile.Read(ref disposing) != 0) return false;
            if (!started)
            {
                started = true;
                current = Event(ProviderEventKind.Started);
                return true;
            }
            OllamaChatStep step;
            try
            {
                EnsureActive();
                step = pendingTerminal ?? await NextAsync().ConfigureAwait(false);
                EnsureActive();
            }
            catch (Exception error) when (error is OperationCanceledException or RequestCutoffException or
                OllamaChatProtocolException or OllamaChatAuthorizationUnavailableException or HttpRequestException or
                IOException or ContractException || error is ObjectDisposedException && IsStopped)
            {
                step = Cutoff() ?? OllamaChatFailures.Fail(error switch
                {
                    RequestCutoffException cutoff => cutoff.Code,
                    OllamaChatProtocolException protocol => protocol.Code,
                    OllamaChatAuthorizationUnavailableException => ProviderFailureCode.ConsentMissing,
                    ContractException { Code: ErrorCode.PayloadTooLarge } => ProviderFailureCode.ResponseTooLarge,
                    ContractException => ProviderFailureCode.ResponseSchema,
                    HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } or
                        HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => ProviderFailureCode.ResponseTruncated,
                    _ => ProviderFailureCode.Network
                });
            }
            if (step.Text is { } text)
            {
                if (step.Outcome is not null) pendingTerminal = step with { Text = null };
                emitted += text.Length;
                firstText = true;
                lastProgress = clock.GetTimestamp();
                ArmProgress();
                current = Event(ProviderEventKind.TextDelta, text);
                return true;
            }
            ended = true;
            await RetireAsync().ConfigureAwait(false);
            step = Cutoff() ?? step;
            Result = new(action, provenance, step, emitted);
            current = Event(step.Outcome switch
            {
                TextGenerationOutcome.Completed => ProviderEventKind.Completed,
                TextGenerationOutcome.Canceled => ProviderEventKind.Canceled,
                _ => ProviderEventKind.Failed
            }, error: Result.Error);
            return Volatile.Read(ref disposing) == 0;
        }
        catch
        {
            ended = true;
            Result ??= new(action, provenance, OllamaChatFailures.Fail(ProviderFailureCode.Network), emitted);
            await RetireAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            io.Release();
            Volatile.Write(ref moving, 0);
        }
    }

    private async Task<OllamaChatStep> NextAsync()
    {
        if (!initialized)
        {
            initialized = true;
            var failure = await InitializeAsync().ConfigureAwait(false);
            if (failure is not null) return failure;
        }
        while (true)
        {
            var record = await reader!.ReadAsync(ioToken).ConfigureAwait(false);
            EnsureActive();
            if (record is null) throw new OllamaChatProtocolException(ProviderFailureCode.ResponseTruncated);
            var step = parser!.Accept(record.Value);
            if (step.Outcome is not null)
            {
                if (step.Usage is not null)
                    await reader.VerifyEndAsync(ioToken).ConfigureAwait(false);
                EnsureActive();
                return step;
            }
            if (step.Text is not null) return step;
        }
    }

    private async Task<OllamaChatStep?> InitializeAsync()
    {
        EnsureTimesAndSources();
        if (action.Input.Utf8Bytes > action.Limits.MaxInputBytes ||
            action.Input.InputTokenReservation > action.Limits.MaxInputTokens)
            return OllamaChatFailures.Fail(ProviderFailureCode.InputLimit);
        Observe(caller);
        Observe(operation);
        Observe(enumeration);
        requestWindow = new(clock, action.StartedAt, action.StartedUtc, action.Context.Deadline,
            action.Limits.MaxRequestTime, action.EffectiveDeadline, CancellationToken.None);
        Observe(requestWindow.Token);
        progress = new(Timeout.InfiniteTimeSpan, clock);
        Observe(progress.Token);
        ArmProgress();
        EnsureActive();
        var permission = await owner.Source.AuthorizeAsync(action, ioToken).ConfigureAwait(false);
        // Burn before binding checks, including late/canceled returns. Reuse transfers no second lease.
        bool taken = permission is not null && permission.TryConsume(out dispatchLease);
        EnsureActive();
        if (permission is null) return OllamaChatFailures.Fail(ProviderFailureCode.ConsentMissing);
        if (!taken) return OllamaChatFailures.Fail(ProviderFailureCode.ConsentConsumed);
        if (!ReferenceEquals(permission.Action, action) || permission.ExpiresAt > action.EffectiveDeadline)
            return OllamaChatFailures.Fail(ProviderFailureCode.ConsentMismatch);
        permitExpiry = permission.ExpiresAt;
        EnsureActive();
        consentWindow = new(clock, action.StartedAt, action.StartedUtc, action.Context.Deadline,
            action.Limits.MaxRequestTime, permission.ExpiresAt, CancellationToken.None);
        Observe(consentWindow.Token);
        EnsureActive();
        request = new(HttpMethod.Post, action.Origin.Endpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
        request.Content = OllamaChatRequestEncoder.Encode(action, EnsureActive);
        EnsureActive();
        response = await owner.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ioToken).ConfigureAwait(false);
        EnsureActive();
        if (response.StatusCode != HttpStatusCode.OK)
            return OllamaChatFailures.Fail(OllamaChatFailures.Status(response.StatusCode), OpenAiTransport.RetryAdvice(response, clock));
        if (!OllamaChatTransport.IsNdjson(response.Content))
            return OllamaChatFailures.Fail(ProviderFailureCode.ResponseSchema);
        if (response.Content.Headers.ContentLength > action.Limits.MaxStreamBytes)
            return OllamaChatFailures.Fail(ProviderFailureCode.ResponseTooLarge);
        body = await response.Content.ReadAsStreamAsync(ioToken).ConfigureAwait(false);
        EnsureActive();
        reader = new(body, action.Limits, EnsureActive, response.Content.Headers.ContentLength);
        parser = new(action);
        return null;
    }

    private void EnsureTimesAndSources()
    {
        if (IsStopped) throw new OperationCanceledException();
        if (owner.IsQuarantined) throw new OllamaChatCleanupException();
        var elapsed = clock.GetElapsedTime(action.StartedAt);
        var utc = clock.GetUtcNow();
        if (elapsed < TimeSpan.Zero || utc >= action.EffectiveDeadline ||
            elapsed >= action.EffectiveDeadline - action.StartedUtc)
            throw new RequestCutoffException(ProviderFailureCode.DeadlineExceeded);
        if (permitExpiry is { } expiry && (utc >= expiry || elapsed >= expiry - action.StartedUtc))
            throw new RequestCutoffException(ProviderFailureCode.ConsentExpired);
        if (!firstText && elapsed >= action.Limits.FirstDeltaTimeout)
            throw new RequestCutoffException(ProviderFailureCode.FirstDeltaTimeout);
        if (clock.GetElapsedTime(lastProgress) >= action.Limits.IdleTimeout)
            throw new RequestCutoffException(ProviderFailureCode.IdleTimeout);
    }

    private void EnsureActive()
    {
        EnsureTimesAndSources();
        requestWindow?.EnsureActive();
        consentWindow?.EnsureActive();
        ioToken.ThrowIfCancellationRequested();
    }

    private OllamaChatStep? Cutoff()
    {
        try { EnsureTimesAndSources(); return null; }
        catch (OperationCanceledException) { return new(Outcome: TextGenerationOutcome.Canceled); }
        catch (RequestCutoffException cutoff) { return OllamaChatFailures.Fail(cutoff.Code); }
    }

    private void ArmProgress()
    {
        var idle = action.Limits.IdleTimeout - clock.GetElapsedTime(lastProgress);
        var first = action.Limits.FirstDeltaTimeout - clock.GetElapsedTime(action.StartedAt);
        var due = !firstText && first < idle ? first : idle;
        progress?.CancelAfter(due > TimeSpan.Zero ? due : TimeSpan.Zero);
    }

    private ProviderEvent Event(ProviderEventKind kind, string? text = null, MartletError? error = null) => new()
    {
        Version = ContractVersion.Current, Ids = action.Context.Ids, Epoch = action.Context.Epoch,
        Sequence = sequence++, ProviderId = OllamaChatAdapter.ProviderId, Provenance = provenance,
        Kind = kind, Text = text, Error = error
    };

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (lifetime)
        {
            if (disposal is not null) return new(disposal);
            Volatile.Write(ref disposing, 1);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        RequestCancellation();
        _ = DisposeOwnedAsync(completion);
        return new(disposal);
    }

    private async Task DisposeOwnedAsync(TaskCompletionSource completion)
    {
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            ended = true;
            await RetireAsync().ConfigureAwait(false);
            Result ??= new(action, provenance, new(Outcome: TextGenerationOutcome.Canceled), emitted);
            completion.SetResult();
        }
        catch (Exception) { completion.SetException(new OllamaChatCleanupException()); }
        finally { io.Release(); }
    }

    private async Task RetireAsync()
    {
        if (retired)
        {
            await release.Task.ConfigureAwait(false);
            return;
        }
        bool failed = false;
        Task cancellation;
        lock (lifetime)
        {
            retiring = true;
            if (!stop.IsCancellationRequested) callbacks = stop.CancelAsync();
            cancellation = callbacks;
        }
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception) { failed = true; }
        foreach (var signal in signals)
        {
            try { await signal.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { failed = true; }
        }
        signals.Clear();
        // Cleanup boundaries surface a quarantine, never a success-shaped release or raw dependency exception.
        foreach (var resource in new IDisposable?[] { consentWindow, progress, requestWindow })
        {
            try { resource?.Dispose(); }
            catch (Exception) { failed = true; }
        }
        requestWindow = consentWindow = null;
        try { if (body is not null) await body.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { failed = true; }
        foreach (var resource in new IDisposable?[] { response, request, stop })
        {
            try { resource?.Dispose(); }
            catch (Exception) { failed = true; }
        }
        if (!failed)
        {
            try
            {
                if (dispatchLease is not null) await dispatchLease.DisposeAsync().ConfigureAwait(false);
                dispatchLease = null;
            }
            catch (Exception) { failed = true; }
        }
        lock (lifetime) retired = true;
        owner.Retire(this, failed);
        if (failed)
        {
            Result = new(action, provenance, new(Outcome: TextGenerationOutcome.Failed), emitted, OllamaChatFailures.CleanupError());
            release.SetException(new OllamaChatCleanupException());
            throw new OllamaChatCleanupException();
        }
        release.SetResult();
    }

    public override string ToString() => nameof(OllamaChatStream);

    private sealed class Enumerator(OllamaChatStream owner) : IAsyncEnumerator<ProviderEvent>
    {
        public ProviderEvent Current => owner.current;
        public ValueTask<bool> MoveNextAsync() => owner.MoveNextAsync();
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
}

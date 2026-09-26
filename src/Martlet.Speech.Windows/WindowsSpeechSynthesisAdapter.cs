using System.Runtime.InteropServices;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Speech.Windows;

public sealed class WindowsSpeechSynthesisAdapter : IAsyncDisposable
{
    public const string ProviderId = "windows-speech";
    public const string ModelId = "windows-installed";
    public static PcmFormat Format { get; } = new()
    {
        SampleRate = 24_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
    };
    private readonly object sync = new();
    private readonly Func<IWindowsSpeechEngine> createEngine;
    private readonly TimeProvider clock;
    private readonly EvidenceProvenance provenance;
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private Task? active;
    private Task? disposal;
    private bool disposed, quarantined;

    // No native construction, discovery, credentials, network or audio device access here.
    public WindowsSpeechSynthesisAdapter(TimeProvider? timeProvider = null)
        : this(() => new SystemSpeechEngine(), timeProvider ?? TimeProvider.System, EvidenceProvenance.Live) { }

    internal WindowsSpeechSynthesisAdapter(Func<IWindowsSpeechEngine> createEngine,
        TimeProvider clock, EvidenceProvenance provenance = EvidenceProvenance.Fixture)
    {
        this.createEngine = createEngine;
        this.clock = clock;
        this.provenance = provenance;
        shutdownToken = shutdown.Token;
    }

    public Task<IReadOnlyList<WindowsSpeechVoice>> GetInstalledVoicesAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            EnsureAvailable();
            var task = Task.Run<IReadOnlyList<WindowsSpeechVoice>>(() =>
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
                stop.Token.ThrowIfCancellationRequested();
                IWindowsSpeechEngine? engine = null;
                try
                {
                    engine = createEngine();
                    var voices = engine.GetVoices();
                    stop.Token.ThrowIfCancellationRequested();
                    return voices;
                }
                catch (Exception error) when (IsNativeFailure(error))
                {
                    throw new WindowsSpeechException(WindowsSpeechFailure.EngineUnavailable);
                }
                finally { ReleaseEngine(engine); }
            });
            active = task;
            return task;
        }
    }

    public Task<WindowsSpeechResult> SynthesizeAsync(ProviderRequestContext context, string voiceId,
        BoundedSpeechInput input, SpeechSynthesisLimits limits, WindowsSpeechAuthorization? authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        context.Validate();
        limits.Validate();
        ContractRules.Require(!string.IsNullOrWhiteSpace(voiceId) && voiceId.Length <= 4096,
            "Select an exact installed Windows voice ID.");
        lock (sync)
        {
            EnsureAvailable();
            var started = clock.GetTimestamp();
            var utc = clock.GetUtcNow();
            var task = Task.Run(() => SynthesizeOwned(context, voiceId, input, limits, authorization,
                started, utc, cancellationToken));
            active = task;
            return task;
        }
    }

    private WindowsSpeechResult SynthesizeOwned(ProviderRequestContext context, string voiceId,
        BoundedSpeechInput input, SpeechSynthesisLimits limits, WindowsSpeechAuthorization? authorization,
        long started, DateTimeOffset utc, CancellationToken caller)
    {
        IWindowsSpeechEngine? engine = null;
        BoundedSpeechBuffer? output = null;
        bool cleanupFailed = false;
        WindowsSpeechResult result;
        try
        {
            EnsureNotStopped();
            if (authorization is null || !authorization.AllowGeneratedSpeech)
                throw new WindowsSpeechException(WindowsSpeechFailure.ConsentMissing);
            if (!authorization.Matches(context, voiceId, input, limits))
                throw new WindowsSpeechException(WindowsSpeechFailure.ConsentMismatch);
            if (clock.GetUtcNow() >= authorization.ExpiresAt)
                throw new WindowsSpeechException(WindowsSpeechFailure.ConsentExpired);
            if (input.Utf8Bytes > limits.MaxInputBytes)
                throw new WindowsSpeechException(WindowsSpeechFailure.InputLimit);
            EnsureLifetime();
            if (!authorization.TryConsume())
                throw new WindowsSpeechException(WindowsSpeechFailure.ConsentConsumed);
            output = new BoundedSpeechBuffer(checked((int)(limits.MaxSamples * 2)), clock,
                limits, started, EnsureLifetime);
            output.EnsureActive();
            engine = createEngine();
            output.EnsureActive();
            engine.Synthesize(voiceId, input.Text, output, output.EnsureActive);
            output.EnsureActive();
            var bytes = output.Complete();
            result = new(SpeechSynthesisOutcome.Completed, WindowsSpeechFailure.None, provenance,
                new(bytes, context, EnsureLifetime));
        }
        catch (OperationCanceledException)
        {
            result = new(SpeechSynthesisOutcome.Canceled, WindowsSpeechFailure.None, provenance, null);
        }
        catch (WindowsSpeechException error)
        {
            result = new(error.Failure is WindowsSpeechFailure.DeadlineExceeded or
                WindowsSpeechFailure.FirstAudioTimeout or WindowsSpeechFailure.IdleTimeout
                ? SpeechSynthesisOutcome.DeadlineExceeded : SpeechSynthesisOutcome.Failed, error.Failure, provenance, null);
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            result = new(SpeechSynthesisOutcome.Failed,
                output?.Failure is { } failure && failure != WindowsSpeechFailure.None
                    ? failure : WindowsSpeechFailure.EngineFailed, provenance, null);
        }
        finally
        {
            try { ReleaseEngine(engine); }
            catch (WindowsSpeechException) { cleanupFailed = true; }
            finally { output?.Dispose(); }
        }
        if (cleanupFailed)
        {
            return new(SpeechSynthesisOutcome.Failed, WindowsSpeechFailure.CleanupFailed, provenance, null);
        }
        // Disposal can block. Recheck the original flags/deadlines before exposing buffered output.
        if (result.Audio is not null)
        {
            try { EnsureLifetime(); }
            catch (OperationCanceledException)
            {
                return new(SpeechSynthesisOutcome.Canceled, WindowsSpeechFailure.None, provenance, null);
            }
            catch (WindowsSpeechException error)
            {
                return new(SpeechSynthesisOutcome.DeadlineExceeded, error.Failure, provenance, null);
            }
        }
        return result;

        void EnsureNotStopped()
        {
            caller.ThrowIfCancellationRequested();
            shutdownToken.ThrowIfCancellationRequested();
        }
        void EnsureLifetime()
        {
            EnsureNotStopped();
            var now = clock.GetUtcNow();
            var elapsed = clock.GetElapsedTime(started);
            if (elapsed >= limits.MaxRequestTime || elapsed >= context.Deadline - utc ||
                now >= context.Deadline || (authorization is not null &&
                (now >= authorization.ExpiresAt || elapsed >= authorization.ExpiresAt - utc)))
                throw new WindowsSpeechException(WindowsSpeechFailure.DeadlineExceeded);
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (quarantined) throw new WindowsSpeechException(WindowsSpeechFailure.Quarantined);
        if (active is { IsCompleted: false }) throw new WindowsSpeechException(WindowsSpeechFailure.Busy);
    }

    private void ReleaseEngine(IWindowsSpeechEngine? engine)
    {
        try { engine?.Dispose(); }
        catch (Exception error) when (IsNativeFailure(error))
        {
            lock (sync) quarantined = true;
            throw new WindowsSpeechException(WindowsSpeechFailure.CleanupFailed);
        }
        catch
        {
            lock (sync) quarantined = true;
            throw;
        }
    }

    private static bool IsNativeFailure(Exception error) =>
        error is InvalidOperationException or COMException or IOException or ArgumentException or
            PlatformNotSupportedException or System.Security.SecurityException;

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            shutdown.Cancel();
            return new(disposal = DisposeOwnedAsync(active));
        }
    }

    private async Task DisposeOwnedAsync(Task? owned)
    {
        try
        {
            if (owned is not null) await owned.ConfigureAwait(false);
        }
        finally { shutdown.Dispose(); }
    }
}

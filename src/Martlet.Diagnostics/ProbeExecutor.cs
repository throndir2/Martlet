using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public sealed class ProbeExecutor
{
    private readonly SemaphoreSlim slots;
    private readonly object gate = new();
    private TaskCompletionSource idle = CompletedSignal();
    private int activeOperations;
    private int running;
    private readonly int concurrency;
    private readonly TimeSpan runTimeout;
    public ProbeRegistry Registry { get; }
    public TimeProvider Clock { get; }
    public int ActiveOperationCount { get { lock (gate) return activeOperations; } }

    public ProbeExecutor(ProbeRegistry registry, TimeProvider? clock = null, int maximumConcurrency = 2, TimeSpan? runTimeout = null)
    {
        if (maximumConcurrency is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        this.runTimeout = runTimeout ?? TimeSpan.FromSeconds(5);
        if (this.runTimeout <= TimeSpan.Zero || this.runTimeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(runTimeout));
        Registry = registry;
        Clock = clock ?? TimeProvider.System;
        concurrency = maximumConcurrency;
        slots = new(maximumConcurrency);
    }

    public DoctorReport Catalog(IEnumerable<string>? ids = null) => Report(
        Registry.Select(ids).Select(definition => DiagnosticCatalog.Result(definition, "probe.not_run")).ToArray());

    public DoctorReport Pending(IEnumerable<string>? ids = null) => Report(
        Registry.Select(ids).Select(definition => definition.Execute is null
            ? DiagnosticCatalog.Result(definition, definition.UnavailableFindingId)
            : definition.Effects.Count != 1 || definition.Effects[0] != ProbeEffect.LocalReadOnly
                ? DiagnosticCatalog.Result(definition, "probe.effects_blocked")
                : DiagnosticCatalog.Result(definition, "probe.running", ProbeExecution.Running)).ToArray());

    public async Task<DoctorReport> RunAsync(IEnumerable<string>? ids = null, CancellationToken cancellationToken = default)
    {
        var definitions = Registry.Select(ids);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("A diagnostic run is already active.");
        var start = Clock.GetUtcNow();
        var runTimestamp = Clock.GetTimestamp();
        try
        {
            using var deadline = new CancellationTokenSource(runTimeout, Clock);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var results = new ProbeResult[definitions.Count];
            var settings = new SettingsLoadResult?[definitions.Count];
            var next = -1;
            // Outstanding callbacks from a previous run cannot be overlapped by a rerun.
            var busy = ActiveOperationCount != 0;
            async Task Worker()
            {
                while (Interlocked.Increment(ref next) is var index && index < definitions.Count)
                {
                    var definition = definitions[index];
                    if (definition.Execute is null)
                        results[index] = DiagnosticCatalog.Result(definition, definition.UnavailableFindingId);
                    else if (definition.Effects.Count != 1 || definition.Effects[0] != ProbeEffect.LocalReadOnly)
                        results[index] = DiagnosticCatalog.Result(definition, "probe.effects_blocked");
                    else if (stop.IsCancellationRequested || Clock.GetElapsedTime(runTimestamp) >= runTimeout)
                        results[index] = Interrupted(definition, cancellationToken.IsCancellationRequested);
                    else if (busy || !slots.Wait(0))
                        results[index] = DiagnosticCatalog.Result(definition, "probe.busy");
                    else
                        (results[index], settings[index]) = await ExecuteAsync(definition, runTimestamp, stop.Token, cancellationToken).ConfigureAwait(false);
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, definitions.Count)).Select(_ => Worker())).ConfigureAwait(false);
            var loaded = settings.FirstOrDefault(item => item is not null);
            var report = Report(results) with
            {
                StartedAt = start,
                CompletedAt = Later(start),
                SettingsState = loaded?.State,
                ProfileId = loaded?.Settings?.Profile.Id,
                ProfileKind = loaded?.Settings?.Profile.Kind,
                Setup = SetupStatus.From(loaded?.Settings)
            };
            report.Validate();
            return RefreshAge(report);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task<(ProbeResult, SettingsLoadResult?)> ExecuteAsync(ProbeDefinition definition, long runTimestamp,
        CancellationToken runStop, CancellationToken caller)
    {
        var start = Clock.GetUtcNow();
        var timestamp = Clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(definition.Timeout, Clock);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(runStop, deadline.Token);
        var stop = new CancellationTokenSource();
        lock (gate)
        {
            if (activeOperations++ == 0)
                idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        // Run the synchronous prefix off the UI thread. The slot belongs to the task until it actually ends.
        var operation = Task.Run(() => definition.Execute!(stop.Token), stop.Token);
        Task cancellation = Task.CompletedTask;
        try
        {
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = wait.Token.Register(() => interrupted.TrySetResult());
            await Task.WhenAny(operation, interrupted.Task).ConfigureAwait(false);
            var elapsed = Clock.GetElapsedTime(timestamp);
            if (caller.IsCancellationRequested || runStop.IsCancellationRequested ||
                deadline.IsCancellationRequested || elapsed >= definition.Timeout || Clock.GetElapsedTime(runTimestamp) >= runTimeout)
            {
                // The monotonic deadline wins even when the callback and timer complete in the same scheduler turn.
                cancellation = stop.CancelAsync();
                return (Interrupted(definition, caller.IsCancellationRequested) with
                {
                    StartedAt = start,
                    DurationMilliseconds = elapsed.TotalMilliseconds,
                    OperationStillRunning = !operation.IsCompleted || !cancellation.IsCompleted
                }, null);
            }
            var end = Later(start);
            ProbeResult Timed(string finding, ProbeExecution execution) => DiagnosticCatalog.Result(definition, finding, execution) with
            {
                StartedAt = start,
                CompletedAt = end,
                DurationMilliseconds = elapsed.TotalMilliseconds
            };
            if (operation.IsCanceled)
                return (Timed("probe.unknown", ProbeExecution.Completed), null);
            if (operation.IsFaulted)
                return (Timed("probe.fault", ProbeExecution.Faulted), null);
            var observation = operation.Result;
            try
            {
                if (observation is null || string.IsNullOrEmpty(observation.FindingId) ||
                    observation.FindingId.StartsWith("fixture.", StringComparison.Ordinal) && observation.Provenance != EvidenceProvenance.Fixture ||
                    observation.Settings is not null && definition.Id != "settings.load" ||
                    observation.Settings is null && observation.FindingId.StartsWith("settings.", StringComparison.Ordinal))
                    return (Timed("probe.invalid_evidence", ProbeExecution.Faulted), null);
                var result = Timed(observation.FindingId, ProbeExecution.Completed) with
                {
                    Provenance = observation.Provenance,
                    ObservedAt = observation.Provenance is EvidenceProvenance.Live or EvidenceProvenance.Fixture
                        ? observation.ObservedAt ?? end : observation.ObservedAt,
                    MaximumAgeMilliseconds = definition.MaximumAge.TotalMilliseconds
                };
                result = Age(result, Clock.GetUtcNow());
                result.Validate();
                if (observation.Settings is { } settings)
                {
                    ContractRules.Require(observation.Provenance is EvidenceProvenance.Live or EvidenceProvenance.Fixture &&
                        observation.FindingId == DiagnosticCatalog.SettingsFinding(settings), "Settings finding and payload disagree.");
                    settings.Settings?.Validate();
                    (Report([result]) with
                    {
                        SettingsState = settings.State,
                        ProfileId = settings.Settings?.Profile.Id,
                        ProfileKind = settings.Settings?.Profile.Kind
                    }).Validate();
                }
                return (result, observation.Settings);
            }
            catch (ContractException) { return (Timed("probe.invalid_evidence", ProbeExecution.Faulted), null); }
            catch (ArgumentException) { return (Timed("probe.invalid_evidence", ProbeExecution.Faulted), null); }
        }
        finally
        {
            // Cancellation handlers are also arbitrary callback work: never await them on the reporting path.
            _ = Task.WhenAll(operation, cancellation).ContinueWith(task =>
            {
                _ = task.Exception;
                stop.Dispose();
                slots.Release();
                lock (gate)
                {
                    if (--activeOperations == 0)
                        idle.TrySetResult();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static ProbeResult Interrupted(ProbeDefinition definition, bool canceled) =>
        DiagnosticCatalog.Result(definition, canceled ? "probe.canceled" : "probe.timeout",
            canceled ? ProbeExecution.Canceled : ProbeExecution.TimedOut);

    public DoctorReport RefreshAge(DoctorReport report)
    {
        var now = Clock.GetUtcNow();
        return report with { CreatedAt = now, Probes = Array.AsReadOnly(report.Probes.Select(probe => Age(probe, now)).ToArray()) };
    }

    private static ProbeResult Age(ProbeResult result, DateTimeOffset now)
    {
        if (result.ObservedAt is not { } observed || result.MaximumAgeMilliseconds is not { } maximumAge)
            return result;
        var elapsed = (now - observed).TotalMilliseconds;
        var age = elapsed < 0 ? (double?)null : Math.Max(elapsed, result.AgeMilliseconds ?? 0);
        return result with
        {
            AgeMilliseconds = age,
            Freshness = result.Freshness == EvidenceFreshness.Stale || age >= maximumAge
                ? EvidenceFreshness.Stale : age is null ? EvidenceFreshness.Unknown : EvidenceFreshness.Current
        };
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Task pending;
        lock (gate) pending = idle.Task;
        using var timer = new CancellationTokenSource();
        await Task.WhenAny(pending, Task.Delay(timeout, Clock, timer.Token)).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        return ActiveOperationCount == 0;
    }

    private DoctorReport Report(ProbeResult[] probes) => new()
    {
        Version = ContractVersion.Current,
        ApplicationVersion = FoundationStatusService.ApplicationVersion,
        CreatedAt = Clock.GetUtcNow(),
        Probes = Array.AsReadOnly(probes)
    };

    private DateTimeOffset Later(DateTimeOffset start) => Clock.GetUtcNow() is var now && now >= start ? now : start;
    private static TaskCompletionSource CompletedSignal()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

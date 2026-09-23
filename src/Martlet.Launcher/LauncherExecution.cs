using Martlet.Readiness;
using Martlet.Updates;

namespace Martlet.Launcher;

internal sealed class SimulatedLauncherLossException : Exception;

internal sealed class LauncherExecution
{
    private readonly LauncherEvidenceStore evidence;
    private readonly TimeSpan readinessTimeout;

    internal Action<LauncherEvidencePoint, Guid, int?>? Io
    {
        get => evidence.Io;
        set => evidence.Io = value;
    }

    internal LauncherExecution(
        string existingPrivateEvidenceRoot,
        TimeSpan? readinessTimeout)
    {
        evidence = new(existingPrivateEvidenceRoot);
        this.readinessTimeout = LauncherSupport.ValidateTimeout(readinessTimeout);
    }

    internal LauncherEvidenceStore.LauncherLease Acquire(LaunchTarget target)
    {
        LauncherSupport.Validate(target);
        evidence.RequireSeparateFrom(target);
        return evidence.Acquire(target.ProfileId, target.LeaseDirectory);
    }

    internal bool Probe(LaunchTarget target, LauncherEvidenceStore.LauncherLease lease,
        CancellationToken token, Action requireAuthorization)
    {
        LauncherSupport.Validate(target);
        evidence.RequireSeparateFrom(target);
        token.ThrowIfCancellationRequested();
        var deadline = target.ActivationDeadlineUtc ??
            throw new LauncherException(LauncherFailure.InvalidActivation);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return false;
        if (remaining > readinessTimeout)
            remaining = readinessTimeout;
        return ProbeOwnedAsync(target, lease, remaining, deadline, token, requireAuthorization)
            .GetAwaiter().GetResult();
    }

    internal OwnedDesktopLaunch StartDesktop(
        LaunchTarget target,
        CancellationToken token, Action verify, Action requireConsent)
    {
        LauncherSupport.Validate(target);
        evidence.RequireSeparateFrom(target);
        token.ThrowIfCancellationRequested();
        var lease = evidence.Acquire(
            target.ProfileId, target.LeaseDirectory);
        var deadline = DateTimeOffset.UtcNow + readinessTimeout;
        try
        {
            return StartDesktopOwnedAsync(
                    target, lease, readinessTimeout, deadline, token, verify, requireConsent)
                .GetAwaiter().GetResult();
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async Task<bool> ProbeOwnedAsync(
        LaunchTarget target,
        LauncherEvidenceStore.LauncherLease lease,
        TimeSpan timeout,
        DateTimeOffset deadline,
        CancellationToken token, Action requireAuthorization)
    {
        await using var server = CreateServer(
            target, DesktopReadinessPurpose.ActivationProbe, timeout, deadline);
        var attempt = evidence.Begin(
            lease, LauncherAttemptKind.ActivationReadiness, target, server.DeadlineUtc);
        using var executablePin = PinExecutable(target, attempt.Id);
        OwnedWindowsProcess? process = null;
        try
        {
            process = OwnedWindowsProcess.Start(target.ExecutablePath, target.PayloadDirectory,
                MinimalProcessEnvironment.Create(attempt.RuntimeDirectory, server.Environment),
                requireAuthorization: () =>
                {
                    token.ThrowIfCancellationRequested();
                    requireAuthorization();
                    if (server.IsExpired)
                        throw new LauncherException(LauncherFailure.ReadinessTimedOut, attempt.Id);
                });
            attempt.ProcessStarted(process.ProcessId);
            var readiness = await server.WaitAsync(process.ProcessId, process.ExitToken,
                process.ReadExitCode, token).ConfigureAwait(false);
            attempt.RecordReadiness(readiness, process.ProcessId);
            var cleanup = await process.TerminateAndWaitAsync(
                LauncherSupport.ProcessCleanupTimeout).ConfigureAwait(false);
            var failure = readiness.IsInitialized && cleanup
                ? (LauncherFailure?)null : !cleanup ? LauncherFailure.CleanupFailed : Map(readiness);
            attempt.RecordTerminal(failure, process.ProcessId, process.ReadExitCode(), cleanup);
            if (token.IsCancellationRequested || readiness.Failure == PrivateReadinessFailure.Cancelled)
                throw new OperationCanceledException(token);
            return failure is null;
        }
        catch (Exception error) when (IsRecordedFailure(error))
        {
            throw await RecordFailureAsync(attempt, process, error, token).ConfigureAwait(false);
        }
        finally { process?.Dispose(); }
    }

    private async Task<OwnedDesktopLaunch> StartDesktopOwnedAsync(
        LaunchTarget target,
        LauncherEvidenceStore.LauncherLease lease,
        TimeSpan timeout,
        DateTimeOffset deadline,
        CancellationToken token, Action verify, Action requireConsent)
    {
        await using var server = CreateServer(
            target, DesktopReadinessPurpose.DesktopLaunch, timeout, deadline);
        var attempt = evidence.Begin(
            lease, LauncherAttemptKind.DesktopLaunch, target, server.DeadlineUtc);
        FileStream? executablePin = null;
        OwnedWindowsProcess? process = null;
        var transferred = false;
        try
        {
            executablePin = PinExecutable(target, attempt.Id);
            verify();
            process = OwnedWindowsProcess.Start(
                target.ExecutablePath,
                target.PayloadDirectory,
                MinimalProcessEnvironment.Create(attempt.RuntimeDirectory, server.Environment),
                requireAuthorization: () =>
                {
                    requireConsent();
                    if (server.IsExpired)
                        throw new LauncherException(LauncherFailure.ReadinessTimedOut, attempt.Id);
                });
            attempt.ProcessStarted(process.ProcessId);
            var readiness = await server.WaitAsync(
                process.ProcessId,
                process.ExitToken,
                process.ReadExitCode,
                token).ConfigureAwait(false);
            attempt.RecordReadiness(readiness, process.ProcessId);
            if (server.IsExpired)
                readiness = new PrivateReadinessResult(PrivateReadinessFailure.TimedOut);
            if (!readiness.IsInitialized)
            {
                var cleanup = await process.TerminateAndWaitAsync(
                    LauncherSupport.ProcessCleanupTimeout).ConfigureAwait(false);
                var readinessFailure = cleanup
                    ? Map(readiness)
                    : LauncherFailure.CleanupFailed;
                attempt.RecordTerminal(
                    readinessFailure,
                    process.ProcessId,
                    readiness.ExitCode ?? process.ReadExitCode(),
                    cleanup);
                if (readinessFailure == LauncherFailure.Cancelled ||
                    token.IsCancellationRequested)
                    throw new LauncherException(LauncherFailure.Cancelled, attempt.Id);
                throw new LauncherException(readinessFailure, attempt.Id);
            }
            verify();
            if (server.IsExpired)
                throw new LauncherException(LauncherFailure.ReadinessTimedOut, attempt.Id);
            transferred = true;
            return new OwnedDesktopLaunch(
                target, attempt, lease, executablePin, process);
        }
        catch (Exception error) when (IsRecordedFailure(error))
        {
            throw await RecordFailureAsync(attempt, process, error, token).ConfigureAwait(false);
        }
        finally
        {
            if (!transferred)
            {
                try { process?.Dispose(); }
                finally { executablePin?.Dispose(); }
            }
        }
    }

    private static bool IsRecordedFailure(Exception error) =>
        error is LauncherException or ActivationException or IOException or UnauthorizedAccessException or OperationCanceledException;

    private static async Task<LauncherException> RecordFailureAsync(
        LauncherEvidenceStore.LauncherAttempt attempt, OwnedWindowsProcess? process,
        Exception error, CancellationToken token)
    {
        var cleanup = process is null || await process.TerminateAndWaitAsync(
            LauncherSupport.ProcessCleanupTimeout).ConfigureAwait(false);
        var failure = !cleanup ? LauncherFailure.CleanupFailed : error switch
        {
            LauncherException launcher => launcher.Failure,
            OperationCanceledException when token.IsCancellationRequested => LauncherFailure.Cancelled,
            ActivationException activation when activation.Failure == ActivationFailure.Cancelled => LauncherFailure.Cancelled,
            ActivationException => LauncherFailure.InvalidActivation,
            IOException or UnauthorizedAccessException => LauncherFailure.EvidenceUnavailable,
            _ => LauncherFailure.Unavailable
        };
        if (!attempt.HasTerminal)
            attempt.RecordTerminal(failure, process?.ProcessId, process?.ReadExitCode(), cleanup);
        return new LauncherException(failure, attempt.Id);
    }

    internal async Task<DesktopLaunchResult> CompleteDesktopAsync(
        OwnedDesktopLaunch launch,
        CancellationToken token)
    {
        using (launch)
        {
            var process = launch.Process;
            var attempt = launch.Attempt;
            int exitCode;
            try
            {
                exitCode = await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                var cleanup = await process.TerminateAndWaitAsync(
                    LauncherSupport.ProcessCleanupTimeout).ConfigureAwait(false);
                var cancellationFailure = cleanup
                    ? LauncherFailure.Cancelled
                    : LauncherFailure.CleanupFailed;
                attempt.RecordTerminal(
                    cancellationFailure, process.ProcessId, process.ReadExitCode(), cleanup);
                throw new LauncherException(cancellationFailure, attempt.Id);
            }

            var treeClean = await process.TerminateAndWaitAsync(
                LauncherSupport.ProcessCleanupTimeout).ConfigureAwait(false);
            var terminal = !treeClean
                ? LauncherFailure.CleanupFailed
                : exitCode == 0
                    ? (LauncherFailure?)null
                    : LauncherFailure.ProcessCrashed;
            attempt.RecordTerminal(
                terminal, process.ProcessId, exitCode, treeClean);
            if (terminal is { } terminalFailure)
                throw new LauncherException(terminalFailure, attempt.Id);
            return new DesktopLaunchResult(
                attempt.Id,
                launch.Target.ProfileId,
                launch.Target.Version,
                process.ProcessId,
                exitCode);
        }
    }

    internal async Task AbortDesktopAsync(
        OwnedDesktopLaunch launch,
        LauncherFailure failure)
    {
        using (launch)
        {
            var cleanup = await launch.Process.TerminateAndWaitAsync(
                LauncherSupport.ProcessCleanupTimeout).ConfigureAwait(false);
            launch.Attempt.RecordTerminal(
                cleanup ? failure : LauncherFailure.CleanupFailed,
                launch.Process.ProcessId,
                launch.Process.ReadExitCode(),
                cleanup);
        }
    }

    internal sealed class OwnedDesktopLaunch(
        LaunchTarget target,
        LauncherEvidenceStore.LauncherAttempt attempt,
        LauncherEvidenceStore.LauncherLease lease,
        FileStream executablePin,
        OwnedWindowsProcess process) : IDisposable
    {
        private int disposed;
        internal LaunchTarget Target { get; } = target;
        internal LauncherEvidenceStore.LauncherAttempt Attempt { get; } = attempt;
        internal OwnedWindowsProcess Process { get; } = process;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try { Process.Dispose(); }
            finally
            {
                try { executablePin.Dispose(); }
                finally { lease.Dispose(); }
            }
        }
    }

    private static PrivateReadinessServer CreateServer(
        LaunchTarget target,
        DesktopReadinessPurpose purpose,
        TimeSpan timeout,
        DateTimeOffset deadline) =>
        new(new PrivateReadinessBinding(
                purpose,
                target.Version,
                target.ProfileId,
                target.SettingsRevision,
                target.PayloadSha256,
                target.ExecutableSha256,
                target.ExecutablePath),
            timeout,
            deadline);

    private static FileStream PinExecutable(
        LaunchTarget target,
        Guid attemptId)
    {
        try
        {
            var stream = new FileStream(
                target.ExecutablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.SequentialScan);
            if (stream.Length != target.ExecutableBytes)
            {
                stream.Dispose();
                throw new LauncherException(
                    LauncherFailure.TamperedPayload, attemptId);
            }
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != target.ExecutableSha256)
            {
                stream.Dispose();
                throw new LauncherException(LauncherFailure.TamperedPayload, attemptId);
            }
            return stream;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                LauncherFailure.TamperedPayload, attemptId);
        }
    }

    private static LauncherFailure Map(PrivateReadinessResult readiness) =>
        readiness.Failure switch
        {
            PrivateReadinessFailure.TimedOut =>
                LauncherFailure.ReadinessTimedOut,
            PrivateReadinessFailure.ProcessExited =>
                LauncherFailure.ProcessExitedBeforeReadiness,
            PrivateReadinessFailure.WrongProcessPath =>
                LauncherFailure.ProcessPathMismatch,
            PrivateReadinessFailure.Cancelled =>
                LauncherFailure.Cancelled,
            PrivateReadinessFailure.Unavailable =>
                LauncherFailure.Unavailable,
            _ => LauncherFailure.ReadinessRejected
        };
}

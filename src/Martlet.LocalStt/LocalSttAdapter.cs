namespace Martlet.LocalStt;

public sealed class LocalSttAdapter : IAsyncDisposable
{
    private readonly ILocalSttPackageVerifier packageVerifier;
    private readonly ILocalSttProcessRunner processRunner;
    private readonly ILocalSttEgressAuditor egressAuditor;
    private readonly ILocalSttWorkspaceFactory workspaceFactory;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim slot = new(1, 1);
    private int quarantined;
    private int disposed;

    public LocalSttAdapter(
        ILocalSttPackageVerifier packageVerifier,
        ILocalSttProcessRunner processRunner,
        ILocalSttEgressAuditor egressAuditor,
        TimeProvider? timeProvider = null)
        : this(
            packageVerifier,
            processRunner,
            egressAuditor,
            new EphemeralLocalSttWorkspaceFactory(),
            timeProvider ?? TimeProvider.System)
    {
    }

    internal LocalSttAdapter(
        ILocalSttPackageVerifier packageVerifier,
        ILocalSttProcessRunner processRunner,
        ILocalSttEgressAuditor egressAuditor,
        ILocalSttWorkspaceFactory workspaceFactory,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(packageVerifier);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(egressAuditor);
        ArgumentNullException.ThrowIfNull(workspaceFactory);
        ArgumentNullException.ThrowIfNull(clock);
        this.packageVerifier = packageVerifier;
        this.processRunner = processRunner;
        this.egressAuditor = egressAuditor;
        this.workspaceFactory = workspaceFactory;
        this.clock = clock;
    }

    public async Task<LocalSttResult> TranscribeAsync(
        LocalSttRequest request,
        CanonicalWaveAudio audio,
        LocalAudioAuthorization? authorization,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audio);
        if (cancellationToken.IsCancellationRequested)
            return LocalSttResult.Canceled(request.OperationId);
        try
        {
            request.Validate(clock.GetUtcNow());
        }
        catch (LocalSttContractException)
        {
            return LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.DeadlineExceeded);
        }
        if (!await slot.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.Busy);
        try
        {
            if (quarantined != 0)
                return LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.Quarantined);
            var verification = await packageVerifier.VerifyForLaunchAsync(cancellationToken).ConfigureAwait(false);
            if (verification.Package is null)
                return cancellationToken.IsCancellationRequested ||
                    verification.Status == PackageVerificationStatus.Canceled
                    ? LocalSttResult.Canceled(request.OperationId)
                    : LocalSttResult.Failed(request.OperationId, MapVerification(verification.Status));

            await using var package = verification.Package;
            if (cancellationToken.IsCancellationRequested ||
                verification.Status == PackageVerificationStatus.Canceled)
                return LocalSttResult.Canceled(request.OperationId);
            if (verification.Status != PackageVerificationStatus.Verified)
                return LocalSttResult.Failed(request.OperationId, MapVerification(verification.Status));

            var authorizationCheckedAt = clock.GetUtcNow();
            var authorizationFailure = ValidateAuthorization(
                request,
                audio,
                package,
                authorization,
                authorizationCheckedAt);
            if (authorizationFailure is { } failure)
                return LocalSttResult.Failed(request.OperationId, failure);
            if (!authorization!.TryConsume())
                return LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.AuthorizationConsumed);
            if (cancellationToken.IsCancellationRequested)
                return LocalSttResult.Canceled(request.OperationId);

            var cutoffAt = request.Deadline < authorization.ExpiresAt
                ? request.Deadline
                : authorization.ExpiresAt;
            var authorizationCutoff = authorization.ExpiresAt < request.Deadline;
            var remaining = cutoffAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.DeadlineExceeded);
            using var deadline = new CancellationTokenSource();
            using var deadlineTimer = clock.CreateTimer(
                static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                deadline,
                remaining,
                Timeout.InfiniteTimeSpan);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            var workspaceResult = await workspaceFactory.CreateAsync(
                request.OperationId,
                audio,
                operation.Token).ConfigureAwait(false);
            if (workspaceResult.Status != WorkspaceStatus.Ready ||
                workspaceResult.Workspace is null)
            {
                if (workspaceResult.Status == WorkspaceStatus.CleanupFailed)
                {
                    Interlocked.Exchange(ref quarantined, 1);
                    return LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.WorkspaceIo);
                }
                return operation.IsCancellationRequested
                    ? CutoffResult(
                        request.OperationId,
                        cancellationToken,
                        deadline.Token,
                        authorizationCutoff)
                    : LocalSttResult.Failed(request.OperationId, MapWorkspace(workspaceResult.Status));
            }

            return await ExecuteOwnedAsync(
                request,
                package,
                workspaceResult.Workspace,
                cutoffAt,
                operation.Token,
                cancellationToken,
                deadline.Token,
                authorizationCutoff).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? LocalSttResult.Canceled(request.OperationId)
                : LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.DeadlineExceeded);
        }
        finally
        {
            slot.Release();
        }
    }

    private async Task<LocalSttResult> ExecuteOwnedAsync(
        LocalSttRequest request,
        VerifiedLocalSttPackage package,
        ILocalSttWorkspace workspace,
        DateTimeOffset cutoffAt,
        CancellationToken operationToken,
        CancellationToken originalCancellationToken,
        CancellationToken deadlineToken,
        bool authorizationCutoff)
    {
        LocalSttResult result = LocalSttResult.Failed(
            request.OperationId,
            LocalSttFailureCode.EgressUnavailable);
        ILocalSttEgressAuditSession? audit = null;
        ILocalSttProcess? process = null;
        var processId = 0;
        var treeExited = false;
        LocalSttPrivacyEvidence? privacyEvidence = null;
        LocalSttProcessCompletion? completion = null;

        try
        {
            result = await ExecuteCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            result = CutoffResult(
                request.OperationId,
                originalCancellationToken,
                deadlineToken,
                authorizationCutoff);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = LocalSttResult.Failed(
                request.OperationId,
                process is null
                    ? LocalSttFailureCode.EgressUnavailable
                    : LocalSttFailureCode.ProcessFailed);
        }
        finally
        {
            if (process is not null && !treeExited)
            {
                bool stopped;
                try
                {
                    stopped = await process.KillTreeAsync(
                        LocalSttPackageManifest.ProcessCleanupTimeout).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or
                    UnauthorizedAccessException or ArgumentException or AggregateException)
                {
                    stopped = false;
                }
                treeExited = stopped;
                if (!stopped)
                {
                    Interlocked.Exchange(ref quarantined, 1);
                    result = LocalSttResult.Failed(
                        request.OperationId,
                        LocalSttFailureCode.ProcessCleanupFailed);
                }
            }

            if (audit is not null && processId > 0 && treeExited)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(
                        LocalSttPackageManifest.ProcessCleanupTimeout);
                    var report = await audit.CompleteAsync(cleanup.Token).ConfigureAwait(false);
                    if (!EgressAuditPolicy.TryAccept(
                            report,
                            request.OperationId,
                            processId,
                            package.NetworkPolicy,
                            out privacyEvidence))
                        if (!IsProcessCleanupFailure(result))
                            result = LocalSttResult.Failed(
                                request.OperationId,
                                LocalSttFailureCode.EgressViolation);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or
                    UnauthorizedAccessException or InvalidOperationException)
                {
                    if (!IsProcessCleanupFailure(result))
                        result = LocalSttResult.Failed(
                            request.OperationId,
                            LocalSttFailureCode.EgressViolation);
                }
            }
            else if (processId > 0)
            {
                result = LocalSttResult.Failed(
                    request.OperationId,
                    treeExited
                        ? LocalSttFailureCode.EgressViolation
                        : LocalSttFailureCode.ProcessCleanupFailed);
            }

            if (process is not null)
            {
                try
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                    AggregateException)
                {
                    Interlocked.Exchange(ref quarantined, 1);
                    result = LocalSttResult.Failed(
                        request.OperationId,
                        LocalSttFailureCode.ProcessCleanupFailed);
                }
            }
            if (audit is not null)
            {
                try
                {
                    await audit.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or
                    UnauthorizedAccessException)
                {
                    if (!IsProcessCleanupFailure(result))
                        result = LocalSttResult.Failed(
                            request.OperationId,
                            LocalSttFailureCode.EgressViolation);
                }
            }

            WorkspaceStatus workspaceCleanup;
            try
            {
                workspaceCleanup = await workspace.CleanupAsync().ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                workspaceCleanup = WorkspaceStatus.AccessDenied;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                workspaceCleanup = WorkspaceStatus.IoFailure;
            }
            if (workspaceCleanup != WorkspaceStatus.Ready)
            {
                Interlocked.Exchange(ref quarantined, 1);
                if (!IsProcessCleanupFailure(result))
                    result = LocalSttResult.Failed(
                        request.OperationId,
                        workspaceCleanup == WorkspaceStatus.AccessDenied
                            ? LocalSttFailureCode.WorkspaceAccessDenied
                            : LocalSttFailureCode.WorkspaceIo);
            }

            if (completion is not null)
                result = result.WithExecution(
                    privacyEvidence,
                    completion.StandardOutput.Length,
                    completion.StandardError.Length,
                    completion.ExitCode);
        }
        return result;

        async Task<LocalSttResult> ExecuteCoreAsync()
        {
            var begin = await egressAuditor.BeginAsync(
                new(
                    request.OperationId,
                    package.PackageId,
                    package.ManifestSha256,
                    package.ExecutableArchiveSha256,
                    package.ModelSha256,
                    package.NetworkPolicy),
                operationToken).AsTask().WaitAsync(operationToken).ConfigureAwait(false);
            if (operationToken.IsCancellationRequested ||
                begin.Status == LocalSttEgressBeginStatus.Canceled)
            {
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);
            }
            audit = begin.Session;
            if (begin.Status != LocalSttEgressBeginStatus.Ready ||
                audit is null ||
                !audit.DenialEstablishedBeforeLaunch)
                return result;

            if (operationToken.IsCancellationRequested || clock.GetUtcNow() >= cutoffAt)
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);
            var launch = WhisperCliPolicy.Create(package, workspace);
            var started = processRunner.Start(launch);
            if (started.Status == LocalSttProcessStartStatus.CleanupFailed)
            {
                Interlocked.Exchange(ref quarantined, 1);
                return LocalSttResult.Failed(
                    request.OperationId,
                    LocalSttFailureCode.ProcessCleanupFailed);
            }
            if (started.Status != LocalSttProcessStartStatus.Started ||
                started.Process is null ||
                started.Process.Id <= 0)
            {
                return LocalSttResult.Failed(
                    request.OperationId,
                    LocalSttFailureCode.ProcessStartFailed);
            }
            process = started.Process;
            processId = process.Id;
            if (operationToken.IsCancellationRequested || clock.GetUtcNow() >= cutoffAt)
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);
            if (!await audit.BindProcessTreeAsync(processId, operationToken)
                    .AsTask().WaitAsync(operationToken).ConfigureAwait(false))
                return operationToken.IsCancellationRequested
                    ? CutoffResult(
                        request.OperationId,
                        originalCancellationToken,
                        deadlineToken,
                        authorizationCutoff)
                    : LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.EgressUnavailable);

            var remaining = cutoffAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);

            var cutoff = Task.Delay(Timeout.InfiniteTimeSpan, operationToken);
            var first = await Task.WhenAny(process.Completion, cutoff).ConfigureAwait(false);
            if (first == cutoff || operationToken.IsCancellationRequested)
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);

            completion = await process.Completion.ConfigureAwait(false);
            treeExited = completion.TreeExited;
            if (operationToken.IsCancellationRequested || clock.GetUtcNow() >= cutoffAt)
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);
            if (completion.Status == LocalSttProcessCompletionStatus.OutputLimit)
                return LocalSttResult.Failed(
                    request.OperationId,
                    LocalSttFailureCode.ProcessOutputLimit);
            if (completion.Status != LocalSttProcessCompletionStatus.Exited ||
                !completion.TreeExited)
                return LocalSttResult.Failed(
                    request.OperationId,
                    LocalSttFailureCode.ProcessFailed);
            if (!TranscriptParser.DiagnosticsAreSafe(
                    completion.StandardOutput,
                    completion.StandardError))
                return LocalSttResult.Failed(
                    request.OperationId,
                    LocalSttFailureCode.ProcessOutputMalformed);
            if (completion.ExitCode != 0)
                return LocalSttResult.Failed(
                    request.OperationId,
                    LocalSttFailureCode.ProcessFailed);
            var transcript = await workspace.ReadTranscriptAsync(operationToken)
                .ConfigureAwait(false);
            if (operationToken.IsCancellationRequested || clock.GetUtcNow() >= cutoffAt)
                return CutoffResult(
                    request.OperationId,
                    originalCancellationToken,
                    deadlineToken,
                    authorizationCutoff);
            return transcript.Status switch
            {
                WorkspaceStatus.Ready when transcript.Bytes is not null =>
                    TranscriptParser.Parse(request.OperationId, transcript.Bytes),
                WorkspaceStatus.MissingOutput =>
                    LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.TranscriptMissing),
                WorkspaceStatus.OutputLimit =>
                    LocalSttResult.Failed(request.OperationId, LocalSttFailureCode.TranscriptLimit),
                _ => LocalSttResult.Failed(request.OperationId, MapWorkspace(transcript.Status))
            };
        }
    }

    private static LocalSttResult CutoffResult(
        Guid operationId,
        CancellationToken originalCancellationToken,
        CancellationToken deadlineToken,
        bool authorizationCutoff) =>
        originalCancellationToken.IsCancellationRequested
            ? LocalSttResult.Canceled(operationId)
            : deadlineToken.IsCancellationRequested
                ? LocalSttResult.Failed(
                    operationId,
                    authorizationCutoff
                        ? LocalSttFailureCode.AuthorizationExpired
                        : LocalSttFailureCode.DeadlineExceeded)
                : LocalSttResult.Failed(
                    operationId,
                    authorizationCutoff
                        ? LocalSttFailureCode.AuthorizationExpired
                        : LocalSttFailureCode.DeadlineExceeded);

    private static bool IsProcessCleanupFailure(LocalSttResult result) =>
        result.Failure?.Code == LocalSttFailureCode.ProcessCleanupFailed;

    private static LocalSttFailureCode? ValidateAuthorization(
        LocalSttRequest request,
        CanonicalWaveAudio audio,
        VerifiedLocalSttPackage package,
        LocalAudioAuthorization? authorization,
        DateTimeOffset now)
    {
        if (authorization is null ||
            !authorization.AllowLocalAudioProcessing ||
            !authorization.AllowEphemeralAudioFile ||
            !authorization.RequireDeniedEgress ||
            !authorization.RightsReviewedForCandidate)
            return LocalSttFailureCode.AuthorizationMissing;
        if (authorization.OperationId != request.OperationId ||
            authorization.PackageId != package.PackageId ||
            authorization.ManifestSha256 != package.ManifestSha256 ||
            authorization.ModelId != package.ModelId ||
            authorization.ModelSha256 != package.ModelSha256 ||
            authorization.Language != package.Language ||
            !audio.Matches(authorization.AudioSha256, authorization.AudioBytes))
            return LocalSttFailureCode.AuthorizationMismatch;
        if (authorization.ExpiresAt <= now ||
            request.Deadline <= now ||
            authorization.ExpiresAt > request.Deadline ||
            authorization.ExpiresAt - now > LocalSttPackageManifest.MaximumActionLifetime)
            return LocalSttFailureCode.AuthorizationExpired;
        return null;
    }

    private static LocalSttFailureCode MapVerification(PackageVerificationStatus status) => status switch
    {
        PackageVerificationStatus.Missing => LocalSttFailureCode.PackageMissing,
        PackageVerificationStatus.Changed => LocalSttFailureCode.PackageChanged,
        PackageVerificationStatus.UnsafePath => LocalSttFailureCode.PackageUnsafePath,
        PackageVerificationStatus.AccessDenied => LocalSttFailureCode.PackageAccessDenied,
        PackageVerificationStatus.UnsupportedHost => LocalSttFailureCode.UnsupportedHost,
        _ => LocalSttFailureCode.PackageInvalid
    };

    private static LocalSttFailureCode MapWorkspace(WorkspaceStatus status) => status switch
    {
        WorkspaceStatus.DiskFull => LocalSttFailureCode.DiskFull,
        WorkspaceStatus.AccessDenied => LocalSttFailureCode.WorkspaceAccessDenied,
        WorkspaceStatus.UnsafePath => LocalSttFailureCode.PackageUnsafePath,
        WorkspaceStatus.OutputLimit => LocalSttFailureCode.TranscriptLimit,
        WorkspaceStatus.MissingOutput => LocalSttFailureCode.TranscriptMissing,
        _ => LocalSttFailureCode.WorkspaceIo
    };

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return ValueTask.CompletedTask;
    }
}

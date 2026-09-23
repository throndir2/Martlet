using System.Text;
using Martlet.LocalStt;

namespace Martlet.LocalStt.Tests;

public sealed class AdapterTests
{
    [Fact]
    public async Task Public_candidate_api_cannot_enable_execution_even_with_matching_hashes_and_audit_fixture()
    {
        await using var harness = new AdapterHarness();
        await using var candidate = new LocalSttAdapter(harness.Verifier, harness.Egress, harness.Clock);
        var request = harness.Request();
        var result = await candidate.TranscribeAsync(request, harness.Audio, harness.Authorization(request));
        Assert.Equal(LocalSttFailureCode.PackageUnqualified, result.Failure!.Code);
        Assert.Equal(0, harness.Verifier.Calls);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.False(typeof(SystemLocalSttProcessRunner).IsPublic);
        Assert.False(typeof(ILocalSttProcessRunner).IsPublic);
        Assert.False(typeof(LocalSttProcessStartRequest).IsPublic);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("package")]
    [InlineData("manifest")]
    [InlineData("model-hash")]
    [InlineData("operation")]
    [InlineData("audio-size")]
    public async Task Every_action_identity_and_original_budget_is_bound_before_consumption(string mismatch)
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();
        var authorization = harness.Authorization(request,
            requestDeadline: mismatch == "deadline" ? request.Deadline.AddSeconds(-1) : null,
            packageId: mismatch == "package" ? "different" : null,
            manifestSha256: mismatch == "manifest" ? new string('0', 64) : null,
            modelSha256: mismatch == "model-hash" ? new string('0', 64) : null,
            operationId: mismatch == "operation" ? Guid.NewGuid() : null,
            audioBytes: mismatch == "audio-size" ? 44 : null);
        var result = await harness.Adapter.TranscribeAsync(request, harness.Audio, authorization);
        Assert.Equal(LocalSttFailureCode.AuthorizationMismatch, result.Failure!.Code);
        Assert.True(authorization.TryConsume());
        Assert.Equal(0, harness.Workspaces.Calls);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("file")]
    [InlineData("egress")]
    public async Task Each_distinct_effect_requires_explicit_permission(string permission)
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();
        var result = await harness.Adapter.TranscribeAsync(request, harness.Audio,
            harness.Authorization(request, allowProcess: permission != "process",
                allowFile: permission != "file", deniedEgress: permission != "egress"));
        Assert.Equal(LocalSttFailureCode.AuthorizationMissing, result.Failure!.Code);
        Assert.Equal(0, harness.Workspaces.Calls);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Fact]
    public async Task Original_deadline_covers_package_verification_without_consuming_permission()
    {
        await using var harness = new AdapterHarness();
        harness.Verifier.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = harness.Request(TimeSpan.FromSeconds(5));
        var authorization = harness.Authorization(request);
        var pending = harness.Adapter.TranscribeAsync(request, harness.Audio, authorization);
        await WaitUntilAsync(() => harness.Verifier.Calls == 1);
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalSttFailureCode.DeadlineExceeded, result.Failure!.Code);
        Assert.True(authorization.TryConsume());
        Assert.Equal(0, harness.Workspaces.Calls);
    }

    [Fact]
    public async Task Cancellation_at_egress_return_disposes_the_returned_session_before_owner_reuse()
    {
        await using var harness = new AdapterHarness();
        using var cancel = new CancellationTokenSource();
        harness.Egress.BeforeReturn = cancel.Cancel;
        harness.Egress.Session.DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = harness.Request();
        var pending = harness.Adapter.TranscribeAsync(request, harness.Audio, harness.Authorization(request), cancel.Token);
        await WaitUntilAsync(() => harness.Egress.Session.DisposeCalls == 1);
        var other = harness.Request();
        Assert.Equal(LocalSttFailureCode.Busy,
            (await harness.Adapter.TranscribeAsync(other, harness.Audio, harness.Authorization(other))).Failure!.Code);
        Assert.False(pending.IsCompleted);
        harness.Egress.Session.DisposeGate.SetResult();
        Assert.Equal(LocalSttOutcome.Canceled, (await pending).Outcome);
        Assert.Equal(0, harness.Processes.Calls);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
    }

    [Fact]
    public async Task Late_egress_admission_is_not_abandoned_after_cancellation()
    {
        await using var harness = new AdapterHarness();
        using var cancel = new CancellationTokenSource();
        harness.Egress.BeginGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Egress.IgnoreBeginCancellation = true;
        var request = harness.Request();
        var pending = harness.Adapter.TranscribeAsync(request, harness.Audio, harness.Authorization(request), cancel.Token);
        await WaitUntilAsync(() => harness.Egress.Calls == 1);
        cancel.Cancel();
        Assert.False(pending.IsCompleted);
        var other = harness.Request();
        Assert.Equal(LocalSttFailureCode.Busy,
            (await harness.Adapter.TranscribeAsync(other, harness.Audio, harness.Authorization(other))).Failure!.Code);
        harness.Egress.BeginGate.SetResult();
        Assert.Equal(LocalSttOutcome.Canceled, (await pending).Outcome);
        Assert.Equal(1, harness.Egress.Session.DisposeCalls);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Fact]
    public async Task Failed_audit_disposal_discards_success_and_quarantines_before_owner_reuse()
    {
        await using var harness = new AdapterHarness();
        harness.Egress.Session.FailDisposal = true;
        var firstRequest = harness.Request();
        var first = await harness.Adapter.TranscribeAsync(
            firstRequest, harness.Audio, harness.Authorization(firstRequest));
        Assert.Equal(LocalSttFailureCode.EgressViolation, first.Failure!.Code);
        Assert.Null(first.Text);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
        var secondRequest = harness.Request();
        var second = await harness.Adapter.TranscribeAsync(
            secondRequest, harness.Audio, harness.Authorization(secondRequest));
        Assert.Equal(LocalSttFailureCode.Quarantined, second.Failure!.Code);
        Assert.Equal(1, harness.Egress.Calls);
        Assert.Equal(1, harness.Processes.Calls);
    }

    [Fact]
    public async Task Dispose_cancels_admitted_work_and_waits_for_private_cleanup_before_returning()
    {
        await using var harness = new AdapterHarness();
        harness.Processes.Process = new FakeProcess();
        harness.Workspaces.Workspace.CleanupGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = harness.Request();
        var pending = harness.Adapter.TranscribeAsync(request, harness.Audio, harness.Authorization(request));
        await harness.WaitForProcessStartAsync();
        var disposing = harness.Adapter.DisposeAsync().AsTask();
        await WaitUntilAsync(() => harness.Workspaces.Workspace.CleanupCalls == 1);
        Assert.False(disposing.IsCompleted);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, harness.Processes.Process.KillCalls);
        harness.Workspaces.Workspace.CleanupGate.SetResult();
        await disposing;
        Assert.Equal(LocalSttOutcome.Canceled, (await pending).Outcome);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            harness.Adapter.TranscribeAsync(request, harness.Audio, harness.Authorization(request)));
    }

    [Fact]
    public async Task Cancellation_during_final_audit_discards_success_and_clears_transcript_bytes()
    {
        await using var harness = new AdapterHarness();
        using var cancel = new CancellationTokenSource();
        harness.Egress.Session.BeforeComplete = cancel.Cancel;
        var request = harness.Request();
        var result = await harness.Adapter.TranscribeAsync(request, harness.Audio, harness.Authorization(request), cancel.Token);
        Assert.Equal(LocalSttOutcome.Canceled, result.Outcome);
        Assert.Null(result.Text);
        Assert.All(harness.Workspaces.Workspace.Transcript, value => Assert.Equal(0, value));
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
    }

    [Fact]
    public async Task Caller_disposal_after_admission_does_not_change_the_authorized_audio_copy()
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();
        var permit = harness.Authorization(request);
        harness.Verifier.BeforeReturn = harness.Audio.Dispose;
        var result = await harness.Adapter.TranscribeAsync(request, harness.Audio, permit);
        Assert.Equal(LocalSttOutcome.Completed, result.Outcome);
        Assert.Equal(1, harness.Workspaces.Calls);
        Assert.Equal(1, harness.Processes.Calls);
    }

    [Fact]
    public async Task One_permit_cannot_be_admitted_by_two_adapter_owners()
    {
        await using var first = new AdapterHarness();
        await using var second = new AdapterHarness();
        first.Egress.BeginGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = first.Request();
        var permit = first.Authorization(request);
        var pending = first.Adapter.TranscribeAsync(request, first.Audio, permit);
        await WaitUntilAsync(() => first.Egress.Calls == 1);
        var duplicate = await second.Adapter.TranscribeAsync(request, second.Audio, permit);
        Assert.Equal(LocalSttFailureCode.AuthorizationConsumed, duplicate.Failure!.Code);
        Assert.Equal(0, second.Workspaces.Calls);
        first.Egress.BeginGate.SetResult();
        Assert.Equal(LocalSttOutcome.Completed, (await pending).Outcome);
    }

    [Fact]
    public async Task Construction_and_precancellation_are_inert_and_do_not_consume_authorization()
    {
        await using var harness = new AdapterHarness();
        Assert.Equal(0, harness.Verifier.Calls);
        Assert.Equal(0, harness.Workspaces.Calls);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Equal(0, harness.Processes.Calls);
        var request = harness.Request();
        var authorization = harness.Authorization(request);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var canceled = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            authorization,
            cancel.Token);
        var completed = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            authorization);

        Assert.Equal(LocalSttOutcome.Canceled, canceled.Outcome);
        Assert.Equal(LocalSttOutcome.Completed, completed.Outcome);
        Assert.Equal(1, harness.Processes.Calls);
    }

    [Fact]
    public async Task Cancellation_after_verification_disposes_all_package_locks_before_returning()
    {
        await using var harness = new AdapterHarness();
        var held = new TrackingStream();
        harness.Verifier.Package = new(
            LocalSttPackageManifest.Current,
            harness.Package.ExecutablePath,
            harness.Package.ModelPath,
            harness.Package.RuntimeDirectory,
            [held]);
        using var cancel = new CancellationTokenSource();
        harness.Verifier.BeforeReturn = cancel.Cancel;
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request),
            cancel.Token);

        Assert.Equal(LocalSttOutcome.Canceled, result.Outcome);
        Assert.True(held.WasDisposed);
        Assert.Equal(0, harness.Workspaces.Calls);
    }

    [Fact]
    public async Task Authorization_expiring_during_egress_setup_never_launches_a_process()
    {
        await using var harness = new AdapterHarness();
        harness.Egress.BeginGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = harness.Request(TimeSpan.FromSeconds(20));
        var authorization = harness.Authorization(
            request,
            expiresAt: harness.Clock.GetUtcNow().AddSeconds(5));
        var pending = harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            authorization);
        await WaitUntilAsync(() => harness.Egress.Calls == 1);

        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        var result = await pending;

        Assert.Equal(LocalSttFailureCode.AuthorizationExpired, result.Failure!.Code);
        Assert.Equal(0, harness.Processes.Calls);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
    }

    [Fact]
    public async Task Hung_process_audit_binding_is_bounded_and_kills_the_started_tree()
    {
        await using var harness = new AdapterHarness();
        harness.Egress.Session.BindGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Processes.Process = new FakeProcess();
        var request = harness.Request(TimeSpan.FromSeconds(5));
        var pending = harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));
        await harness.WaitForProcessStartAsync();

        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        var result = await pending;

        Assert.Equal(LocalSttFailureCode.EgressViolation, result.Failure!.Code);
        Assert.Equal(1, harness.Processes.Process.KillCalls);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
    }

    [Fact]
    public async Task Completed_transcript_uses_exact_fixed_launch_and_passed_no_network_audit()
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();
        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttOutcome.Completed, result.Outcome);
        Assert.Equal("fixture transcript", result.Text);
        Assert.Null(result.Failure);
        Assert.NotNull(result.PrivacyEvidence);
        Assert.Equal(LocalSttNetworkPolicy.NoNetwork, result.PrivacyEvidence.Policy);
        Assert.True(result.PrivacyEvidence.DenialEstablishedBeforeLaunch);
        Assert.Equal(1, harness.Processes.Calls);
        Assert.Equal(1, harness.Egress.Session.BindCalls);
        Assert.Equal(1, harness.Egress.Session.CompleteCalls);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);

        var launch = Assert.IsType<LocalSttProcessStartRequest>(harness.Processes.Request);
        Assert.Equal(harness.Package.ExecutablePath, launch.ExecutablePath);
        Assert.Equal(harness.Package.RuntimeDirectory, launch.WorkingDirectory);
        Assert.Equal(
            [
                "--model", harness.Package.ModelPath,
                "--file", harness.Workspaces.Workspace.AudioPath,
                "--language", "en",
                "--threads", "4",
                "--processors", "1",
                "--no-gpu",
                "--no-timestamps",
                "--output-txt",
                "--output-file", harness.Workspaces.Workspace.TranscriptPrefixPath,
                "--no-prints"
            ],
            launch.Arguments.ToArray());
        Assert.Equal(
            ["LANG", "LC_ALL", "OMP_NUM_THREADS", "PATH", "SYSTEMROOT", "TEMP", "TMP"],
            launch.Environment.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(launch.Environment.Keys, key =>
            key.Contains("KEY", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("HOME", StringComparison.OrdinalIgnoreCase));
        Assert.True(launch.CloseStandardInput);
        Assert.Equal(harness.Package.ExecutableArchiveSha256, harness.Egress.Request!.ExecutableArchiveSha256);
        Assert.Equal(harness.Package.ModelSha256, harness.Egress.Request.ModelSha256);
    }

    [Fact]
    public async Task Empty_owned_result_is_explicit_no_speech_and_never_dispatches_a_fallback()
    {
        await using var harness = new AdapterHarness();
        harness.Workspaces.Workspace.Transcript = " \r\n\t"u8.ToArray();
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttOutcome.NoSpeech, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal(1, harness.Processes.Calls);
        Assert.Equal(1, harness.Workspaces.Workspace.ReadCalls);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("language")]
    [InlineData("audio")]
    public async Task Wrong_model_language_or_audio_binding_never_writes_audio_or_starts_a_process(string mismatch)
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();
        var authorization = harness.Authorization(
            request,
            modelId: mismatch == "model" ? "ggml-tiny.en" : null,
            language: mismatch == "language" ? "fr" : null,
            audioSha256: mismatch == "audio" ? new string('0', 64) : null);

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            authorization);

        Assert.Equal(LocalSttFailureCode.AuthorizationMismatch, result.Failure!.Code);
        Assert.Equal(0, harness.Workspaces.Calls);
        Assert.Equal(0, harness.Processes.Calls);
        Assert.Equal(0, harness.Egress.Calls);
    }

    [Fact]
    public async Task Authorization_is_single_use_even_after_a_completed_action()
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();
        var authorization = harness.Authorization(request);
        Assert.Equal(
            LocalSttOutcome.Completed,
            (await harness.Adapter.TranscribeAsync(request, harness.Audio, authorization)).Outcome);

        var second = await harness.Adapter.TranscribeAsync(request, harness.Audio, authorization);

        Assert.Equal(LocalSttFailureCode.AuthorizationConsumed, second.Failure!.Code);
        Assert.Equal(1, harness.Processes.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_action_or_rights_permission_stays_inert(bool allow, bool rightsReviewed)
    {
        await using var harness = new AdapterHarness();
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request, allow: allow, rightsReviewed: rightsReviewed));

        Assert.Equal(LocalSttFailureCode.AuthorizationMissing, result.Failure!.Code);
        Assert.Equal(0, harness.Workspaces.Calls);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Theory]
    [InlineData(PackageVerificationStatus.Missing, LocalSttFailureCode.PackageMissing)]
    [InlineData(PackageVerificationStatus.Changed, LocalSttFailureCode.PackageChanged)]
    [InlineData(PackageVerificationStatus.UnsafePath, LocalSttFailureCode.PackageUnsafePath)]
    [InlineData(PackageVerificationStatus.AccessDenied, LocalSttFailureCode.PackageAccessDenied)]
    [InlineData(PackageVerificationStatus.Invalid, LocalSttFailureCode.PackageInvalid)]
    [InlineData(PackageVerificationStatus.UnsupportedHost, LocalSttFailureCode.UnsupportedHost)]
    public async Task Artifact_gate_fails_closed_before_authorization_or_audio(
        PackageVerificationStatus status,
        LocalSttFailureCode expected)
    {
        await using var harness = new AdapterHarness();
        harness.Verifier.Status = status;
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(0, harness.Workspaces.Calls);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Theory]
    [InlineData("disk", LocalSttFailureCode.DiskFull)]
    [InlineData("access", LocalSttFailureCode.WorkspaceAccessDenied)]
    [InlineData("io", LocalSttFailureCode.WorkspaceIo)]
    [InlineData("path", LocalSttFailureCode.PackageUnsafePath)]
    public async Task Disk_access_and_path_failures_never_start_a_process(
        string fixture,
        LocalSttFailureCode expected)
    {
        await using var harness = new AdapterHarness();
        harness.Workspaces.Status = fixture switch
        {
            "disk" => WorkspaceStatus.DiskFull,
            "access" => WorkspaceStatus.AccessDenied,
            "io" => WorkspaceStatus.IoFailure,
            _ => WorkspaceStatus.UnsafePath
        };
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(expected, result.Failure!.Code);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Fact]
    public async Task Failed_cleanup_during_workspace_creation_quarantines_without_launch()
    {
        await using var harness = new AdapterHarness();
        harness.Workspaces.Status = WorkspaceStatus.CleanupFailed;
        var request = harness.Request();
        var first = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));
        var secondRequest = harness.Request();
        var second = await harness.Adapter.TranscribeAsync(
            secondRequest,
            harness.Audio,
            harness.Authorization(secondRequest));

        Assert.Equal(LocalSttFailureCode.WorkspaceIo, first.Failure!.Code);
        Assert.Equal(LocalSttFailureCode.Quarantined, second.Failure!.Code);
        Assert.Equal(0, harness.Processes.Calls);
    }

    [Fact]
    public async Task Denied_egress_must_exist_before_launch()
    {
        await using var harness = new AdapterHarness();
        harness.Egress.Session.DenialEstablishedBeforeLaunch = false;
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttFailureCode.EgressUnavailable, result.Failure!.Code);
        Assert.Equal(0, harness.Processes.Calls);
        Assert.Equal(1, harness.Egress.Session.DisposeCalls);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task Any_network_attempt_discards_an_otherwise_valid_transcript(int loopback, int external)
    {
        await using var harness = new AdapterHarness();
        harness.Egress.Session.LoopbackAttempts = loopback;
        harness.Egress.Session.NonLoopbackAttempts = external;
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttFailureCode.EgressViolation, result.Failure!.Code);
        Assert.Null(result.Text);
        Assert.Null(result.PrivacyEvidence);
    }

    [Fact]
    public async Task Malformed_transcript_is_rejected_and_private_pipe_content_is_redacted()
    {
        await using var harness = new AdapterHarness();
        harness.Workspaces.Workspace.Transcript = [0xff, 0xfe];
        harness.Processes.Process = FakeProcess.Completed(
            stderr: Encoding.UTF8.GetBytes("SECRET_CANARY C:\\private\\input.wav"));
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttFailureCode.TranscriptMalformed, result.Failure!.Code);
        Assert.DoesNotContain("SECRET_CANARY", result.ToString());
        Assert.DoesNotContain("private", result.Failure.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.StandardOutputBytes);
        Assert.True(result.StandardErrorBytes > 0);
    }

    [Fact]
    public async Task Unexpected_stdout_is_not_accepted_as_a_transcript()
    {
        await using var harness = new AdapterHarness();
        harness.Processes.Process = FakeProcess.Completed(
            stdout: Encoding.UTF8.GetBytes("unowned transcript"));
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttFailureCode.ProcessOutputMalformed, result.Failure!.Code);
        Assert.Equal(0, harness.Workspaces.Workspace.ReadCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transcript_byte_and_character_bounds_discard_all_partial_text(bool byteBound)
    {
        await using var harness = new AdapterHarness();
        harness.Workspaces.Workspace.Transcript = Encoding.UTF8.GetBytes(new string(
            byteBound ? '\u0800' : 'a',
            byteBound
                ? LocalSttPackageManifest.MaximumTranscriptBytes
                : LocalSttPackageManifest.MaximumTranscriptCharacters + 1));
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttFailureCode.TranscriptLimit, result.Failure!.Code);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Malformed_stderr_and_nonzero_exit_never_expose_or_accept_output()
    {
        await using var malformed = new AdapterHarness();
        malformed.Processes.Process = FakeProcess.Completed(stderr: [0xff, 0xfe]);
        var malformedRequest = malformed.Request();
        var malformedResult = await malformed.Adapter.TranscribeAsync(
            malformedRequest,
            malformed.Audio,
            malformed.Authorization(malformedRequest));
        Assert.Equal(LocalSttFailureCode.ProcessOutputMalformed, malformedResult.Failure!.Code);

        await using var failed = new AdapterHarness();
        failed.Processes.Process = FakeProcess.Completed(
            exitCode: 17,
            stderr: Encoding.UTF8.GetBytes("SECRET failure details"));
        var failedRequest = failed.Request();
        var failedResult = await failed.Adapter.TranscribeAsync(
            failedRequest,
            failed.Audio,
            failed.Authorization(failedRequest));
        Assert.Equal(LocalSttFailureCode.ProcessFailed, failedResult.Failure!.Code);
        Assert.DoesNotContain("SECRET", failedResult.ToString());
        Assert.Equal(0, failed.Workspaces.Workspace.ReadCalls);
    }

    [Fact]
    public async Task Output_limit_kills_a_still_running_tree_and_discards_partial_bytes()
    {
        await using var harness = new AdapterHarness();
        harness.Processes.Process = FakeProcess.Completed(
            LocalSttProcessCompletionStatus.OutputLimit,
            treeExited: false,
            exitCode: null,
            stdout: Encoding.UTF8.GetBytes("partial"));
        var request = harness.Request();

        var result = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));

        Assert.Equal(LocalSttFailureCode.ProcessOutputLimit, result.Failure!.Code);
        Assert.Equal(1, harness.Processes.Process.KillCalls);
        Assert.Equal(0, harness.Workspaces.Workspace.ReadCalls);
    }

    [Fact]
    public async Task Hung_child_is_killed_at_the_original_deadline_and_late_output_is_ignored()
    {
        await using var harness = new AdapterHarness();
        var hung = new FakeProcess();
        harness.Processes.Process = hung;
        var request = harness.Request(TimeSpan.FromSeconds(10));
        var pending = harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));
        await harness.WaitForProcessStartAsync();

        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        var result = await pending;
        hung.Complete(stdout: Encoding.UTF8.GetBytes("late private output"));

        Assert.Equal(LocalSttOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(LocalSttFailureCode.DeadlineExceeded, result.Failure!.Code);
        Assert.Equal(1, hung.KillCalls);
        Assert.Null(result.Text);
        Assert.Equal(1, harness.Egress.Session.CompleteCalls);
    }

    [Fact]
    public async Task Cancellation_kills_only_the_owned_tree_and_wins_over_late_success()
    {
        await using var harness = new AdapterHarness();
        var hung = new FakeProcess();
        harness.Processes.Process = hung;
        var request = harness.Request();
        using var cancel = new CancellationTokenSource();
        var pending = harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request),
            cancel.Token);
        await harness.WaitForProcessStartAsync();

        cancel.Cancel();
        var result = await pending;
        hung.Complete();

        Assert.Equal(LocalSttOutcome.Canceled, result.Outcome);
        Assert.Equal(1, hung.KillCalls);
        Assert.Null(result.Text);
        Assert.Equal(1, harness.Workspaces.Workspace.CleanupCalls);
    }

    [Fact]
    public async Task Failed_tree_cleanup_quarantines_the_adapter()
    {
        await using var harness = new AdapterHarness();
        var hung = new FakeProcess { KillSucceeds = false };
        harness.Processes.Process = hung;
        var request = harness.Request();
        using var cancel = new CancellationTokenSource();
        var pending = harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request),
            cancel.Token);
        await harness.WaitForProcessStartAsync();
        cancel.Cancel();

        var first = await pending;
        var secondRequest = harness.Request();
        var second = await harness.Adapter.TranscribeAsync(
            secondRequest,
            harness.Audio,
            harness.Authorization(secondRequest));

        Assert.Equal(LocalSttFailureCode.ProcessCleanupFailed, first.Failure!.Code);
        Assert.Equal(LocalSttFailureCode.Quarantined, second.Failure!.Code);
        Assert.Equal(1, harness.Processes.Calls);
    }

    [Fact]
    public async Task Failed_private_file_cleanup_discards_text_and_quarantines_the_adapter()
    {
        await using var harness = new AdapterHarness();
        harness.Workspaces.Workspace.CleanupStatus = WorkspaceStatus.AccessDenied;
        var request = harness.Request();

        var first = await harness.Adapter.TranscribeAsync(
            request,
            harness.Audio,
            harness.Authorization(request));
        var secondRequest = harness.Request();
        var second = await harness.Adapter.TranscribeAsync(
            secondRequest,
            harness.Audio,
            harness.Authorization(secondRequest));

        Assert.Equal(LocalSttFailureCode.WorkspaceAccessDenied, first.Failure!.Code);
        Assert.Null(first.Text);
        Assert.Equal(LocalSttFailureCode.Quarantined, second.Failure!.Code);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The controlled fixture did not reach the expected state.");
            await Task.Yield();
        }
    }
}

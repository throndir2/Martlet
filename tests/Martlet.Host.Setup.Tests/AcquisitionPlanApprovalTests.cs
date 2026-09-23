using System.Security.Cryptography;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionPlanApprovalTests
{
    [Fact]
    public async Task Preview_binds_exact_manifest_source_rights_destination_disk_and_setup_journal()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var coordinator = harness.Coordinator();

        var preview = await coordinator.PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);

        Assert.Equal(ArtifactAcquisitionPreviewState.Proposed, preview.State);
        Assert.True(preview.CanApprove);
        Assert.Null(preview.Failure);
        var plan = preview.Plan;
        Assert.Equal(ArtifactAcquisitionDisposition.Reviewable, plan.Disposition);
        Assert.Equal(harness.Candidate.IdentityFingerprint, plan.Candidate.IdentityFingerprint);
        Assert.Equal(harness.Candidate.ManifestSha256, plan.Candidate.ManifestSha256);
        Assert.Equal(harness.Candidate.SourceUrl, plan.Candidate.SourceUrl);
        Assert.Equal(harness.Candidate.SourceRevision, plan.Candidate.SourceRevision);
        Assert.Equal(harness.Candidate.ExpectedSha256, plan.Candidate.ExpectedSha256);
        Assert.Equal(harness.Candidate.ExpectedBytes, plan.Candidate.ExpectedBytes);
        Assert.Equal(harness.Rights.Fingerprint, plan.RightsAuthorizationFingerprint);
        Assert.Equal(harness.SetupPlan.Fingerprint, plan.SetupPlanFingerprint);
        Assert.Equal(harness.SetupPlan.HostFactsFingerprint, plan.HostFactsFingerprint);
        Assert.Equal(harness.SetupPreview.JournalPath, plan.SetupJournalPath);
        Assert.Equal(harness.SetupPreview.JournalVersion, plan.SetupJournalVersion);
        Assert.Equal(harness.SetupPreview.JournalRevision, plan.SetupJournalRevision);
        Assert.Equal(harness.Root, plan.ArtifactRootPath);
        Assert.Equal(
            Path.Combine(
                harness.Root,
                $"artifact-{harness.Candidate.ArtifactId}-{harness.Candidate.ExpectedSha256![..16]}.bin"),
            plan.DestinationPath);
        Assert.Equal(plan.DestinationPath + ".partial", plan.PartialPath);
        Assert.Equal(plan.DestinationPath + ".acquisition.json", plan.JournalPath);
        Assert.Equal(0, plan.ExistingBytes);
        Assert.True(plan.ObservedAvailableBytes >= plan.RequiredAvailableBytes);
        Assert.Equal(
            [
                SetupConsentScope.LocalJournal,
                SetupConsentScope.ArtifactDownload,
                SetupConsentScope.ModelAndVoiceRights
            ],
            plan.RequiredConsentScopes.ToArray());
        Assert.Equal(64, plan.Fingerprint.Length);
    }

    [Fact]
    public async Task Approval_defaults_to_no_and_requires_exact_scopes()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var preview = await harness.Coordinator().PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);

        var defaultNo = preview.Approve();
        var partial = preview.Approve(
            ArtifactAcquisitionDecision.Approve,
            [
                SetupConsentScope.LocalJournal,
                SetupConsentScope.ArtifactDownload
            ]);
        var blanket = preview.Approve(
            ArtifactAcquisitionDecision.Approve,
            [
                .. preview.Plan.RequiredConsentScopes,
                SetupConsentScope.HostFilesystem
            ]);
        var exact = preview.Approve(
            ArtifactAcquisitionDecision.Approve,
            preview.Plan.RequiredConsentScopes);

        Assert.False(defaultNo.IsApproved);
        Assert.Equal(ArtifactAcquisitionFailure.ConsentRequired, defaultNo.Failure);
        Assert.Equal(ArtifactAcquisitionFailure.ConsentScopeMismatch, partial.Failure);
        Assert.Equal(ArtifactAcquisitionFailure.ConsentScopeMismatch, blanket.Failure);
        Assert.True(exact.IsApproved);
    }

    [Fact]
    public async Task Separate_rights_review_defaults_to_no_and_blocks_preview()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var refusedRights = new ArtifactRightsReview(
            harness.Candidate,
            "fixture-rights-v1",
            harness.Rights.EvidenceSha256).Authorize();

        var preview = await harness.Coordinator().PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            refusedRights);

        Assert.Equal(ArtifactAcquisitionPreviewState.Blocked, preview.State);
        Assert.False(preview.CanApprove);
        Assert.Equal(ArtifactAcquisitionFailure.RightsNotApproved, preview.Failure);
        Assert.False(preview.Approve(
            ArtifactAcquisitionDecision.Approve,
            preview.Plan.RequiredConsentScopes).IsApproved);
    }

    [Fact]
    public async Task Production_default_refuses_network_and_consumes_only_one_approval()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var coordinator = ArtifactAcquisitionCoordinator.CreateForFixture(
            harness.SetupFileSystem,
            harness.Storage,
            clock: harness.Clock,
            freeSpaceReserveBytes: 0);
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var first = await coordinator.RunAsync(preview.Plan, approval);
        var second = await coordinator.RunAsync(preview.Plan, approval);
        var paths = harness.Storage.GetPaths(harness.Candidate);

        Assert.Equal(ArtifactAcquisitionRunState.Blocked, first.State);
        Assert.Equal(ArtifactAcquisitionFailure.NetworkRefused, first.Failure);
        Assert.Equal(ArtifactAcquisitionRunState.Refused, second.State);
        Assert.Equal(ArtifactAcquisitionFailure.ConsentConsumed, second.Failure);
        Assert.True(File.Exists(paths.JournalPath));
        Assert.False(File.Exists(paths.PartialPath));
        Assert.False(File.Exists(paths.DestinationPath));
        Assert.False(first.RuntimeEnabled);
        Assert.False(first.HostReady);
    }

    [Fact]
    public async Task Changed_setup_journal_after_preview_refuses_before_network_or_acquisition_write()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        harness.SetupFileSystem.AppendWhitespace();

        var result = await coordinator.RunAsync(preview.Plan, approval);

        Assert.Equal(ArtifactAcquisitionRunState.Refused, result.State);
        Assert.Equal(ArtifactAcquisitionFailure.SetupJournalChanged, result.Failure);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.False(File.Exists(preview.Plan.JournalPath));
        Assert.False(File.Exists(preview.Plan.PartialPath));
    }

    [Fact]
    public async Task Unowned_partial_or_final_is_never_overwritten_or_deleted()
    {
        foreach (var final in new[] { false, true })
        {
            await using var harness = await AcquisitionHarness.CreateAsync();
            var paths = harness.Storage.GetPaths(harness.Candidate);
            var path = final ? paths.DestinationPath : paths.PartialPath;
            await File.WriteAllTextAsync(path, "UNOWNED-PRIVATE-CANARY");

            var preview = await harness.Coordinator().PreviewAsync(
                harness.Manifest,
                harness.Candidate.ArtifactId,
                harness.SetupPlan,
                harness.SetupPreview,
                harness.Rights);

            Assert.Equal(ArtifactAcquisitionPreviewState.Blocked, preview.State);
            Assert.Equal(
                final
                    ? ArtifactAcquisitionFailure.FinalConflict
                    : ArtifactAcquisitionFailure.PartialConflict,
                preview.Failure);
            Assert.Equal("UNOWNED-PRIVATE-CANARY", await File.ReadAllTextAsync(path));
            Assert.False(preview.CanApprove);
            Assert.Equal(0, harness.Handler.Calls);
        }
    }

    [Fact]
    public async Task Disk_forecast_and_separately_bound_local_staging_are_enforced_before_approval()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var constrained = new FaultingAcquisitionStorage(harness.Storage)
        {
            AvailableBytesOverride = harness.Candidate.ExpectedBytes - 1
        };
        var preview = await harness.Coordinator(storage: constrained)
            .PreviewAsync(
                harness.Manifest,
                harness.Candidate.ArtifactId,
                harness.SetupPlan,
                harness.SetupPreview,
                harness.Rights);
        Assert.Equal(ArtifactAcquisitionFailure.FreeSpaceInsufficient, preview.Failure);

        var otherRoot = Path.Combine(harness.Root, "other");
        Directory.CreateDirectory(otherRoot);
        var otherStorage = new LocalArtifactAcquisitionStorage(
            otherRoot,
            harness.DirectoryCommitter);
        var other = await harness.Coordinator(storage: otherStorage)
                .PreviewAsync(
                    harness.Manifest,
                    harness.Candidate.ArtifactId,
                    harness.SetupPlan,
                    harness.SetupPreview,
                    harness.Rights);
        Assert.True(other.CanApprove);
        Assert.Equal(otherRoot, other.Plan.ArtifactRootPath);
        Assert.NotEqual(preview.Plan.Fingerprint, other.Plan.Fingerprint);
    }

    [Fact]
    public async Task Expired_setup_facts_block_a_new_acquisition_preview()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        harness.Clock.UtcNow = harness.SetupPlan.ExpiresAtUtc;

        var preview = await harness.Coordinator().PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);

        Assert.Equal(ArtifactAcquisitionPreviewState.Blocked, preview.State);
        Assert.Equal(ArtifactAcquisitionFailure.PlanStale, preview.Failure);
        Assert.False(preview.CanApprove);
        Assert.Equal(0, harness.Handler.Calls);
    }

    [Fact]
    public async Task Missing_content_pin_is_blocked_even_with_explicit_fixture_rights()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Martlet.Host.Setup.MissingPin.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = ArtifactManifestReader.Read(
                await File.ReadAllBytesAsync(Path.Combine(
                    AppContext.BaseDirectory,
                    "host-artifacts.v1.json")));
            var candidate = manifest.DescribeArtifact("f5-v1-vocabulary");
            var clock = new MutableTimeProvider(SetupTestData.Now);
            var setupPlan = AcquisitionTestData.BoundPlan(manifest, clock.UtcNow, SetupRole.Tts);
            var setupFileSystem = new FakeSetupFileSystem(
                Path.Combine(root, "setup-journal-v1.json"));
            var setupCoordinator = new SetupCoordinator(setupFileSystem, clock);
            var review = await setupCoordinator.PreviewReviewAsync(setupPlan);
            Assert.True((await setupCoordinator.RecordReviewAsync(setupPlan,
                review.Approve(SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]))).ReviewRecorded);
            var setupPreview = await setupCoordinator.PreviewReviewAsync(setupPlan);
            var rights = new ArtifactRightsReview(
                candidate,
                "fixture-rights-v1",
                Convert.ToHexStringLower(SHA256.HashData("fixture rights"u8)))
                .Authorize(ArtifactRightsDecision.Approve);
            var storage = new LocalArtifactAcquisitionStorage(
                root,
                new RecordingDirectoryCommitter());
            var coordinator = new ArtifactAcquisitionCoordinator(
                setupFileSystem,
                storage,
                clock: clock,
                freeSpaceReserveBytes: 0);

            var preview = await coordinator.PreviewAsync(
                manifest,
                candidate.ArtifactId,
                setupPlan,
                setupPreview,
                rights);

            Assert.Equal(
                ArtifactAcquisitionFailure.ContentPinMissing,
                preview.Failure);
            Assert.False(preview.CanApprove);
            Assert.False(File.Exists(preview.Plan.PartialPath));
            Assert.False(File.Exists(preview.Plan.DestinationPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Artifact_outside_selected_role_is_refused_before_storage_or_network()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var unselected = harness.Manifest.DescribeArtifact("f5-v1-weights");
        var rights = new ArtifactRightsReview(
            unselected,
            "fixture-rights-v1",
            harness.Rights.EvidenceSha256)
            .Authorize(ArtifactRightsDecision.Approve);

        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(
            async () => await harness.Coordinator().PreviewAsync(
                harness.Manifest,
                unselected.ArtifactId,
                harness.SetupPlan,
                harness.SetupPreview,
                rights));

        Assert.Equal(ArtifactAcquisitionFailure.ArtifactNotSelected, error.Failure);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.Equal("setup-journal-v2.json", Path.GetFileName(Assert.Single(Directory.GetFiles(harness.Root))));
    }

    [Fact]
    public async Task Unknown_component_terms_are_not_approved_by_an_aggregate_evidence_hash()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Martlet.Host.Setup.DirectSource.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = ArtifactManifestReader.Read(
                await File.ReadAllBytesAsync(Path.Combine(
                    AppContext.BaseDirectory,
                    "host-artifacts.v1.json")));
            var candidate = manifest.DescribeArtifact("ollama-linux-amd64");
            Assert.False(candidate.DirectTransportEligible);
            var clock = new MutableTimeProvider(SetupTestData.Now);
            var setupPlan = AcquisitionTestData.BoundPlan(manifest, clock.UtcNow);
            var setupFileSystem = new FakeSetupFileSystem(
                Path.Combine(root, "setup-journal-v1.json"));
            var setupCoordinator = new SetupCoordinator(setupFileSystem, clock);
            var review = await setupCoordinator.PreviewReviewAsync(setupPlan);
            Assert.True((await setupCoordinator.RecordReviewAsync(setupPlan,
                review.Approve(SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]))).ReviewRecorded);
            var setupPreview = await setupCoordinator.PreviewReviewAsync(setupPlan);
            var rights = new ArtifactRightsReview(
                candidate,
                "fixture-rights-v1",
                Convert.ToHexStringLower(SHA256.HashData("fixture rights"u8)))
                .Authorize(ArtifactRightsDecision.Approve);
            var storage = new LocalArtifactAcquisitionStorage(
                root,
                new RecordingDirectoryCommitter());
            var coordinator = new ArtifactAcquisitionCoordinator(
                setupFileSystem,
                storage,
                new RefusingArtifactDownloadTransport(),
                clock: clock,
                freeSpaceReserveBytes: 0);

            var preview = await coordinator.PreviewAsync(
                manifest,
                candidate.ArtifactId,
                setupPlan,
                setupPreview,
                rights);

            Assert.Equal(
                ArtifactAcquisitionFailure.RightsNotApproved,
                preview.Failure);
            Assert.False(preview.CanApprove);
            Assert.False(File.Exists(preview.Plan.JournalPath));
            Assert.False(File.Exists(preview.Plan.PartialPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Supplied_provenance_is_not_permission_and_preview_remains_passive()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        Assert.Equal(
            "synthetic_fixture",
            harness.Candidate.InventoryProvenance);
        using var transport =
            new HttpsArtifactDownloadTransport(harness.Handler);
        var coordinator = new ArtifactAcquisitionCoordinator(
            harness.SetupFileSystem,
            harness.Storage,
            transport,
            clock: harness.Clock,
            freeSpaceReserveBytes: 0);

        var preview = await coordinator.PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);

        Assert.Null(preview.Failure);
        Assert.True(preview.CanApprove);
        var refused = await coordinator.RunAsync(preview.Plan, preview.Approve());
        Assert.Equal(ArtifactAcquisitionFailure.ConsentRequired, refused.Failure);
        Assert.False(harness.SetupPlan.ExecutionAuthorized);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.False(File.Exists(preview.Plan.JournalPath));
    }
}

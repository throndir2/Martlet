using System.Security.Cryptography;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionBoundaryTests
{
    [Fact]
    public async Task Public_review_is_not_download_approval_and_retains_external_unknowns()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var before = await File.ReadAllBytesAsync(harness.SetupFileSystem.JournalPath);
        using var coordinator = new ArtifactAcquisitionCoordinator(harness.SetupFileSystem, harness.Storage, harness.Clock);
        var preview = await coordinator.PreviewAsync(harness.Manifest, harness.Candidate.ArtifactId,
            harness.SetupPlan, harness.SetupPreview, harness.Rights);
        Assert.True(preview.CanApprove);
        var refused = await coordinator.RunAsync(preview.Plan, preview.Approve());
        Assert.Equal(ArtifactAcquisitionFailure.ConsentRequired, refused.Failure);
        Assert.False(File.Exists(preview.Plan.JournalPath));
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(harness.SetupFileSystem.JournalPath));
        Assert.False(harness.SetupPlan.ExecutionAuthorized);
        Assert.All(harness.SetupPreview.Steps, step => Assert.Equal(SetupObservationState.Unknown, step.Observation));
    }

    [Fact]
    public async Task Rights_claims_require_bounded_exact_per_claim_declarations()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var selection = harness.Manifest.DescribeAcquisition(["ollama-llm"], "ubuntu-24.04-x64");
        var claim = Assert.Single(harness.Candidate.Licenses);
        var hash = Convert.ToHexStringLower(SHA256.HashData("rights terms"u8));
        Assert.False(new ArtifactRightsReview(selection, harness.Candidate.ArtifactId, "review", hash,
            clock: harness.Clock).Authorize(ArtifactRightsDecision.Approve).IsApproved);
        var terms = new ArtifactReviewedLicense(claim.Id, "Explicit authored bytes permission", hash);
        Assert.Throws<ArtifactAcquisitionException>(() =>
            new ArtifactRightsReview(selection, harness.Candidate.ArtifactId, "review", hash, [terms, terms]));
        Assert.Throws<ArtifactAcquisitionException>(() =>
            new ArtifactRightsReview(selection, harness.Candidate.ArtifactId, "review", hash,
                [new ArtifactReviewedLicense("foreign-claim", "Explicit permission", hash)]));
        Assert.Throws<ArtifactAcquisitionException>(() => new ArtifactReviewedLicense(claim.Id, "unknown", hash));
        Assert.True(new ArtifactRightsReview(selection, harness.Candidate.ArtifactId, "review", hash,
            [terms], harness.Clock).Authorize(ArtifactRightsDecision.Approve).IsApproved);
        foreach (var placeholder in new[] { " ", "unknown", "UNREVIEWED", "pending", "n/a" })
            Assert.Throws<ArtifactAcquisitionException>(() => new ArtifactReviewedLicense(claim.Id, placeholder, hash));
    }

    [Fact]
    public async Task Rights_from_another_role_selection_do_not_authorize_the_same_artifact()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var selection = harness.Manifest.DescribeAcquisition(["ollama-llm", "f5-tts"], "ubuntu-24.04-x64");
        var hash = Convert.ToHexStringLower(SHA256.HashData("authored claim terms"u8));
        var rights = new ArtifactRightsReview(selection, harness.Candidate.ArtifactId, "rights-v1", hash,
            harness.Candidate.Licenses.Select(claim => new ArtifactReviewedLicense(
                claim.Id, "Authored inert copying permission", hash)), harness.Clock).Authorize(ArtifactRightsDecision.Approve);
        var preview = await harness.Coordinator().PreviewAsync(harness.Manifest, harness.Candidate.ArtifactId,
            harness.SetupPlan, harness.SetupPreview, rights);
        Assert.Equal(ArtifactAcquisitionFailure.RightsNotApproved, preview.Failure);
        Assert.Equal(0, harness.Handler.Calls);
    }

    [Fact]
    public async Task Old_or_cross_purpose_acquisition_journal_is_preserved()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var coordinator = harness.Coordinator();
        var (preview, _) = await harness.ApproveAsync(coordinator);
        var document = ArtifactAcquisitionJournalDocument.Create(preview.Plan, harness.Clock.UtcNow) with { Revision = 1 };
        var json = System.Text.Encoding.UTF8.GetString(ArtifactAcquisitionJournalCodec.Write(document));
        foreach (var invalid in new[]
        {
            json.Replace("\"formatVersion\":2", "\"formatVersion\":1", StringComparison.Ordinal),
            json.Replace("\"purpose\":\"ArtifactAcquisition\"", "\"purpose\":\"LocalReview\"", StringComparison.Ordinal)
        })
        {
            await File.WriteAllTextAsync(preview.Plan.JournalPath, invalid);
            var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
                await coordinator.PreviewAsync(harness.Manifest, harness.Candidate.ArtifactId,
                    harness.SetupPlan, harness.SetupPreview, harness.Rights));
            Assert.Equal(ArtifactAcquisitionFailure.JournalCorrupt, error.Failure);
            Assert.Equal(invalid, await File.ReadAllTextAsync(preview.Plan.JournalPath));
            Assert.Equal(0, harness.Handler.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_review_or_expired_consent_between_API_and_CDN_prevents_second_request(bool expire)
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var coordinator = harness.Coordinator();
        var (preview, approval) = await harness.ApproveAsync(coordinator);
        harness.Handler.Respond = (request, _) =>
        {
            if (expire) harness.Clock.UtcNow = preview.Plan.ExpiresAtUtc;
            else harness.SetupFileSystem.AppendWhitespace();
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            };
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/test?secret=PRIVATE-HOP-CANARY");
            return Task.FromResult(response);
        };
        var result = await coordinator.RunAsync(preview.Plan, approval);
        Assert.Equal(expire ? ArtifactAcquisitionFailure.PlanStale : ArtifactAcquisitionFailure.SetupJournalChanged,
            result.Failure);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.False(File.Exists(preview.Plan.PartialPath));
        Assert.False(File.Exists(preview.Plan.DestinationPath));
        Assert.DoesNotContain("PRIVATE-HOP-CANARY", await File.ReadAllTextAsync(preview.Plan.JournalPath));
    }
}

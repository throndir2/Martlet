using System.Text;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionJournalAndRemedyTests
{
    [Fact]
    public void Every_failure_has_a_unique_stable_sanitized_remedy()
    {
        var remedies = Enum.GetValues<ArtifactAcquisitionFailure>()
            .Select(ArtifactAcquisitionRemedies.For)
            .ToArray();

        Assert.Equal(remedies.Length, remedies.Select(value => value.Code).Distinct().Count());
        Assert.All(remedies, remedy =>
        {
            Assert.StartsWith("ACQUIRE_", remedy.Code, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE", remedy.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE", remedy.Instruction, StringComparison.Ordinal);
            Assert.NotEmpty(remedy.Summary);
            Assert.NotEmpty(remedy.Instruction);
        });
    }

    [Fact]
    public async Task Journal_rejects_unknown_duplicate_null_enum_and_integrity_mutations()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var preview = await harness.Coordinator().PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);
        var document = ArtifactAcquisitionJournalDocument.Create(
            preview.Plan,
            harness.Clock.UtcNow) with
        {
            Revision = 1
        };
        var valid = Encoding.UTF8.GetString(
            ArtifactAcquisitionJournalCodec.Write(document));
        var variants = new[]
        {
            valid[..^1] + ",\"futureField\":true}",
            "{\"formatVersion\":1," + valid[1..],
            valid.Replace(
                "\"state\":\"Prepared\"",
                "\"state\":0",
                StringComparison.Ordinal),
            valid.Replace(
                "\"state\":\"Prepared\"",
                "\"state\":\"prepared\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"artifactId\":\"ollama-linux-amd64\"",
                "\"artifactId\":null",
                StringComparison.Ordinal),
            valid.Replace(
                "\"planFingerprint\":\"",
                "\"planFingerprint\":\"0",
                StringComparison.Ordinal)
        };

        foreach (var variant in variants)
        {
            var error = Assert.Throws<ArtifactAcquisitionException>(
                () => ArtifactAcquisitionJournalCodec.Read(
                    Encoding.UTF8.GetBytes(variant)));
            Assert.Equal(ArtifactAcquisitionFailure.JournalCorrupt, error.Failure);
            Assert.DoesNotContain("ollama-linux-amd64", error.Message);
        }
    }

    [Fact]
    public async Task Journal_state_invariants_reject_success_shaped_failure_or_unverified_final()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var preview = await harness.Coordinator().PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);
        var prepared = ArtifactAcquisitionJournalDocument.Create(
            preview.Plan,
            harness.Clock.UtcNow) with
        {
            Revision = 1
        };
        var invalidDocuments = new[]
        {
            prepared with
            {
                State = ArtifactAcquisitionJournalState.Failed,
                LastFailure = null
            },
            prepared with
            {
                State = ArtifactAcquisitionJournalState.Finalized,
                PersistedBytes = harness.Candidate.ExpectedBytes,
                FinalOwned = true,
                ComputedSha256 = harness.Candidate.ExpectedSha256
            },
            prepared with
            {
                State = ArtifactAcquisitionJournalState.Verified,
                PersistedBytes = harness.Candidate.ExpectedBytes,
                PartialOwned = true,
                ComputedSha256 = new string('0', 64)
            }
        };

        foreach (var invalid in invalidDocuments)
        {
            var error = Assert.Throws<ArtifactAcquisitionException>(
                () => ArtifactAcquisitionJournalCodec.Write(invalid));
            Assert.Equal(ArtifactAcquisitionFailure.JournalCorrupt, error.Failure);
        }
    }

    [Fact]
    public async Task Journal_revision_domain_covers_maximum_artifact_checkpoint_count()
    {
        await using var harness = await AcquisitionHarness.CreateAsync();
        var preview = await harness.Coordinator().PreviewAsync(
            harness.Manifest,
            harness.Candidate.ArtifactId,
            harness.SetupPlan,
            harness.SetupPreview,
            harness.Rights);
        var maximumCheckpoints =
            17_592_186_044_416L / (8L * 1024 * 1024);
        var document = ArtifactAcquisitionJournalDocument.Create(
            preview.Plan,
            harness.Clock.UtcNow) with
        {
            Revision = maximumCheckpoints + 32
        };

        var bytes = ArtifactAcquisitionJournalCodec.Write(document);
        var roundTrip = ArtifactAcquisitionJournalCodec.Read(bytes);

        Assert.Equal(maximumCheckpoints + 32, roundTrip.Revision);
    }
}

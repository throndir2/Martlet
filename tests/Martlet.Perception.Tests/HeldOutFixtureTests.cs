using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Perception;

namespace Martlet.Perception.Tests;

public sealed class HeldOutFixtureTests
{
    [Fact]
    public async Task Held_out_tasks_prove_bounded_usefulness_shape_only()
    {
        var document = Load();
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal("synthetic-shape-only-not-ai", document.FixtureProvenance);
        Assert.Equal(4, document.Tasks.Count);
        Assert.Equal(document.Tasks.Count,
            document.Tasks.Select(task => task.FixtureId)
                .Distinct(StringComparer.Ordinal).Count());

        var epoch = 100L;
        foreach (var fixture in document.Tasks)
        {
            var role = ParseRole(fixture.Role);
            Assert.InRange(
                fixture.MaximumAgeMs,
                1,
                checked((int)PerceptionProtocol.MaximumFrameAge.TotalMilliseconds));
            Assert.InRange(fixture.Width, 1, PerceptionProtocol.MaximumImageWidth);
            Assert.InRange(fixture.Height, 1, PerceptionProtocol.MaximumImageHeight);
            var worker = DeterministicPerceptionFixtureIdentity.Create(role);
            var (adapter, _) = PerceptionTestData.Adapter(worker);
            await using var scheduler = new PerceptionFreshnessScheduler(
                [adapter],
                new()
                {
                    Budget = new()
                    {
                        MaximumCpuUnits = worker.Resources.CpuUnits,
                        MaximumGpuMemoryMiB = worker.Resources.GpuMemoryMiB,
                        MaximumConcurrency = 1
                    }
                },
                PerceptionTestData.Clock);
            var content = SelectedWindowFrameContent.FromInlinePng(
                PerceptionTestData.Png(fixture.Width, fixture.Height));
            var request = PerceptionTestData.Intent(
                role,
                epoch: epoch++,
                worker: worker,
                maximumFrameAge: TimeSpan.FromMilliseconds(
                    fixture.MaximumAgeMs),
                content: content,
                question: fixture.Question ?? "OCR has no question.");

            var result = await scheduler.ScheduleAsync(
                request, PerceptionTestData.Authorize(request));

            Assert.Equal(PerceptionJobOutcome.Completed, result.Outcome);
            Assert.Equal(PerceptionEvidenceKind.SyntheticFixture,
                result.Observation?.Provenance.Evidence);
            Assert.True(result.Observation?.ExpiresAtUtc >
                result.Observation?.Provenance.CapturedAtUtc);
            if (fixture.ExpectedOutput == "ocr_regions")
            {
                Assert.Null(fixture.Question);
                Assert.NotNull(result.Observation?.Ocr);
                Assert.True(result.Observation.Ocr.Regions.Count >=
                    fixture.MinimumItems);
            }
            else
            {
                Assert.Equal("answer_with_uncertainty", fixture.ExpectedOutput);
                Assert.False(string.IsNullOrWhiteSpace(fixture.Question));
                Assert.NotNull(result.Observation?.Vlm);
                Assert.False(string.IsNullOrWhiteSpace(
                    result.Observation.Vlm.Answer));
                Assert.False(string.IsNullOrWhiteSpace(
                    result.Observation.Vlm.Uncertainty));
            }
        }
    }

    [Fact]
    public async Task Held_out_scheduler_cases_have_no_backlog_shape()
    {
        var document = Load();
        Assert.Equal(2, document.SchedulerCases.Count);
        foreach (var fixture in document.SchedulerCases)
        {
            var role = ParseRole(fixture.Role);
            Assert.True(fixture.NewerEpoch > fixture.OlderEpoch);
            var worker = DeterministicPerceptionFixtureIdentity.Create(role);
            var (adapter, transport) = PerceptionTestData.Adapter(worker);
            await using var scheduler = new PerceptionFreshnessScheduler(
                [adapter],
                new()
                {
                    Budget = new()
                    {
                        MaximumCpuUnits = 1,
                        MaximumGpuMemoryMiB = 0,
                        MaximumConcurrency = 1
                    }
                },
                PerceptionTestData.Clock);
            var newest = PerceptionTestData.Intent(
                role, fixture.NewerEpoch, worker);

            var newestResult = await scheduler.ScheduleAsync(
                newest, PerceptionTestData.Authorize(newest));
            var older = PerceptionTestData.Intent(
                role, fixture.OlderEpoch, worker);
            var olderResult = await scheduler.ScheduleAsync(
                older, PerceptionTestData.Authorize(older));

            Assert.Equal(PerceptionJobOutcome.Completed, newestResult.Outcome);
            Assert.Equal(PerceptionWorkerFailure.LateEpoch, olderResult.Failure);
            Assert.Single(transport.Requests);
            Assert.Contains(fixture.ExpectedOutcome,
                new[] { "latest_only", "late_rejected" });
        }
    }

    private static HeldOutDocument Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "perception-held-out.json");
        var bytes = File.ReadAllBytes(path);
        Assert.InRange(bytes.Length, 1, 32 * 1024);
        return JsonSerializer.Deserialize<HeldOutDocument>(bytes, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Held-out perception fixture is empty.");
    }

    private static PerceptionRole ParseRole(string role) => role switch
    {
        "ocr" => PerceptionRole.Ocr,
        "vlm" => PerceptionRole.VisualQuestionAnswering,
        _ => throw new InvalidDataException("Held-out perception fixture has an unknown role.")
    };

    private sealed record HeldOutDocument
    {
        public required int SchemaVersion { get; init; }
        public required string FixtureProvenance { get; init; }
        public required IReadOnlyList<HeldOutTask> Tasks { get; init; }
        public required IReadOnlyList<HeldOutSchedulerCase> SchedulerCases { get; init; }
    }

    private sealed record HeldOutTask
    {
        public required string FixtureId { get; init; }
        public required string Role { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public string? Question { get; init; }
        public required string ExpectedOutput { get; init; }
        public required int MinimumItems { get; init; }
        public required int MaximumAgeMs { get; init; }
    }

    private sealed record HeldOutSchedulerCase
    {
        public required string FixtureId { get; init; }
        public required string Role { get; init; }
        public required long OlderEpoch { get; init; }
        public required long NewerEpoch { get; init; }
        public required string ExpectedOutcome { get; init; }
    }
}

using System.Text;
using System.Text.Json.Nodes;

namespace Martlet.Host.Setup.Tests;

public sealed class JournalTests
{
    [Fact]
    public async Task Invalid_json_is_refused_and_never_overwritten()
    {
        var fileSystem = new FakeSetupFileSystem();
        fileSystem.SetRaw("{not-json"u8);
        var coordinator = new SetupCoordinator(fileSystem, new FakeStateProbe(),
            clock: new MutableTimeProvider(SetupTestData.Now));

        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.PreviewAsync(SetupTestData.ReviewedPlan()));

        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        Assert.Equal(0, fileSystem.WriteAttempts);
    }

    [Fact]
    public async Task Integrity_mismatch_is_refused_and_never_overwritten()
    {
        var (fileSystem, coordinator, plan) = await CompletedJournalAsync();
        var text = Encoding.UTF8.GetString(fileSystem.Content!);
        text = text.Replace("\"planId\":\"reviewed-plan-v1\"", "\"planId\":\"reviewed-plan-v2\"",
            StringComparison.Ordinal);
        fileSystem.SetRaw(Encoding.UTF8.GetBytes(text));
        var writesBefore = fileSystem.WriteAttempts;

        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.PreviewAsync(plan));

        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        Assert.Equal(writesBefore, fileSystem.WriteAttempts);
    }

    [Fact]
    public async Task Duplicate_property_is_refused_before_deserialization()
    {
        var (fileSystem, coordinator, plan) = await CompletedJournalAsync();
        var text = Encoding.UTF8.GetString(fileSystem.Content!);
        fileSystem.SetRaw(Encoding.UTF8.GetBytes("{\"formatVersion\":1," + text[1..]));

        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.PreviewAsync(plan));

        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
    }

    [Fact]
    public async Task Unknown_property_is_refused()
    {
        var (fileSystem, coordinator, plan) = await CompletedJournalAsync();
        var text = Encoding.UTF8.GetString(fileSystem.Content!);
        fileSystem.SetRaw(Encoding.UTF8.GetBytes(text[..^1] + ",\"futureField\":true}"));

        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.PreviewAsync(plan));

        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
    }

    [Fact]
    public async Task Explicit_null_reference_fields_are_sanitized_as_corruption()
    {
        var (fileSystem, coordinator, plan) = await CompletedJournalAsync();
        var original = JsonNode.Parse(fileSystem.Content!)!.AsObject();
        var variants = new List<JsonObject>();
        foreach (var field in new[] { "planId", "integritySha256", "desiredStateFingerprint" })
        {
            var variant = JsonNode.Parse(original.ToJsonString())!.AsObject();
            variant[field] = null;
            variants.Add(variant);
        }
        var nullStep = JsonNode.Parse(original.ToJsonString())!.AsObject();
        nullStep["steps"]!.AsArray()[0] = null;
        variants.Add(nullStep);

        foreach (var variant in variants)
        {
            fileSystem.SetRaw(Encoding.UTF8.GetBytes(variant.ToJsonString()));
            var error = await Assert.ThrowsAsync<SetupException>(
                async () => await coordinator.PreviewAsync(plan));
            Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        }
    }

    [Fact]
    public async Task Oversized_journal_is_preserved_and_refused()
    {
        var fileSystem = new FakeSetupFileSystem();
        fileSystem.SetRaw(new byte[SetupJournalCodec.MaximumBytes + 1]);
        var coordinator = new SetupCoordinator(fileSystem, new FakeStateProbe(),
            clock: new MutableTimeProvider(SetupTestData.Now));

        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.PreviewAsync(SetupTestData.ReviewedPlan()));

        Assert.Equal(SetupFailure.JournalTooLarge, error.Failure);
        Assert.Equal(0, fileSystem.WriteAttempts);
    }

    [Fact]
    public async Task Concurrent_change_after_preview_refuses_without_overwrite()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var initialApproval = await SetupTestData.ApprovalAsync(coordinator, plan);
        executor.Enqueue("/test/step-one", SetupExecutionResult.Canceled());
        _ = await coordinator.RunAsync(plan, initialApproval);

        var preview = await coordinator.PreviewAsync(plan);
        var approval = preview.Approve(SetupApprovalDecision.Approve,
            preview.RequiredConsentScopes, SetupPrivilege.Administrator);
        fileSystem.AppendWhitespace();
        var writesBefore = fileSystem.WriteAttempts;
        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupFailure.JournalConcurrentChange, result.Failure);
        Assert.Equal(writesBefore, fileSystem.WriteAttempts);
    }

    [Fact]
    public void Codec_rejects_numeric_or_case_aliased_enums()
    {
        var plan = SetupTestData.ReviewedPlan();
        var approval = SetupApproval.Allowed(plan, @"C:\inert-h05a\setup-journal-v1.json",
            null, false, plan.RequiredConsentScopes, SetupPrivilege.Administrator);
        var document = SetupJournalDocument.Create(plan, approval, SetupTestData.Now) with
        {
            Revision = 1,
            IntegritySha256 = ""
        };
        var valid = Encoding.UTF8.GetString(SetupJournalCodec.Write(document));
        foreach (var replacement in new[] { "0", "\"running\"" })
        {
            var invalid = valid.Replace("\"state\":\"Approved\"", "\"state\":" + replacement,
                StringComparison.Ordinal);
            var error = Assert.Throws<SetupException>(() =>
                SetupJournalCodec.Read(Encoding.UTF8.GetBytes(invalid)));
            Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        }
    }

    private static async Task<(FakeSetupFileSystem FileSystem, SetupCoordinator Coordinator, SetupPlan Plan)>
        CompletedJournalAsync()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);
        Assert.Equal(SetupRunState.Completed, (await coordinator.RunAsync(plan, approval)).State);
        return (fileSystem, coordinator, plan);
    }
}

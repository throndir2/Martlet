using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Support.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public async Task ActualKnownFailureToJournalToPreviewToConsentedZip()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var config = Path.Combine(scope.Root, "authored fixture config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"), "{not valid authored fixture settings}");
        var executor = new ProbeExecutor(ProbeRegistry.Local(new SettingsStore(config)), clock);
        var report = await executor.RunAsync(["settings.load"]);
        Assert.Equal("settings.malformed", Assert.Single(report.Probes).DiagnosticCode);
        Assert.Equal(3, report.ExitCode);
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        journal.Append(DiagnosticEvent.FromProbe(report.Probes[0], clock.Utc));
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), report, BuildMetadata.FromExecutingAssemblies(),
            journal.Select(Fixtures.Range(clock)), clock);
        Assert.False(File.Exists(scope.Output));
        var approvedBytes = snapshot.Files.ToDictionary(f => f.Name, f => snapshot.Preview(f.Name));
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        var receipt = snapshot.Export(consent, scope.Output);
        Assert.Equal(snapshot.Digest, receipt.Digest);
        Assert.Equal(new FileInfo(scope.Output).Length, receipt.ZipBytes);
        using var zip = ZipFile.OpenRead(scope.Output);
        Assert.Equal(new[] { "build.json", "settings.json", "doctor.json", "events.json", "manifest.json" },
            zip.Entries.Select(e => e.FullName));
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            var bytes = output.ToArray();
            Assert.Equal(approvedBytes[entry.FullName], bytes);
            var info = snapshot.Files.Single(f => f.Name == entry.FullName);
            Assert.Equal(info.Bytes, bytes.Length);
            Assert.Equal(info.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.DoesNotContain(scope.Root, Encoding.UTF8.GetString(bytes));
        }
        using var manifest = JsonDocument.Parse(approvedBytes["manifest.json"]);
        Assert.Equal(4, manifest.RootElement.GetProperty("files").GetArrayLength());
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            Assert.Equal(approvedBytes[name].Length, item.GetProperty("bytes").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(approvedBytes[name])), item.GetProperty("sha256").GetString());
        }
        var doctor = Encoding.UTF8.GetString(approvedBytes["doctor.json"]);
        Assert.Contains("settings.malformed", doctor);
        Assert.Contains(DiagnosticCatalog.Remedy("settings.restore").Guidance, doctor);
    }

    [Fact]
    public void AllowlistDropsRawTextAndUsesCoherentFreshPseudonyms()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var first = Fixtures.Snapshot(scope, clock);
        var all = string.Join("\n", first.Files.Select(f => Encoding.UTF8.GetString(first.Preview(f.Name))));
        foreach (var secret in new[] { Fixtures.Canary, "Joanna", "203.0.113.7", "DO-NOT-EXPORT", scope.Root })
            Assert.DoesNotContain(secret, all);
        using var report = JsonDocument.Parse(first.Preview("doctor.json"));
        using var events = JsonDocument.Parse(first.Preview("events.json"));
        Assert.Equal(report.RootElement.GetProperty("probes")[0].GetProperty("trace_id").GetGuid(),
            events.RootElement[0].GetProperty("event").GetProperty("trace_id").GetGuid());
        var raw = DiagnosticJournal.ReadClosed(scope.Journal, Fixtures.Range(clock)).Records[0].Event.TraceId;
        Assert.NotEqual(raw, events.RootElement[0].GetProperty("event").GetProperty("trace_id").GetGuid());
        using var second = SupportSnapshot.Freeze(Fixtures.Settings(), Fixtures.Report(clock, raw),
            BuildMetadata.FromExecutingAssemblies(), DiagnosticJournal.ReadClosed(scope.Journal, Fixtures.Range(clock)), clock);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Digest, second.Digest);
        Assert.NotEqual(first.Preview("events.json"), second.Preview("events.json"));
    }

    [Fact]
    public void ChangesAfterPreviewCannotAlterFrozenBytesAndPreviewCopiesAreSafe()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var probes = Fixtures.Report(clock).Probes.ToList();
        var source = Fixtures.Report(clock) with { Probes = probes };
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        journal.Append(Fixtures.Event(clock));
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), source, BuildMetadata.FromExecutingAssemblies(),
            journal.Select(Fixtures.Range(clock)), clock);
        var frozen = snapshot.Preview("doctor.json");
        probes.Clear();
        journal.Append(Fixtures.Event(clock));
        var copy = snapshot.Preview("doctor.json");
        Array.Fill(copy, (byte)'X');
        Assert.Equal(frozen, snapshot.Preview("doctor.json"));
        using var logs = JsonDocument.Parse(snapshot.Preview("events.json"));
        Assert.Equal(1, logs.RootElement.GetArrayLength());
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        snapshot.Export(consent, scope.Output);
        using var zip = ZipFile.OpenRead(scope.Output);
        using var reader = new StreamReader(zip.GetEntry("doctor.json")!.Open());
        Assert.Equal(Encoding.UTF8.GetString(frozen), reader.ReadToEnd());
    }

    [Fact]
    public void WrongReplayedAndDestinationConsentCannotExport()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var snapshot = Fixtures.Snapshot(scope, clock);
        Fixtures.Failure(SupportFailure.ConsentMismatch, () => snapshot.Approve(Guid.NewGuid(), snapshot.Digest, scope.Output));
        Fixtures.Failure(SupportFailure.ConsentMismatch, () => snapshot.Approve(snapshot.Id, "wrong", scope.Output));
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        var elsewhere = Path.Combine(scope.Root, "elsewhere.zip");
        Fixtures.Failure(SupportFailure.ConsentMismatch, () => snapshot.Export(consent, elsewhere));
        using var secondScope = new TestScope();
        using var second = Fixtures.Snapshot(secondScope, clock);
        Fixtures.Failure(SupportFailure.ConsentMismatch, () => second.Export(consent, scope.Output));
        snapshot.Export(consent, scope.Output);
        Fixtures.Failure(SupportFailure.ConsentConsumed, () => snapshot.Export(consent, scope.Output));
        Assert.False(File.Exists(elsewhere));
        Assert.DoesNotContain(scope.Output, consent.ToString());
    }

    [Fact]
    public void ExistingOutputAtApprovalOrAfterApprovalIsPreserved()
    {
        using var scope = new TestScope();
        using var snapshot = Fixtures.Snapshot(scope, new());
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        File.WriteAllText(scope.Output, Fixtures.Canary);
        Fixtures.Failure(SupportFailure.DestinationExists, () => snapshot.Export(consent, scope.Output));
        Fixtures.Failure(SupportFailure.DestinationExists, () => snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output));
        Assert.Equal(Fixtures.Canary, File.ReadAllText(scope.Output));
    }

    [Fact]
    public void FailedFinalMoveCannotClaimSuccessAndCannotOverwriteRacingFile()
    {
        using var scope = new TestScope();
        using var snapshot = Fixtures.Snapshot(scope, new());
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        var fs = new FaultFileSystem { BeforeMove = (_, _) => File.WriteAllText(scope.Output, Fixtures.Canary) };
        Fixtures.Failure(SupportFailure.IoFailure, () => snapshot.Export(consent, scope.Output, fs));
        Assert.Equal(Fixtures.Canary, File.ReadAllText(scope.Output));
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
        Fixtures.Failure(SupportFailure.ConsentConsumed, () => snapshot.Export(consent, scope.Output));
    }

    [Fact]
    public void CancelAndTimeoutAfterNonCooperativeWriteAreObservedBeforeFinalization()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var snapshot = Fixtures.Snapshot(scope, clock);
        using var canceled = new CancellationTokenSource();
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        var fs = new FaultFileSystem { BeforeWrite = _ => canceled.Cancel() };
        Fixtures.Failure(SupportFailure.Canceled, () => snapshot.Export(consent, scope.Output, fs, canceled.Token));
        Assert.False(File.Exists(scope.Output));
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
        consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        fs.BeforeWrite = _ => clock.Advance(TimeSpan.FromSeconds(2));
        Fixtures.Failure(SupportFailure.DeadlineExceeded, () => snapshot.Export(consent, scope.Output, fs, timeout: TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(scope.Output));
    }

    [Fact]
    public async Task OriginalCancellationIsCheckedEvenWhenCallbackDeliveryIsBlocked()
    {
        using var scope = new TestScope();
        using var snapshot = Fixtures.Snapshot(scope, new());
        using var source = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = source.Token.Register(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        var canceling = Task.Run(source.Cancel);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(canceling.IsCompleted);
            Fixtures.Failure(SupportFailure.Canceled, () => snapshot.Export(
                snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output, source.Token));
            Fixtures.Failure(SupportFailure.Canceled, () => DiagnosticJournal.Start(Path.Combine(scope.Root, "never-created"),
                cancellationToken: source.Token));
            Assert.False(File.Exists(scope.Output));
        }
        finally { release.Set(); }
        await canceling;
    }

    [Fact]
    public void FlushCloseAndCleanupFailuresAreVisibleAndOwnedPartialIsPreserved()
    {
        using var scope = new TestScope();
        using var snapshot = Fixtures.Snapshot(scope, new());
        var fs = new FaultFileSystem { BeforeFlush = _ => throw new IOException(Fixtures.Canary) };
        Fixtures.Failure(SupportFailure.IoFailure, () => snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output, fs));
        Assert.False(File.Exists(scope.Output));
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
        fs.BeforeDelete = _ => throw new UnauthorizedAccessException(Fixtures.Canary);
        Fixtures.Failure(SupportFailure.AccessDenied, () => snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output, fs));
        Assert.Single(Directory.GetFiles(scope.Root, "*.partial"));
        Assert.True(snapshot.HasPendingCleanup);
        Fixtures.Failure(SupportFailure.CleanupPending, () => snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output));
        fs.BeforeFlush = null; fs.BeforeDelete = null;
        snapshot.RetryCleanup();
        Assert.False(snapshot.HasPendingCleanup);
        fs.BeforeClose = stream => { stream.Dispose(); throw new IOException(Fixtures.Canary); };
        Fixtures.Failure(SupportFailure.IoFailure, () => snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output, fs));
        Assert.Single(Directory.GetFiles(scope.Root, "*.partial"));
        Assert.True(snapshot.HasPendingCleanup);
        fs.BeforeClose = null;
        snapshot.RetryCleanup();
        Assert.False(snapshot.HasPendingCleanup);
    }

    [Fact]
    public void ReportRecordedStaleAndNotRunStayIncompleteWithoutInventingStatus()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        var report = new DoctorReport
        {
            Version = ContractVersion.Current, ApplicationVersion = "0.1.0", CreatedAt = clock.Utc,
            Probes =
            [
                new()
                {
                    Id = "fixture.session", Stage = Stage.Application, Required = true, Outcome = ProbeOutcome.Passed,
                    Provenance = EvidenceProvenance.Fixture, Freshness = EvidenceFreshness.Stale, ObservedAt = clock.Utc,
                    Summary = Fixtures.Canary, DiagnosticCode = "fixture.passed", ActionId = "pipeline.unavailable", Remedy = Fixtures.Canary
                },
                new()
                {
                    Id = "audio.input", Stage = Stage.Application, Required = false, Outcome = ProbeOutcome.Skipped,
                    Provenance = EvidenceProvenance.NotRun, Freshness = EvidenceFreshness.Unknown, Summary = Fixtures.Canary,
                    DiagnosticCode = "probe.not_run", ActionId = "diagnostics.refresh", Remedy = Fixtures.Canary
                }

            ]
        };
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), report, BuildMetadata.FromExecutingAssemblies(),
            journal.Select(Fixtures.Range(clock)), clock);
        using var json = JsonDocument.Parse(snapshot.Preview("doctor.json"));
        Assert.Equal(report.ExitCode, json.RootElement.GetProperty("shared_doctor_exit_code").GetInt32());
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("stale", json.RootElement.GetProperty("probes")[0].GetProperty("freshness").GetString());
        Assert.Equal("not_run", json.RootElement.GetProperty("probes")[1].GetProperty("provenance").GetString());
    }

    [Fact]
    public async Task ProjectionNeverReexecutesProbesOrCallsNetworkDeviceOrCredentialAdapters()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var localCalls = 0;
        var externalCalls = 0;
        var executor = new ProbeExecutor(new ProbeRegistry(
        [
            new("settings.load", Stage.Settings, true, [ProbeEffect.LocalReadOnly], _ =>
            {
                localCalls++;
                return Task.FromResult(new ProbeObservation("settings.valid", EvidenceProvenance.Live, clock.Utc));
            }),
            new("provider.connection", Stage.Provider, true, [ProbeEffect.Network, ProbeEffect.ProviderCost, ProbeEffect.Permissioned], _ =>
            {
                externalCalls++;
                throw new InvalidOperationException("No network or credential adapter may run.");
            }),
            new("audio.input", Stage.Capture, false, [ProbeEffect.Device, ProbeEffect.Permissioned], _ =>
            {
                externalCalls++;
                throw new InvalidOperationException("No device adapter may run.");
            })
        ]), clock);
        var report = await executor.RunAsync();
        Assert.Equal(1, localCalls);
        Assert.Equal(0, externalCalls);
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), report, BuildMetadata.FromExecutingAssemblies(),
            journal.Select(Fixtures.Range(clock)), clock);
        snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output);
        Assert.Equal(1, localCalls);
        Assert.Equal(0, externalCalls);
    }

    [Fact]
    public async Task CancellationAfterIoWithBlockedCallbacksNeverFinalizes()
    {
        using var scope = new TestScope();
        using var snapshot = Fixtures.Snapshot(scope, new());
        using var source = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = source.Token.Register(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        Task? canceling = null;
        var fs = new FaultFileSystem { BeforeWrite = _ =>
        {
            canceling = Task.Run(source.Cancel);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        } };
        try
        {
            Fixtures.Failure(SupportFailure.Canceled, () => snapshot.Export(
                snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output, fs, source.Token));
            Assert.False(File.Exists(scope.Output));
            Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
        }
        finally { release.Set(); }
        Assert.NotNull(canceling);
        await canceling;
    }

    [Fact]
    public async Task SnapshotCannotDisposeOrExportConcurrentlyWithPendingIo()
    {
        using var scope = new TestScope();
        using var snapshot = Fixtures.Snapshot(scope, new());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var consent = snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output);
        var fs = new FaultFileSystem { BeforeWrite = _ =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        } };
        var exporting = Task.Run(() => snapshot.Export(consent, scope.Output, fs));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Fixtures.Failure(SupportFailure.Busy, snapshot.Dispose);
            Fixtures.Failure(SupportFailure.Busy, () => snapshot.Export(consent, scope.Output));
            Assert.False(File.Exists(scope.Output));
        }
        finally { release.Set(); }
        await exporting;
        Assert.True(File.Exists(scope.Output));
    }

    [Fact]
    public void SettingsAndManifestRejectUnexpectedFieldsAndNeverExportInventoryPaths()
    {
        var settings = Encoding.UTF8.GetString(SupportJson.Write(Fixtures.Settings()));
        Fixtures.Failure(SupportFailure.InvalidData, () => SupportJson.Read<SettingsSummary>(Encoding.UTF8.GetBytes(
            settings.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"raw_config\": \"secret\""))));
        var manifest = """
            {"schemaVersion":1,"channel":"INTERNAL DEVELOPMENT ONLY - UNSIGNED","applicationVersion":"0.1.0.0",
            "rid":"win-x64","sdkVersion":"10.0.401","runtimeVersion":"10.0.9",
            "sourceCommit":"f260faaf6cb974844051c30091369f069af72180","sourceDirty":false,
            "files":[{"path":"C:\\Users\\Joanna\\secret-source","bytes":5,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
            """;
        var build = BuildMetadata.FromPayloadManifest(Encoding.UTF8.GetBytes(manifest));
        Assert.Equal(BuildSource.SuppliedPayloadManifest, build.Source);
        Assert.Equal(1, build.InventoriedFileCount);
        Assert.DoesNotContain("Joanna", JsonSerializer.Serialize(build));
        Fixtures.Failure(SupportFailure.InvalidData, () => BuildMetadata.FromPayloadManifest(Encoding.UTF8.GetBytes(
            manifest.Replace("\"sourceDirty\":false", "\"sourceDirty\":false,\"environment\":\"secret\""))));
        Fixtures.Failure(SupportFailure.InvalidData, () => BuildMetadata.FromPayloadManifest(Encoding.UTF8.GetBytes(
            manifest.Replace("\"bytes\":5", "\"bytes\":5,\"provider_error\":\"secret\""))));
        Fixtures.Failure(SupportFailure.InvalidData, () => BuildMetadata.FromPayloadManifest(Encoding.UTF8.GetBytes(
            manifest.Replace("10.0.401", Fixtures.Canary))));
    }

    [Fact]
    public void NoGenericDataSourcesOrExternalFileInclusionAndDisposedPreviewIsClosed()
    {
        var types = new[] { typeof(SupportSnapshot), typeof(DiagnosticEvent), typeof(SettingsSummary), typeof(BuildMetadata) };
        foreach (var method in types.SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)))
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(object) ||
                p.ParameterType == typeof(AppSettings) || typeof(Delegate).IsAssignableFrom(p.ParameterType));
        using var scope = new TestScope();
        var snapshot = Fixtures.Snapshot(scope, new());
        Fixtures.Failure(SupportFailure.InvalidData, () => snapshot.Preview("..\\settings.json"));
        Fixtures.Failure(SupportFailure.InvalidData, () => snapshot.Preview(scope.Output));
        snapshot.Dispose();
        Fixtures.Failure(SupportFailure.Closed, () => snapshot.Preview("settings.json"));
        Fixtures.Failure(SupportFailure.Closed, () => snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output));
    }
}

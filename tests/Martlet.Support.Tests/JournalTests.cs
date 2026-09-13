using System.Text;
using Martlet.Core.Contracts;
using Martlet.Diagnostics;

namespace Martlet.Support.Tests;

public sealed class JournalTests
{
    [Fact]
    public void ExactEventJsonRejectsUnmappedDuplicateCaseNumericAndContentFields()
    {
        var original = Fixtures.Event(new());
        var json = Encoding.UTF8.GetString(SupportJson.WriteEvent(original));
        Assert.Equal(original, SupportJson.ReadEvent(Encoding.UTF8.GetBytes(json)));
        foreach (var changed in new[]
        {
            json.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"text\": \"authored secret\""),
            json.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"schema_version\": 1"),
            json.Replace("\"live\"", "\"Live\""), json.Replace("\"live\"", "3"),
            json.Replace("\"settings.malformed\"", "\"authored-secret\""),
            json.Replace("\"settings.restore\"", "\"provider.setup\""),
            json.Replace("\"schema_version\": 1", "\"schema_version\": 2"),
            json.Replace("\"live\"", "\"unknown\"")
        })
            Assert.Throws<SupportException>(() => SupportJson.ReadEvent(Encoding.UTF8.GetBytes(changed)));
        Assert.Throws<SupportException>(() => SupportJson.WriteEvent(original with { MonotonicDurationMilliseconds = double.NaN }));
        Assert.Throws<SupportException>(() => SupportJson.WriteEvent(original with { QueueDepth = -1 }));
        Assert.Throws<SupportException>(() => SupportJson.WriteEvent(original with { TimestampUtc = original.TimestampUtc.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<SupportException>(() => SupportJson.ReadEvent(new byte[SupportJson.MaximumEventBytes + 1]));
        Assert.Throws<SupportException>(() => SupportJson.ReadEvent(new byte[] { (byte)'{', 0xff, (byte)'}' }));
    }

    [Fact]
    public void UnknownNotRunAndStaleCannotClaimPass()
    {
        var item = Fixtures.Event(new()) with { Code = "fixture.passed", ActionId = "pipeline.unavailable",
            Severity = DiagnosticSeverity.Information, Provenance = EvidenceProvenance.Fixture };
        foreach (var bad in new[]
        {
            item with { Freshness = EvidenceFreshness.Stale },
            item with { Freshness = EvidenceFreshness.Unknown },
            item with { Provenance = EvidenceProvenance.NotRun },
            item with { Provenance = EvidenceProvenance.Unknown }
        }) Assert.Throws<SupportException>(() => bad.Validate());
    }

    [Fact]
    public void StartIsExplicitAndPathsAndOptionsAreBounded()
    {
        using var scope = new TestScope();
        _ = new JournalOptions();
        Assert.False(Directory.Exists(scope.Journal));
        foreach (var path in new[] { "relative", @"\\server\share\logs", @"C:\", @"C:\logs:stream", "" })
            Fixtures.Failure(SupportFailure.InvalidPath, () => DiagnosticJournal.Start(path));
        foreach (var options in new[]
        {
            new JournalOptions { RetentionDays = 8 }, new JournalOptions { MaximumTotalBytes = 60 * 1024 * 1024 },
            new JournalOptions { MaximumRecordBytes = 100 }, new JournalOptions { MaximumSegmentBytes = 1024 }
        }) Fixtures.Failure(SupportFailure.InvalidData, () => DiagnosticJournal.Start(scope.Journal, options));
        Assert.False(Directory.Exists(scope.Journal));
    }

    [Fact]
    public void SerialWritesRealBytesRotationBoundsAndReadOnlySelection()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var options = new JournalOptions { MaximumTotalBytes = 65_536, MaximumSegmentBytes = 8192 };
        Directory.CreateDirectory(scope.Journal);
        var sentinel = Path.Combine(scope.Journal, "unrelated.txt");
        File.WriteAllText(sentinel, Fixtures.Canary);
        long latest;
        using (var journal = DiagnosticJournal.Start(scope.Journal, options, clock))
        {
            latest = 0;
            for (var i = 0; i < 160; i++)
            {
                var receipt = journal.Append(Fixtures.Event(clock));
                Assert.Equal(++latest, receipt.Sequence);
                Assert.InRange(receipt.EncodedBytes, 1, options.MaximumRecordBytes);
            }
            var selected = journal.Select(Fixtures.Range(clock));
            Assert.True(selected.Records.Count < 160);
            Assert.Equal(latest, selected.Records[^1].Sequence);
            Assert.Equal(selected.Records.OrderBy(r => r.Sequence), selected.Records);
            Fixtures.Failure(SupportFailure.Busy, () => DiagnosticJournal.Start(scope.Journal, options, clock));
            Fixtures.Failure(SupportFailure.Busy, () => DiagnosticJournal.ReadClosed(scope.Journal, Fixtures.Range(clock)));
        }
        var before = Directory.GetFiles(scope.Journal).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
        Assert.Equal(latest, DiagnosticJournal.ReadClosed(scope.Journal, Fixtures.Range(clock)).Records[^1].Sequence);
        foreach (var file in Directory.GetFiles(scope.Journal)) Assert.Equal(before[Path.GetFileName(file)], File.ReadAllBytes(file));
        Assert.True(Directory.GetFiles(scope.Journal).Where(f => f != sentinel).Sum(f => new FileInfo(f).Length) <= options.MaximumTotalBytes);
        Assert.Equal(Fixtures.Canary, File.ReadAllText(sentinel));
        using var reopened = DiagnosticJournal.Start(scope.Journal, options, clock);
        Assert.Equal(latest + 1, reopened.Append(Fixtures.Event(clock)).Sequence);
    }

    [Fact]
    public void RetentionUsesClockAndReceiptNotFutureOrOutOfOrderEventTime()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        journal.Append(Fixtures.Event(clock) with { TimestampUtc = clock.Utc.AddYears(10) });
        journal.Append(Fixtures.Event(clock) with { TimestampUtc = clock.Utc.AddYears(-10) });
        Assert.Equal(2, journal.Select(Fixtures.Range(clock)).Records.Count);
        clock.Advance(TimeSpan.FromDays(8));
        var receipt = journal.Append(Fixtures.Event(clock));
        var only = Assert.Single(journal.Select(Fixtures.Range(clock)).Records);
        Assert.Equal(receipt.Sequence, only.Sequence);
        Assert.Equal(clock.Utc, only.AcceptedUtc);
    }

    [Fact]
    public void ActivePartialWriteFailsThenRecoversOnlyTheTruncatedTail()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var fs = new FaultFileSystem();
        var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        journal.Append(Fixtures.Event(clock));
        fs.PartialActiveWrite = true;
        Fixtures.Failure(SupportFailure.IoFailure, () => journal.Append(Fixtures.Event(clock)));
        Fixtures.Failure(SupportFailure.Closed, () => journal.Append(Fixtures.Event(clock)));
        journal.Close();
        Fixtures.Failure(SupportFailure.CorruptJournal, () => DiagnosticJournal.ReadClosed(scope.Journal, Fixtures.Range(clock)));
        using var recovered = DiagnosticJournal.Start(scope.Journal, clock: clock);
        Assert.True(recovered.Recovery.TruncatedTailBytes > 0);
        Assert.Single(recovered.Select(Fixtures.Range(clock)).Records);
        Assert.Equal(3, recovered.Append(Fixtures.Event(clock)).Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteCorruptionOrFinalTailIsPreserved(bool tail)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var journal = DiagnosticJournal.Start(scope.Journal, clock: clock)) journal.Append(Fixtures.Event(clock));
        var segment = Assert.Single(Directory.GetFiles(scope.Journal, "*.jsonl"));
        File.AppendAllText(segment, tail ? "{\"unfinished\"" : "{\"unsupported\":true}\n");
        var before = File.ReadAllBytes(segment);
        clock.Advance(TimeSpan.FromDays(8));
        Fixtures.Failure(SupportFailure.CorruptJournal, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
        Assert.Equal(before, File.ReadAllBytes(segment));
    }

    [Fact]
    public void CorruptManifestNeverDeletesEvidenceAndIncompatibleOptionsFail()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var journal = DiagnosticJournal.Start(scope.Journal, clock: clock)) journal.Append(Fixtures.Event(clock));
        Fixtures.Failure(SupportFailure.InvalidData, () => DiagnosticJournal.Start(scope.Journal, new() { RetentionDays = 2 }, clock));
        var manifest = Path.Combine(scope.Journal, DiagnosticJournal.ManifestName);
        File.AppendAllText(manifest, "corrupt");
        var before = Directory.GetFiles(scope.Journal).ToDictionary(p => p, File.ReadAllBytes);
        Fixtures.Failure(SupportFailure.CorruptJournal, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public void SelectionIsBoundedWithoutDroppingAndUnicodeDirectoryWorks()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var path = Path.Combine(scope.Root, "spaced \u03bb journal");
        using var journal = DiagnosticJournal.Start(path, clock: clock);
        journal.Append(Fixtures.Event(clock)); journal.Append(Fixtures.Event(clock));
        Fixtures.Failure(SupportFailure.LimitExceeded, () => journal.Select(Fixtures.Range(clock, 1)));
        Assert.Equal(2, journal.Select(Fixtures.Range(clock)).Records.Count);
    }

    [Fact]
    public void SyntacticallyValidCorruptionFailsTheJournalChecksum()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var journal = DiagnosticJournal.Start(scope.Journal, clock: clock))
        {
            var receipt = journal.Append(Fixtures.Event(clock) with { Count = 12 });
            var active = Assert.Single(Directory.GetFiles(scope.Journal, "*.open"));
            Assert.Equal(receipt.EncodedBytes, new FileInfo(active).Length);
        }
        var segment = Assert.Single(Directory.GetFiles(scope.Journal, "*.jsonl"));
        var bytes = File.ReadAllText(segment);
        Assert.Contains("\"count\":12", bytes);
        File.WriteAllText(segment, bytes.Replace("\"count\":12", "\"count\":13"));
        Fixtures.Failure(SupportFailure.CorruptJournal, () => DiagnosticJournal.ReadClosed(scope.Journal, Fixtures.Range(clock)));
    }

    [Fact]
    public void FailedRetentionDeleteAndAtomicManifestReplaceRemainFailures()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var fs = new FaultFileSystem();
        var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        journal.Append(Fixtures.Event(clock));
        clock.Advance(TimeSpan.FromDays(8));
        fs.BeforeDelete = _ => throw new IOException(Fixtures.Canary);
        Fixtures.Failure(SupportFailure.IoFailure, () => journal.Append(Fixtures.Event(clock)));
        journal.Close();
        Assert.Single(Directory.GetFiles(scope.Journal, "*.open"));
        fs.BeforeDelete = null;
        journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        fs.BeforeMove = (_, destination) =>
        {
            if (destination.EndsWith(DiagnosticJournal.ManifestName, StringComparison.Ordinal))
                throw new IOException(Fixtures.Canary);
        };
        Fixtures.Failure(SupportFailure.IoFailure, () => journal.Append(Fixtures.Event(clock)));
        journal.Close();
        Assert.Single(Directory.GetFiles(scope.Journal, "*.pending"));
        fs.BeforeMove = null;
        using var recovered = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        Assert.Empty(Directory.GetFiles(scope.Journal, "*.pending"));
        Assert.Empty(recovered.Select(Fixtures.Range(clock)).Records);
        recovered.Append(Fixtures.Event(clock));
    }

    [Fact]
    public void AnUnregisteredOwnedLayoutNameIsPreservedAndRejected()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var journal = DiagnosticJournal.Start(scope.Journal, clock: clock)) journal.Append(Fixtures.Event(clock));
        var manifest = DiagnosticJournal.LoadManifest(scope.Journal, new SupportFileSystem());
        var unexpected = Path.Combine(scope.Journal, $"{manifest.Owner:N}.31.jsonl");
        File.WriteAllText(unexpected, Fixtures.Canary);
        Fixtures.Failure(SupportFailure.CorruptJournal, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
        Assert.Equal(Fixtures.Canary, File.ReadAllText(unexpected));
    }

    [Fact]
    public void ChangedRetentionMetadataFailsChecksumBeforeDeletingRecords()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using (var journal = DiagnosticJournal.Start(scope.Journal, clock: clock)) journal.Append(Fixtures.Event(clock));
        var manifest = Path.Combine(scope.Journal, DiagnosticJournal.ManifestName);
        var original = File.ReadAllText(manifest);
        Assert.Contains("2026-09-13", original);
        File.WriteAllText(manifest, original.Replace("2026-09-13", "2000-01-01"));
        var segment = Assert.Single(Directory.GetFiles(scope.Journal, "*.jsonl"));
        var before = File.ReadAllBytes(segment);
        Fixtures.Failure(SupportFailure.CorruptJournal, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
        Assert.Equal(before, File.ReadAllBytes(segment));
    }

    [Fact]
    public async Task BackpressureAndCloseKeepOwnershipWhileIoIsBlocked()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fs = new FaultFileSystem();
        using var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        fs.BeforeWrite = stream =>
        {
            if (stream is FileStream file && file.Name.EndsWith(".open", StringComparison.Ordinal))
            { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        };
        var writing = Task.Run(() => journal.Append(Fixtures.Event(clock)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Fixtures.Failure(SupportFailure.Busy, () => journal.Append(Fixtures.Event(clock)));
            Fixtures.Failure(SupportFailure.Busy, journal.Dispose);
            Fixtures.Failure(SupportFailure.Busy, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
        }
        finally { release.Set(); }
        Assert.Equal(1, (await writing).Sequence);
    }

    [Fact]
    public void AccessFlushAndFinalizeFailuresAreSanitizedAndNotSuccess()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var fs = new FaultFileSystem();
        var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        fs.BeforeOpen = path => { if (path.EndsWith(".open", StringComparison.Ordinal)) throw new UnauthorizedAccessException(Fixtures.Canary); };
        Fixtures.Failure(SupportFailure.AccessDenied, () => journal.Append(Fixtures.Event(clock)));
        journal.Close();
        fs.BeforeOpen = null;
        journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        Assert.Equal(1, journal.Recovery.AbandonedReservations);
        fs.BeforeFlush = stream => { if (stream.Name.EndsWith(".open", StringComparison.Ordinal)) throw new IOException(Fixtures.Canary); };
        Fixtures.Failure(SupportFailure.IoFailure, () => journal.Append(Fixtures.Event(clock)));
        journal.Close();
        fs.BeforeFlush = null;
        journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        journal.Append(Fixtures.Event(clock));
        fs.BeforeMove = (_, destination) => { if (destination.EndsWith(".jsonl", StringComparison.Ordinal)) throw new IOException(Fixtures.Canary); };
        Fixtures.Failure(SupportFailure.IoFailure, () => journal.Close());
        fs.BeforeMove = null;
        using var recovered = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        Assert.Equal(2, recovered.Select(Fixtures.Range(clock)).Records.Count);
    }

    [Fact]
    public void CancellationDeadlineAndProtectedEvidenceAreExplicit()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Fixtures.Failure(SupportFailure.Canceled, () => DiagnosticJournal.Start(scope.Journal, clock: clock, cancellationToken: canceled.Token));
        Assert.False(Directory.Exists(scope.Journal));
        var fs = new FaultFileSystem();
        using (var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs))
        {
            fs.BeforeWrite = _ => clock.Advance(TimeSpan.FromSeconds(2));
            Fixtures.Failure(SupportFailure.DeadlineExceeded, () => journal.Append(Fixtures.Event(clock), timeout: TimeSpan.FromSeconds(1)));
        }
        fs.BeforeWrite = null;
        using (var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs)) journal.Append(Fixtures.Event(clock));
        var segment = Assert.Single(Directory.GetFiles(scope.Journal, "*.jsonl"));
        File.SetAttributes(segment, FileAttributes.ReadOnly);
        try
        {
            clock.Advance(TimeSpan.FromDays(8));
            Fixtures.Failure(SupportFailure.AccessDenied, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
            Assert.True(File.Exists(segment));
        }
        finally { File.SetAttributes(segment, FileAttributes.Normal); }
    }

    [Fact]
    public void FailedLeaseCloseRetainsOwnershipUntilActualClose()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var fs = new FaultFileSystem();
        var journal = DiagnosticJournal.Start(scope.Journal, new(), clock, fs);
        journal.Append(Fixtures.Event(clock));
        fs.BeforeClose = stream =>
        {
            if (stream is FileStream file && file.Name.EndsWith(DiagnosticJournal.LockName, StringComparison.Ordinal))
                throw new IOException(Fixtures.Canary);
        };
        try
        {
            Fixtures.Failure(SupportFailure.IoFailure, () => journal.Close());
            Fixtures.Failure(SupportFailure.Busy, () => DiagnosticJournal.Start(scope.Journal, clock: clock));
        }
        finally { fs.BeforeClose = null; journal.Close(); }
        using var next = DiagnosticJournal.Start(scope.Journal, clock: clock);
        Assert.Single(next.Select(Fixtures.Range(clock)).Records);
    }

    [Fact]
    public void LinkedScopeAndExportParentAreRejectedWithoutTouchingTarget()
    {
        using var scope = new TestScope();
        var target = Path.Combine(scope.Root, "target");
        var link = Path.Combine(scope.Root, "link");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "unrelated.txt");
        File.WriteAllText(sentinel, Fixtures.Canary);
        if (OperatingSystem.IsWindows())
        {
            // A private junction needs no symlink privilege or change to machine permissions.
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            try
            {
                Assert.True(process.WaitForExit(10_000));
                Assert.Equal(0, process.ExitCode);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            Fixtures.Failure(SupportFailure.InvalidPath, () => DiagnosticJournal.Start(link));
            Fixtures.Failure(SupportFailure.InvalidPath, () => DiagnosticJournal.Start(Path.Combine(link, "child")));
            using var snapshot = Fixtures.Snapshot(scope, new());
            Fixtures.Failure(SupportFailure.InvalidPath, () => snapshot.Approve(snapshot.Id, snapshot.Digest, Path.Combine(link, "bundle.zip")));
            Assert.Equal(Fixtures.Canary, File.ReadAllText(sentinel));
            Assert.Single(Directory.GetFileSystemEntries(target));
        }
        finally { Directory.Delete(link); }
    }
}

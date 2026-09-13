using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Support.Tests;

internal sealed class TestScope : IDisposable
{
    // Test binaries are directed to a private C: session artifact tree, never an application data directory.
    internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "support-test-data", Guid.NewGuid().ToString("N"));
    internal string Journal => Path.Combine(Root, "journal");
    internal string Output => Path.Combine(Root, "reviewed.zip");
    internal TestScope() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class ManualClock : TimeProvider
{
    internal DateTimeOffset Utc = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private long ticks;
    public override DateTimeOffset GetUtcNow() => Utc;
    public override long GetTimestamp() => ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    internal void Advance(TimeSpan amount) { Utc += amount; ticks += amount.Ticks; }
}

internal static class Fixtures
{
    internal const string Canary = "authored-secret-DO-NOT-EXPORT transcript Joanna 203.0.113.7 C:\\Users\\Joanna\\private";
    internal static DiagnosticEvent Event(ManualClock clock) => new()
    {
        SchemaVersion = 1, TimestampUtc = clock.Utc, Severity = DiagnosticSeverity.Error,
        Component = SupportComponent.Diagnostics, Stage = Stage.Settings, Code = "settings.malformed",
        ActionId = "settings.restore", Provenance = EvidenceProvenance.Live, Freshness = EvidenceFreshness.Current,
        Adapter = AdapterAlias.None, TraceId = Guid.NewGuid(), TurnId = Guid.NewGuid()
    };

    internal static DoctorReport Report(ManualClock clock, Guid? trace = null) => new()
    {
        Version = ContractVersion.Current, ApplicationVersion = "0.1.0", CreatedAt = clock.Utc,
        SettingsState = SettingsLoadState.Invalid, Probes =
        [
            new()
            {
                Id = "settings.load", Stage = Stage.Settings, Required = true, Outcome = ProbeOutcome.Failed,
                Provenance = EvidenceProvenance.Live, Freshness = EvidenceFreshness.Current, ObservedAt = clock.Utc,
                Summary = Canary, DiagnosticCode = "settings.malformed", ActionId = "settings.restore", Remedy = Canary,
                Error = new() { Code = ErrorCode.SettingsMalformed, Stage = Stage.Settings,
                    Summary = Canary, ActionId = "settings.restore", Retryable = false, TraceId = trace }
            }
        ]
    };

    internal static SettingsSummary Settings() => SettingsSummary.FromStatus(SettingsLoadState.Invalid, null, null);
    internal static LogRange Range(ManualClock clock, int records = 2048) =>
        new(clock.Utc - TimeSpan.FromDays(30), clock.Utc + TimeSpan.FromDays(1), records);
    internal static SupportSnapshot Snapshot(TestScope scope, ManualClock clock)
    {
        using var journal = DiagnosticJournal.Start(scope.Journal, clock: clock);
        var item = Event(clock);
        journal.Append(item);
        return SupportSnapshot.Freeze(Settings(), Report(clock, item.TraceId),
            BuildMetadata.FromExecutingAssemblies(), journal.Select(Range(clock)), clock);
    }

    internal static void Failure(SupportFailure expected, Action action)
    {
        var exception = Assert.Throws<SupportException>(action);
        Assert.Equal(expected, exception.Failure);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Canary, exception.ToString());
    }
}

internal sealed class FaultFileSystem : SupportFileSystem
{
    internal Action<Stream>? BeforeWrite;
    internal Action<FileStream>? BeforeFlush;
    internal Action<Stream>? BeforeClose;
    internal Action<string, string>? BeforeMove;
    internal Action<string>? BeforeDelete;
    internal Action<string>? BeforeOpen;
    internal bool PartialActiveWrite;
    internal int Writes;

    internal override FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        BeforeOpen?.Invoke(path);
        return base.Open(path, mode, access, share);
    }
    internal override void Write(Stream stream, ReadOnlySpan<byte> bytes)
    {
        Writes++;
        BeforeWrite?.Invoke(stream);
        if (PartialActiveWrite && stream is FileStream file && file.Name.EndsWith(".open", StringComparison.Ordinal))
        {
            base.Write(stream, bytes[..(bytes.Length / 2)]);
            throw new IOException(Fixtures.Canary);
        }
        base.Write(stream, bytes);
    }
    internal override void Flush(FileStream stream) { BeforeFlush?.Invoke(stream); base.Flush(stream); }
    internal override void Close(Stream stream) { BeforeClose?.Invoke(stream); base.Close(stream); }
    internal override void Move(string source, string destination, bool overwrite)
    { BeforeMove?.Invoke(source, destination); base.Move(source, destination, overwrite); }
    internal override void Delete(string path) { BeforeDelete?.Invoke(path); base.Delete(path); }
}

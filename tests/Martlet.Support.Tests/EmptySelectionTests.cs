using System.IO.Compression;
using System.Text;

namespace Martlet.Support.Tests;

public sealed class EmptySelectionTests
{
    [Fact]
    public void EmptySelectionValidatesAndNeedsNoJournalForReportOnlyExport()
    {
        var clock = new ManualClock();
        Fixtures.Failure(SupportFailure.InvalidData, () => JournalSelection.Empty(null!));
        foreach (var range in new[]
        {
            new LogRange(clock.Utc, clock.Utc.AddMinutes(-1)),
            new LogRange(clock.Utc, clock.Utc, 0),
            new LogRange(clock.Utc, clock.Utc, MaximumBytes: 1),
            new LogRange(clock.Utc.ToOffset(TimeSpan.FromHours(1)), clock.Utc)
        })
            Fixtures.Failure(SupportFailure.InvalidData, () => JournalSelection.Empty(range));
        using var scope = new TestScope();
        var selected = JournalSelection.Empty(Fixtures.Range(clock));
        Assert.Empty(selected.Records);
        using var snapshot = SupportSnapshot.Freeze(Fixtures.Settings(), Fixtures.Report(clock),
            BuildMetadata.FromExecutingAssemblies(), selected, clock);
        Assert.False(Directory.Exists(scope.Journal));
        Assert.Empty(Directory.GetFiles(scope.Root));
        Assert.Equal("[]", Encoding.UTF8.GetString(snapshot.Preview("events.json")));
        var bytes = snapshot.Files.ToDictionary(f => f.Name, f => snapshot.Preview(f.Name));
        snapshot.Export(snapshot.Approve(snapshot.Id, snapshot.Digest, scope.Output), scope.Output);
        using var zip = ZipFile.OpenRead(scope.Output);
        foreach (var file in zip.Entries)
        {
            using var stream = file.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(bytes[file.FullName], copy.ToArray());
        }
        Assert.False(Directory.Exists(scope.Journal));
    }
}

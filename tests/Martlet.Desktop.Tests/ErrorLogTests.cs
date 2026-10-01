using Martlet.Logging;

namespace Martlet.Desktop.Tests;

public sealed class ErrorLogTests
{
    [Fact]
    public async Task RecordsErrorsBackgroundFaultsAndUncleanExit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.ErrorLog." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // A marker left behind by a process that no longer exists is an unclean exit.
        File.WriteAllText(Path.Combine(directory, "test.999999.running"), "");

        Assert.True(ErrorLog.Initialize(directory, "test"));
        Assert.False(File.Exists(Path.Combine(directory, "test.999999.running")));
        Assert.True(File.Exists(Path.Combine(directory, $"test.{Environment.ProcessId}.running")));

        ErrorLog.Error("Handled failure", new InvalidOperationException("boom-one"));
        var faulted = Task.Run(() => throw new TimeoutException("boom-two"));
        faulted.Forget();
        await Assert.ThrowsAsync<TimeoutException>(() => faulted);
        for (var i = 0; i < 50 && !(ErrorLog.ReadRecent() ?? "").Contains("boom-two"); i++) await Task.Delay(20);

        var log = ErrorLog.ReadRecent()!;
        Assert.Contains("did not exit cleanly", log);
        Assert.Contains("System.InvalidOperationException: boom-one", log);
        Assert.Contains("faulted failed", log);
        Assert.Contains("System.TimeoutException: boom-two", log);

        ErrorLog.MarkCleanExit();
        Assert.False(File.Exists(Path.Combine(directory, $"test.{Environment.ProcessId}.running")));
        Directory.Delete(directory, recursive: true);
    }
}

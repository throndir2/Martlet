using System.IO;
using System.Text.RegularExpressions;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The shared host role setup on this PC and its wait until the role is ready (issue #963).</summary>
public sealed class HostRoleSetupTests
{
    private static HostCheck Check(string text) => new(true, text);

    [Fact]
    public async Task WaitStopsAsSoonAsTheProbeSaysReady()
    {
        var checks = 0;
        var why = await RoleWait.UntilReadyAsync(_ => Task.FromResult(Check($"check {++checks}")),
            check => Task.FromResult(checks < 3 ? new RoleNotReady(check.Text) : null), TimeSpan.Zero, CancellationToken.None);
        Assert.Null(why);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task WaitGivesTheLastReasonAfterThirteenChecks()
    {
        var checks = 0;
        var why = await RoleWait.UntilReadyAsync(_ => Task.FromResult(Check($"check {++checks}")),
            check => Task.FromResult<RoleNotReady?>(new(check.Text)), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(13, checks);
        Assert.Equal(RoleWait.Attempts, checks);
        Assert.Equal("check 13", why);
        Assert.Equal(TimeSpan.FromSeconds(5), RoleWait.Interval);
    }

    [Fact]
    public async Task WaitStopsAtOnceWhenTheProbeGivesUp()
    {
        var checks = 0;
        var why = await RoleWait.UntilReadyAsync(_ => Task.FromResult(Check($"check {++checks}")),
            _ => Task.FromResult<RoleNotReady?>(new("its reader failed to load", GiveUp: true)), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(1, checks);
        Assert.Equal("its reader failed to load", why);
    }

    [Fact]
    public async Task WaitStopsWhenCanceled()
    {
        using var cancel = new CancellationTokenSource();
        var checks = 0;
        var wait = RoleWait.UntilReadyAsync(_ => { checks++; cancel.Cancel(); return Task.FromResult(Check("not yet")); },
            check => Task.FromResult<RoleNotReady?>(new(check.Text)), TimeSpan.FromMinutes(1), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(1, checks);
    }

    /// <summary>The role pages use the shared steps and don't copy the install or the wait loop again.</summary>
    [Theory]
    [InlineData("MainWindow.Pictures.LoadPicturesWorkflow.cs")]
    [InlineData("MainWindow.Reading.cs")]
    [InlineData("MainWindow.SingingSetup.cs")]
    [InlineData("MainWindow.Motion.cs")]
    [InlineData("MainWindow.Listening.cs")]
    public void RolePagesUseTheSharedSetupAndWait(string file)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Martlet.Desktop", file));
        Assert.Contains("InstallRoleHereAsync(", source, StringComparison.Ordinal);
        Assert.Contains("WaitForRoleAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"EngineAsync\([^;]*\[""add"""), source);
        Assert.DoesNotContain("HostsWindow.SetUpThisPcAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt >= 12", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "src", "Martlet.Desktop")))
                return folder.FullName;
        throw new DirectoryNotFoundException("src/Martlet.Desktop was not found above " + AppContext.BaseDirectory);
    }
}

using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class AppUpdateStagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Martlet.AppUpdateStagingTests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void InstallRootIsTheProgramFolderOnlyBesideTheInstallersUninstallFolder()
    {
        var program = Path.Combine(root, "Programs", "Martlet");
        var desktop = Directory.CreateDirectory(Path.Combine(program, "Desktop")).FullName;
        Assert.Null(AppUpdateStaging.InstallRoot(desktop + Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(program, "uninstall"));
        Assert.Equal(program, AppUpdateStaging.InstallRoot(desktop + Path.DirectorySeparatorChar));
        // A development build runs from bin\..., not from a Desktop folder.
        Assert.Null(AppUpdateStaging.InstallRoot(Directory.CreateDirectory(Path.Combine(program, "bin")).FullName));
        Assert.Equal(program + ".next", AppUpdateStaging.StagedRoot(program));
        Assert.Equal(program + ".previous", AppUpdateStaging.PreviousRoot(program));
    }

    [Fact]
    public void StagedScriptSwitchesFoldersRunsOnlyTheFinishStepAndFallsBackToTheWholeInstaller()
    {
        var program = Path.Combine(root, "Programs", "Martlet");
        var script = AppUpdateHelper.Script(Path.Combine(root, "updates"), @"C:\u\Martlet-2.0.0-win-x64.exe", "2.0.0", 42,
            unattended: false, @"C:\p\Martlet.Desktop.exe", " --after-update", program, staged: true);
        var lines = script.Split("\r\n");
        int Line(string text) => Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));

        var rename = Line($"move \"{program}\" \"{program}.previous\"");
        var switched = Line($"move \"{program}.next\" \"{program}\"");
        var finish = Line(AppUpdateStaging.FinishArgument);
        var whole = Line("/SILENT /SUPPRESSMSGBOXES");
        var restart = Line("start \"\" /D \"C:\\p\" /B \"C:\\p\\Martlet.Desktop.exe\" --after-update");
        Assert.All(new[] { rename, switched, finish, whole, restart }, index => Assert.True(index > 0));
        Assert.True(rename < switched && switched < finish && finish < whole && whole < restart);
        Assert.Contains("/VERYSILENT", lines[finish]);
        // Martlet starts once, after either route; the leftover folders go after it.
        Assert.Equal(1, lines.Count(line => line.StartsWith("start ", StringComparison.Ordinal)));
        Assert.True(Line($"rd /s /q \"{program}.previous\"") < restart);
        Assert.True(lines.ToList().FindLastIndex(line => line.Contains($"rd /s /q \"{program}.next\"", StringComparison.Ordinal)) > restart);
    }

    [Fact]
    public void ClassicScriptRunsTheWholeInstallerAndRemovesAStaleStagedFolder()
    {
        var program = Path.Combine(root, "Programs", "Martlet");
        var script = AppUpdateHelper.Script(Path.Combine(root, "updates"), @"C:\u\Martlet-2.0.0-win-x64.exe", "2.0.0", 42,
            unattended: true, null, installRoot: program);
        Assert.DoesNotContain("move ", script);
        Assert.DoesNotContain(AppUpdateStaging.FinishArgument, script);
        Assert.Contains("/VERYSILENT /SUPPRESSMSGBOXES", script);
        Assert.Contains($"rd /s /q \"{program}.next\"", script);
        Assert.Throws<ArgumentException>(() => AppUpdateHelper.Script(root, "i.exe", "2.0.0", 42, true, null, staged: true));
    }

    [Fact]
    public void CleanUpRemovesTheStagedAndPreviousFoldersOnly()
    {
        var program = Directory.CreateDirectory(Path.Combine(root, "Programs", "Martlet")).FullName;
        Directory.CreateDirectory(Path.Combine(AppUpdateStaging.StagedRoot(program), "Desktop"));
        Directory.CreateDirectory(Path.Combine(AppUpdateStaging.PreviousRoot(program), "Desktop"));
        AppUpdateStaging.CleanUp(program);
        Assert.False(Directory.Exists(AppUpdateStaging.StagedRoot(program)));
        Assert.False(Directory.Exists(AppUpdateStaging.PreviousRoot(program)));
        Assert.True(Directory.Exists(program));
    }
}

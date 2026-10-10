using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class PcFolderTests
{
    private const string Data = @"C:\Users\sam\AppData\Local\Martlet";
    private const string Machine = @"C:\ProgramData\Martlet";

    [Fact]
    public void TheDefaultDataFolderUsesTheMachineWideFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pc = PcFolder.For(Data + @"\", null, Data, Machine);

        Assert.Equal(PcFolderKind.MachineWide, pc.Kind);
        Assert.Equal(Machine, pc.Directory);
        Assert.True(pc.Shared);
        Assert.True(pc.Separate(Data));
        Assert.Equal(@"%ProgramData%\Martlet", pc.Where);
    }

    [Fact]
    public void AnyOtherDataFolderKeepsThePcScopeItself()
    {
        var disposable = Path.Combine(Path.GetTempPath(), "martlet-pc-" + Guid.NewGuid().ToString("N"));
        var pc = PcFolder.For(disposable, null, Data, Machine);

        Assert.Equal(PcFolderKind.DataFolder, pc.Kind);
        Assert.False(pc.Shared);
        Assert.False(pc.Separate(disposable));
    }

    [Fact]
    public void TheOverrideSharesOneFolderBetweenDataFolders()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shared = Path.Combine(Path.GetTempPath(), "martlet-pc-shared");
        var first = PcFolder.For(Path.Combine(Path.GetTempPath(), "a"), shared, Data, Machine);
        var second = PcFolder.For(Data, shared, Data, Machine);

        Assert.Equal(PcFolderKind.Override, first.Kind);
        Assert.Equal(first, second);
        Assert.True(first.Separate(Data));
        Assert.Equal(PcFolderKind.MachineWide, PcFolder.For(Data, "relative", Data, Machine).Kind);
    }

    [Fact]
    public void PreparingASharedFolderLetsEveryWindowsUserChangeIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "martlet-pc-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(PcFolder.EveryUserCanChange(directory));
            Assert.True(new PcFolder(directory, PcFolderKind.Override).Prepare());
            Assert.True(PcFolder.EveryUserCanChange(directory));
            Assert.False(PcFolder.LetEveryUserChange(directory));

            var plain = directory + "-own";
            Assert.Null(new PcFolder(plain, PcFolderKind.DataFolder).Prepare());
            Assert.True(Directory.Exists(plain));
            Directory.Delete(plain);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HostRolesNameTheWindowsUserWhoseDockerDesktopRunsThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-host-roles-" + Guid.NewGuid().ToString("N"));
        const string sam = "S-1-5-21-1004336348-1177238915-682003330-1001";
        try
        {
            ThisPcHostRoles.Save(directory, ["stt", "ollama"], sam);
            Assert.Equal(["ollama", "stt"], ThisPcHostRoles.Load(directory));
            Assert.Equal(sam, ThisPcHostRoles.WindowsUser(directory));
            Assert.StartsWith("# windows user: " + sam, File.ReadAllText(Path.Combine(directory, ThisPcHostRoles.FileName)), StringComparison.Ordinal);

            ThisPcHostRoles.Save(directory, ["ollama", "stt"], "not a sid\nollama");
            Assert.Null(ThisPcHostRoles.WindowsUser(directory));
            Assert.Equal(["ollama", "stt"], ThisPcHostRoles.Load(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

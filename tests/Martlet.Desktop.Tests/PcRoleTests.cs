using System.IO;
using Martlet.Core.Installation;

namespace Martlet.Desktop.Tests;

/// <summary>What this PC is for is one choice for every Windows user of this PC (the PC folder), and an earlier per-user choice
/// moves there on the first start after the update.</summary>
public sealed class PcRoleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-pc-role-" + Guid.NewGuid().ToString("N"));
    private string Sam => Path.Combine(root, "sam");
    private string Alex => Path.Combine(root, "alex");
    private PcFolder Pc => new(Path.Combine(root, "pc"), PcFolderKind.Override);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void An_earlier_choice_moves_to_the_pc_folder_and_a_second_windows_user_sees_it()
    {
        DeviceRolePreference.Save(Sam, DeviceRole.Host);
        Directory.CreateDirectory(Sam);
        File.WriteAllLines(Path.Combine(Sam, ThisPcHostRoles.FileName), ["ollama"]);

        var first = PcRole.Load(Sam, Pc);
        Assert.Equal(new PcRole.Reading(DeviceRole.Host, true, true, null), first);
        Assert.Equal(DeviceRole.Host, DeviceRolePreference.Load(Pc.Directory));
        Assert.Equal(["ollama"], ThisPcHostRoles.Load(Pc.Directory));
        Assert.Equal(PcRole.ThisWindowsUser, ThisPcHostRoles.WindowsUser(Pc.Directory));

        // Alex never chose: the PC's role counts, and Alex has no choice of their own yet.
        Assert.Equal(new PcRole.Reading(DeviceRole.Host, false, false, null), PcRole.Load(Alex, Pc));
        // Sam's start again moves nothing.
        Assert.Equal(new PcRole.Reading(DeviceRole.Host, true, false, null), PcRole.Load(Sam, Pc));
    }

    [Fact]
    public void A_choice_already_in_the_pc_folder_wins_over_an_earlier_per_user_one()
    {
        DeviceRolePreference.Save(Pc.Directory, DeviceRole.Companion);
        DeviceRolePreference.Save(Alex, DeviceRole.Host);

        Assert.Equal(new PcRole.Reading(DeviceRole.Companion, true, false, null), PcRole.Load(Alex, Pc));
        Assert.Equal(DeviceRole.Companion, DeviceRolePreference.Load(Pc.Directory));
    }

    [Fact]
    public void Saving_changes_the_pc_folder_only_when_the_role_changes()
    {
        PcRole.Save(Sam, Pc, DeviceRole.Host);
        Assert.Equal(DeviceRole.Host, DeviceRolePreference.Load(Pc.Directory));
        Assert.Equal(DeviceRole.Host, DeviceRolePreference.Load(Sam));
        var file = PcRole.FilePath(Pc);
        var changed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, changed);

        // Alex's Martlet follows: the PC folder keeps the time of Sam's change, and Alex now has a choice of their own.
        PcRole.Save(Alex, Pc, DeviceRole.Host);
        Assert.Equal(changed, File.GetLastWriteTimeUtc(file));
        Assert.Equal(DeviceRole.Host, DeviceRolePreference.Load(Alex));

        PcRole.Save(Alex, Pc, DeviceRole.Companion);
        Assert.NotEqual(changed, File.GetLastWriteTimeUtc(file));
        Assert.Equal(DeviceRole.Companion, DeviceRolePreference.Peek(Pc.Directory));
    }

    [Fact]
    public void A_data_folder_of_its_own_keeps_its_choice_as_before()
    {
        var own = new PcFolder(Sam, PcFolderKind.DataFolder);
        Assert.Equal(new PcRole.Reading(null, false, false, null), PcRole.Load(Sam, own));
        PcRole.Save(Sam, own, DeviceRole.Host);
        Assert.Equal(new PcRole.Reading(DeviceRole.Host, true, false, null), PcRole.Load(Sam, own));
        Assert.Single(Directory.GetFiles(Sam));
    }

    [Fact]
    public void Peek_says_nothing_for_a_missing_or_unknown_choice()
    {
        Assert.Null(DeviceRolePreference.Peek(Sam));
        Directory.CreateDirectory(Sam);
        File.WriteAllText(Path.Combine(Sam, DeviceRolePreference.FileName), "Toaster");
        Assert.Null(DeviceRolePreference.Peek(Sam));
        Assert.Equal(DeviceRole.Companion, DeviceRolePreference.Load(Sam));
    }
}

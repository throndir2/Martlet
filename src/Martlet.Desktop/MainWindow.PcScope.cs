using System.IO;
using System.Windows;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>The PC scope (docs/ACCOUNTS.md): what this PC is for and the host service it runs are the same for every Windows
/// user of this PC. They are kept in the PC folder (<see cref="PcFolder"/>, %ProgramData%\Martlet), not in this Windows
/// user's data folder. A switch made by another Windows user's Martlet on this PC is followed here within 30 seconds.</summary>
public partial class MainWindow
{
    private PcFolder? pcFolder;
    /// <summary>This Windows user's data folder has a choice of its own (made, followed or the welcome tour skipped).</summary>
    private bool roleChosenHere;
    /// <summary>This start moved this Windows user's earlier choice to the PC folder.</summary>
    private bool pcRoleMoved;
    /// <summary>Why the PC folder couldn't be read at start (this Windows user's own choice counts then), or null.</summary>
    private string? pcRoleProblem;

    private PcFolder? Pc => store is null ? null : pcFolder ??= PcFolder.For(store.DataDirectory);

    /// <summary>Reads the PC's role at start, moving this Windows user's earlier choice to the PC folder when it has none.</summary>
    private DeviceRole? LoadPcRole()
    {
        var reading = PcRole.Load(store!.DataDirectory, Pc!);
        (roleChosenHere, pcRoleMoved, pcRoleProblem) = (reading.ChosenHere, reading.Moved, reading.Problem);
        if (reading.Moved)
            ErrorLog.Info($"Moved this Windows user's choice ({(reading.Role == DeviceRole.Host ? "host" : "companion")} PC) to " +
                $"{Pc!.Where}: every Windows user of this PC shares it now.");
        if (reading.Problem is { } problem)
            ErrorLog.Warn($"Couldn't use {Pc!.Where} for what this PC is for ({problem}); this Windows user's own choice counts for now.");
        return reading.Role;
    }

    /// <summary>The welcome tour was skipped on a PC that another Windows user already chose for: this Windows user keeps
    /// that choice as their own, so the tour doesn't show again at the next start.</summary>
    private void KeepRoleChoice()
    {
        if (store is null || roleChosenHere) return;
        try
        {
            DeviceRolePreference.Save(store.DataDirectory, Role);
            roleChosenHere = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Couldn't keep this PC's role in this Windows user's data folder", error);
        }
    }

    /// <summary>Another Windows user's Martlet on this PC made it a host or a companion PC: this Martlet follows. Switching to a
    /// host PC waits while Martlet replies or hears you here.</summary>
    private void FollowPcRole()
    {
        if (closing || store is null || Pc is not { } pc || !pc.Separate(store.DataDirectory)) return;
        if (DeviceRolePreference.Peek(pc.Directory) is not { } wanted || wanted == deviceRole) return;
        var host = wanted == DeviceRole.Host;
        if (host && Talking) return;
        ErrorLog.Info($"Another Windows user of this PC made it a {(host ? "host" : "companion")} PC, so it is one here too.");
        if (Tour.Visibility == Visibility.Visible) HideTour();
        SetRole(wanted);
        Navigate(NavHome);
        ActionText.Text = host
            ? "Another Windows user of this PC made it a Martlet host. Home shows its host service and what it still needs."
            : "Another Windows user of this PC made it your companion PC again.";
    }

    /// <summary>The roles this PC's host service runs, as Martlet last read them in this Windows user's Docker Desktop. Null
    /// when it never read them, or read them in another Windows user's Docker Desktop (Docker Desktop keeps its containers for
    /// the Windows user who runs it, so they aren't this Windows user's to start).</summary>
    private IReadOnlyList<string>? RememberedHostRoles() =>
        Pc is not { } pc || ThisPcHostRoles.WindowsUser(pc.Directory) is { } user && user != PcRole.ThisWindowsUser
            ? null
            : ThisPcHostRoles.Load(pc.Directory);

    /// <summary>Settings › What this PC is for: where the choice is kept and who shares it, and whose Docker Desktop Martlet
    /// last read this PC's host service in. No paths or user names.</summary>
    private string PcRoleWhere()
    {
        if (store is null || Pc is not { } pc) return "";
        var text = pcRoleProblem is not null
            ? $"Martlet couldn't use {pc.Where}, so this choice is this Windows user's only for now."
            : pc.Kind switch
            {
                PcFolderKind.MachineWide => $"Every Windows user of this PC shares this choice and this PC's host service ({pc.Where}).",
                PcFolderKind.Override => $"Every Martlet started with {pc.Where} shares this choice and this PC's host service (a test setup).",
                _ => "This data folder keeps this choice: Martlet runs with its own data folder, so other Windows users of this PC don't share it."
            };
        if (pcRoleMoved) text += " Martlet moved this Windows user's earlier choice there.";
        var user = ThisPcHostRoles.WindowsUser(pc.Directory);
        if (user is not null && ThisPcHostRoles.Load(pc.Directory) is not null)
            text += user == PcRole.ThisWindowsUser
                ? " Martlet last read this PC's host service in this Windows user's Docker Desktop."
                : " Martlet last read this PC's host service in another Windows user's Docker Desktop: sign in to Windows as that user to manage it.";
        return text;
    }
}

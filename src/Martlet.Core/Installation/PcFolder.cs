using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Martlet.Core.Settings;

namespace Martlet.Core.Installation;

/// <summary>Where a PC folder comes from: the machine-wide folder every Windows user of this PC shares, the data folder
/// itself (Martlet runs with its own --data-directory, such as a test), or MARTLET_PC_DIRECTORY.</summary>
public enum PcFolderKind { MachineWide, DataFolder, Override }

/// <summary>The folder for the PC scope (docs/ACCOUNTS.md, Scopes): what this PC is for (companion or host PC) and the host
/// service Martlet runs on it. Every Windows user of this PC shares it, so it is the same for each of them. It is
/// %ProgramData%\Martlet when Martlet uses the default data folder (%LOCALAPPDATA%\Martlet); any other data folder keeps
/// these files itself, so a disposable data folder never changes the real PC. MARTLET_PC_DIRECTORY points several data
/// folders at one PC folder (verification). It holds no secrets.</summary>
public sealed record PcFolder(string Directory, PcFolderKind Kind)
{
    public const string OverrideVariable = "MARTLET_PC_DIRECTORY";
    public const string MachineWideName = @"%ProgramData%\Martlet";

    /// <summary>Whether other data folders (other Windows users) share this folder.</summary>
    public bool Shared => Kind != PcFolderKind.DataFolder;

    /// <summary>Where the folder is, in words that hold no user name: %ProgramData%\Martlet, MARTLET_PC_DIRECTORY or
    /// "the data folder".</summary>
    public string Where => Kind switch
    {
        PcFolderKind.MachineWide => MachineWideName,
        PcFolderKind.Override => OverrideVariable,
        _ => "the data folder"
    };

    public static PcFolder For(string dataDirectory)
    {
        string? defaultData;
        try { defaultData = SettingsStore.DefaultDataDirectory(); }
        catch (InvalidOperationException) { defaultData = null; }
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return For(dataDirectory, Environment.GetEnvironmentVariable(OverrideVariable), defaultData,
            string.IsNullOrWhiteSpace(common) ? null : Path.Combine(common, "Martlet"));
    }

    public static PcFolder For(string dataDirectory, string? overrideDirectory, string? defaultDataDirectory, string? machineWideDirectory)
    {
        var data = Path.GetFullPath(dataDirectory);
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && Path.IsPathFullyQualified(overrideDirectory))
            return new(Path.GetFullPath(overrideDirectory), PcFolderKind.Override);
        return OperatingSystem.IsWindows() && machineWideDirectory is not null && defaultDataDirectory is not null &&
               SamePath(data, defaultDataDirectory)
            ? new(Path.GetFullPath(machineWideDirectory), PcFolderKind.MachineWide)
            : new(data, PcFolderKind.DataFolder);
    }

    /// <summary>Whether this folder is not <paramref name="dataDirectory"/> itself, so a value moves here from it.</summary>
    public bool Separate(string dataDirectory) => !SamePath(Directory, dataDirectory);

    /// <summary>Creates the folder. A shared one also lets every local Windows user change what is in it (BUILTIN\Users:
    /// Modify, inherited by its files), since Martlet installs per user and never asks for elevation. Returns whether every
    /// Windows user can change it, or null when that couldn't be read or set (another Windows user made it, for example).</summary>
    public bool? Prepare()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (!Shared || !OperatingSystem.IsWindows()) return null;
        try
        {
            LetEveryUserChange(Directory);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or IOException or
            PrivilegeNotHeldException)
        {
            return EveryUserCanChange(Directory);
        }
    }

    /// <summary>Adds BUILTIN\Users: Modify, inherited by subfolders and files, unless the folder grants it already. Returns
    /// whether it changed the folder.</summary>
    [SupportedOSPlatform("windows")]
    public static bool LetEveryUserChange(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (EveryUserCanChange(directory) == true) return false;
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        return true;
    }

    /// <summary>Whether BUILTIN\Users may change the folder and the files in it; null when the folder is missing or its
    /// permissions can't be read.</summary>
    [SupportedOSPlatform("windows")]
    public static bool? EveryUserCanChange(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists) return null;
            var inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            return info.GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference == Users && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                    (rule.InheritanceFlags & inherited) == inherited && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}

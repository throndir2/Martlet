using System.IO;
using System.Security.Principal;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>What this computer is for: the PC you talk to your companion on, or a host that lends its power to one.</summary>
internal enum DeviceRole { Companion, Host }

/// <summary>Reads and writes device-role.txt in one folder: the welcome-tour choice. Absent means not chosen yet.</summary>
internal static class DeviceRolePreference
{
    internal const string FileName = "device-role.txt";

    internal static DeviceRole? Load(string directory)
    {
        try { return Parse(Read(directory)) ?? DeviceRole.Companion; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return DeviceRole.Companion; }
    }

    /// <summary>The role the file says, or null when it is missing, unreadable now (another Windows user's Martlet is saving
    /// it, say) or names no role.</summary>
    internal static DeviceRole? Peek(string directory)
    {
        try { return Parse(Read(directory)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void Save(string directory, DeviceRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"device-role.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, role.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Read(string directory)
    {
        // Shared for writing and deleting, so another Windows user's Martlet can replace the file while this one reads it.
        using var stream = new FileStream(Path.Combine(directory, FileName), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var buffer = new char[16];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    private static DeviceRole? Parse(string text) => text.Trim() switch
    {
        "Companion" => DeviceRole.Companion,
        "Host" => DeviceRole.Host,
        _ => null
    };
}

/// <summary>What this PC is for, in the PC scope (docs/ACCOUNTS.md): one choice for every Windows user of this PC, kept in
/// the PC folder (<see cref="PcFolder"/>, %ProgramData%\Martlet). Each Windows user's data folder also keeps its own copy of
/// the last choice made or followed there: it marks that this Windows user has chosen (or skipped) the welcome tour, and an
/// older Martlet reads it.</summary>
internal static class PcRole
{
    /// <summary>What reading the role at start found: the PC's role (null when nobody chose yet), whether this Windows user's
    /// data folder has a choice of its own, whether that choice moved to the PC folder now, and why the PC folder couldn't be
    /// used (then the data folder's choice counts).</summary>
    internal sealed record Reading(DeviceRole? Role, bool ChosenHere, bool Moved, string? Problem);

    /// <summary>This Windows user's SID: it tells apart whose Docker Desktop runs this PC's host service. Not a secret.</summary>
    internal static string? ThisWindowsUser { get; } = ReadWindowsUser();

    /// <summary>Reads the PC's role. On the first start after the update, this Windows user's earlier choice (and the roles
    /// of this PC's host service) becomes the PC's when the PC folder has none yet, so the next Windows user sees the same.</summary>
    internal static Reading Load(string dataDirectory, PcFolder folder)
    {
        var mine = DeviceRolePreference.Load(dataDirectory);
        if (!folder.Separate(dataDirectory)) return new(mine, mine is not null, false, null);
        try
        {
            var role = DeviceRolePreference.Load(folder.Directory);
            var moved = false;
            if (role is null && mine is { } earlier)
            {
                folder.Prepare();
                DeviceRolePreference.Save(folder.Directory, earlier);
                (role, moved) = (earlier, true);
            }
            if (ThisPcHostRoles.Load(folder.Directory) is null && ThisPcHostRoles.Load(dataDirectory) is { } roles)
            {
                folder.Prepare();
                ThisPcHostRoles.Save(folder.Directory, roles, ThisWindowsUser);
            }
            return new(role, mine is not null, moved, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(mine, mine is not null, false, error.Message);
        }
    }

    /// <summary>Saves a choice for the whole PC and as this Windows user's own. The PC folder's file is written only when the
    /// role changes, so its time stays the time of the last change (the shared settings' role entry uses it).</summary>
    internal static void Save(string dataDirectory, PcFolder folder, DeviceRole role)
    {
        if (folder.Separate(dataDirectory) && DeviceRolePreference.Peek(folder.Directory) != role)
        {
            folder.Prepare();
            DeviceRolePreference.Save(folder.Directory, role);
        }
        DeviceRolePreference.Save(dataDirectory, role);
    }

    /// <summary>The file that holds the PC's role.</summary>
    internal static string FilePath(PcFolder folder) => Path.Combine(folder.Directory, DeviceRolePreference.FileName);

    private static string? ReadWindowsUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }
}

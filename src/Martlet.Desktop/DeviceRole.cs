using System.IO;

namespace Martlet.Desktop;

/// <summary>What this computer is for: the PC you talk to your companion on, or a host that lends its power to one.</summary>
internal enum DeviceRole { Companion, Host }

/// <summary>Saves the welcome-tour choice in device-role.txt next to the other local preferences. Absent means first run.</summary>
internal static class DeviceRolePreference
{
    private const string FileName = "device-role.txt";

    internal static DeviceRole? Load(string directory)
    {
        try
        {
            using var reader = new StreamReader(Path.Combine(directory, FileName));
            var buffer = new char[16];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, count).Trim() switch
            {
                "Companion" => DeviceRole.Companion,
                "Host" => DeviceRole.Host,
                _ => DeviceRole.Companion
            };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return DeviceRole.Companion; }
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
}

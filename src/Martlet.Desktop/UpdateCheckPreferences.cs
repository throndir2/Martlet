using System.IO;

namespace Martlet.Desktop;

internal static class UpdateCheckPreferences
{
    internal static bool Load(string directory)
    {
        var path = Path.Combine(directory, "update-checks.txt");
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[16];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            // "Disabled" was saved while checks were opt-in; it resets to the current default (on).
            return new string(buffer, 0, count) switch
            {
                "On" or "Enabled" or "Disabled" => true,
                "Off" => false,
                _ => throw new InvalidDataException("Unrecognized update-check preference.")
            };
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    internal static void Save(string directory, bool enabled)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "update-checks.txt");
        var temporary = Path.Combine(directory, $"update-checks.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, enabled ? "On" : "Off");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

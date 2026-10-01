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
            return new string(buffer, 0, count) switch
            {
                "Enabled" => true,
                "Disabled" => false,
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
            File.WriteAllText(temporary, enabled ? "Enabled" : "Disabled");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

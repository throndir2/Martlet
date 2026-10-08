using System.IO;
using System.Text.Json;

namespace Martlet.Desktop;

/// <summary>
/// Whether clicks pass through the character on this PC, saved locally (character-click-through.json: <c>{"ClickThrough":true}</c>)
/// and never shared with the other Martlet computers. It sits in Martlet's data folder, so it outlasts restarts and updates, and
/// Reset position leaves it alone. No file means the character catches clicks.
/// </summary>
internal static class CharacterClickThroughStore
{
    internal const string FileName = "character-click-through.json";

    private sealed record Saved(bool ClickThrough);

    internal static bool Load(string? directory)
    {
        if (directory is null) return false;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return false;
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))?.ClickThrough == true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Warn("Whether clicks pass through the character couldn't be read; it catches clicks.", error);
            return false;
        }
    }

    /// <summary>Saves that clicks pass through the character, or with false forgets it. False when it couldn't be saved.</summary>
    internal static bool Save(string? directory, bool on)
    {
        if (directory is null) return false;
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!on)
            {
                File.Delete(path);
                return true;
            }
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"character-click-through.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved(true)));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Whether clicks pass through the character couldn't be saved.", error);
            return false;
        }
    }
}

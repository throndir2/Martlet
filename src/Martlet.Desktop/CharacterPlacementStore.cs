using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>
/// Where the character was last left on this PC's desktop, saved locally (character-placement.json: Locked, Left, Top, Width,
/// Height in device-independent pixels, and Screen, ScreenLeft, ScreenTop: the monitor it was on and its spot there) and never
/// shared with the other Martlet computers. It sits in Martlet's data folder, so it outlasts restarts, shutdowns and updates.
/// No file means the character shows at its default spot.
/// </summary>
internal static class CharacterPlacementStore
{
    internal const string FileName = "character-placement.json";

    internal static RendererPlacement? Load(string? directory)
    {
        if (directory is null) return null;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            var placement = JsonSerializer.Deserialize<RendererPlacement>(File.ReadAllText(path));
            if (placement is { IsValid: true }) return placement;
            ErrorLog.Warn("The character's saved position isn't usable; it shows unlocked at its default spot.");
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Warn("The character's saved position couldn't be read; it shows unlocked at its default spot.", error);
            return null;
        }
    }

    /// <summary>Saves where the character is (locked or not), or with null forgets it. False when it couldn't be saved.</summary>
    internal static bool Save(string? directory, RendererPlacement? placement)
    {
        if (directory is null) return false;
        var path = Path.Combine(directory, FileName);
        try
        {
            if (placement is not { IsValid: true })
            {
                File.Delete(path);
                return true;
            }
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"character-placement.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
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
            ErrorLog.Warn("The character's position couldn't be saved.", error);
            return false;
        }
    }
}

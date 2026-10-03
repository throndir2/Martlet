using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>
/// Where the character is locked on this PC's desktop, saved locally (character-placement.json: Locked, Left, Top, Width,
/// Height in device-independent pixels) and never shared with the other Martlet computers. No file means unlocked: the
/// character then shows at its default spot and moves freely, as before.
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
            if (placement is { Locked: true, IsValid: true }) return placement;
            ErrorLog.Warn("The character's saved position isn't usable; it shows unlocked at its default spot.");
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Warn("The character's saved position couldn't be read; it shows unlocked at its default spot.", error);
            return null;
        }
    }

    /// <summary>Saves a locked place, or with null (unlocked) removes it. False when it couldn't be saved.</summary>
    internal static bool Save(string? directory, RendererPlacement? placement)
    {
        if (directory is null) return false;
        var path = Path.Combine(directory, FileName);
        try
        {
            if (placement is not { Locked: true })
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
            ErrorLog.Warn("The character's position lock couldn't be saved.", error);
            return false;
        }
    }
}

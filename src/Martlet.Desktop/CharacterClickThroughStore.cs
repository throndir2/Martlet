using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>
/// Whether clicks pass through the character on this PC (<see cref="CharacterClickThroughFile"/>, character-click-through.json),
/// never shared with the other Martlet computers. It sits in Martlet's data folder, so it outlasts restarts and updates, and
/// Reset position leaves it alone. No file means the character catches clicks.
/// </summary>
internal static class CharacterClickThroughStore
{
    internal const string FileName = CharacterClickThroughFile.FileName;

    internal static bool Load(string? directory)
    {
        if (directory is null) return false;
        try { return CharacterClickThroughFile.Read(directory); }
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
        try
        {
            CharacterClickThroughFile.Save(directory, on);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Whether clicks pass through the character couldn't be saved.", error);
            return false;
        }
    }
}

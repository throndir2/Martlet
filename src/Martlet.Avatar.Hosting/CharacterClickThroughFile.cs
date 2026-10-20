using System.Text.Json;

namespace Martlet.Avatar.Hosting;

/// <summary>character-click-through.json in Martlet's data folder (<c>{"ClickThrough":true}</c>): whether clicks pass through the
/// character on this PC. It stays on this PC, like the character's place on its screens (docs/CLUSTER.md, "What stays with each
/// computer"). No file means the character catches clicks.</summary>
public static class CharacterClickThroughFile
{
    public const string FileName = "character-click-through.json";

    private sealed record Saved(bool ClickThrough);

    /// <summary>Whether clicks pass through (false without a file); throws <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="JsonException"/> when the file can't be read.</summary>
    public static bool Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return false;
        return JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))?.ClickThrough == true;
    }

    /// <summary>Saves that clicks pass through the character, or with false forgets it.</summary>
    public static void Save(string directory, bool on)
    {
        var path = Path.Combine(directory, FileName);
        if (!on)
        {
            File.Delete(path);
            return;
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
    }
}

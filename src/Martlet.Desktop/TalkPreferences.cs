using System.IO;
using System.Text.Json;

namespace Martlet.Desktop;

// How the user prefers to talk. Choosing hands-free here never starts listening by itself; screen watching is never
// saved as on, only how chatty Martlet is and what it looks at.
internal sealed record TalkPreferences(bool HandsFree = false, double Sensitivity = 0.5, int PauseIndex = 1, bool VoiceId = false,
    int ScreenChattiness = 1, int ScreenScope = 0)
{
    private const string FileName = "talk-preferences.json";
    internal static readonly TimeSpan[] Pauses = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200)];

    internal static TalkPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<TalkPreferences>(File.ReadAllText(path)) ?? new();
            return loaded with
            {
                Sensitivity = double.IsFinite(loaded.Sensitivity) ? Math.Clamp(loaded.Sensitivity, 0, 1) : 0.5,
                PauseIndex = Math.Clamp(loaded.PauseIndex, 0, Pauses.Length - 1),
                ScreenChattiness = Math.Clamp(loaded.ScreenChattiness, 0, 2),
                ScreenScope = Math.Clamp(loaded.ScreenScope, 0, 1)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"talk-preferences.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

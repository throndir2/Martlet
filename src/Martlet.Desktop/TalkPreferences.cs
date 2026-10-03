using System.IO;
using System.Text.Json;

namespace Martlet.Desktop;

// How the user talks with Martlet, chosen in Companion (Listening, Voice and Vision) and used by the talk window while it is
// open: always listening or push-to-talk, whether replies are spoken, whether Thinking also hears the recording (HearVoice, off
// by default), whether what the PC plays is removed from the microphone (ReduceEcho, on by default) and whether (and at what)
// Martlet may look. The talk
// window's mic and vision buttons pause them there (Stop and Esc pause vision, never listening). A camera address is saved
// without its user name or password.
internal sealed record TalkPreferences(bool HandsFree = true, double Sensitivity = 0.5, int PauseIndex = 1, bool VoiceId = false,
    int ScreenChattiness = 1, int ScreenScope = 0, string CameraId = "", string CameraName = "", string VideoAddress = "",
    bool SpeakReplies = true, bool Watch = false, int Version = 0, bool HearVoice = false, bool BargeIn = true, bool ReduceEcho = true)
{
    private const string FileName = "talk-preferences.json";
    // Version 2 made always listening the default; earlier files chose push-to-talk only because it was the old default.
    private const int CurrentVersion = 2;
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
                HandsFree = loaded.Version < CurrentVersion || loaded.HandsFree,
                Sensitivity = double.IsFinite(loaded.Sensitivity) ? Math.Clamp(loaded.Sensitivity, 0, 1) : 0.5,
                PauseIndex = Math.Clamp(loaded.PauseIndex, 0, Pauses.Length - 1),
                ScreenChattiness = Math.Clamp(loaded.ScreenChattiness, 0, 2),
                ScreenScope = Math.Clamp(loaded.ScreenScope, 0, 3),
                CameraId = loaded.CameraId ?? "",
                CameraName = loaded.CameraName ?? "",
                VideoAddress = WatchSource.WithoutCredentials(loaded.VideoAddress ?? ""),
                Version = CurrentVersion
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
                File.WriteAllText(temporary, JsonSerializer.Serialize(this with
                {
                    VideoAddress = WatchSource.WithoutCredentials(VideoAddress),
                    Version = CurrentVersion
                }));
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

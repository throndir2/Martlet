using System.Text.Json;
using Martlet.Core.Sync;

namespace Martlet.Core.Speakers;

/// <summary>Companion › People's two on or off choices, each in its own text file in Martlet's data folder ("on" or "off"; a
/// missing or unknown file means on): whether Martlet recognizes the voices it hears (<see cref="RecognitionFile"/>, the household
/// setting <see cref="RecognitionKey"/>) and whether it keeps the last few clips of voices the owner hasn't named
/// (<see cref="ClipsFile"/>, the household setting <see cref="ClipsKey"/>). Both are the same on all the owner's computers; the
/// clips themselves stay on the computer that heard them. The voice list is <see cref="RosterFile"/> (the shared voice list).</summary>
public static class VoiceChoices
{
    public const string RecognitionFile = "voice-recognition.txt", ClipsFile = "voice-clips.txt", RosterFile = "voices.json";
    public const string RecognitionKey = "voice-recognition", ClipsKey = "voice-clips";

    private sealed record Switch(bool On);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The choice in <paramref name="file"/>: true for "on", false for "off", null when there is none or it can't be read.</summary>
    public static bool? Read(string directory, string file)
    {
        try { return File.ReadAllText(Path.Combine(directory, file)).Trim() switch { "on" => true, "off" => false, _ => null }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether Martlet recognizes voices (on unless the owner turned it off).</summary>
    public static bool Recognition(string directory) => Read(directory, RecognitionFile) ?? true;

    /// <summary>Whether Martlet keeps the last few clips of voices the owner hasn't named (on unless the owner turned it off).</summary>
    public static bool KeepClips(string directory) => Read(directory, ClipsFile) ?? true;

    public static void Write(string directory, string file, bool on)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), on ? "on" : "off");
    }

    /// <summary>When <paramref name="file"/> last changed; null when the owner never chose.</summary>
    public static DateTimeOffset? ChangedAt(string directory, string file)
    {
        var path = Path.Combine(directory, file);
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A choice as it travels: <c>{"on":true}</c>.</summary>
    public static string Share(bool on) => JsonSerializer.Serialize(new Switch(on), Json);

    /// <summary>A travelled choice, or null when it isn't one.</summary>
    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<Switch>(value, Json)?.On; }
        catch (JsonException) { return null; }
    }

    /// <summary>The household setting <see cref="ClipsKey"/> backed by <see cref="ClipsFile"/> in <paramref name="directory"/>.
    /// <paramref name="follow"/> makes this computer take another computer's choice (the desktop's clip keeper, which deletes the
    /// clips it kept when the choice is off); without it the file is written.</summary>
    public static ISharedSection ClipsSection(string directory, Action<bool>? follow = null) => new DelegateSection(ClipsKey,
        "Keeping clips of voices", _ => Task.FromResult<SharedLocal?>(new(Share(KeepClips(directory)), null,
            Read(directory, ClipsFile) is null, ChangedAt(directory, ClipsFile))),
        (setting, _) =>
        {
            if (Parse(setting.Value) is not { } on)
                return Task.FromResult(SharedApply.Waiting("It was chosen on a newer Martlet; update this PC to use it."));
            if (on == KeepClips(directory)) return Task.FromResult(SharedApply.Done);
            try
            {
                if (follow is null) Write(directory, ClipsFile, on);
                else follow(on);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            }
            return Task.FromResult(SharedApply.Done);
        },
        () => Share(true));
}

using System.IO;

namespace Martlet.Conversation;

/// <summary>What the voice-sound rules see at the moment a touch asks for a sound (<see cref="VoiceSoundGate.Decide"/>).
/// <paramref name="Voice"/>: a voice is set up for replies. <paramref name="Aloud"/>: Speak Martlet's replies aloud is on.
/// <paramref name="Stopped"/>: the conversation is paused, muted or locked. <paramref name="Makes"/>: the voice makes this sound.
/// <paramref name="MartletSpeaking"/>: a reply, remark or song is playing. <paramref name="UserTalking"/>: the microphone hears
/// the user now. <paramref name="Playing"/>: another voice sound still plays. <paramref name="Ready"/>: the sound's clip is made
/// for this voice.</summary>
public readonly record struct VoiceSoundMoment(bool Voice, bool Aloud, bool Stopped, bool Makes, bool MartletSpeaking, bool UserTalking,
    bool Playing, bool Ready);

public enum VoiceSoundVerdict { Play, Skip, Make }

public readonly record struct VoiceSoundDecision(VoiceSoundVerdict Verdict, string Why);

/// <summary>When a touch zone's voice sound plays. It never holds up the conversation: it plays only a clip made earlier (never
/// a request to the voice at the touch), never over Martlet's own voice or a song, never while the user talks, and a reply's own
/// voice cuts it the moment it starts (<see cref="ConversationRuntime.PlayClipAsync"/>). A clip not made yet is made in the
/// background (<see cref="VoiceSoundVerdict.Make"/>) and plays from the next touch.</summary>
public static class VoiceSoundGate
{
    public static VoiceSoundDecision Decide(VoiceSoundMoment moment) =>
        !moment.Voice ? new(VoiceSoundVerdict.Skip, "Martlet has no voice set up")
        : !moment.Aloud ? new(VoiceSoundVerdict.Skip, "Speak Martlet's replies aloud is off")
        : moment.Stopped ? new(VoiceSoundVerdict.Skip, "the conversation is paused or muted")
        : !moment.Makes ? new(VoiceSoundVerdict.Skip, "the voice doesn't make that sound")
        : moment.MartletSpeaking ? new(VoiceSoundVerdict.Skip, "Martlet is speaking")
        : moment.UserTalking ? new(VoiceSoundVerdict.Skip, "you are talking")
        : moment.Playing ? new(VoiceSoundVerdict.Skip, "another voice sound is still playing")
        : !moment.Ready ? new(VoiceSoundVerdict.Make, "its clip isn't made for this voice yet")
        : new(VoiceSoundVerdict.Play, "the voice makes it and nothing else is said");
}

/// <summary>Voice sounds kept on this PC, per voice and character (the key of <see cref="QuickSoundLibrary.Key"/>):
/// voice-sounds\&lt;key&gt;\&lt;cue&gt;.pcm in the data folder, one 24 kHz mono 16-bit clip per sound, at most
/// <see cref="MaximumLength"/> long. Each is made once with the voice (never automatically with a paid cloud voice).</summary>
public static class VoiceSoundLibrary
{
    public const string Folder = "voice-sounds";
    /// <summary>The longest a voice sound plays (a laugh takes longer than a quick sound's "Mm,").</summary>
    public static TimeSpan MaximumLength { get; } = TimeSpan.FromMilliseconds(2_500);
    public static int MaximumBytes => (int)(QuickSoundAudio.SampleRate * MaximumLength.TotalSeconds) * 2;

    public static string Directory(string dataDirectory, string key) => Path.Combine(dataDirectory, Folder, key);

    /// <summary>The clip's file name for <paramref name="cue"/> ("clear throat": clear_throat.pcm), or null for a cue that isn't
    /// letters and single spaces.</summary>
    public static string? FileName(string cue) =>
        cue is { Length: > 0 and <= 32 } && cue.All(c => char.IsAsciiLetterLower(c) || c == ' ') && cue[0] != ' ' && cue[^1] != ' ' &&
        !cue.Contains("  ", StringComparison.Ordinal) ? cue.Replace(' ', '_') + ".pcm" : null;

    /// <summary>The clip kept for <paramref name="cue"/>, or null when none is (or it can't be read).</summary>
    public static byte[]? Load(string dataDirectory, string key, string cue)
    {
        if (FileName(cue) is not { } name) return null;
        try
        {
            var path = Path.Combine(Directory(dataDirectory, key), name);
            if (!File.Exists(path) || new FileInfo(path).Length is var length && (length == 0 || length > MaximumBytes || length % 2 != 0)) return null;
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Keeps <paramref name="pcm"/> as the clip for <paramref name="cue"/>; false when it can't be kept.</summary>
    public static bool Save(string dataDirectory, string key, string cue, ReadOnlySpan<byte> pcm)
    {
        if (FileName(cue) is not { } name || pcm.Length is 0 || pcm.Length > MaximumBytes || pcm.Length % 2 != 0) return false;
        try
        {
            var folder = Directory(dataDirectory, key);
            System.IO.Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, $"{name}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporary, pcm.ToArray());
            File.Move(temporary, Path.Combine(folder, name), overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The sounds kept for <paramref name="key"/> and how long each is, by cue.</summary>
    public static IReadOnlyList<(string Cue, TimeSpan Duration)> Made(string dataDirectory, string key)
    {
        var folder = Directory(dataDirectory, key);
        if (!System.IO.Directory.Exists(folder)) return [];
        try
        {
            return [.. System.IO.Directory.GetFiles(folder, "*.pcm").Select(path => (Path.GetFileNameWithoutExtension(path).Replace('_', ' '),
                    new FileInfo(path).Length))
                .Where(file => FileName(file.Item1) is not null && file.Item2 is > 0 && file.Item2 <= MaximumBytes)
                .OrderBy(file => file.Item1, StringComparer.Ordinal)
                .Select(file => (file.Item1, TimeSpan.FromSeconds(file.Item2 / 2.0 / QuickSoundAudio.SampleRate)))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
}

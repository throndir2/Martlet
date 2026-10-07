using System.IO;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// How the user talks with Martlet, chosen in Companion (Listening, Voice and Vision) and used by the talk window while it is
// open: always listening or push-to-talk, whether replies are spoken, whether Thinking also hears the recording (HearVoice:
// on, off, or null when never chosen, which means on only while the recording stays on this PC; see HearVoiceFor), whether
// talking over a reply stops it (BargeIn, opt-in and off by default), how readily what is heard counts as
// words (WordCheck: Relaxed, Normal by default, or Sensitive), whether what the PC plays is removed
// from the microphone (ReduceEcho, on by default), whether always listening also hears what the PC plays (HearPc, off by
// default) and whether (and at what) Martlet may look (Watch, on by default, and ScreenScope, a WatchKind: your whole
// screen by default; a saved file keeps the choices in it, and Martlet still looks only after Start watching), how chatty it
// is about what it sees and what the PC plays (ScreenChattiness: a ChattinessChoice, Normal by default; 3 is Martlet
// decides), and whether Martlet decides where the character looks while it watches your screen (DecideGaze, off by default:
// the character follows the mouse), and how loud Martlet speaks and sings (VoiceVolume, 0 to 1, full by default; this PC
// only, since each PC has its own speakers, and applied to Martlet's own audio, never to Windows' volume). The talk window's mic and vision buttons pause them there (Stop and Esc pause vision,
// never listening). A camera address is saved without its user name or password.
// Companion › Listening › When Thinking can hear you: with HearVoice on and a Thinking model that hears, what you said goes
// straight to Thinking as the recording alone while speech-to-text runs beside the reply (the default), or TranscribeFirst
// waits for the transcript and sends both.
// Companion › Listening › Describe PC sounds (DescribePcSounds, on by default): while Martlet hears what this PC plays, the sound
// digest describes its non-speech sound in one line for the next reply (PcSoundDigest); it never runs without HearPc.
internal sealed record TalkPreferences(bool HandsFree = true, double Sensitivity = 0.5, int PauseIndex = 1, bool VoiceId = false,
    int ScreenChattiness = 1, int ScreenScope = (int)WatchKind.ActiveScreen, string CameraId = "", string CameraName = "",
    string VideoAddress = "", bool SpeakReplies = true, bool Watch = true, int Version = 0, bool? HearVoice = null,
    bool BargeIn = false, bool ReduceEcho = true, bool HearPc = false, ListeningSensitivity WordCheck = ListeningSensitivity.Normal,
    bool DecideGaze = false, bool TranscribeFirst = false, double VoiceVolume = 1.0, bool DescribePcSounds = true)
{
    private const string FileName = "talk-preferences.json";

    /// <summary>Whether Thinking hears your recording with the Thinking route <paramref name="thinking"/>, and why: your own
    /// choice (ticked or turned off) always wins; never chosen, it is on only while the recording stays on this PC
    /// (<see cref="HearingModelCatalog.StaysOnThisPc"/>). Hearing that is only on by default is LocalOnly: the conversation checks
    /// again that the recording stays on this PC before it sends one.</summary>
    internal (bool On, bool LocalOnly) HearVoiceFor(SetupRoute? thinking) => HearVoice is { } chosen ? (chosen, false)
        : (HearingModelCatalog.StaysOnThisPc(thinking?.RouteType, thinking?.Origin, thinking?.ModelId), true);
    // Version 2 made always listening the default; earlier files chose push-to-talk only because it was the old default.
    // Version 3 made barge-in opt-in; earlier files have it on only because it was the old default.
    // Version 4 made HearVoice three-way; earlier files have it off only because that was the old default (never chosen).
    private const int AlwaysListeningVersion = 2, OptInBargeInVersion = 3, HearVoiceChoiceVersion = 4, CurrentVersion = HearVoiceChoiceVersion;
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
                HandsFree = loaded.Version < AlwaysListeningVersion || loaded.HandsFree,
                BargeIn = loaded.Version >= OptInBargeInVersion && loaded.BargeIn,
                HearVoice = loaded.Version < HearVoiceChoiceVersion && loaded.HearVoice == false ? null : loaded.HearVoice,
                Sensitivity = double.IsFinite(loaded.Sensitivity) ? Math.Clamp(loaded.Sensitivity, 0, 1) : 0.5,
                PauseIndex = Math.Clamp(loaded.PauseIndex, 0, Pauses.Length - 1),
                ScreenChattiness = (int)ChattinessTags.Choice(loaded.ScreenChattiness),
                ScreenScope = Math.Clamp(loaded.ScreenScope, 0, 3),
                CameraId = loaded.CameraId ?? "",
                CameraName = loaded.CameraName ?? "",
                VideoAddress = WatchSource.WithoutCredentials(loaded.VideoAddress ?? ""),
                WordCheck = Enum.IsDefined(loaded.WordCheck) ? loaded.WordCheck : ListeningSensitivity.Normal,
                VoiceVolume = Martlet.Audio.PcmGain.Clamp(loaded.VoiceVolume),
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

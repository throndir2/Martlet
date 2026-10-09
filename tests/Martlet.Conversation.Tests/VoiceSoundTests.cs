using Martlet.Audio;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

/// <summary>Touch zones' voice sounds: the "sound:&lt;cue&gt;" entries and the sounds each voice makes alone, the rules for when one
/// plays, the clips kept per voice, and a clip played whole on its own run outside any reply.</summary>
public sealed class VoiceSoundTests
{
    private static byte[] Tone(int milliseconds, short value = 1000)
    {
        var pcm = new byte[QuickSoundAudio.SampleRate * milliseconds / 1000 * 2];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (short)(i % 8 < 4 ? value : -value);
            pcm[i] = (byte)sample;
            pcm[i + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    [Fact]
    public void Sound_entries_name_a_cue_the_active_voice_makes_alone()
    {
        Assert.Equal("sound:laugh", VoiceSounds.Entry("laugh"));
        Assert.Equal("clear throat", VoiceSounds.CueOf("sound:clear throat"));
        Assert.Null(VoiceSounds.CueOf("sound:"));
        Assert.Null(VoiceSounds.CueOf("expression:smile"));
        Assert.Null(VoiceSounds.CueOf(null));
        // Chatterbox's nine sounds in its own spelling; tones are not sounds.
        var turbo = VoiceSounds.Supported(SpeechEngines.Chatterbox);
        Assert.Equal(9, turbo.Count);
        Assert.Equal("[laugh]", VoiceSounds.Text(VoiceSounds.Tag(SpeechEngines.Chatterbox, "laugh")!));
        Assert.Null(VoiceSounds.Tag(SpeechEngines.Chatterbox, "whispering"));
        // Dia's own tag for the same cue, and never (mumbles), which needs words after it.
        Assert.Equal("(gasps)", VoiceSounds.Text(VoiceSounds.Tag(SpeechEngines.Dia, "gasp")!));
        Assert.DoesNotContain(VoiceSounds.Supported(SpeechEngines.Dia), tag => tag.Cue == "mumble");
        Assert.Equal("[laughs]", VoiceSounds.Text(VoiceSounds.Tag(SpeechEngines.ElevenLabs, "laugh")!));
        // Voices that say words only make none, and say so.
        Assert.Empty(VoiceSounds.Supported(SpeechEngines.F5));
        Assert.Empty(VoiceSounds.Supported(null));
        Assert.Contains("F5-TTS says words only", VoiceSounds.NoSoundsReason(SpeechEngines.F5, true));
        Assert.Contains("no voice", VoiceSounds.NoSoundsReason(null, false));
        Assert.Null(VoiceSounds.NoSoundsReason(SpeechEngines.Chatterbox, true));
        Assert.Equal("Clear throat", VoiceSounds.Label("clear throat"));
        // The engine of a Voice route.
        Assert.Same(SpeechEngines.ElevenLabs, VoiceSounds.EngineOf(Route(SetupRouteType.ElevenLabs, "eleven_v3")));
        Assert.Same(SpeechEngines.Dia, VoiceSounds.EngineOf(Route(SetupRouteType.GatewayF5, SpeechEngines.Dia.DefaultModel)));
        Assert.Null(VoiceSounds.EngineOf(Route(SetupRouteType.OpenAi, "gpt-4o-mini-tts")));
    }

    private static SetupRoute Route(SetupRouteType type, string model) => new()
    {
        Role = SetupRole.Tts, RouteType = type, ProviderAlias = "fixture", Origin = "https://127.0.0.1", ModelId = model,
        ConfigurationRevision = Guid.NewGuid()
    };

    [Fact]
    public void A_voice_sound_never_plays_over_speech_and_waits_for_its_clip()
    {
        var plays = new VoiceSoundMoment(Voice: true, Aloud: true, Stopped: false, Makes: true, MartletSpeaking: false, UserTalking: false,
            Playing: false, Ready: true);
        Assert.Equal(VoiceSoundVerdict.Play, VoiceSoundGate.Decide(plays).Verdict);
        Assert.Equal(VoiceSoundVerdict.Skip, VoiceSoundGate.Decide(plays with { Voice = false }).Verdict);
        Assert.Equal(VoiceSoundVerdict.Skip, VoiceSoundGate.Decide(plays with { Aloud = false }).Verdict);
        Assert.Equal(VoiceSoundVerdict.Skip, VoiceSoundGate.Decide(plays with { Stopped = true }).Verdict);
        Assert.Equal(VoiceSoundVerdict.Skip, VoiceSoundGate.Decide(plays with { Makes = false }).Verdict);
        Assert.Equal("Martlet is speaking", VoiceSoundGate.Decide(plays with { MartletSpeaking = true }).Why);
        Assert.Equal("you are talking", VoiceSoundGate.Decide(plays with { UserTalking = true }).Why);
        Assert.Equal(VoiceSoundVerdict.Skip, VoiceSoundGate.Decide(plays with { Playing = true }).Verdict);
        // A clip not made yet is made, never asked of the voice at the touch.
        Assert.Equal(VoiceSoundVerdict.Make, VoiceSoundGate.Decide(plays with { Ready = false }).Verdict);
        // Speaking wins over a missing clip: nothing is made beside a reply from here.
        Assert.Equal(VoiceSoundVerdict.Skip, VoiceSoundGate.Decide(plays with { Ready = false, MartletSpeaking = true }).Verdict);
    }

    [Fact]
    public void Voice_sound_clips_are_kept_per_voice_and_cue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "martlet-voice-sounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("clear_throat.pcm", VoiceSoundLibrary.FileName("clear throat"));
            Assert.Null(VoiceSoundLibrary.FileName("../x"));
            Assert.Null(VoiceSoundLibrary.FileName("Laugh"));
            Assert.Null(VoiceSoundLibrary.Load(folder, "key", "laugh"));
            var clip = Tone(400);
            Assert.True(VoiceSoundLibrary.Save(folder, "key", "clear throat", clip));
            Assert.Equal(clip, VoiceSoundLibrary.Load(folder, "key", "clear throat"));
            Assert.Null(VoiceSoundLibrary.Load(folder, "other", "clear throat"));
            var made = Assert.Single(VoiceSoundLibrary.Made(folder, "key"));
            Assert.Equal("clear throat", made.Cue);
            Assert.Equal(400, made.Duration.TotalMilliseconds, 1);
            // Too long to be a voice sound.
            Assert.False(VoiceSoundLibrary.Save(folder, "key", "laugh", Tone(3_000)));
            // A sound may be longer than a quick sound, up to its own limit.
            var trimmed = QuickSoundAudio.Prepare([.. new byte[4_800], .. Tone(3_000), .. new byte[4_800]], VoiceSoundLibrary.MaximumLength);
            Assert.Equal(VoiceSoundLibrary.MaximumBytes, trimmed.Length);
            Assert.Equal(QuickSoundAudio.MaximumBytes, QuickSoundAudio.Prepare(Tone(3_000)).Length);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_voice_sound_plays_whole_on_its_own_run_and_one_at_a_time()
    {
        await using var h = new Harness();
        var clip = Tone(300);
        var output = new OutputSelection(OutputPolicy.DefaultAtStart);
        var playing = h.Runtime.PlayClipAsync(clip, output, CancellationToken.None);
        Assert.True(h.Runtime.ClipPlaying);
        Assert.False(await h.Runtime.PlayClipAsync(clip, output, CancellationToken.None));
        await Harness.Until(() => playing.IsCompleted, h.Clock);
        Assert.True(await playing);
        Assert.False(h.Runtime.ClipPlaying);
        Assert.Equal(1, h.Device.Opens);
        Assert.True(h.Device.Bytes.AsSpan().SequenceEqual(clip));
        // Nothing to play.
        Assert.False(await h.Runtime.PlayClipAsync(Array.Empty<byte>(), output, CancellationToken.None));
    }
}

using Martlet.Audio.Tests;
using Martlet.Core.Settings;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

/// <summary>Companion › Voice › Quick sounds while Martlet thinks: the rules (a slow, confirmed, spoken reply only; never twice,
/// at most one every 20 s, clips take turns), the turn that plays one ahead of its own voice without cutting either, the watcher
/// that counts from the moment a reply is confirmed (never while a reply started early is held), and the clips kept on disk.</summary>
public sealed class QuickSoundTests
{
    private static readonly QuickSoundOptions On = new() { Enabled = true };

    private static QuickSoundMoment Moment(int milliseconds, bool confirmed = true, bool spoken = true, bool audio = false,
        bool reasoning = false, bool paused = false, bool ended = false) =>
        new(spoken, confirmed, TimeSpan.FromMilliseconds(milliseconds), audio, reasoning, paused, ended);

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

    private static byte[] Silence(int milliseconds) => new byte[QuickSoundAudio.SampleRate * milliseconds / 1000 * 2];

    // Lets fixture time pass 5 ms at a time, with the work it wakes running in between.
    private static async Task Pass(Harness h, int milliseconds)
    {
        for (var passed = 0; passed < milliseconds; passed += 5)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1);
        }
    }

    private static void SlowThinking(Harness h, TaskCompletionSource answer) =>
        h.Llm.Respond = async (_, token) =>
        {
            await answer.Task.WaitAsync(token);
            return TextRecordingHandler.Sse(Harness.Trace("Hello fixture."));
        };

    [Fact]
    public void A_quick_sound_plays_only_when_a_confirmed_spoken_reply_is_slow()
    {
        var gate = new QuickSoundGate(new RuntimeClock());
        Assert.Equal(QuickSoundVerdict.Never, gate.Decide(Moment(2000)).Verdict);
        gate.Options = On;
        Assert.Equal(QuickSoundVerdict.Wait, gate.Decide(Moment(699)).Verdict);
        var slow = gate.Decide(Moment(700));
        Assert.Equal(QuickSoundVerdict.Play, slow.Verdict);
        Assert.Contains("no audio 700 ms after it was confirmed", slow.Why);
        // Not before the reply is confirmed (a reply started early that is still held), however long it has waited.
        Assert.Equal(QuickSoundVerdict.Wait, gate.Decide(Moment(5000, confirmed: false)).Verdict);
        // Never on a fast reply (its own first audio is ready), a reply without a voice, a paused or an ended one.
        Assert.Equal(QuickSoundVerdict.Never, gate.Decide(Moment(900, audio: true)).Verdict);
        Assert.Equal(QuickSoundVerdict.Never, gate.Decide(Moment(900, audio: true, confirmed: false)).Verdict);
        Assert.Equal(QuickSoundVerdict.Never, gate.Decide(Moment(900, spoken: false)).Verdict);
        Assert.Equal(QuickSoundVerdict.Never, gate.Decide(Moment(900, paused: true)).Verdict);
        Assert.Equal(QuickSoundVerdict.Never, gate.Decide(Moment(900, ended: true)).Verdict);
        // A Thinking model that thinks before it answers: sooner.
        Assert.Equal(QuickSoundVerdict.Wait, gate.Decide(Moment(250, reasoning: true)).Verdict);
        Assert.Equal(QuickSoundVerdict.Play, gate.Decide(Moment(300, reasoning: true)).Verdict);
        // The delay the owner chose.
        gate.Options = QuickSoundOptions.Of(true, 1500);
        Assert.Equal(QuickSoundVerdict.Wait, gate.Decide(Moment(1000)).Verdict);
        Assert.Equal(QuickSoundVerdict.Play, gate.Decide(Moment(1500)).Verdict);
        Assert.Equal(QuickSoundOptions.DefaultDelay, QuickSoundOptions.Of(true, 123).Delay);
        Assert.False(QuickSoundOptions.Off.Enabled);
    }

    [Fact]
    public void Quick_sounds_keep_twenty_seconds_apart_and_take_turns()
    {
        var clock = new RuntimeClock();
        var gate = new QuickSoundGate(clock) { Options = On };
        Assert.Equal(-1, gate.Next(0));
        Assert.Equal(0, gate.Next(3));
        gate.Played();
        clock.Advance(TimeSpan.FromSeconds(5));
        var soon = gate.Decide(Moment(1000));
        Assert.Equal(QuickSoundVerdict.Never, soon.Verdict);
        Assert.Contains("5 s ago", soon.Why);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(QuickSoundVerdict.Play, gate.Decide(Moment(1000)).Verdict);
        Assert.Equal(1, gate.Next(3));
        gate.Played();
        gate.Played();
        Assert.Equal(0, gate.Next(3));
        Assert.Equal(3, gate.Count);
    }

    [Fact]
    public async Task A_quick_sound_plays_ahead_of_a_slow_reply_and_the_reply_follows_it_uncut()
    {
        await using var h = new Harness();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowThinking(h, answer);
        var turn = h.Start();
        var clip = Tone(300);
        Assert.True(turn.Spoken);
        Assert.True(turn.PlayQuickSound(clip));
        // Never twice in one reply.
        Assert.False(turn.PlayQuickSound(clip));
        await Harness.Until(() => h.Device.Samples >= clip.Length / 2, h.Clock);
        Assert.Equal(1, h.Device.Opens);
        answer.SetResult();
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        // The quick sound is never part of the reply's text.
        Assert.Equal("Hello fixture.", turn.Content.Text);
        // The whole clip played first, then the whole reply on its own run.
        Assert.Equal(2, h.Device.Opens);
        Assert.Equal(clip.Length / 2 + result.AcceptedSamples, h.Device.Samples);
        Assert.True(h.Device.Bytes.AsSpan(0, clip.Length).SequenceEqual(clip));
        var timings = result.Timings!;
        Assert.NotNull(timings.QuickSoundAfter);
        Assert.True(timings.PlaybackStartedAfter > timings.QuickSoundAfter);
        // The reply latency line says when it played, from the same moment as the line's total.
        var line = ReplyLatency.Describe(null, 0, h.Clock, result, null)!;
        Assert.Contains($"Quick sound at {timings.QuickSoundAfter!.Value.TotalMilliseconds:0} ms.", line);
    }

    [Fact]
    public async Task A_quick_sound_is_refused_for_an_unspoken_held_or_paused_reply_and_once_the_reply_is_heard()
    {
        await using var h = new Harness();
        h.Answer("Hello.");
        var text = h.Start(Harness.Request(speech: false));
        Assert.False(text.Spoken);
        Assert.False(text.PlayQuickSound(Tone(200)));
        await Harness.Finish(text, h.Clock);
        await Harness.Until(() => text.OwnershipRelease.IsCompleted, h.Clock);

        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowThinking(h, answer);
        var held = h.Runtime.StartEarly(Harness.Request(), h.Permissions, prepareVoice: true);
        // Not while it is held: it may still be let go.
        Assert.False(held.PlayQuickSound(Tone(200)));
        Assert.True(held.Release());
        // Not while it is paused because the user talks over it.
        Assert.True(held.Pause());
        Assert.False(held.PlayQuickSound(Tone(200)));
        Assert.True(held.Resume());
        answer.SetResult();
        await Harness.Until(() => held.Snapshot.FirstAudioAfter is not null, h.Clock);
        // Not once its own audio started.
        Assert.False(held.PlayQuickSound(Tone(200)));
        var result = await Harness.Finish(held, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Null(result.Timings!.QuickSoundAfter);
        Assert.Equal(1, h.Device.Opens);
    }

    [Fact]
    public async Task The_watcher_counts_from_the_moment_the_reply_is_confirmed_and_plays_once()
    {
        await using var h = new Harness();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowThinking(h, answer);
        var gate = new QuickSoundGate(h.Clock) { Options = On };
        var turn = h.Runtime.StartEarly(Harness.Request(), h.Permissions, prepareVoice: true);
        var watching = QuickSoundWatcher.WatchAsync(turn, gate, [new("Hmm...", Tone(200))], () => !turn.Held, reasoning: false, h.Clock);
        // A second passes while the user's turn is still open: nothing plays.
        await Pass(h, 1000);
        Assert.False(watching.IsCompleted);
        Assert.Equal(0, h.Device.Opens);
        Assert.True(turn.Release());
        await Pass(h, 600);
        Assert.False(watching.IsCompleted);
        Assert.Equal(0, h.Device.Opens);
        await Harness.Until(() => watching.IsCompleted, h.Clock);
        var outcome = await watching;
        Assert.True(outcome.Played);
        Assert.Equal("Hmm...", outcome.Clip);
        Assert.InRange(outcome.AfterConfirmed!.Value.TotalMilliseconds, 700, 800);
        answer.SetResult();
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(1, gate.Count);
        Assert.Equal(2, h.Device.Opens);
        await Harness.Until(() => turn.OwnershipRelease.IsCompleted, h.Clock);

        // A reply started early that is let go never gets one.
        var answer2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowThinking(h, answer2);
        var letGo = h.Runtime.StartEarly(Harness.Request(), h.Permissions, prepareVoice: true);
        var none = QuickSoundWatcher.WatchAsync(letGo, gate, [new("Mm,", Tone(200))], () => !letGo.Held, reasoning: false, h.Clock);
        await Pass(h, 1000);
        _ = letGo.StopAsync();
        await Harness.Until(() => letGo.Completion.IsCompleted, h.Clock);
        await Harness.Until(() => none.IsCompleted, h.Clock);
        var nothing = await none;
        Assert.False(nothing.Played);
        Assert.Equal("the reply ended", nothing.Why);
        answer2.TrySetResult();
        Assert.Equal(2, h.Device.Opens);
    }

    [Fact]
    public async Task The_watcher_plays_nothing_when_the_reply_is_quick()
    {
        await using var h = new Harness();
        h.Answer("Quick answer.");
        // A long delay: the reply's own audio is always ready first here, however slow the test machine is.
        var gate = new QuickSoundGate(h.Clock) { Options = On with { Delay = TimeSpan.FromSeconds(5) } };
        var turn = h.Start();
        var watching = QuickSoundWatcher.WatchAsync(turn, gate, [new("Mm,", Tone(200))], () => true, reasoning: false, h.Clock);
        var result = await Harness.Finish(turn, h.Clock);
        await Harness.Until(() => watching.IsCompleted, h.Clock);
        var outcome = await watching;
        Assert.False(outcome.Played);
        Assert.Equal("its own first audio was ready in time", outcome.Why);
        Assert.Null(result.Timings!.QuickSoundAfter);
        Assert.Equal(1, h.Device.Opens);
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public void A_clip_is_cut_to_the_voice_and_kept_short()
    {
        var clip = QuickSoundAudio.Prepare([.. Silence(200), .. Tone(300), .. Silence(400)]);
        // The voice and 30 ms either side, fading in from silence.
        Assert.Equal((300 + 60) * QuickSoundAudio.SampleRate / 1000 * 2, clip.Length);
        Assert.Equal(0, (short)(clip[0] | clip[1] << 8));
        Assert.Equal(QuickSoundAudio.MaximumBytes, QuickSoundAudio.Prepare(Tone(3000)).Length);
        Assert.Empty(QuickSoundAudio.Prepare(Silence(500)));
        Assert.Equal(QuickSoundPhrases.Base, QuickSoundPhrases.For(SpeechEngines.Chatterbox));
        Assert.Equal("(inhales)", QuickSoundPhrases.For(SpeechEngines.Dia)[^1]);
    }

    [Fact]
    public void Quick_sounds_are_kept_per_voice_and_character_and_read_back()
    {
        SetupRoute Route(SetupRouteType type, string? voice) => new()
        {
            Role = SetupRole.Tts, RouteType = type, ProviderAlias = "fixture", Origin = "https://example.invalid/",
            ModelId = "fixture-voice", VoiceId = voice, ConfigurationRevision = Guid.NewGuid()
        };
        var host = QuickSoundLibrary.Voice(Route(SetupRouteType.GatewayF5, null) with
        {
            Gateway = new()
            {
                SchemaVersion = 1, Origin = "https://192.168.1.20:9443", HostId = "diva",
                SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = SelfHostSetup.GatewayRole
            },
            Reference = new()
            {
                SchemaVersion = 1, PresetId = Guid.NewGuid(), PresetName = "Wren", ReferenceRevision = "r1", AudioSha256 = new string('b', 64),
                TranscriptRevision = "t1", StoreRevision = 1, ProcessingDestinationId = "diva", RightsAcknowledgementId = Guid.NewGuid(),
                RightsStatementVersion = "1", AppliedAtUtc = DateTimeOffset.UnixEpoch, ApplyRevision = Guid.NewGuid()
            }
        })!.Value;
        var cloud = QuickSoundLibrary.Voice(Route(SetupRouteType.OpenAi, "coral"))!.Value;
        Assert.False(host.Paid);
        // A saved Windows voice from an older Martlet has no quick sounds: Martlet no longer speaks with Windows voices.
        Assert.Null(QuickSoundLibrary.Voice(Route(SetupRouteType.LocalWindowsTts, "TTS_MS_EN-US_ZIRA_11.0")));
        Assert.True(cloud.Paid);
        Assert.Null(QuickSoundLibrary.Voice(Route(SetupRouteType.GatewayF5, null)));
        Assert.Null(QuickSoundLibrary.Voice(null));
        var persona = Guid.NewGuid();
        var key = QuickSoundLibrary.Key(host.Identity, QuickSoundLibrary.Character(persona));
        Assert.NotEqual(key, QuickSoundLibrary.Key(host.Identity, QuickSoundLibrary.Character(Guid.NewGuid())));
        Assert.NotEqual(key, QuickSoundLibrary.Key(cloud.Identity, QuickSoundLibrary.Character(persona)));
        Assert.Equal("none", QuickSoundLibrary.Character(null));

        var directory = Path.Combine(Path.GetTempPath(), "Martlet.QuickSounds." + Guid.NewGuid().ToString("N"));
        try
        {
            var set = new QuickSoundSet(key, host.Words, DateTimeOffset.UnixEpoch, [new("Mm,", Tone(300)), new("Hmm...", Tone(500, 2000))]);
            Assert.True(QuickSoundLibrary.Save(directory, set));
            var read = QuickSoundLibrary.Load(directory, key)!;
            Assert.Equal(["Mm,", "Hmm..."], read.Clips.Select(c => c.Text));
            Assert.True(read.Clips[1].Pcm.Span.SequenceEqual(set.Clips[1].Pcm.Span));
            Assert.Equal(host.Words, read.Voice);
            Assert.Null(QuickSoundLibrary.Load(directory, QuickSoundLibrary.Key(cloud.Identity, "none")));
            Assert.Single(QuickSoundLibrary.All(directory));
            // A clip that grew past the limit on disk isn't trusted.
            File.WriteAllBytes(Path.Combine(QuickSoundLibrary.Directory(directory, key), "0.pcm"), new byte[QuickSoundAudio.MaximumBytes + 2]);
            Assert.Null(QuickSoundLibrary.Load(directory, key));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

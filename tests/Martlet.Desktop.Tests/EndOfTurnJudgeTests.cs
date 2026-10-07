using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Providers;
using Martlet.Sherpa;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>The end-of-turn judge: Smart Turn's features match Pipecat's reference implementation, the bundled model runs (the
/// build puts it and ONNX Runtime beside these tests), the quick transcript is reused and Companion › Listening's choice and
/// status.</summary>
public sealed class EndOfTurnJudgeTests
{
    // Two tones rising over the first 0.25 s, 2 s long: the same signal Pipecat's numpy features were computed from.
    private static float[] Signal()
    {
        var samples = new float[32000];
        for (var n = 0; n < samples.Length; n++)
            samples[n] = (float)((0.3 * Math.Sin(2 * Math.PI * 220 * n / 16000) + 0.1 * Math.Sin(2 * Math.PI * 1250 * n / 16000)) * Math.Min(1, n / 4000.0));
        return samples;
    }

    [Fact]
    public void Features_match_the_reference_whisper_features()
    {
        var features = WhisperFeatures.Compute(Signal());
        Assert.Equal(80 * 800, features.Length);
        float At(int band, int frame) => features[band * 800 + frame];
        // compute_whisper_log_mel_features (pipecat smart_turn/_whisper_features.py) on the same 8 s, zeros in front.
        Assert.Equal(-0.18717086, At(0, 0), 3);
        Assert.Equal(0.593477, At(10, 700), 3);
        Assert.Equal(-0.2129213, At(40, 750), 3);
        Assert.Equal(-0.14233655, At(79, 799), 3);
        Assert.Equal(-0.12207177, features.Average(), 3);
        Assert.Equal(1.7870787, features.Max(), 3);
        Assert.Equal(-0.2129213, features.Min(), 3);
    }

    [Fact]
    public void The_bundled_model_judges_like_the_reference_runtime()
    {
        var judge = SmartTurnEngine.Bundled(AppContext.BaseDirectory);
        Assert.NotNull(judge);
        using (judge)
        {
            judge!.Warm();
            // onnxruntime 1.29 in Python on the reference features gives 0.374717.
            Assert.Equal(0.3747, judge.Probability(Signal()), 2);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            judge.Probability(Signal());
            Assert.InRange(watch.ElapsedMilliseconds, 0, 500);
        }
    }

    [Fact]
    public async Task The_desktop_judge_answers_with_the_bundled_model()
    {
        using var judge = new SmartTurnJudge(SmartTurnEngine.Bundled(AppContext.BaseDirectory));
        Assert.True(judge.Available);
        await judge.WarmAsync();
        Assert.True(judge.Loaded);
        var pcm = new byte[Signal().Length * 2];
        var samples = Signal();
        for (var i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(samples[i] * 32767));
        var answer = await judge.JudgeAsync(new(pcm, TimeSpan.FromMilliseconds(260)), CancellationToken.None);
        Assert.Equal(TurnVerdict.Incomplete, answer.Verdict);
        Assert.InRange(answer.Probability!.Value, 0.3, 0.45);
    }

    [Fact]
    public void A_missing_model_makes_the_judge_unavailable_so_the_plain_pause_decides()
    {
        using var judge = new SmartTurnJudge(null);
        Assert.False(judge.Available);
        Assert.Contains("isn't in Martlet's folder", judge.Problem);
        Assert.Null(SmartTurnEngine.Bundled(Path.GetTempPath()));
        Assert.Contains("can't run here", MainWindow.TurnJudgeText(true, (judge.Name, false, judge.Problem, null, [])));
        Assert.Equal("Off. The pause above alone decides when you finished talking.", MainWindow.TurnJudgeText(false, null));
    }

    [Fact]
    public async Task Speech_to_text_reuses_the_quick_transcript_through_the_usual_authorization()
    {
        var again = new CountingTranscriber();
        var reused = new ReusedWords(Task.FromResult(new LocalTranscript("Can you remind me?")), again);
        var adapter = new LocalTranscriptionAdapter(reused);
        var (context, audio, limits, permission) = Request("parakeet-tdt-0.6b-v3-int8");
        var result = await adapter.TranscribeAsync(context, "parakeet-tdt-0.6b-v3-int8", audio, limits, permission, CancellationToken.None);
        Assert.Equal(TranscriptionOutcome.Completed, result.Outcome);
        Assert.Equal("Can you remind me?", result.Text);
        Assert.True(reused.Reused);
        Assert.Equal(0, again.Calls);
        // The one-use permission was spent as for any transcription.
        var second = await adapter.TranscribeAsync(context, "parakeet-tdt-0.6b-v3-int8", audio, limits, permission, CancellationToken.None);
        Assert.Equal(ProviderFailureCode.ConsentConsumed, second.Failure?.Code);
    }

    [Fact]
    public async Task A_failed_quick_transcript_is_transcribed_again()
    {
        var again = new CountingTranscriber();
        var reused = new ReusedWords(Task.FromException<LocalTranscript>(new InvalidOperationException("unloaded")), again);
        var (context, audio, limits, permission) = Request("parakeet-tdt-0.6b-v3-int8");
        var result = await new LocalTranscriptionAdapter(reused).TranscribeAsync(context, "parakeet-tdt-0.6b-v3-int8", audio, limits, permission,
            CancellationToken.None);
        Assert.Equal("again", result.Text);
        Assert.False(reused.Reused);
        Assert.Equal(1, again.Calls);
    }

    [Fact]
    public void Judging_turns_is_on_by_default_and_stays_off_once_turned_off()
    {
        Assert.True(new TalkPreferences().JudgeTurns);
        var directory = Directory.CreateTempSubdirectory("martlet-judge-");
        try
        {
            // A file saved before the choice existed has it on.
            File.WriteAllText(Path.Combine(directory.FullName, "talk-preferences.json"), JsonSerializer.Serialize(new { HandsFree = true, Version = 4 }));
            Assert.True(TalkPreferences.Load(directory.FullName).JudgeTurns);
            Assert.True((new TalkPreferences() with { JudgeTurns = false }).Save(directory.FullName));
            Assert.False(TalkPreferences.Load(directory.FullName).JudgeTurns);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void The_status_counts_the_newest_decisions()
    {
        var at = DateTimeOffset.UnixEpoch;
        var plain = TimeSpan.FromMilliseconds(800);
        EndOfTurnDecision[] decisions =
        [
            new(at, EndOfTurnDecision.Complete, TimeSpan.FromMilliseconds(300), plain, TimeSpan.FromMilliseconds(30), 0.9, "Smart Turn v3.2"),
            new(at, EndOfTurnDecision.WentOn, TimeSpan.FromMilliseconds(500), plain, TimeSpan.FromMilliseconds(40), 0.2, "Smart Turn v3.2"),
            new(at, EndOfTurnDecision.Slow, plain, plain, Judge: "Smart Turn v3.2"),
            new(at, EndOfTurnDecision.Complete, TimeSpan.FromMilliseconds(280), plain, TimeSpan.FromMilliseconds(28), 0.95, "Smart Turn v3.2")
        ];
        Assert.Equal("On. Smart Turn v3.2 on this PC (loaded in 120 ms). Last 4 pauses: 2 finished, 1 unfinished, 1 left to the pause; " +
            "judge median 30 ms. Last: complete after 280 ms of silence.",
            MainWindow.TurnJudgeText(true, ("Smart Turn v3.2", true, null, TimeSpan.FromMilliseconds(120), decisions)));
    }

    private static (ProviderRequestContext, BoundedWaveAudio, TranscriptionLimits, AudioUploadAuthorization) Request(string model)
    {
        var context = new ProviderRequestContext
        {
            Ids = new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, Epoch = 1,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30)
        };
        var audio = BoundedWaveAudio.FromPcm(Martlet.Audio.CapturedUtterance.Format, new byte[16000]);
        var limits = LiveConversationConfiguration.TranscriptionLimits;
        var permission = new AudioUploadAuthorization(LocalTranscriptionAdapter.Binding(model), context.Ids, context.Epoch, limits,
            DateTimeOffset.UtcNow.AddSeconds(30), true, false);
        return (context, audio, limits, permission);
    }

    private sealed class CountingTranscriber : ILocalTranscriber
    {
        internal int Calls;

        public Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new LocalTranscript("again"));
        }
    }
}

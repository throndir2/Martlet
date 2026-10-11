using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class TranscriptionPiecesTests
{
    private static BoundedWaveAudio Talking(double seconds)
    {
        var samples = (int)(seconds * 16_000);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(i % 16_000 < 15_000 ? 6_000 * Math.Sin(i * 0.2) : 0));
        return BoundedWaveAudio.FromPcm(ProviderFixtures.Format(), pcm);
    }

    [Fact]
    public void TenMinutesOfTalkingFitsOneRecordingAndSplitsIntoRequestSizedPiecesWithNothingLost()
    {
        var audio = Talking(600);
        Assert.Equal(TimeSpan.FromMinutes(10), audio.Duration);
        var pieces = audio.Split(TimeSpan.FromSeconds(25));
        Assert.True(pieces.Count >= 24);
        Assert.All(pieces, piece => Assert.True(piece.Duration <= TimeSpan.FromSeconds(25)));
        Assert.Equal(audio.SamplesPerChannel, pieces.Sum(piece => piece.SamplesPerChannel));
        Assert.Same(pieces[0], pieces[0].Split(TimeSpan.FromSeconds(25)).Single());
        // Longer than OpenAI's 25 MB upload is still refused.
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(ProviderFixtures.Format(), new byte[TranscriptionLimits.HardMaxAudioBytes]));
    }

    [Fact]
    public void PiecesHeardInTurnJoinIntoOneTranscriptInOrder()
    {
        var context = ProviderFixtures.Context();
        var first = new TranscriptionResult(context, EvidenceProvenance.Fixture, TranscriptionOutcome.Completed, " Once upon a time ",
            ["en"], new(UsageKind.Duration, Seconds: 24), evidence: new()
            {
                Engine = "parakeet", MeanProbability = 0.9, MinimumProbability = 0.5,
                WordsStart = TimeSpan.FromSeconds(1), WordsEnd = TimeSpan.FromSeconds(23)
            });
        var silent = new TranscriptionResult(context, EvidenceProvenance.Fixture, TranscriptionOutcome.NoSpeech,
            usage: new(UsageKind.Duration, Seconds: 20));
        var last = new TranscriptionResult(context, EvidenceProvenance.Fixture, TranscriptionOutcome.Completed, "there was a dragon.",
            ["en"], new(UsageKind.Duration, Seconds: 10), evidence: new()
            {
                Engine = "parakeet", MeanProbability = 0.7, MinimumProbability = 0.3,
                WordsStart = TimeSpan.FromSeconds(0.5), WordsEnd = TimeSpan.FromSeconds(9)
            });
        var whole = TranscriptionResult.Join([first, silent, last],
            [TimeSpan.Zero, TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(44)]);
        Assert.Equal(TranscriptionOutcome.Completed, whole.Outcome);
        Assert.Equal("Once upon a time there was a dragon.", whole.Text);
        Assert.Equal(["en"], whole.Languages);
        Assert.Equal(54, whole.Usage.Seconds);
        Assert.Equal(0.8, whole.Evidence!.MeanProbability!.Value, 3);
        Assert.Equal(0.3, whole.Evidence.MinimumProbability);
        Assert.Equal(TimeSpan.FromSeconds(1), whole.Evidence.WordsStart);
        Assert.Equal(TimeSpan.FromSeconds(53), whole.Evidence.WordsEnd);

        var nothing = TranscriptionResult.Join([silent, silent], [TimeSpan.Zero, TimeSpan.FromSeconds(20)]);
        Assert.Equal(TranscriptionOutcome.NoSpeech, nothing.Outcome);
        Assert.Null(nothing.Text);
        var failed = new TranscriptionResult(context, EvidenceProvenance.Fixture, TranscriptionOutcome.Failed);
        Assert.Throws<ContractException>(() => TranscriptionResult.Join([first, failed], [TimeSpan.Zero, TimeSpan.FromSeconds(24)]));
    }
}

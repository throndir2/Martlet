using System.Buffers.Binary;
using Martlet.Core.Audio;

namespace Martlet.Core.Tests;

public sealed class SpeechPiecesTests
{
    private const int Rate = 16_000;

    // Tone (loud) everywhere except quiet gaps of 200 ms starting at the given seconds.
    private static byte[] Speech(double seconds, params double[] pauses)
    {
        var samples = (int)(seconds * Rate);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var at = i / (double)Rate;
            var quiet = pauses.Any(p => at >= p && at < p + 0.2);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), quiet ? (short)0 : (short)(8_000 * Math.Sin(i * 0.3)));
        }
        return pcm;
    }

    [Fact]
    public void ARecordingThatFitsIsOnePiece()
    {
        var pcm = Speech(10);
        Assert.Equal([(0, pcm.Length)], SpeechPieces.Split(pcm, Rate, TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void ALongRecordingIsCutAtItsPausesIntoPiecesThatEachFitAndCoverEverything()
    {
        // Ten minutes of talking with a pause a little before every 25 s mark.
        var pauses = Enumerable.Range(1, 30).Select(i => i * 22.0).ToArray();
        var pcm = Speech(600, pauses);
        var pieces = SpeechPieces.Split(pcm, Rate, TimeSpan.FromSeconds(25));
        Assert.True(pieces.Count >= 24);
        Assert.Equal(0, pieces[0].Start);
        for (var i = 1; i < pieces.Count; i++) Assert.Equal(pieces[i - 1].Start + pieces[i - 1].Length, pieces[i].Start);
        Assert.Equal(pcm.Length, pieces[^1].Start + pieces[^1].Length);
        Assert.All(pieces, piece => Assert.InRange(piece.Length, 2, 25 * Rate * 2));
        Assert.All(pieces, piece => Assert.Equal(0, piece.Start % 2));
        // Every cut falls in a pause, not in the middle of a word.
        foreach (var piece in pieces.Skip(1))
        {
            var at = piece.Start / 2 / (double)Rate;
            Assert.Contains(pauses, p => at >= p && at <= p + 0.2);
        }
    }

    [Fact]
    public void TalkingWithoutPausesIsStillCutIntoPiecesThatFit()
    {
        var pcm = Speech(61);
        var pieces = SpeechPieces.Split(pcm, Rate, TimeSpan.FromSeconds(25));
        Assert.Equal(3, pieces.Count);
        Assert.Equal(pcm.Length, pieces.Sum(p => p.Length));
        Assert.All(pieces, piece => Assert.True(piece.Length <= 25 * Rate * 2));
    }
}

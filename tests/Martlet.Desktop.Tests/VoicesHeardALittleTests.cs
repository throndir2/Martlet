using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Speakers;
using Martlet.Desktop;
using Martlet.Sherpa;

namespace Martlet.Desktop.Tests;

/// <summary>Voices heard only a little (docs/VOICES.md#voices-heard-only-a-little): short remarks of a new voice in the last 30
/// minutes add up to a new voice in People, without any audio kept.</summary>
public sealed class VoicesHeardALittleTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("martlet-voices-a-little-").FullName;

    public VoicesHeardALittleTests()
    {
        // No clips are written in the background, so the folder can be deleted at once.
        File.WriteAllText(Path.Combine(directory, Martlet.Core.Speakers.VoiceChoices.ClipsFile), "off");
    }

    public void Dispose() => Directory.Delete(directory, true);

    private static float[] Voice(int seed, int variant = 0, double noise = 0)
    {
        var random = new Random(seed);
        var vector = VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
        var jitter = new Random(seed * 1000 + variant);
        var amplitude = noise * Math.Sqrt(12) / Math.Sqrt(VoicePrints.Dimension);
        return VoicePrints.Normalize(vector.Select(v => v + (float)((jitter.NextDouble() - 0.5) * amplitude)).ToArray());
    }

    private static SpeakerAnalysis Long(float[] print) => new([new HeardSpeaker(0, print, 3.0, 0.1, 3.2)], 1, false, null, 3.2);

    private static SpeakerAnalysis Short(float[] print, int voices = 1, bool overlap = false) => new([], voices, overlap, print, 1.3);

    private sealed class Engine(Queue<SpeakerAnalysis> next) : IVoiceEngine<SpeakerAnalysis>
    {
        public SpeakerAnalysis Analyze(float[] samples) => next.Dequeue();
        public void Load() { }
        public void Dispose() { }
    }

    [Fact]
    public void Short_remarks_of_a_new_voice_add_it_and_turning_recognition_off_forgets_what_waits()
    {
        var next = new Queue<SpeakerAnalysis>();
        using var voices = new LocalVoices(directory, "desk-1", engine: () => new Engine(next),
            remote: (_, _, _) => throw new InvalidOperationException("No computer is in the list."), queue: new WorkQueue());
        var utterance = new float[2 * 16_000];

        next.Enqueue(Long(Voice(1)));
        Assert.True(voices.Recognize(utterance).Speaker!.Added);

        // One short remark is never enough: it waits, as a voiceprint only.
        next.Enqueue(Short(Voice(5, 1, 0.5)));
        Assert.Null(voices.Recognize(utterance).Speaker!.Voice);
        next.Enqueue(Short(Voice(5, 2, 0.5)));
        Assert.Null(voices.Recognize(utterance).Speaker!.Voice);
        Assert.Equal(2, voices.Unplaced);
        // A short remark where two people spoke can't be one voice's: it isn't kept.
        next.Enqueue(Short(Voice(5, 3, 0.5), voices: 2, overlap: true));
        Assert.Null(voices.Recognize(utterance).Speaker!.Voice);
        Assert.Equal(2, voices.Unplaced);

        next.Enqueue(Short(Voice(5, 4, 0.5)));
        var third = voices.Recognize(utterance).Speaker!;
        Assert.True(third.Added);
        Assert.Equal(VoiceMatchKind.New, third.Kind);
        Assert.Equal(2, voices.Roster.Live.Count);
        Assert.Equal(0, voices.Unplaced);
        // The new voice is recognized from then on, also in a short remark.
        next.Enqueue(Short(Voice(5, 5, 0.3)));
        Assert.Equal(third.Voice!.Id, voices.Recognize(utterance).Speaker!.Voice!.Id);

        next.Enqueue(Short(Voice(9, 1, 0.5)));
        voices.Recognize(utterance);
        Assert.Equal(1, voices.Unplaced);
        voices.SetEnabled(false);
        Assert.Equal(0, voices.Unplaced);
    }
}

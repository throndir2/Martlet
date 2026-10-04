using System.IO;
using Martlet.Core.Speakers;
using Martlet.Desktop;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Runs the real sherpa-onnx engine and models: the runtime and voice models the build puts beside these tests, and
/// Parakeet from MARTLET_SPEECH_ROOT (a data folder's speech directory with Parakeet downloaded). Opt-in: set
/// MARTLET_SPEECH_FIXTURES to a folder with a1.wav, a2.wav (one speaker) and b1.wav (another), 16 kHz mono PCM16 with a 44-byte
/// header. Nothing is downloaded by the test.</summary>
public sealed class LocalSpeechNativeTests
{
    private static string? Root => Environment.GetEnvironmentVariable("MARTLET_SPEECH_ROOT");
    private static string? Fixtures => Environment.GetEnvironmentVariable("MARTLET_SPEECH_FIXTURES");

    private static float[] Read(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures!, name + ".wav"));
        return Pcm.ToFloats(bytes.AsSpan(44));
    }

    [Fact]
    public void VoicesAreRecognizedAcrossUtterancesOnThisPc()
    {
        if (Fixtures is null) return;
        var data = Directory.CreateTempSubdirectory("martlet-voices-");
        try
        {
            using var voices = new LocalVoices(data.FullName, "desk-test");
            Assert.True(voices.Active);
            var first = voices.Recognize(Read("a1"));
            Assert.True(first.Speaker!.Added);
            var other = voices.Recognize(Read("b1"));
            Assert.True(other.Speaker!.Added);
            var again = voices.Recognize(Read("a2"));
            Assert.Equal(VoiceMatchKind.Known, again.Speaker!.Kind);
            Assert.Equal(first.Speaker.Voice!.Id, again.Speaker.Voice!.Id);
            Assert.Equal(2, voices.Roster.Live.Count);
            Assert.True(File.Exists(Path.Combine(data.FullName, LocalVoices.RosterFile)));
            using var reloaded = new LocalVoices(data.FullName, "desk-test");
            Assert.Equal(voices.Roster.Digest(), reloaded.Roster.Digest());
        }
        finally { data.Delete(true); }
    }

    [Fact]
    public async Task ParakeetTranscribesOnThisPc()
    {
        if (Root is null || Fixtures is null) return;
        using var listener = new ParakeetListener(Root);
        Assert.True(listener.Installed);
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures!, "b1.wav"))[44..];
        var heard = await listener.TranscribeAsync(Martlet.Sherpa.SherpaComponents.ParakeetModelId, bytes, CancellationToken.None);
        Assert.Contains("sister", heard.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("parakeet", heard.Evidence?.Engine);
    }
}

using System.Buffers.Binary;
using Martlet.Audio;
using Martlet.Core.Voices;
using Martlet.Desktop;
using Martlet.F5;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Record your own voice: one recording of each line becomes one voice of the owner's own, with the lines' words.</summary>
public sealed class OwnVoiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-own-voice-" + Guid.NewGuid().ToString("N"));

    /// <summary>What the microphone records for a line: quiet, <paramref name="seconds"/> of voice (a tone), then the pause.</summary>
    private static OwnVoiceTake Line(double seconds, int seed)
    {
        var rate = OwnVoiceTake.SampleRate;
        var count = (int)((0.8 + seconds + 1.5) * rate);
        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var voice = i >= 0.8 * rate && i < (0.8 + seconds) * rate;
            var sample = (voice ? 0.3 : 0.0005) * Math.Sin(2 * Math.PI * (180 + seed * 20) * i / rate);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(sample * 32767));
        }
        return OwnVoiceTake.From(pcm)!;
    }

    [Fact]
    public async Task The_lines_become_one_voice_of_your_own()
    {
        var takes = OwnVoiceScript.Lines.Select((_, i) => Line(5 + i * 0.5, i)).ToArray();

        var added = await OwnVoiceDialog.AddVoiceAsync(directory, "f5-host", "My voice", takes, CancellationToken.None);

        var voice = Assert.Single(F5Voices.View(directory).Live, v => v.Id == F5SharedVoices.Id(added));
        Assert.Equal("My voice", voice.Name);
        Assert.Equal(SpeakingVoiceRights.OwnVoice, voice.Rights);
        Assert.Equal(string.Join(" ", OwnVoiceScript.Lines), voice.Transcript);
        Assert.Equal(OwnVoiceScript.Lines, voice.Clips!.Select(c => c.Transcript));
        Assert.Equal(OwnVoiceTake.SampleRate, voice.SampleRate);
        Assert.InRange(voice.DurationMilliseconds - OwnVoiceTake.JoinedMilliseconds(takes), -2, 2);
        Assert.Equal("f5-host", added.Rights.ProcessingDestinationId);
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

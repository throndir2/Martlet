using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Tests;
using Martlet.Discord.Calls;

namespace Martlet.Desktop.Tests;

/// <summary>Martlet in your own Discord calls on the desktop: where Martlet's voice goes (the chosen call output, mirrored to the
/// usual output) and who spoke what the call's listener heard.</summary>
public sealed class DiscordCallsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("martlet-calls-").FullName;
    private static readonly PcmFormat Format = new() { SampleRate = 48_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian };

    public void Dispose() => Directory.Delete(directory, true);

    private sealed class Device(string name, int capacity = 4800) : IPlaybackDevice
    {
        public string Name { get; } = name;
        public int Written { get; private set; }
        public int Padding { get; set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailWrites { get; set; }
        public PlaybackDeviceInfo Info { get; } = new(48_000, 2, 32, DeviceSampleEncoding.IeeeFloat, capacity, true);
        public int GetPadding(CancellationToken cancellationToken) => Padding;
        public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
        {
            if (FailWrites) throw new Martlet.Core.Contracts.ContractException(Martlet.Core.Contracts.ErrorCode.AudioDeviceLost, "lost");
            Written += pcm.Length / 2;
            return pcm.Length / 2;
        }
        public void Start(CancellationToken cancellationToken) => Started = true;
        public void StopAndReset() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class Speakers : IPlaybackDeviceFactory
    {
        public List<OutputSelection> Opened { get; } = [];
        public List<Device> Devices { get; } = [];
        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken)
        {
            Opened.Add(selection);
            var device = new Device(selection.EndpointId ?? "default");
            Devices.Add(device);
            return device;
        }
    }

    private DiscordCallService Calls(DiscordCallPreferences preferences)
    {
        Assert.True(preferences.Save(directory));
        return new(directory, _ => null, new NoText());
    }

    private sealed class NoText : ICallTextReader
    {
        public Task<IReadOnlyList<TextLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TextLine>>([]);
    }

    private static readonly OutputSelection Usual = new(OutputPolicy.DefaultAtStart);

    [Fact]
    public void OffTheVoiceGoesWhereItAlwaysWent()
    {
        var speakers = new Speakers();
        var calls = Calls(new() { On = false, OutputId = "{cable}", OutputName = "CABLE Input" });
        using var device = calls.Output(speakers).Open(Usual, Format, default);
        Assert.Equal([Usual], speakers.Opened);
    }

    [Fact]
    public void OnWithACableTheVoiceGoesIntoTheCallAndAlsoToTheUsualOutput()
    {
        var speakers = new Speakers();
        var calls = Calls(new() { On = true, OutputId = "{cable}", OutputName = "CABLE Input" });
        using var device = calls.Output(speakers).Open(Usual, Format, default);
        Assert.Equal([new OutputSelection(OutputPolicy.FixedEndpoint, "{cable}"), Usual], speakers.Opened);
        var mirrored = Assert.IsType<MirroredPlaybackDevice>(device);
        device.Start(default);
        Assert.Equal(480, mirrored.Write(new byte[960], default));
        Assert.All(speakers.Devices, opened => Assert.Equal(480, opened.Written));
        Assert.All(speakers.Devices, opened => Assert.True(opened.Started));
    }

    [Fact]
    public void TheUsualOutputTakesOnlyWhatFitsAndNeverHoldsTheCallBack()
    {
        var speakers = new Speakers();
        var calls = Calls(new() { On = true, OutputId = "{cable}", OutputName = "CABLE Input" });
        using var device = calls.Output(speakers).Open(Usual, Format, default);
        speakers.Devices[1].Padding = 4800 - 100;
        Assert.Equal(480, device.Write(new byte[960], default));
        Assert.Equal((480, 100), (speakers.Devices[0].Written, speakers.Devices[1].Written));
        speakers.Devices[1].Padding = 0;
        speakers.Devices[1].FailWrites = true;
        Assert.Equal(480, device.Write(new byte[960], default));
        Assert.Equal(960, speakers.Devices[0].Written);
        device.Dispose();
        Assert.All(speakers.Devices, opened => Assert.True(opened.Disposed));
    }

    [Fact]
    public void OnlyTheCallWhenAskedAndTheUsualOutputWhenTheCableIsGone()
    {
        var speakers = new Speakers();
        var calls = Calls(new() { On = true, OutputId = "{cable}", OutputName = "CABLE Input", AlsoSpeakers = false });
        using (var device = calls.Output(speakers).Open(Usual, Format, default)) Assert.IsType<Device>(device);
        Assert.Equal([new OutputSelection(OutputPolicy.FixedEndpoint, "{cable}")], speakers.Opened);
        speakers.Opened.Clear();
        calls.Outputs = [new("{speakers}", "Speakers", true)];
        using (calls.Output(speakers).Open(Usual, Format, default)) { }
        Assert.Equal([Usual], speakers.Opened);
    }

    [Fact]
    public void EachUtteranceIsNamedAfterWhoWasLitWhileItWasHeard()
    {
        var clock = new ManualClock();
        var pictures = 0;
        var calls = new DiscordCallService(directory, _ => { pictures++; return new(new byte[64 * 64 * 4], 64, 64); }, new NoText());
        Assert.True(calls.Save(saved => saved with { On = true }));
        // Nobody is lit in the fixture picture: the utterance is "someone".
        calls.Attribution.Tick(true, clock);
        SpinWait.SpinUntil(() => pictures > 0, 2000);
        clock.Advance(TimeSpan.FromSeconds(2));
        calls.Attribution.Tick(false, clock);
        Assert.Null(calls.Attribution.Take());
        Assert.Equal(0, calls.Attribution.Attributed);
        // Seeing speakers off takes no pictures.
        Assert.True(calls.Save(saved => saved with { SeeSpeakers = false }));
        var before = pictures;
        calls.Attribution.Tick(true, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        calls.Attribution.Tick(true, clock);
        Assert.Equal(before, pictures);
        Assert.Equal("off", calls.Attribution.Source);
    }
}

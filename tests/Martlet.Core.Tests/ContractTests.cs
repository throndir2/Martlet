using System.Text;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ContractTests
{
    private static CorrelationIds Ids => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
    private static byte[] Golden(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", name));
    private static ProviderEvent Event => ContractJson.Read<ProviderEvent>(Golden("event-completed.json"));

    [Theory]
    [InlineData("settings-v1.json")]
    public void SettingsGoldenRoundTrips(string name)
    {
        var settings = SettingsJson.Read(Golden(name));
        Assert.Equal(settings.Profile.Id, SettingsJson.Read(ContractJson.Write(settings)).Profile.Id);
        Assert.Equal(ProfileKind.NotConfigured, settings.Profile.Kind);
    }

    [Theory]
    [InlineData("event-completed.json", ProviderEventKind.Completed)]
    [InlineData("event-delta.json", ProviderEventKind.TextDelta)]
    [InlineData("event-refused.json", ProviderEventKind.Refused)]
    [InlineData("event-canceled.json", ProviderEventKind.Canceled)]
    [InlineData("event-failed.json", ProviderEventKind.Failed)]
    [InlineData("event-no-speech.json", ProviderEventKind.NoSpeech)]
    public void EventGoldenVariantsRoundTrip(string name, ProviderEventKind kind)
    {
        var value = ContractJson.Read<ProviderEvent>(Golden(name));
        Assert.Equal(kind, value.Kind);
        Assert.Equal(EvidenceProvenance.Fixture, value.Provenance);
        Assert.Equal(value, ContractJson.Read<ProviderEvent>(ContractJson.Write(value)));
        Assert.Equal(kind != ProviderEventKind.TextDelta, value.IsTerminal);
    }

    [Theory]
    [InlineData("invalid-event-version.json")]
    [InlineData("invalid-event-enum.json")]
    [InlineData("invalid-event-error.json")]
    [InlineData("invalid-event-provenance.json")]
    public void InvalidGoldenPayloadsAreRejected(string name) =>
        Assert.Throws<ContractException>(() => ContractJson.Read<ProviderEvent>(Golden(name)));

    [Fact]
    public void NewOptionalFieldsAndMinorVersionsWorkButDuplicateFieldsDoNot()
    {
        var json = Encoding.UTF8.GetString(ContractJson.Write(Event with { Version = new() { Major = 1, Minor = 9 } }));
        var additive = json.Replace("\"sequence\": 0,", "\"sequence\": 0, \"future_optional\": {\"flag\": true},", StringComparison.Ordinal);
        Assert.Equal(9, ContractJson.Read<ProviderEvent>(Encoding.UTF8.GetBytes(additive)).Version.Minor);
        var duplicate = json.Replace("\"sequence\": 0,", "\"sequence\": 0, \"sequence\": 1,", StringComparison.Ordinal);
        Assert.Throws<ContractException>(() => ContractJson.Read<ProviderEvent>(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public void JsonStrictnessAndSizeLimitsAreExecutable()
    {
        var json = Encoding.UTF8.GetString(Golden("event-completed.json"));
        foreach (var invalid in new[]
        {
            json.Replace("\"completed\"", "2", StringComparison.Ordinal),
            json.Replace("\"provider_id\": \"fixture\"", "\"provider_id\": null", StringComparison.Ordinal),
            json.Replace("\"sequence\": 0,", "", StringComparison.Ordinal),
            json + " true",
            "{\"nested\":" + new string('[', 20) + "0" + new string(']', 20) + "}"
        })
            Assert.Throws<ContractException>(() => ContractJson.Read<ProviderEvent>(Encoding.UTF8.GetBytes(invalid)));
        Assert.Equal(ErrorCode.PayloadTooLarge, Assert.Throws<ContractException>(() =>
            ContractJson.Read<ProviderEvent>(new byte[ContractRules.MaxJsonBytes + 1])).Code);
        Assert.Throws<ContractException>(() => ContractJson.Write(Event, maximumBytes: 10));
    }

    [Fact]
    public void EventVariantsEnforceBoundsAndPrivacySeparation()
    {
        Assert.Throws<ContractException>(() => (Event with { Sequence = -1 }).Validate());
        Assert.Throws<ContractException>(() => (Event with { Epoch = long.MaxValue }).Validate());
        Assert.Throws<ContractException>(() => (Event with { Text = new string('x', ContractRules.MaxTextCharacters + 1) }).Validate());
        Assert.Throws<ContractException>(() => (Event with { Text = "\0" }).Validate());
        Assert.Throws<ContractException>(() => (Event with { FinalSampleCount = 4_320_001 }).Validate());
        Assert.Throws<ContractException>(() => (Event with { Ids = Ids with { RequestId = Guid.Empty } }).Validate());
        Assert.Throws<ContractException>(() => (Event with { Kind = ProviderEventKind.TextDelta, Text = "" }).Validate());
        Assert.Throws<ContractException>(() => (Event with { Kind = ProviderEventKind.Canceled, Text = "unexpected" }).Validate());
        (Event with { Provenance = EvidenceProvenance.Live }).Validate();
        (Event with { Kind = ProviderEventKind.Started, Text = null }).Validate();
    }

    [Fact]
    public void SuppressionIsNotFailure()
    {
        var result = ContractJson.Read<TurnResult>(Golden("turn-suppressed.json"));
        Assert.Equal(TurnOutcome.Suppressed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(result, ContractJson.Read<TurnResult>(ContractJson.Write(result)));
        Assert.Throws<ContractException>(() => (result with { Suppression = null }).Validate());
        Assert.Throws<ContractException>(() => (result with { Outcome = TurnOutcome.Failed }).Validate());
        foreach (var outcome in new[] { TurnOutcome.Completed, TurnOutcome.Canceled })
            (result with { Outcome = outcome, Suppression = null }).Validate();
    }

    [Fact]
    public void AudioTransportDoesNotImplyIncrementalSynthesisOrComputeCancel()
    {
        var caps = ContractJson.Read<ProviderCapabilities>(Golden("capabilities-tts.json"));
        caps.RequireFeature(ProviderFeature.TtsAudioTransport);
        Assert.Equal(ErrorCode.ProviderCapability, Assert.Throws<ContractException>(() => caps.RequireFeature(ProviderFeature.TtsIncrementalSynthesis)).Code);
        Assert.Equal(CancellationCapability.DiscardOnly, caps.Cancellation);
        Assert.Throws<ContractException>(() => (caps with { Provenance = EvidenceProvenance.Unknown }).Validate());
        Assert.Throws<ContractException>(() => (caps with { Role = ProviderRole.Llm }).Validate());
        Assert.Throws<ContractException>(() => (caps with { MaxInputBytes = 0 }).Validate());
        Assert.Throws<ContractException>(() => (caps with { TtsAudioTransport = CapabilitySupport.Unknown }).RequireFeature(ProviderFeature.TtsAudioTransport));
    }

    [Theory]
    [InlineData(16000, 1)]
    [InlineData(24000, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 2)]
    public void PcmFramesAreBoundedAlignedAndOwned(int rate, int channels)
    {
        var format = new PcmFormat { SampleRate = rate, Channels = channels, Encoding = PcmEncoding.Signed16LittleEndian };
        var data = new byte[rate / 50 * channels * 2];
        var frame = new PcmFrame(Ids, 0, 0, 0, format, data);
        data[0] = 42;
        Assert.Equal(0, frame.Data.Span[0]);
        Assert.Equal(rate / 50, frame.SamplesPerChannel);
        Assert.Throws<ContractException>(() => new PcmFrame(Ids, 0, 0, 0, format, [1]));
        Assert.Throws<ContractException>(() => new PcmFrame(Ids, 0, 0, 0, format, []));
        Assert.Throws<ContractException>(() => new PcmFrame(Ids, 0, 0, 0, format, new byte[rate / 10 * channels * 2 + channels * 2]));
        Assert.Throws<ContractException>(() => new PcmFrame(Ids, 0, 0, long.MaxValue, format, new byte[channels * 2]));
        Assert.Throws<ContractException>(() => (format with { SampleRate = 8000 }).Validate());
        Assert.Throws<ContractException>(() => (format with { Encoding = (PcmEncoding)7 }).Validate());
    }
}

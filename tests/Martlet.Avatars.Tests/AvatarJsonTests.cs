using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Avatars.Tests;

public sealed class AvatarJsonTests
{
    [Fact]
    public void GoldenRoundTripsProductionEnvelope()
    {
        var frame = AvatarJson.ReadFrame(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "frame-v1.json")));
        Assert.Equal(TestData.Ids, frame.Ids);
        Assert.Equal(0.6, frame.Blendshapes["jawOpen"]);
        Assert.Equal(0.2, frame.Blendshapes["eyeBlinkLeft"]);
        Assert.Equal(4800, frame.SampleOffset);
        Assert.Equal(frame.SourceId, AvatarJson.ReadFrame(AvatarJson.WriteFrame(frame)).SourceId);
    }

    [Theory]
    [InlineData("aa")]
    [InlineData("JawOpen")]
    [InlineData("jaw_open")]
    [InlineData("jawopen")]
    [InlineData("headYaw")]
    public void RejectsNonCanonicalBlendshape(string name) =>
        Assert.Throws<ContractException>(() => (TestData.Frame with
        {
            Blendshapes = new Dictionary<string, double> { [name] = 0.5 }
        }).Validate());

    [Fact]
    public void CanonicalCatalogHasExactly52AndEveryNameValidates()
    {
        Assert.Equal(52, AvatarChannels.BlendshapeNames.Count);
        (TestData.Frame with
        {
            Blendshapes = AvatarChannels.BlendshapeNames.ToDictionary(n => n, _ => 0.5)
        }).Validate();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    public void RejectsInvalidCoefficientsInBothPayloads(double value)
    {
        Assert.Throws<ContractException>(() => AvatarJson.WriteFrame(TestData.Frame with
        {
            Blendshapes = new Dictionary<string, double> { ["jawOpen"] = value }
        }));
        Assert.Throws<ContractException>(() => AvatarJson.WriteFrame(TestData.Frame with
        {
            Semantics = [new() { Channel = SemanticChannel.VowelAa, Value = value }]
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AcceptsCoefficientEndpoints(double value) => (TestData.Frame with
    {
        Blendshapes = new Dictionary<string, double> { ["jawOpen"] = value },
        Semantics = [new() { Channel = SemanticChannel.VowelAa, Value = value }]
    }).Validate();

    [Theory]
    [InlineData("\"sequence\": 7", "\"sequence\": 7, \"sequence\": 8")]
    [InlineData("\"jawOpen\": 0.6", "\"jawOpen\": 0.6, \"jawOpen\": 0.4")]
    [InlineData("\"sequence\": 7", "\"sequence\": 7, \"execute\": \"private-secret\"")]
    [InlineData("\"major\": 1", "\"major\": 1, \"execute\": \"private-secret\"")]
    [InlineData("\"session_id\":", "\"Session_id\":")]
    [InlineData("\"minor\": 0", "\"minor\": 1")]
    [InlineData("\"major\": 1", "\"major\": 2")]
    [InlineData("\"sequence\": 7", "\"sequence\": \"7\"")]
    [InlineData("\"sequence\": 7", "\"sequence\": 7.5")]
    [InlineData("\"sequence\": 7", "\"sequence\": 2147483648")]
    [InlineData("\"epoch\": 3", "\"epoch\": -1")]
    [InlineData("\"sample_rate\": 48000", "\"sample_rate\": 22050")]
    [InlineData("\"sample_offset\": 4800", "\"sample_offset\": 9007199254740992")]
    [InlineData("\"sample_offset\": 4800", "\"sample_offset\": -1")]
    [InlineData("\"jawOpen\": 0.6", "\"jawOpen\": 1e400")]
    [InlineData("\"blendshapes\":", "\"unknown_blendshapes\":")]
    [InlineData("\"semantics\": []", "\"semantics\": null")]
    [InlineData("\"semantics\": []", "\"semantics\": [{\"channel\":\"VowelAa\",\"value\":0.2}]")]
    [InlineData("\"semantics\": []", "\"semantics\": [{\"channel\":1,\"value\":0.2}]")]
    [InlineData("\"semantics\": []", "\"semantics\": [{\"channel\":\"vowel_aa\",\"value\":0.2,\"script\":\"private-secret\"}]")]
    [InlineData("\"semantics\": []", "\"semantics\": [{\"channel\":\"vowel_aa\",\"value\":0.2},{\"channel\":\"vowel_aa\",\"value\":0.3}]")]
    public void RejectsMalformedClosedEnvelopeWithSanitizedErrors(string before, string after)
    {
        var json = Encoding.UTF8.GetString(AvatarJson.WriteFrame(TestData.Frame));
        Assert.Contains(before, json);
        var error = Assert.Throws<ContractException>(() =>
            AvatarJson.ReadFrame(Encoding.UTF8.GetBytes(json.Replace(before, after, StringComparison.Ordinal))));
        Assert.DoesNotContain("private-secret", error.Message);
    }

    [Fact]
    public void RejectsUnknownCorrelationProperty()
    {
        var json = Encoding.UTF8.GetString(AvatarJson.WriteFrame(TestData.Frame));
        json = json.Replace("\"ids\": {", "\"ids\": {\"private_field\":\"private-secret\",", StringComparison.Ordinal);
        Assert.Throws<ContractException>(() => AvatarJson.ReadFrame(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void SemanticEnumTokensAreExactSnakeCase()
    {
        var frame = TestData.Frame with
        {
            Blendshapes = new Dictionary<string, double>(),
            Semantics = Enum.GetValues<SemanticChannel>().Select(c => new SemanticValue { Channel = c, Value = 0.5 }).ToArray()
        };
        var bytes = AvatarJson.WriteFrame(frame);
        Assert.Contains("\"vowel_aa\"", Encoding.UTF8.GetString(bytes));
        Assert.Equal(13, AvatarJson.ReadFrame(bytes).Semantics.Count);
    }

    [Fact]
    public void RejectsEmptyPayloadAndUndefinedSemantic()
    {
        Assert.Throws<ContractException>(() => (TestData.Frame with { Blendshapes = new Dictionary<string, double>() }).Validate());
        Assert.Throws<ContractException>(() => new SemanticValue { Channel = (SemanticChannel)999, Value = 0 }.Validate());
        Assert.Throws<ContractException>(() => new ChannelReference { Blendshape = "jawOpen", Semantic = SemanticChannel.MouthOpen }.Validate());
        Assert.Throws<ContractException>(() => new ChannelReference().Validate());
    }

    [Fact]
    public void JsonByteDepthAndUtf8BoundsAreEnforced()
    {
        var error = Assert.Throws<ContractException>(() => AvatarJson.ReadFrame(new byte[AvatarJson.MaxFrameBytes + 1]));
        Assert.Equal(ErrorCode.PayloadTooLarge, error.Code);
        Assert.Throws<ContractException>(() => AvatarJson.ReadFrame(Encoding.UTF8.GetBytes(new string('[', 17) + new string(']', 17))));
        Assert.Throws<ContractException>(() => AvatarJson.ReadFrame(new byte[] { (byte)'{', 0xFF, (byte)'}' }));
    }

    [Fact]
    public void DisabledConfigurationRoundTripsWithoutInventingSources()
    {
        var value = AvatarJson.ReadConfiguration(AvatarJson.WriteConfiguration(AvatarConfiguration.Disabled));
        Assert.False(value.Enabled);
        Assert.Equal(AvatarBackend.Audio2Face, value.PreferredBackend);
        Assert.Empty(value.Assignments);
    }

    [Fact]
    public void ConfigurationRoundTripsMappingsAndExplicitOmission()
    {
        var config = TestData.Configuration with
        {
            RequestedAspects = [AvatarAspect.Mouth, AvatarAspect.Head],
            OmittedAspects = [AvatarAspect.Head]
        };
        var actual = AvatarJson.ReadConfiguration(AvatarJson.WriteConfiguration(config));
        Assert.Equal(AvatarAspect.Head, Assert.Single(actual.OmittedAspects));
        Assert.Equal("aa", actual.MappingProfiles[0].Mappings[0].TargetParameterId);
    }

    [Theory]
    [InlineData("\"preferred_backend\": \"audio2_face\"", "\"preferred_backend\": \"Audio2Face\"")]
    [InlineData("\"preferred_backend\": \"audio2_face\"", "\"preferred_backend\": 0")]
    [InlineData("\"accept_reduced\": false", "\"accept_reduced\": false, \"override_safety\": true")]
    [InlineData("\"blendshape\": \"jawOpen\"", "\"blendshape\": \"jawOpen\", \"pose\": [1,2,3]")]
    [InlineData("\"output_maximum\": 1", "\"output_maximum\": 1, \"script\": \"private-secret\"")]
    [InlineData("\"enabled\": true", "\"enabled\": true, \"enabled\": false")]
    public void ConfigurationRejectsUnknownPropertiesEnumsAndDuplicates(string before, string after)
    {
        var json = Encoding.UTF8.GetString(AvatarJson.WriteConfiguration(TestData.Configuration));
        Assert.Contains(before, json);
        Assert.Throws<ContractException>(() => AvatarJson.ReadConfiguration(Encoding.UTF8.GetBytes(json.Replace(before, after, StringComparison.Ordinal))));
    }

    [Fact]
    public void ConfigurationCollectionsAndBytesAreBounded()
    {
        Assert.Throws<ContractException>(() => (TestData.Configuration with
        {
            RequestedAspects = [AvatarAspect.Mouth, AvatarAspect.Mouth]
        }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Configuration with
        {
            Assignments = Enumerable.Repeat(TestData.Configuration.Assignments[0], 33).ToArray()
        }).Validate());
        Assert.Throws<ContractException>(() => AvatarJson.ReadConfiguration(new byte[AvatarJson.MaxConfigurationBytes + 1]));
    }
}

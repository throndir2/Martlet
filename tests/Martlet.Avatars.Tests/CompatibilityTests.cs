using Martlet.Core.Contracts;

namespace Martlet.Avatars.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public void ModelExtensionDoesNotImplyMetadataOrFullFaceSupport()
    {
        var unknown = TestData.Model with { ModelId = "unknown.vrm", MetadataKnown = false, Parameters = [] };
        Assert.Equal(Compatibility.Unknown, Assess(model: unknown).Compatibility);
        var noExpressions = TestData.Model with { Parameters = [] };
        Assert.Equal(Compatibility.Unsupported, Assess(model: noExpressions).Compatibility);
        Assert.Equal(Compatibility.RequiresMapping, CompatibilityEngine.Assess(
            TestData.Source, TestData.Model, AvatarAspect.Mouth, null).Compatibility);
    }

    [Fact]
    public void Audio2FaceFullRigToBasicVowelIsExplicitlyReducedNotArkitCompatible()
    {
        var source = TestData.Source with
        {
            Channels = AvatarChannels.BlendshapeNames.Select(n => new ChannelReference { Blendshape = n }).ToArray()
        };
        var result = Assess(source: source);
        Assert.Equal(Compatibility.Reduced, result.Compatibility);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(CompatibilityIssueCode.PartialMapping, issue.Code);
        Assert.Contains(RemedyKind.AcceptReducedMapping, issue.Remedies);
        Assert.Equal(Compatibility.Unsupported,
            CompatibilityEngine.Assess(source, TestData.Model, AvatarAspect.Expression, TestData.Profile).Compatibility);
    }

    [Fact]
    public void MissingOptionalVrmExpressionIsNotSynthesized()
    {
        var source = TestData.Source with
        {
            Channels = [new() { Semantic = SemanticChannel.Happy }]
        };
        var profile = TestData.Profile with
        {
            Mappings = [new()
            {
                Source = new() { Semantic = SemanticChannel.Happy }, TargetParameterId = "happy",
                OutputMinimum = 0, OutputMaximum = 1
            }]
        };
        var assessment = CompatibilityEngine.Assess(source, TestData.Model, AvatarAspect.Expression, profile);
        Assert.Equal(Compatibility.Unsupported, assessment.Compatibility);
        Assert.Contains(assessment.Issues, i => i.Code == CompatibilityIssueCode.UnsupportedAspect);
    }

    [Theory]
    [InlineData(AvatarAspect.Head)]
    [InlineData(AvatarAspect.Body)]
    [InlineData(AvatarAspect.SecondaryMotion)]
    [InlineData(AvatarAspect.Gaze)]
    public void Audio2FaceMouthOnlySourceDoesNotClaimUnsupportedPoseOrGaze(AvatarAspect aspect)
    {
        var model = TestData.Model with
        {
            Parameters = [new() { Id = "native-control", Aspect = aspect, Minimum = -1, Maximum = 1, Neutral = 0 }]
        };
        var assessment = CompatibilityEngine.Assess(TestData.Source, model, aspect, TestData.Profile);
        Assert.Equal(Compatibility.Unsupported, assessment.Compatibility);
    }

    [Fact]
    public void ActualEyeLookChannelCanBeMappedToMetadataBackedGaze()
    {
        var channel = new ChannelReference { Blendshape = "eyeLookInLeft" };
        var source = TestData.Source with { Channels = [channel] };
        var model = TestData.Model with
        {
            Parameters = [new() { Id = "look-in-left", Aspect = AvatarAspect.Gaze, Minimum = 0, Maximum = 1, Neutral = 0 }]
        };
        var profile = TestData.Profile with
        {
            Mappings = [new() { Source = channel, TargetParameterId = "look-in-left", OutputMinimum = 0, OutputMaximum = 1 }]
        };
        Assert.Equal(Compatibility.Supported, CompatibilityEngine.Assess(source, model, AvatarAspect.Gaze, profile).Compatibility);
    }

    [Theory]
    [InlineData(RuntimeReadiness.NotChecked)]
    [InlineData(RuntimeReadiness.Missing)]
    [InlineData(RuntimeReadiness.Installed)]
    [InlineData(RuntimeReadiness.Disabled)]
    [InlineData(RuntimeReadiness.Unavailable)]
    public void StructuralSupportDoesNotBypassRuntimeReadiness(RuntimeReadiness readiness)
    {
        var result = Assess(source: TestData.Source with { Readiness = readiness });
        Assert.Equal(Compatibility.Supported, result.Compatibility);
        Assert.False(result.Ready);
        Assert.Equal(readiness, result.SourceReadiness);
        Assert.Contains(result.Issues, i => i.Code == CompatibilityIssueCode.RuntimeNotReady);
        Assert.False(Assess(model: TestData.Model with { Readiness = readiness }).Ready);
    }

    [Fact]
    public void UnknownSourceChannelsStayUnknown()
    {
        Assert.Equal(Compatibility.Unknown,
            Assess(source: TestData.Source with { ChannelsKnown = false, Channels = [] }).Compatibility);
        Assert.Throws<ContractException>(() => (TestData.Source with { ChannelsKnown = false }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Model with { MetadataKnown = false }).Validate());
    }

    [Fact]
    public void RejectsWrongMappingIdentity()
    {
        AssertIssue(CompatibilityIssueCode.MappingBindingMismatch,
            Assess(profile: TestData.Profile with { ModelId = "other-model" }));
        AssertIssue(CompatibilityIssueCode.MappingBindingMismatch,
            Assess(profile: TestData.Profile with { SourceId = "other-source" }));
    }

    [Fact]
    public void PartialMappingsCannotInventAbsentSourceChannels()
    {
        var extra = TestData.Profile.Mappings[0] with { Source = new() { Blendshape = "mouthFunnel" } };
        AssertIssue(CompatibilityIssueCode.SourceChannelMissing, Assess(profile: TestData.Profile with { Mappings = [extra] }));
    }

    [Fact]
    public void ParameterLookupUsesExactModelIdsAndAuthoredBounds()
    {
        var mapping = TestData.Profile.Mappings[0];
        AssertIssue(CompatibilityIssueCode.ModelParameterMissing,
            Assess(profile: TestData.Profile with { Mappings = [mapping with { TargetParameterId = "AA" }] }));
        AssertIssue(CompatibilityIssueCode.MappingOutOfBounds,
            Assess(profile: TestData.Profile with { Mappings = [mapping with { OutputMaximum = 1.001 }] }));
        AssertIssue(CompatibilityIssueCode.MappingOutOfBounds,
            Assess(profile: TestData.Profile with { Mappings = [mapping with { OutputMinimum = -0.001 }] }));
    }

    [Fact]
    public void MouthMappingCannotTakeOwnershipOfExpressionParameter()
    {
        var model = TestData.Model with
        {
            Parameters =
            [
                TestData.Model.Parameters[0],
                new() { Id = "happy", Aspect = AvatarAspect.Expression, Minimum = 0, Maximum = 1, Neutral = 0 }
            ]
        };
        AssertIssue(CompatibilityIssueCode.AspectMismatch, Assess(model: model,
            profile: TestData.Profile with
            {
                Mappings = [TestData.Profile.Mappings[0] with { TargetParameterId = "happy" }]
            }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1_000_001)]
    public void MetadataAndMappingNumbersAreFiniteAndBounded(double value)
    {
        Assert.Throws<ContractException>(() => (TestData.Model.Parameters[0] with { Maximum = value }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Profile.Mappings[0] with { OutputMaximum = value }).Validate());
    }

    [Fact]
    public void ModelBoundsNeutralAndUniqueChannelsAreRequired()
    {
        Assert.Throws<ContractException>(() => (TestData.Model.Parameters[0] with { Neutral = 2 }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Model.Parameters[0] with { Minimum = 1 }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Model with
        {
            Parameters = [TestData.Model.Parameters[0], TestData.Model.Parameters[0]]
        }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Source with { Channels = [TestData.Jaw, TestData.Jaw] }).Validate());
        Assert.Throws<ContractException>(() => (TestData.Profile with
        {
            Mappings = [TestData.Profile.Mappings[0], TestData.Profile.Mappings[0]]
        }).Validate());
    }

    [Fact]
    public void AmplitudeCannotAdvertiseRichExpressions()
    {
        Assert.Throws<ContractException>(() => (TestData.Source with { Backend = AvatarBackend.Amplitude }).Validate());
        (TestData.Source with
        {
            Backend = AvatarBackend.Amplitude, Channels = [new() { Semantic = SemanticChannel.MouthOpen }]
        }).Validate();
    }

    private static CompatibilityAssessment Assess(SourceCapabilities? source = null, ModelCapabilities? model = null,
        MappingProfile? profile = null) =>
        CompatibilityEngine.Assess(source ?? TestData.Source, model ?? TestData.Model, AvatarAspect.Mouth, profile ?? TestData.Profile);

    private static void AssertIssue(CompatibilityIssueCode code, CompatibilityAssessment assessment)
    {
        Assert.Equal(Compatibility.Unsupported, assessment.Compatibility);
        Assert.Contains(assessment.Issues, i => i.Code == code && i.Remedies.Contains(RemedyKind.OmitAspect));
    }
}

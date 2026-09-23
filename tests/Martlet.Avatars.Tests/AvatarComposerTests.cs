using System.Collections;
using Martlet.Core.Contracts;

namespace Martlet.Avatars.Tests;

public sealed class AvatarComposerTests
{
    [Fact]
    public void FloatingPointInterpolationCannotExceedAuthoredEndpointBounds()
    {
        var model = TestData.Model with
        {
            Parameters = [TestData.Model.Parameters[0] with { Minimum = -0.1, Maximum = 0.2 }]
        };
        var profile = TestData.Profile with
        {
            Mappings = [TestData.Profile.Mappings[0] with { OutputMinimum = -0.1, OutputMaximum = 0.2 }]
        };
        var composition = Create(TestData.Configuration with { MappingProfiles = [profile] }, model).Composition!;
        foreach (var value in new[] { 0, double.Epsilon, 0.5, Math.BitDecrement(1.0), 1 })
        {
            var frame = TestData.Frame with { Blendshapes = new Dictionary<string, double> { ["jawOpen"] = value } };
            var actual = composition.Compose(frame, new(TestData.Binding), TestData.Position()).Parameters["aa"];
            Assert.InRange(actual, -0.1, 0.2);
            if (value == 1) Assert.Equal(0.2, actual);
        }
    }

    [Fact]
    public void AllSixAspectOmissionPermutationsEnforceActualAvailableChannels()
    {
        var aspects = Enum.GetValues<AvatarAspect>();
        for (var mask = 0; mask < 64; mask++)
        {
            var omitted = aspects.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            var config = TestData.Configuration with
            {
                RequestedAspects = aspects, OmittedAspects = omitted,
                Assignments = aspects.Except(omitted).Select(aspect =>
                    TestData.Configuration.Assignments[0] with { Aspect = aspect, AcceptReduced = true }).ToArray()
            };
            var result = Create(config);
            Assert.Equal(aspects.Except(omitted).All(a => a == AvatarAspect.Mouth), result.IsValid);
        }
    }

    [Fact]
    public void ValidCompositionUsesExactLive2DMetadataMappingAndActualClock()
    {
        var model = TestData.Model with
        {
            Renderer = AvatarRenderer.Live2D,
            Parameters = [new() { Id = "ParamMouthOpenY", Aspect = AvatarAspect.Mouth, Minimum = -2, Maximum = 3, Neutral = -2 }]
        };
        var profile = TestData.Profile with
        {
            Mappings = [TestData.Profile.Mappings[0] with { TargetParameterId = "ParamMouthOpenY", OutputMinimum = -2, OutputMaximum = 3 }]
        };
        var result = Create(TestData.Configuration with { MappingProfiles = [profile] }, model: model);
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal("audio2face", result.RecommendedSourceId);
        var gate = new PlaybackFrameGate(TestData.Binding);
        var future = result.Composition!.Compose(TestData.Frame, gate, TestData.Position(0));
        Assert.Equal(FrameDisposition.Future, future.Disposition);
        Assert.Empty(future.Parameters);
        var due = result.Composition.Compose(TestData.Frame, gate, TestData.Position());
        Assert.Equal(FrameDisposition.Accepted, due.Disposition);
        Assert.Equal(1, due.Parameters["ParamMouthOpenY"]);
    }

    [Fact]
    public void InverseMappingsAreExplicitAndBounded()
    {
        var profile = TestData.Profile with
        {
            Mappings = [TestData.Profile.Mappings[0] with { OutputMinimum = 1, OutputMaximum = 0 }]
        };
        var composition = Create(TestData.Configuration with { MappingProfiles = [profile] }).Composition!;
        var result = composition.Compose(TestData.Frame, new(TestData.Binding), TestData.Position());
        Assert.Equal(0.4, result.Parameters["aa"], 12);
    }

    [Fact]
    public void SourceOverlapReturnsStructuredConflictRatherThanLastWriterWins()
    {
        var owner = TestData.Configuration.Assignments[0];
        var result = Create(TestData.Configuration with { Assignments = [owner, owner with { SourceId = "amplitude" }] });
        Assert.False(result.IsValid);
        Assert.Null(result.Composition);
        Assert.Contains(result.Issues, i => i.Code == CompatibilityIssueCode.ConflictingSources &&
            i.Remedies.Contains(RemedyKind.SelectSource));
    }

    [Fact]
    public void IntentionalOmissionIsValidButCannotBeCombinedWithOwner()
    {
        var config = TestData.Configuration with
        {
            RequestedAspects = [AvatarAspect.Mouth, AvatarAspect.Head],
            OmittedAspects = [AvatarAspect.Head]
        };
        Assert.True(Create(config).IsValid);
        Assert.False(Create(config with { OmittedAspects = [AvatarAspect.Head, AvatarAspect.Mouth] }).IsValid);
        Assert.False(Create(config with { OmittedAspects = [] }).IsValid);
    }

    [Fact]
    public void UnsupportedRequestedAspectCannotBeAcceptedAsReduced()
    {
        var config = TestData.Configuration with
        {
            RequestedAspects = [AvatarAspect.Head],
            Assignments = [TestData.Configuration.Assignments[0] with { Aspect = AvatarAspect.Head, AcceptReduced = true }]
        };
        var result = Create(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == CompatibilityIssueCode.UnsupportedAspect);
    }

    [Fact]
    public void PartialModelMappingRequiresIntentionalReducedAcceptance()
    {
        var source = TestData.Source with { Channels = [TestData.Jaw, new() { Blendshape = "mouthFunnel" }] };
        var rejected = Create(sources: [source]);
        Assert.False(rejected.IsValid);
        var accepted = Create(TestData.Configuration with
        {
            Assignments = [TestData.Configuration.Assignments[0] with { AcceptReduced = true }]
        }, sources: [source]);
        Assert.True(accepted.IsValid);
        Assert.Contains(accepted.Issues, i => i.Code == CompatibilityIssueCode.PartialMapping);
        Assert.Equal(Compatibility.Reduced, accepted.Assessments[0].Assessment.Compatibility);
        var frame = TestData.Frame with
        {
            Blendshapes = new Dictionary<string, double> { ["jawOpen"] = 0.6, ["mouthFunnel"] = 0.9 }
        };
        Assert.Single(accepted.Composition!.Compose(frame, new(TestData.Binding), TestData.Position()).Parameters);
    }

    [Fact]
    public void AcceptReducedCannotBypassMissingRuntimeOrInvalidMapping()
    {
        var config = TestData.Configuration with
        {
            Assignments = [TestData.Configuration.Assignments[0] with { AcceptReduced = true }]
        };
        Assert.False(Create(config, sources: [TestData.Source with { Readiness = RuntimeReadiness.Installed }]).IsValid);
        Assert.False(Create(config with
        {
            MappingProfiles = [TestData.Profile with
            {
                Mappings = [TestData.Profile.Mappings[0] with { OutputMaximum = 100 }]
            }]
        }).IsValid);
    }

    [Fact]
    public void MissingAudio2FaceNeverFallsBackToAvailableAmplitude()
    {
        var amplitude = TestData.Source with
        {
            SourceId = "amplitude", Backend = AvatarBackend.Amplitude,
            Channels = [new() { Semantic = SemanticChannel.MouthOpen }]
        };
        var result = Create(sources: [TestData.Source with { Readiness = RuntimeReadiness.Missing }, amplitude]);
        Assert.False(result.IsValid);
        Assert.Null(result.RecommendedSourceId);
        Assert.Contains(result.Issues, i => i.Code == CompatibilityIssueCode.RuntimeNotReady);
    }

    [Fact]
    public void MissingMappingAndUnknownSourceAreActionable()
    {
        Assert.Contains(Create(TestData.Configuration with { MappingProfiles = [] }).Issues,
            i => i.Code == CompatibilityIssueCode.MappingRequired);
        Assert.Contains(Create(sources: []).Issues, i => i.Code == CompatibilityIssueCode.UnknownCapabilities);
        Assert.Contains(Create(TestData.Configuration with { Assignments = [] }).Issues,
            i => i.Code == CompatibilityIssueCode.MissingAssignment);
    }

    [Fact]
    public void UndeclaredAssignmentsAndOmissionsAreNotSilentlyIgnored()
    {
        Assert.Contains(Create(TestData.Configuration with { RequestedAspects = [] }).Issues,
            i => i.Code == CompatibilityIssueCode.UnrequestedAspect);
        Assert.Contains(Create(TestData.Configuration with { OmittedAspects = [AvatarAspect.Body] }).Issues,
            i => i.Code == CompatibilityIssueCode.UnrequestedAspect);
    }

    [Fact]
    public void DifferentSourcesCanOwnMouthAndNonMouthExpressionWithoutOverlap()
    {
        var semantic = new ChannelReference { Semantic = SemanticChannel.Happy };
        var expressionSource = TestData.Source with
        {
            SourceId = "intent", Backend = AvatarBackend.ModelNative, Channels = [semantic]
        };
        var model = TestData.Model with
        {
            Parameters = [TestData.Model.Parameters[0],
                new() { Id = "happy", Aspect = AvatarAspect.Expression, Minimum = 0, Maximum = 1, Neutral = 0 }]
        };
        var profile = TestData.Profile with
        {
            Id = "intent-mapping", SourceId = "intent",
            Mappings = [new() { Source = semantic, TargetParameterId = "happy", OutputMinimum = 0, OutputMaximum = 1 }]
        };
        var config = TestData.Configuration with
        {
            RequestedAspects = [AvatarAspect.Mouth, AvatarAspect.Expression],
            Assignments = [TestData.Configuration.Assignments[0],
                new() { Aspect = AvatarAspect.Expression, SourceId = "intent", MappingId = profile.Id, AcceptReduced = false }],
            MappingProfiles = [TestData.Profile, profile]
        };
        var result = Create(config, model, [TestData.Source, expressionSource]);
        Assert.True(result.IsValid);
        Assert.Single(result.Composition!.Compose(TestData.Frame, new(TestData.Binding), TestData.Position()).Parameters);
        var intent = TestData.Frame with
        {
            SourceId = "intent", Blendshapes = new Dictionary<string, double>(),
            Semantics = [new() { Channel = SemanticChannel.Happy, Value = 0.75 }]
        };
        var composed = result.Composition.Compose(intent, new(TestData.Binding with { SourceId = "intent" }), TestData.Position());
        Assert.Equal(0.75, Assert.Single(composed.Parameters).Value);
        Assert.Equal("happy", Assert.Single(composed.Parameters).Key);
    }

    [Fact]
    public void MappedChannelMustBePresentAndFrameCannotInventAdvertisedChannels()
    {
        var composition = Create().Composition!;
        Assert.Throws<ContractException>(() => composition.Compose(TestData.Frame with
        {
            Blendshapes = new Dictionary<string, double> { ["mouthFunnel"] = 0.5 }
        }, new(TestData.Binding), TestData.Position()));
        var source = TestData.Source with
        {
            Channels = [TestData.Jaw, new() { Semantic = SemanticChannel.Happy }]
        };
        composition = Create(sources: [source]).Composition!;
        Assert.Throws<ContractException>(() => composition.Compose(TestData.Frame with
        {
            Blendshapes = new Dictionary<string, double>(), Semantics = [new() { Channel = SemanticChannel.Happy, Value = 1 }]
        }, new(TestData.Binding), TestData.Position()));
    }

    [Fact]
    public void UnselectedSourcesAndStoppedOrStaleFramesCannotWriteParameters()
    {
        var composition = Create().Composition!;
        Assert.Throws<ContractException>(() => composition.Compose(TestData.Frame with { SourceId = "unselected" },
            new(TestData.Binding), TestData.Position()));
        var gate = new PlaybackFrameGate(TestData.Binding);
        gate.Stop();
        Assert.Empty(composition.Compose(TestData.Frame, gate, TestData.Position()).Parameters);
        Assert.Empty(composition.Compose(TestData.Frame with { Epoch = 2 }, new(TestData.Binding), TestData.Position()).Parameters);
        Assert.Empty(composition.Compose(TestData.Frame, new(TestData.Binding), TestData.Position(48000)).Parameters);
    }

    [Fact]
    public void CreatedCompositionSnapshotsMutableCallerCollections()
    {
        var channels = new List<ChannelReference> { TestData.Jaw };
        var mappings = TestData.Profile.Mappings.ToList();
        var composition = Create(TestData.Configuration with
        {
            MappingProfiles = [TestData.Profile with { Mappings = mappings }]
        }, sources: [TestData.Source with { Channels = channels }]).Composition!;
        mappings.Clear();
        channels.Clear();
        Assert.Equal(0.6, composition.Compose(TestData.Frame, new(TestData.Binding), TestData.Position()).Parameters["aa"]);
    }

    [Fact]
    public void DisabledAvatarPathNeverInspectsModelOrEnumeratesSources()
    {
        var invalidModel = TestData.Model with { ModelId = "" };
        var result = AvatarComposer.Create(AvatarConfiguration.Disabled, invalidModel, new NeverReadSources());
        Assert.True(result.IsValid);
        Assert.False(result.Enabled);
        Assert.Empty(result.Assessments);
        Assert.Empty(result.Issues);
        Assert.Null(result.RecommendedSourceId);
        Assert.True(AvatarComposer.Create(AvatarConfiguration.Disabled, null, null).IsValid);
    }

    private static CompositionResult Create(AvatarConfiguration? configuration = null, ModelCapabilities? model = null,
        IReadOnlyList<SourceCapabilities>? sources = null) =>
        AvatarComposer.Create(configuration ?? TestData.Configuration, model ?? TestData.Model, sources ?? [TestData.Source]);

    private sealed class NeverReadSources : IReadOnlyList<SourceCapabilities>
    {
        public int Count => throw new InvalidOperationException("Disabled path read source metadata.");
        public SourceCapabilities this[int index] => throw new InvalidOperationException();
        public IEnumerator<SourceCapabilities> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

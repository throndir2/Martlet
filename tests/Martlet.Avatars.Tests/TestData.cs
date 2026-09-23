using Martlet.Core.Contracts;

namespace Martlet.Avatars.Tests;

internal static class TestData
{
    internal static readonly CorrelationIds Ids = new()
    {
        SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TurnId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        RequestId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    };
    internal static ChannelReference Jaw => new() { Blendshape = "jawOpen" };
    internal static AvatarFrame Frame => new()
    {
        Version = ContractVersion.Current, Ids = Ids, SourceId = "audio2face", Epoch = 3, Sequence = 7,
        SampleRate = 48000, SampleOffset = 4800,
        Blendshapes = new Dictionary<string, double> { ["jawOpen"] = 0.6 }, Semantics = []
    };
    internal static PlaybackBinding Binding => new() { Ids = Ids, SourceId = "audio2face", Epoch = 3, SampleRate = 48000 };
    internal static PlaybackPosition Position(long offset = 4800) =>
        new() { Ids = Ids, Epoch = 3, SampleRate = 48000, SampleOffset = offset };
    internal static ModelCapabilities Model => new()
    {
        ModelId = "fixture-vrm", Renderer = AvatarRenderer.Vrm, MetadataKnown = true,
        Readiness = RuntimeReadiness.Available,
        Parameters = [new() { Id = "aa", Aspect = AvatarAspect.Mouth, Minimum = 0, Maximum = 1, Neutral = 0 }]
    };
    internal static SourceCapabilities Source => new()
    {
        SourceId = "audio2face", Backend = AvatarBackend.Audio2Face, ChannelsKnown = true,
        Readiness = RuntimeReadiness.Available, Channels = [Jaw]
    };
    internal static MappingProfile Profile => new()
    {
        Id = "explicit-vowel-reduction", SourceId = "audio2face", ModelId = Model.ModelId,
        Mappings = [new() { Source = Jaw, TargetParameterId = "aa", OutputMinimum = 0, OutputMaximum = 1 }]
    };
    internal static AvatarConfiguration Configuration => new()
    {
        Version = ContractVersion.Current, Enabled = true, PreferredBackend = AvatarBackend.Audio2Face,
        RequestedAspects = [AvatarAspect.Mouth], OmittedAspects = [],
        Assignments = [new() { Aspect = AvatarAspect.Mouth, SourceId = "audio2face", MappingId = Profile.Id, AcceptReduced = false }],
        MappingProfiles = [Profile]
    };
}

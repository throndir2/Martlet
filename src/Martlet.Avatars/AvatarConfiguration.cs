using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatars;

public enum AvatarBackend { Audio2Face, Amplitude, ModelNative }
public enum Compatibility { Supported, RequiresMapping, Reduced, Unsupported, Unknown }
public enum RuntimeReadiness { NotChecked, Missing, Installed, Available, Disabled, Unavailable }
public enum AvatarRenderer { Live2D, Vrm }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelParameter : IContract
{
    public required string Id { get; init; }
    public required AvatarAspect Aspect { get; init; }
    public required double Minimum { get; init; }
    public required double Maximum { get; init; }
    public required double Neutral { get; init; }

    public void Validate()
    {
        ParameterId(Id);
        ContractRules.Defined(Aspect);
        ContractRules.Require(double.IsFinite(Minimum) && double.IsFinite(Maximum) && double.IsFinite(Neutral) &&
            Minimum >= -1_000_000 && Maximum <= 1_000_000 && Minimum < Maximum &&
            Neutral >= Minimum && Neutral <= Maximum, "Model parameter bounds or neutral value are invalid.");
    }

    internal static void ParameterId(string? value)
    {
        ContractRules.Text(value, 128);
        ContractRules.Require(!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl),
            "A model parameter identifier is required.");
    }
}

public sealed record ModelCapabilities : IContract
{
    public required string ModelId { get; init; }
    public required AvatarRenderer Renderer { get; init; }
    public required bool MetadataKnown { get; init; }
    public required RuntimeReadiness Readiness { get; init; }
    public required IReadOnlyList<ModelParameter> Parameters { get; init; }

    public void Validate()
    {
        ContractRules.Identifier(ModelId);
        ContractRules.Defined(Renderer);
        ContractRules.Defined(Readiness);
        AvatarValidation.Items(Parameters, 512);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
        {
            parameter.Validate();
            ContractRules.Require(seen.Add(parameter.Id), "Model parameter identifiers cannot repeat.");
        }
        ContractRules.Require(MetadataKnown || Parameters.Count == 0, "Unknown model metadata cannot declare parameters.");
    }
}

public sealed record SourceCapabilities : IContract
{
    public required string SourceId { get; init; }
    public required AvatarBackend Backend { get; init; }
    public required bool ChannelsKnown { get; init; }
    public required RuntimeReadiness Readiness { get; init; }
    public required IReadOnlyList<ChannelReference> Channels { get; init; }

    public void Validate()
    {
        ContractRules.Identifier(SourceId);
        ContractRules.Defined(Backend);
        ContractRules.Defined(Readiness);
        AvatarValidation.Items(Channels, 65);
        var seen = new HashSet<ChannelReference>();
        foreach (var channel in Channels)
        {
            channel.Validate();
            ContractRules.Require(seen.Add(channel), "Source channels cannot repeat.");
        }
        ContractRules.Require(ChannelsKnown || Channels.Count == 0, "An unverified source cannot declare channels.");
        ContractRules.Require(Backend != AvatarBackend.Amplitude ||
            Channels.All(c => c.Semantic == SemanticChannel.MouthOpen),
            "Amplitude can only advertise semantic mouth openness.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelMapping : IContract
{
    public required ChannelReference Source { get; init; }
    public required string TargetParameterId { get; init; }
    public required double OutputMinimum { get; init; }
    public required double OutputMaximum { get; init; }

    public void Validate()
    {
        ContractRules.Require(Source is not null, "A mapping source channel is required.");
        Source!.Validate();
        ModelParameter.ParameterId(TargetParameterId);
        ContractRules.Require(double.IsFinite(OutputMinimum) && double.IsFinite(OutputMaximum) &&
            Math.Abs(OutputMinimum) <= 1_000_000 && Math.Abs(OutputMaximum) <= 1_000_000 &&
            OutputMinimum != OutputMaximum, "Mapping output bounds must be finite, bounded and distinct.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MappingProfile : IContract
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string ModelId { get; init; }
    public required IReadOnlyList<ChannelMapping> Mappings { get; init; }

    public void Validate()
    {
        ContractRules.Identifier(Id);
        ContractRules.Identifier(SourceId);
        ContractRules.Identifier(ModelId);
        AvatarValidation.Items(Mappings, 512);
        ContractRules.Require(Mappings.Count > 0, "A mapping profile needs at least one mapping.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            mapping.Validate();
            ContractRules.Require(seen.Add(mapping.TargetParameterId), "A profile cannot write a target parameter twice.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AspectAssignment : IContract
{
    public required AvatarAspect Aspect { get; init; }
    public required string SourceId { get; init; }
    public string? MappingId { get; init; }
    public required bool AcceptReduced { get; init; }

    public void Validate()
    {
        ContractRules.Defined(Aspect);
        ContractRules.Identifier(SourceId);
        if (MappingId is not null) ContractRules.Identifier(MappingId);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvatarConfiguration : IContract
{
    public required ContractVersion Version { get; init; }
    public required bool Enabled { get; init; }
    public required AvatarBackend PreferredBackend { get; init; }
    public required IReadOnlyList<AvatarAspect> RequestedAspects { get; init; }
    public required IReadOnlyList<AvatarAspect> OmittedAspects { get; init; }
    public required IReadOnlyList<AspectAssignment> Assignments { get; init; }
    public required IReadOnlyList<MappingProfile> MappingProfiles { get; init; }

    public static AvatarConfiguration Disabled => new()
    {
        Version = ContractVersion.Current, Enabled = false, PreferredBackend = AvatarBackend.Audio2Face,
        RequestedAspects = [], OmittedAspects = [], Assignments = [], MappingProfiles = []
    };

    public void Validate()
    {
        AvatarValidation.Version(Version);
        ContractRules.Defined(PreferredBackend);
        AvatarValidation.Items(RequestedAspects, 6);
        AvatarValidation.Items(OmittedAspects, 6);
        AvatarValidation.Items(Assignments, 32);
        AvatarValidation.Items(MappingProfiles, 16);
        foreach (var aspect in RequestedAspects.Concat(OmittedAspects)) ContractRules.Defined(aspect);
        ContractRules.Require(RequestedAspects.Distinct().Count() == RequestedAspects.Count &&
            OmittedAspects.Distinct().Count() == OmittedAspects.Count, "An aspect list cannot contain duplicates.");
        foreach (var assignment in Assignments) assignment.Validate();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in MappingProfiles)
        {
            profile.Validate();
            ContractRules.Require(seen.Add(profile.Id), "Mapping profile identifiers cannot repeat.");
        }
    }
}

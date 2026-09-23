using System.Text.Json.Serialization;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Avatars;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvatarFrame : IContract
{
    public required ContractVersion Version { get; init; }
    public required CorrelationIds Ids { get; init; }
    public required string SourceId { get; init; }
    public required long Epoch { get; init; }
    public required long Sequence { get; init; }
    public required int SampleRate { get; init; }
    public required long SampleOffset { get; init; }
    public required IReadOnlyDictionary<string, double> Blendshapes { get; init; }
    public required IReadOnlyList<SemanticValue> Semantics { get; init; }

    public void Validate()
    {
        AvatarValidation.Version(Version);
        ContractRules.Require(Ids is not null, "Avatar correlation is required.");
        Ids!.Validate();
        ContractRules.Identifier(SourceId);
        AvatarValidation.Counter(Epoch);
        AvatarValidation.Counter(Sequence);
        AvatarValidation.SampleRate(SampleRate);
        ContractRules.Require(SampleOffset is >= 0 and <= AvatarValidation.MaxSampleOffset,
            "Avatar sample offset is out of range.");
        ContractRules.Require(Blendshapes is { Count: <= 52 } && Semantics is { Count: <= 13 },
            "Avatar channels are missing or exceed their bounds.");
        ContractRules.Require(Blendshapes!.Count + Semantics!.Count > 0, "An avatar frame needs at least one channel.");
        foreach (var (name, value) in Blendshapes)
        {
            ContractRules.Require(AvatarChannels.BlendshapeNames.Contains(name), "The blendshape name is unsupported.");
            AvatarChannels.Coefficient(value);
        }
        var seen = new HashSet<SemanticChannel>();
        foreach (var item in Semantics)
        {
            ContractRules.Require(item is not null, "A semantic value is required.");
            item!.Validate();
            ContractRules.Require(seen.Add(item.Channel), "Semantic channels cannot repeat.");
        }
    }

    public bool TryGetValue(ChannelReference channel, out double value)
    {
        channel.Validate();
        if (channel.Blendshape is { } name) return Blendshapes.TryGetValue(name, out value);
        foreach (var semantic in Semantics)
        {
            if (semantic.Channel != channel.Semantic) continue;
            value = semantic.Value;
            return true;
        }
        value = 0;
        return false;
    }
}

internal static class AvatarValidation
{
    // All wire integers remain exact in JavaScript, including original playback sample positions.
    internal const long MaxSampleOffset = 9_007_199_254_740_991;

    internal static void Version(ContractVersion? version)
    {
        ContractRules.Require(version is not null, "Avatar version is required.");
        version!.Validate();
        ContractRules.Require(version.Minor == 0, "Use avatar contract version 1.0.", ErrorCode.UnsupportedVersion);
    }

    internal static void Counter(long value) =>
        ContractRules.Require(value is >= 0 and <= int.MaxValue, "Avatar epoch or sequence is out of range.");

    internal static void SampleRate(int value) => new PcmFormat
    {
        SampleRate = value, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
    }.Validate();

    internal static void Items<T>(IReadOnlyList<T>? values, int maximum)
    {
        ContractRules.Require(values is not null && values.Count <= maximum && values.All(v => v is not null),
            "An avatar collection is missing, too large or contains missing items.");
    }
}

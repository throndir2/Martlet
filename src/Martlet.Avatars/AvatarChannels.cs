using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatars;

public enum AvatarAspect { Mouth, Expression, Gaze, Head, Body, SecondaryMotion }
public enum SemanticChannel
{
    MouthOpen, VowelAa, VowelIh, VowelOu, VowelEe, VowelOh,
    BlinkLeft, BlinkRight, Happy, Angry, Sad, Relaxed, Surprised
}

public static class AvatarChannels
{
    private static readonly FrozenSet<string> MouthNames = new[]
    {
        "jawForward", "jawLeft", "jawRight", "jawOpen",
        "mouthClose", "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
        "mouthSmileLeft", "mouthSmileRight", "mouthFrownLeft", "mouthFrownRight",
        "mouthDimpleLeft", "mouthDimpleRight", "mouthStretchLeft", "mouthStretchRight",
        "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
        "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight",
        "mouthUpperUpLeft", "mouthUpperUpRight", "tongueOut"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GazeNames = new[]
    {
        "eyeLookDownLeft", "eyeLookInLeft", "eyeLookOutLeft", "eyeLookUpLeft",
        "eyeLookDownRight", "eyeLookInRight", "eyeLookOutRight", "eyeLookUpRight"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> BlendshapeNames { get; } = MouthNames.Concat(GazeNames).Concat(new[]
    {
        "eyeBlinkLeft", "eyeSquintLeft", "eyeWideLeft",
        "eyeBlinkRight", "eyeSquintRight", "eyeWideRight",
        "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
        "cheekPuff", "cheekSquintLeft", "cheekSquintRight", "noseSneerLeft", "noseSneerRight"
    }).ToFrozenSet(StringComparer.Ordinal);

    public static string SemanticName(SemanticChannel channel)
    {
        ContractRules.Defined(channel);
        return JsonNamingPolicy.SnakeCaseLower.ConvertName(channel.ToString());
    }

    public static AvatarAspect Aspect(ChannelReference channel)
    {
        channel.Validate();
        if (channel.Blendshape is { } name)
            return MouthNames.Contains(name) ? AvatarAspect.Mouth :
                GazeNames.Contains(name) ? AvatarAspect.Gaze : AvatarAspect.Expression;
        return channel.Semantic is SemanticChannel.MouthOpen or SemanticChannel.VowelAa or
            SemanticChannel.VowelIh or SemanticChannel.VowelOu or SemanticChannel.VowelEe or SemanticChannel.VowelOh
            ? AvatarAspect.Mouth : AvatarAspect.Expression;
    }

    internal static void Coefficient(double value) =>
        ContractRules.Require(double.IsFinite(value) && value is >= 0 and <= 1,
            "An avatar coefficient must be finite and between zero and one.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelReference : IContract
{
    public string? Blendshape { get; init; }
    public SemanticChannel? Semantic { get; init; }

    public void Validate()
    {
        ContractRules.Require((Blendshape is not null) != (Semantic is not null),
            "A channel must identify exactly one blendshape or semantic.");
        if (Blendshape is { } name)
            ContractRules.Require(AvatarChannels.BlendshapeNames.Contains(name), "The blendshape name is unsupported.");
        if (Semantic is { } semantic) ContractRules.Defined(semantic);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SemanticValue : IContract
{
    public required SemanticChannel Channel { get; init; }
    public required double Value { get; init; }

    public void Validate()
    {
        ContractRules.Defined(Channel);
        AvatarChannels.Coefficient(Value);
    }
}

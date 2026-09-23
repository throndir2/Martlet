using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Avatars;

public static class AvatarJson
{
    public const int MaxFrameBytes = 16_384;
    public const int MaxConfigurationBytes = 65_536;

    public static AvatarFrame ReadFrame(ReadOnlyMemory<byte> json) => Read<AvatarFrame>(json, MaxFrameBytes);
    public static byte[] WriteFrame(AvatarFrame frame) => ContractJson.Write(frame, MaxFrameBytes);
    public static AvatarConfiguration ReadConfiguration(ReadOnlyMemory<byte> json) =>
        Read<AvatarConfiguration>(json, MaxConfigurationBytes);
    public static byte[] WriteConfiguration(AvatarConfiguration configuration) =>
        ContractJson.Write(configuration, MaxConfigurationBytes);

    private static T Read<T>(ReadOnlyMemory<byte> json, int maximum) where T : IContract
    {
        var value = ContractJson.Read<T>(json, maximum);
        // Core correlation/version contracts allow extensions; this cross-language envelope is deliberately closed.
        using var document = JsonDocument.Parse(json);
        CheckNames(document.RootElement.GetProperty("version"), ["major", "minor"]);
        if (typeof(T) == typeof(AvatarFrame))
            CheckNames(document.RootElement.GetProperty("ids"), ["session_id", "turn_id", "request_id"]);
        return value;
    }

    private static void CheckNames(JsonElement element, string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            ContractRules.Require(allowed.Contains(property.Name, StringComparer.Ordinal),
                "An avatar property is unsupported.");
    }
}

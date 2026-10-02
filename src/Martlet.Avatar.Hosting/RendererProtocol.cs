using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatars;

namespace Martlet.Avatar.Hosting;

public sealed record RendererMessage(string Kind, Guid Activation, JsonElement Data);
public sealed record RendererParameter(string Id, double Minimum, double Maximum, double Neutral, string[] Aspects);
public sealed record RendererCapabilities(string ModelId, RendererParameter[] Parameters);
public sealed record RendererLoad(AvatarProfile Profile, string ResourceRevision, bool DarkTheme);
public sealed record RendererTheme(bool Dark);
public sealed record RendererSay(string? Text);
/// <summary>Overlay zoom command: "in", "out", "reset" (default size, unzoomed camera) or "status" (no change).</summary>
public sealed record RendererZoom(string Action);
/// <summary>
/// The overlay's size in device-independent pixels, its top relative to the top of its screen's work area (negative
/// when it extends above the screen; null if unknown), its camera zoom, and how far the top of the character's head
/// sits below the overlay's top edge as a fraction of its height (negative when cut off; null until reported).
/// </summary>
public sealed record RendererView(double Width, double Height, double? ScreenTop, double Zoom, double? HeadTop);
public sealed record RendererMapping(string Target, string Aspect);
public sealed record RendererConfiguration(string SourceId, string ModelRevision, string MappingRevision, RendererMapping[] Targets);
public sealed record RendererIdentity(Guid SessionId, Guid TurnId, Guid RequestId, string SourceId, long Epoch, int SampleRate);
public sealed record RendererParameters(RendererIdentity Identity, long Sequence, long SampleOffset,
    long ActualPlaybackSampleOffset, string ModelRevision, string MappingRevision, IReadOnlyDictionary<string, double> Parameters);

public static class RendererProtocol
{
    public const int MaximumMessageBytes = 262_144;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static RendererMessage Message<T>(string kind, Guid activation, T data) =>
        new(kind, activation, JsonSerializer.SerializeToElement(data, Json));
    public static T Data<T>(RendererMessage message) =>
        message.Data.Deserialize<T>(Json) ?? throw new InvalidDataException("Renderer payload is missing.");

    public static async Task WriteAsync(Stream pipe, RendererMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Renderer message exceeds its limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(bytes, token);
        await pipe.FlushAsync(token);
    }

    public static async Task<RendererMessage> ReadAsync(Stream pipe, CancellationToken token)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 2 or > MaximumMessageBytes) throw new InvalidDataException("Renderer message length is invalid.");
        var bytes = new byte[size];
        await pipe.ReadExactlyAsync(bytes, token);
        var message = JsonSerializer.Deserialize<RendererMessage>(bytes, Json) ??
            throw new InvalidDataException("Renderer message is missing.");
        if (message.Activation == Guid.Empty || string.IsNullOrEmpty(message.Kind) || message.Kind.Length > 32)
            throw new InvalidDataException("Renderer message binding is invalid.");
        return message;
    }
}

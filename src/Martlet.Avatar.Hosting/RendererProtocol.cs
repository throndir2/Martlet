using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatars;

namespace Martlet.Avatar.Hosting;

public sealed record RendererMessage(string Kind, Guid Activation, JsonElement Data);
public sealed record RendererParameter(string Id, double Minimum, double Maximum, double Neutral, string[] Aspects);
/// <summary>
/// What a loaded Live2D model drives after Martlet's fallbacks: its texture count and the divisor its textures are shown
/// at (1, 2 or 4; larger PNGs are halved to fit the GPU budget), the parameters that blink and open the mouth (its
/// EyeBlink/LipSync groups, or the standard ParamEyeLOpen/ParamEyeROpen and ParamMouthOpenY when they are empty), its
/// authored motion groups, expression count and whether physics and the Framework animator run.
/// </summary>
public sealed record RendererModelSummary(int Textures, int TextureDivisor, string[] EyeBlink, string[] LipSync,
    string[] MotionGroups, int Expressions, bool Physics, bool Animated);
public sealed record RendererCapabilities(string ModelId, RendererParameter[] Parameters, RendererModelSummary? Model = null);
/// <summary>Plays (<paramref name="On"/>) or ends one emote or motion on the showing character: an <c>expression</c> (held until
/// ended or replaced), a <c>motion</c> group (played once) or a <c>gesture</c> (<c>nod</c> or <c>shake</c>). The reply says
/// whether the model started it.</summary>
public sealed record RendererAction(string Kind, string Name, bool On = true)
{
    public static IReadOnlyList<string> Kinds { get; } = ["expression", "motion", "gesture"];
}
/// <summary>Starts the renderer. A locked <paramref name="Placement"/> puts the overlay back where it was locked (when that
/// spot is still on a screen) and locks it again. <paramref name="ThemeColors"/> are a character palette's colors by role
/// (#RRGGBB; null for Martlet's own palette of that lightness).</summary>
public sealed record RendererLoad(AvatarProfile Profile, string ResourceRevision, bool DarkTheme, RendererPlacement? Placement = null,
    IReadOnlyDictionary<string, string>? ThemeColors = null);
/// <summary>
/// Locks (or unlocks) the character overlay's place. While locked it can't be dragged, nudged with the arrow keys, moved back
/// to its default spot or resized from the overlay itself; zoom then only zooms the camera within its frame. Only Martlet
/// unlocks it: the overlay's menu can only ask Martlet to lock it. Replied to with the overlay's <see cref="RendererPlacement"/>.
/// </summary>
public sealed record RendererLock(bool Locked);
/// <summary>Where the character overlay is: its window's top-left corner and the character frame's width and height, in
/// device-independent pixels, and whether its place is locked.</summary>
public sealed record RendererPlacement(bool Locked, double Left, double Top, double Width, double Height)
{
    private const double Farthest = 100_000;

    /// <summary>Finite, positive size and within any plausible desktop.</summary>
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Math.Abs(Left) < Farthest && Math.Abs(Top) < Farthest && Width is > 0 and < Farthest && Height is > 0 and < Farthest;
}
/// <summary>The overlay's palette: Martlet's own light or dark one, or a character palette's <paramref name="Colors"/> by role
/// (#RRGGBB).</summary>
public sealed record RendererTheme(bool Dark, IReadOnlyDictionary<string, string>? Colors = null);
/// <summary>Asks for a small PNG of the character as it shows now (at most 320 pixels on its longer side, cropped to the
/// character, transparent around it). The reply's <c>snapshot</c> is a <c>data:image/png;base64,</c> URL, or null when the
/// renderer couldn't take one.</summary>
public sealed record RendererSnapshot;
/// <summary>
/// Shows (or with null text, hides) the speech bubble. By default it follows the character's head through moves, zoom and pan,
/// choosing the side with room on screen, then shifts by the offsets (device-independent pixels, +x right, +y down). Static
/// keeps it in one place: the offsets are then measured from the top-left of the work area of the character's screen.
/// </summary>
public sealed record RendererSay(string? Text, bool Static = false, double OffsetX = 0, double OffsetY = 0);
/// <summary>Where the speech bubble is: "left", "right" or "above" the character's head, "static", or "hidden"; its
/// body's screen rectangle in device-independent pixels (zero when hidden); and, when it was just shown, whether the text as
/// laid out on screen lies within that body (null when hidden or unknown).</summary>
public sealed record RendererBubble(string Placement, double Left, double Top, double Width, double Height, bool? TextFits = null);
/// <summary>Overlay zoom command: "in", "out", "reset" (default size, unzoomed camera) or "status" (no change).</summary>
public sealed record RendererZoom(string Action);
/// <summary>
/// Turns the character's head and eyes toward a point on the desktop (<paramref name="X"/>, <paramref name="Y"/> in physical
/// screen pixels, as Martlet's screenshots measure it) for <paramref name="Seconds"/> (0.5 to 30), after which they follow the
/// mouse again; without a point they follow the mouse at once. Replied to with <see cref="RendererLook"/>.
/// </summary>
public sealed record RendererGaze(double? X = null, double? Y = null, double Seconds = 0)
{
    public const double MinimumSeconds = 0.5, MaximumSeconds = 30;
}
/// <summary>What the character looks at: "mouse" or "point" (where Martlet asked), and the head and eye direction the
/// character was last given, from -1 to 1 (+x right, +y up).</summary>
public sealed record RendererLook(string Target, double X, double Y);
/// <summary>
/// Something chosen on the character overlay's menu that Martlet itself carries out, sent unprompted on the renderer's
/// separate request pipe (never as a command reply): "hide" the character, "open" Martlet's window, "talk" (open the talk
/// window), show the character's "settings" or "lock" its place where it is (Martlet saves it and sends
/// <see cref="RendererLock"/>; unlocking is only in Martlet's window). Zoom, position and keep-on-top stay inside the overlay.
/// </summary>
public sealed record RendererRequest(string Action)
{
    public static IReadOnlyList<string> Actions { get; } = ["hide", "open", "talk", "settings", "lock"];
}
/// <summary>
/// The character frame's size in device-independent pixels, its top relative to the top of its screen's work area
/// (negative when it extends above the screen; null if unknown), its camera zoom, how far the top of the character's head
/// sits below the frame's top edge as a fraction of its height (negative when cut off; null until reported), and the
/// overlay's full drawing width: the frame plus the transparent room beside it the model can move into (null if unknown), and
/// whether its place is locked (null if unknown).
/// </summary>
public sealed record RendererView(double Width, double Height, double? ScreenTop, double Zoom, double? HeadTop, double? DrawWidth = null,
    bool? Locked = null);
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

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
/// ended or replaced), a <c>motion</c> group (played once) or a Martlet <c>gesture</c> (<see cref="CharacterGesture"/>). The reply says
/// whether the model started it.</summary>
public sealed record RendererAction(string Kind, string Name, bool On = true)
{
    public static IReadOnlyList<string> Kinds { get; } = ["expression", "motion", "gesture"];
}
/// <summary>Starts the renderer. A saved <paramref name="Placement"/> puts the overlay back where it was last left, on the same
/// screen when that screen is still connected (else where it was, when that spot is still on a screen), and locks it again
/// when it was locked. <paramref name="ThemeColors"/> are a character palette's colors by role
/// (#RRGGBB; null for Martlet's own palette of that lightness). <paramref name="VoiceMuted"/>: Martlet's voice is muted (its
/// replies aren't spoken), so the overlay's menu offers to unmute it (see <see cref="RendererVoice"/>).</summary>
public sealed record RendererLoad(AvatarProfile Profile, string ResourceRevision, bool DarkTheme, RendererPlacement? Placement = null,
    IReadOnlyDictionary<string, string>? ThemeColors = null, bool VoiceMuted = false);
/// <summary>Whether Martlet's voice is muted now (changed from the overlay's menu, Martlet's window or another computer), so
/// the overlay's menu offers Mute voice or Unmute voice. Only Martlet mutes: the menu asks it with a "mute" or "unmute"
/// <see cref="RendererRequest"/>. Replied to with "ok".</summary>
public sealed record RendererVoice(bool Muted);
/// <summary>
/// Locks (or unlocks) the character overlay's place. While locked it can't be dragged, nudged with the arrow keys, moved back
/// to its default spot or resized from the overlay itself; zoom then only zooms the camera within its frame. Only Martlet
/// unlocks it: the overlay's menu can only ask Martlet to lock it. Replied to with the overlay's <see cref="RendererPlacement"/>.
/// </summary>
public sealed record RendererLock(bool Locked);
/// <summary>Where the character overlay is: its window's top-left corner and the character frame's width and height, in
/// device-independent pixels, and whether its place is locked. <paramref name="Screen"/> names the monitor it is on (Windows'
/// device name, such as <c>\\.\DISPLAY2</c>) and <paramref name="ScreenLeft"/>, <paramref name="ScreenTop"/> its top-left
/// relative to that monitor's work area, so it goes back to the same monitor even after the screens are rearranged.</summary>
public sealed record RendererPlacement(bool Locked, double Left, double Top, double Width, double Height, string? Screen = null,
    double? ScreenLeft = null, double? ScreenTop = null)
{
    private const double Farthest = 100_000;

    /// <summary>Finite, positive size and within any plausible desktop.</summary>
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Math.Abs(Left) < Farthest && Math.Abs(Top) < Farthest && Width is > 0 and < Farthest && Height is > 0 and < Farthest &&
        (Screen is null || (Screen.Length is > 0 and <= 64 && !Screen.Any(char.IsControl))) &&
        (ScreenLeft is null || (double.IsFinite(ScreenLeft.Value) && Math.Abs(ScreenLeft.Value) < Farthest)) &&
        (ScreenTop is null || (double.IsFinite(ScreenTop.Value) && Math.Abs(ScreenTop.Value) < Farthest));
}
/// <summary>The overlay's palette: Martlet's own light or dark one, or a character palette's <paramref name="Colors"/> by role
/// (#RRGGBB).</summary>
public sealed record RendererTheme(bool Dark, IReadOnlyDictionary<string, string>? Colors = null);
/// <summary>
/// Shows (or with null text, hides) the speech bubble. By default it follows the character's head through moves, zoom and pan,
/// choosing the side with room on screen, then shifts by the offsets (device-independent pixels, +x right, +y down). Static
/// keeps it in one place: the offsets are then measured from the top-left of the work area of the character's screen.
/// </summary>
public sealed record RendererSay(string? Text, bool Static = false, double OffsetX = 0, double OffsetY = 0);
/// <summary>Where the speech bubble is: "left", "right" or "above" the character's head, "static", or "hidden"; its
/// body's screen rectangle in device-independent pixels (zero when hidden); and, when it was just shown, whether the text as
/// laid out on screen lies within that body and the <paramref name="Colors"/> it is drawn in (null when hidden or unknown).</summary>
public sealed record RendererBubble(string Placement, double Left, double Top, double Width, double Height, bool? TextFits = null,
    RendererBubbleColors? Colors = null);
/// <summary>The colors a shown speech bubble is drawn in (#RRGGBB): its fill, outline and text, and its halo (null without
/// one, as in Windows' high contrast). They come from the overlay's palette: Surface, Accent, Text and Glow.</summary>
public sealed record RendererBubbleColors(string Fill, string Outline, string Text, string? Halo);
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
/// window), show the character's "settings", "lock" its place where it is or "unlock" it (Martlet saves it and sends
/// <see cref="RendererLock"/>), or "mute" or "unmute" Martlet's voice (Martlet saves it and sends <see cref="RendererVoice"/>).
/// "placed" says the character was moved or resized and has settled: Martlet then asks where it is ("where", replied to with
/// <see cref="RendererPlacement"/>) and saves that on this PC. Zoom, position and keep-on-top stay inside the overlay.
/// </summary>
public sealed record RendererRequest(string Action)
{
    public static IReadOnlyList<string> Actions { get; } = ["hide", "open", "talk", "settings", "lock", "unlock", "mute", "unmute", "placed"];
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
/// <summary>Over the character's renderer pipe: a picture of the character as it shows now (Discord's bot picture and
/// <c>/selfie</c>). <paramref name="Portrait"/> crops a square around the head and shoulders, else the whole character; the
/// longer side is at most <paramref name="Edge"/> pixels (64 to 512). Replied to with <see cref="RendererPicture"/>.</summary>
public sealed record RendererSnapshot(bool Portrait, int Edge = 512)
{
    public const int MinimumEdge = 64, MaximumEdge = 512;
}
/// <summary>A PNG of the character (base64, small enough for one renderer message) and its size in pixels.</summary>
public sealed record RendererPicture(string Png, int Width, int Height);
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

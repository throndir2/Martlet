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
/// ended or replaced), a <c>motion</c> group (played once) or a Martlet <c>gesture</c> (<see cref="CharacterGesture"/>). With
/// <paramref name="Hold"/> it lingers: a held expression stays on, layered with other held ones, until it is ended
/// (<c>On=false</c>), and a held gesture or overlay stays until ended too, instead of playing once; another emote doesn't replace
/// it. The reply says whether the model started it.</summary>
public sealed record RendererAction(string Kind, string Name, bool On = true, bool Hold = false)
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
/// <summary>The camera view (Martlet in your Discord calls): the character in its own ordinary 16:9 window (titled "Martlet
/// camera", in the taskbar, not on top) on a solid <paramref name="Background"/> (#RRGGBB) so OBS can capture that window
/// cleanly and key the color out, then share it as a virtual camera; or, with <paramref name="Picture"/> (the full path of a
/// PNG, JPEG or WebP file on this PC), on that picture, filling the window (the color shows only if it can't be read). Off
/// puts the overlay back where and how it was. While on, the window keeps its size and its place isn't saved; the character
/// is framed freely inside it: dragged anywhere, zoomed in or out (<see cref="MinimumZoom"/> to <see cref="MaximumZoom"/>) and
/// nudged with the arrow keys. Opening it starts from <paramref name="Zoom"/> and the character's middle at
/// <paramref name="X"/>, <paramref name="Y"/> (fractions of the window's width and height from its center, +x right, +y up); a
/// change of background keeps the current framing. Once a framing change settles the overlay sends a "framed"
/// <see cref="RendererRequest"/>.</summary>
public sealed record RendererCamera(bool On, string Background = "#00B140", string? Picture = null, double Zoom = 1, double X = 0,
    double Y = 0)
{
    public const double MinimumZoom = 0.25, MaximumZoom = 16, Farthest = 8;
}
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
/// <summary>Overlay zoom command: "in", "out", "reset" (default size, unzoomed and centered camera), "left", "right", "up" or
/// "down" (moves the character 10 pixels within its view: anywhere in the camera view, or while zoomed in on the overlay) or
/// "status" (no change).</summary>
public sealed record RendererZoom(string Action);
/// <summary>
/// Turns the character's head and eyes. With a point (<paramref name="X"/>, <paramref name="Y"/> in physical screen pixels, as
/// Martlet's screenshots measure it): toward it for <paramref name="Seconds"/> (0.5 to 30). With <paramref name="Mouse"/>:
/// toward the mouse pointer for <paramref name="Seconds"/>, whatever the usual gaze (a touch). With neither: back to the usual
/// gaze at once. <paramref name="Mode"/>, when given, first sets that usual gaze: what the eyes do when nothing holds them.
/// <paramref name="Choice"/> and <paramref name="Free"/>, when given, are what the overlay's Eyes menu shows checked: the owner's
/// choice (<see cref="Choices"/>: <c>personality</c> or a gaze's word) and whether the character may change where it looks.
/// Replied to with <see cref="RendererLook"/>.
/// </summary>
public sealed record RendererGaze(double? X = null, double? Y = null, double Seconds = 0, GazeMode? Mode = null, bool Mouse = false,
    string? Choice = null, bool? Free = null)
{
    public const double MinimumSeconds = 0.5, MaximumSeconds = 30;
    public const string Personality = "personality";

    /// <summary>The owner's choices the Eyes menu shows: <c>personality</c> (as the personality decides), then each gaze's word.</summary>
    public static IReadOnlyList<string> Choices { get; } = [Personality, .. CharacterGaze.Modes.Select(m => m.Word)];

    /// <summary>The owner's choice as the Eyes menu names it: <c>personality</c> for none, else the gaze's word.</summary>
    public static string ChoiceOf(GazeMode? owner) => owner is { } mode ? CharacterGaze.Word(mode) : Personality;
}
/// <summary>What the character looks at: "mouse", "point" (where Martlet asked), "window" (the window the user is using) or
/// "ahead" (straight ahead), the head and eye direction the character was last given, from -1 to 1 (+x right, +y up), and its
/// usual gaze.</summary>
public sealed record RendererLook(string Target, double X, double Y, GazeMode Mode = GazeMode.Mouse);
/// <summary>
/// Something chosen on the character overlay's menu that Martlet itself carries out, sent unprompted on the renderer's
/// separate request pipe (never as a command reply): "hide" the character, "open" Martlet's window, "talk" (open the talk
/// window), show the character's "settings", "lock" its place where it is or "unlock" it (Martlet saves it and sends
/// <see cref="RendererLock"/>), or "mute" or "unmute" Martlet's voice (Martlet saves it and sends <see cref="RendererVoice"/>).
/// The Eyes menu's "look-personality", "look-mouse", "look-near", "look-ahead" and "look-window" choose the usual gaze, and
/// "look-free-on" and "look-free-off" whether the character may change where it looks (Martlet saves them and sends
/// <see cref="RendererGaze"/>).
/// "placed" says the character was moved or resized and has settled: Martlet then asks where it is ("where", replied to with
/// <see cref="RendererPlacement"/>) and saves that on this PC. "clear" (Clear emotes) turns off every lingering emote the
/// character shows. "framed" says the character was moved or zoomed within the camera
/// view and has settled: Martlet then reads the view ("zoom" "status") and saves the framing. Zoom, position and keep-on-top
/// stay inside the overlay. Taps on the character travel on the same pipe as "touch" messages (<see cref="CharacterTouch"/>).
/// </summary>
public sealed record RendererRequest(string Action)
{
    public const string LookPrefix = "look-", FreeOn = "look-free-on", FreeOff = "look-free-off";

    public static IReadOnlyList<string> Actions { get; } = ["hide", "open", "talk", "settings", "lock", "unlock", "mute", "unmute", "placed", "framed", "clear",
        .. RendererGaze.Choices.Select(choice => LookPrefix + choice), FreeOn, FreeOff];
}
/// <summary>
/// The character frame's size in device-independent pixels, its top relative to the top of its screen's work area
/// (negative when it extends above the screen; null if unknown), its camera zoom, how far the top of the character's head
/// sits below the frame's top edge as a fraction of its height (negative when cut off; null until reported), and the
/// overlay's full drawing width: the frame plus the transparent room beside it the model can move into (null if unknown), and
/// whether its place is locked (null if unknown). <paramref name="X"/> and <paramref name="Y"/> are where the character's middle
/// sits as fractions of the drawing's width and height from its center (+x right, +y up), and <paramref name="Camera"/> whether
/// this is the camera view (null if unknown).
/// </summary>
public sealed record RendererView(double Width, double Height, double? ScreenTop, double Zoom, double? HeadTop, double? DrawWidth = null,
    bool? Locked = null, double? X = null, double? Y = null, bool? Camera = null);
/// <summary>Over the character's renderer pipe: a picture of the character as it shows now (Discord's bot picture and
/// <c>/selfie</c>, and touch zones). <paramref name="Portrait"/> crops a square around the head and shoulders, else the whole
/// character; the longer side is at most <paramref name="Edge"/> pixels (64 to 2048; never larger than the capture, and smaller
/// when the PNG would not fit one renderer message). With <paramref name="Whole"/> (touch zones) the character is framed whole
/// for the picture (no zoom, no pan; it shows so for a moment) and the reply also carries the zones probe taken in that
/// framing. Replied to with <see cref="RendererPicture"/>.</summary>
public sealed record RendererSnapshot(bool Portrait, int Edge = 512, bool Whole = false)
{
    public const int MinimumEdge = 64, MaximumEdge = 2048;
}
/// <summary>A PNG of the character (base64, small enough for one renderer message) and its size in pixels. <paramref name="CropLeft"/>,
/// <paramref name="CropTop"/>, <paramref name="CropWidth"/> and <paramref name="CropHeight"/> say where the picture sat on the
/// renderer page, as fractions of the page (touch zones compare it with touches). <paramref name="Probe"/>: for a
/// <see cref="RendererSnapshot.Whole"/> picture, where the model's drawables or bones were in the same framing.</summary>
public sealed record RendererPicture(string Png, int Width, int Height, double CropLeft = 0, double CropTop = 0, double CropWidth = 1,
    double CropHeight = 1, RendererZoneProbe? Probe = null);
public sealed record RendererMapping(string Target, string Aspect);
public sealed record RendererConfiguration(string SourceId, string ModelRevision, string MappingRevision, RendererMapping[] Targets);
public sealed record RendererIdentity(Guid SessionId, Guid TurnId, Guid RequestId, string SourceId, long Epoch, int SampleRate);
public sealed record RendererParameters(RendererIdentity Identity, long Sequence, long SampleOffset,
    long ActualPlaybackSampleOffset, string ModelRevision, string MappingRevision, IReadOnlyDictionary<string, double> Parameters);

public static class RendererProtocol
{
    // A touch zones snapshot (a PNG of the character at its full size, base64) is the largest message.
    public const int MaximumMessageBytes = 8 * 1024 * 1024;
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

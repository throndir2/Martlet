using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>The face in a picture's own pixels: its middle, its width and its roll (radians, clockwise on screen).</summary>
public sealed record EyeFace(double X, double Y, double Width, double Angle = 0)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Angle) && Width > 0 &&
        Math.Abs(Angle) <= 2 * Math.PI;

    /// <summary>The face of a touch zones snapshot (<paramref name="width"/> by <paramref name="height"/> pixels, cut from the
    /// renderer page at <paramref name="crop"/>, fractions of the page) from the probe's face (fractions of the page, its width
    /// a fraction of the page's width); null when the probe has none.</summary>
    public static EyeFace? InSnapshot(RendererFace? face, TouchZoneBox crop, int width, int height) =>
        face is { IsValid: true } && crop.Width > 0 && crop.Height > 0 && width > 0 && height > 0
            ? new EyeFace((face.X - crop.X) / crop.Width * width, (face.Y - crop.Y) / crop.Height * height, face.Width / crop.Width * width, face.Angle)
            : null;
}

/// <summary>The boxes of a face measurement, as fractions of the close-up: each eye's iris and its opening (the white and the
/// iris that show between the eyelids), and, when the answer gave them, each cheek (where a blush goes) and the mouth. Left and
/// right are as the viewer sees them.</summary>
public sealed record EyeBoxes(TouchZoneBox LeftIris, TouchZoneBox LeftEye, TouchZoneBox RightIris, TouchZoneBox RightEye,
    TouchZoneBox? LeftCheek = null, TouchZoneBox? RightCheek = null, TouchZoneBox? Mouth = null)
{
    /// <summary>The same boxes with the two eyes (and the two cheeks) the other way round.</summary>
    [JsonIgnore] public EyeBoxes Swapped => new(RightIris, RightEye, LeftIris, LeftEye, RightCheek, LeftCheek, Mouth);

    /// <summary>The same boxes without the cheeks and the mouth.</summary>
    [JsonIgnore] public EyeBoxes EyesOnly => this with { LeftCheek = null, RightCheek = null, Mouth = null };
}

/// <summary>The close-up of the face the vision model sees: a square of <see cref="Area"/> (pixels of the
/// <see cref="Snapshot"/>, about <see cref="CharacterEyes.FaceWidths"/> face widths wide around <see cref="Face"/>), flattened
/// onto <see cref="Backdrop"/>, scaled to about <see cref="Edge"/> pixels and gridded. Without a snapshot (an MCP rehearsal)
/// it only measures.</summary>
public sealed record EyeCloseUp(ZonePixels? Snapshot, PixelRect Area, EyeFace Face, Rgb Backdrop, int Edge = CharacterEyes.Edge)
{
    /// <summary>The size of the picture sent, in pixels (as <see cref="TouchZonePictures.Compose"/> scales the area).</summary>
    public (int Width, int Height) Size
    {
        get
        {
            var scale = Math.Min((double)Edge / Math.Max(Area.Width, Area.Height), CharacterEyes.MaximumZoom);
            return (Math.Max(1, (int)Math.Round(Area.Width * scale)), Math.Max(1, (int)Math.Round(Area.Height * scale)));
        }
    }

    /// <summary>The picture sent, with <paramref name="marks"/> (fractions of the close-up) drawn and numbered; null without a
    /// snapshot.</summary>
    public ZonePixels? Picture(IReadOnlyList<MarkedBox>? marks = null) => Snapshot is null ? null :
        TouchZonePictures.Compose(Snapshot, Area.Fraction(Snapshot.Width, Snapshot.Height), Edge, CharacterEyes.MaximumZoom, Backdrop, grid: true, marks);

    /// <summary>A point of the close-up (fractions) in the face's own frame: face widths from its middle, x toward the viewer's
    /// right and y down, with its roll taken out.</summary>
    public (double X, double Y) InFace(double x, double y)
    {
        double dx = Area.X + x * Area.Width - Face.X, dy = Area.Y + y * Area.Height - Face.Y;
        double cos = Math.Cos(Face.Angle), sin = Math.Sin(Face.Angle);
        return ((dx * cos + dy * sin) / Face.Width, (-dx * sin + dy * cos) / Face.Width);
    }

    /// <summary>A length across the close-up (a fraction of its width) in face widths.</summary>
    public double Across(double fraction) => fraction * Area.Width / Face.Width;

    /// <summary>A length down the close-up (a fraction of its height) in face widths.</summary>
    public double Down(double fraction) => fraction * Area.Height / Face.Width;

    /// <summary>A close-up with no snapshot behind it: <paramref name="edge"/> pixels square, exactly
    /// <see cref="CharacterEyes.FaceWidths"/> face widths wide around an upright face (MCP rehearsals).</summary>
    public static EyeCloseUp Standard(int edge = CharacterEyes.Edge) =>
        new(null, new(0, 0, edge, edge), new(edge / 2.0, edge / 2.0, edge / CharacterEyes.FaceWidths), TouchZonePictures.LightBackdrop, edge);
}

/// <summary>What one request of an eye measurement asks: where the eyes are, the same again after an answer Martlet couldn't
/// read, or a check of the boxes drawn and numbered on the close-up.</summary>
public enum EyeAskKind { Eyes, Again, Check }

/// <summary>One request of an eye measurement: its kind, the step's name, the instructions and message, the picture sent (null
/// in a rehearsal without a snapshot) and the boxes drawn on it.</summary>
public sealed record EyeAsk(EyeAskKind Kind, string Step, string Instructions, string Text, ZonePixels? Picture, IReadOnlyList<MarkedBox> Marks);

/// <summary>What an eye measurement found: the boxes (fractions of the close-up) and the hint, or why not
/// (<see cref="Failure"/>); how many requests it made, one line per step and what Martlet's checks found in the last answer.</summary>
public sealed record EyeMeasurementResult(EyeBoxes? Boxes, RendererEyes? Hint, string? Failure, int Requests, IReadOnlyList<string> Steps,
    IReadOnlyList<string> Problems);

/// <summary>One picture an eye measurement sent: its file in the model's folder, the step, its size in pixels and bytes and its
/// media type.</summary>
public sealed record EyeSentPicture(string File, string Step, int Width, int Height, int Bytes, string MediaType);

/// <summary>One model's face as vision measured it (Companion › Eyes › Where the face is), kept in character-eyes.json:
/// the hint the renderer gets (<see cref="Left"/> and <see cref="Right"/> eyes, and the cheeks and mouth when they were
/// measured, in face widths), who measured them (vision, or a FIXTURE - NOT AI stand-in), when, in how many requests, the
/// boxes the answer gave (fractions of the close-up) and the pictures sent (in character-eyes\&lt;model&gt;\).</summary>
public sealed record CharacterEyeMeasurement
{
    public const string ByVision = "vision", ByFixture = "fixture";
    public required string ModelId { get; init; }
    public required RendererEye Left { get; init; }
    public required RendererEye Right { get; init; }
    public RendererFeature? CheekLeft { get; init; }
    public RendererFeature? CheekRight { get; init; }
    public RendererFeature? Mouth { get; init; }
    public string By { get; init; } = ByVision;
    public DateTimeOffset MeasuredAt { get; init; }
    public int Requests { get; init; }
    public EyeBoxes? Boxes { get; init; }
    public IReadOnlyList<EyeSentPicture> Pictures { get; init; } = [];
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>The face hint the renderer gets.</summary>
    [JsonIgnore] public RendererEyes Hint => new(Left, Right, CheekLeft, CheekRight, Mouth);

    /// <summary>Whether the cheeks were measured too (a measurement made before Martlet measured them has only the eyes).</summary>
    [JsonIgnore] public bool HasCheeks => CheekLeft is not null && CheekRight is not null;

    /// <summary>A measurement of the model <paramref name="modelId"/> with <paramref name="hint"/>'s parts.</summary>
    public static CharacterEyeMeasurement From(string modelId, RendererEyes hint) => new()
    {
        ModelId = modelId, Left = hint.Left!, Right = hint.Right!, CheekLeft = hint.CheekLeft, CheekRight = hint.CheekRight, Mouth = hint.Mouth
    };
}

/// <summary>
/// Measuring a character's face with the Thinking model's vision, once per model, so that drawings over the eyes (heart_eyes,
/// star_eyes, the dizzy swirls) cover only the iris and stay inside the eye, and a blush lies on the cheeks, on every model.
/// It starts from the touch zones snapshot (the character drawn off screen in its rest pose: no idle motion, so no blink, eyes
/// open and looking ahead) and the face anchor the renderer read while drawing it (that renderer never has a hint, so the
/// anchor is always Martlet's own). A square close-up about <see cref="FaceWidths"/> face widths wide around the face, scaled
/// to about <see cref="Edge"/> pixels with a grid of tenths, goes to the vision model, which marks each eye's iris and
/// opening, each cheek and the mouth. Martlet reads the boxes, puts left and right back the right way round, checks them
/// (both eyes, each iris mostly inside its eye, sizes that fit the face, eyes open and level, each cheek below its eye and the
/// mouth below the eyes) and, when the first answer fails, asks once more with its boxes drawn and numbered on the close-up
/// and the problems listed. The eyes must pass; a cheek or mouth that still fails is left out. The boxes become the renderer's
/// face hint (<see cref="RendererEyes"/>, in face widths from the face anchor's middle, roll taken out), saved per model in
/// character-eyes.json with the pictures sent.
/// </summary>
public static class CharacterEyes
{
    public const string FileName = "character-eyes.json";
    public const string Folder = "character-eyes";
    /// <summary>The picture of the boxes kept for each model: the close-up with the answer's boxes drawn and numbered.</summary>
    public const string BoxesFile = "boxes.png";
    public const int MaximumModels = 32, MaximumBytes = 1024 * 1024, Edge = 768;
    /// <summary>How wide the close-up is, in face widths, and how much it may enlarge the snapshot.</summary>
    public const double FaceWidths = 1.6, MaximumZoom = 8;
    /// <summary>The smallest face (pixels across in the snapshot) worth a close-up.</summary>
    public const double MinimumFace = 16;

    // What Martlet's checks accept, in face widths (the face anchor's width): an eye's opening, how flat it may be before it looks
    // closed, an iris's size, how far apart and how level the two eyes are, and how much of each iris lies inside its eye.
    public const double MinimumEyeWidth = 0.05, MaximumEyeWidth = 0.6, MinimumEyeHeight = 0.012, MaximumEyeHeight = 0.5, MinimumOpen = 0.1;
    public const double MinimumIris = 0.025, MaximumIris = 0.45, MinimumApart = 0.1, MaximumTilt = 0.25, MostlyInside = 0.6;
    // What Martlet's checks accept for a cheek (where a blush goes) and the mouth, in face widths: their width, and how far
    // below the eye line a cheek's middle may be.
    public const double MinimumCheek = 0.06, MaximumCheek = 0.6, MaximumCheekDrop = 0.8, MinimumMouth = 0.02, MaximumMouth = 0.6;

    // ---------- the close-up ----------

    /// <summary>The close-up of the face in <paramref name="snapshot"/>: a square <see cref="FaceWidths"/> face widths wide around
    /// <paramref name="face"/> (pixels of the snapshot), kept inside the snapshot; null when the face is too small or not in it.</summary>
    public static EyeCloseUp? CloseUp(ZonePixels snapshot, EyeFace face, int edge = Edge)
    {
        if (!face.IsValid || face.Width < MinimumFace || face.X < 0 || face.Y < 0 || face.X > snapshot.Width || face.Y > snapshot.Height) return null;
        var side = Math.Min((int)Math.Round(face.Width * FaceWidths), Math.Min(snapshot.Width, snapshot.Height));
        if (side < MinimumFace) return null;
        var x = Math.Clamp((int)Math.Round(face.X - side / 2.0), 0, snapshot.Width - side);
        var y = Math.Clamp((int)Math.Round(face.Y - side / 2.0), 0, snapshot.Height - side);
        return new(snapshot, new(x, y, side, side), face, TouchZonePictures.Backdrop(snapshot), edge);
    }

    // ---------- what the vision model is told ----------

    /// <summary>The instructions of every request: what to mark and the JSON to answer with.</summary>
    public static string Instructions =>
        "You measure the face of a character (a 2D or 3D avatar) in a close-up picture of its face, so that drawings can be " +
        "placed over its eyes and a blush on its cheeks. Only locate them; don't describe or judge the character. Left and right " +
        "are as YOU see them: \"left\" is the one nearer the picture's left edge. The picture has a grid of tenths: the numbers " +
        "along its top edge are fractions of its width (0 at the left edge, 1 at the right edge) and the numbers along its left " +
        "edge are fractions of its height (0 at the top, 1 at the bottom). Read positions from the grid. For each eye give two " +
        "tight boxes: \"iris\" around the colored iris with its pupil (only the part that shows), and \"eye\" around the eye's " +
        "opening, the white and the iris that show between the upper and lower eyelids (not the lashes, the eyelid lines or the " +
        "brows). For each cheek give one box, \"cheek\": the round of the cheek below and a little outside that eye, where an " +
        "anime blush is painted, on the skin (not on the eye, the hair or the face's outline). Give one box for the \"mouth\" " +
        "(the lips, or where the mouth is when it is only a line). Give each box as fractions of THIS picture, with two " +
        "decimals: left and right as fractions of its width, top and bottom as fractions of its height. Answer with JSON only, " +
        "no other text, in this form:\n" +
        "{\"left\":{\"iris\":{\"left\":0.33,\"top\":0.41,\"right\":0.41,\"bottom\":0.52},\"eye\":{\"left\":0.27,\"top\":0.42," +
        "\"right\":0.45,\"bottom\":0.52},\"cheek\":{\"left\":0.24,\"top\":0.57,\"right\":0.4,\"bottom\":0.66}}," +
        "\"right\":{\"iris\":{\"left\":0.59,\"top\":0.41,\"right\":0.67,\"bottom\":0.52},\"eye\":{\"left\":0.55,\"top\":0.42," +
        "\"right\":0.73,\"bottom\":0.52},\"cheek\":{\"left\":0.6,\"top\":0.57,\"right\":0.76,\"bottom\":0.66}}," +
        "\"mouth\":{\"left\":0.45,\"top\":0.7,\"right\":0.55,\"bottom\":0.75}}";

    /// <summary>The message of the first request.</summary>
    public const string Text = "This close-up shows the character's face, with its eyes open and looking straight ahead. Mark both eyes, both cheeks and the mouth.";

    /// <summary>The message of a second request after an answer Martlet couldn't use (<paramref name="problem"/>).</summary>
    public static string AgainText(string problem) =>
        $"This close-up shows the character's face, with its eyes open and looking straight ahead. Martlet couldn't use your last " +
        $"answer: {problem.TrimEnd('.')}. Look again and mark both eyes, both cheeks and the mouth, with JSON only in the form given.";

    /// <summary>The message of a check: the last answer's boxes are drawn and numbered on the close-up, with what Martlet's
    /// checks found.</summary>
    public static string CheckText(IReadOnlyList<string> problems) =>
        "This close-up shows the character's face, with its eyes open and looking straight ahead. Martlet drew your last boxes on " +
        "it: 1 = the left iris, 2 = the left eye's opening, 3 = the right iris, 4 = the right eye's opening, 5 = the left cheek, " +
        "6 = the right cheek, 7 = the mouth (a box you didn't give isn't drawn). Martlet measured these problems:\n- " +
        string.Join("\n- ", problems) + "\nLook again and answer with every box (both eyes, both cheeks and the mouth), corrected, " +
        "with JSON only in the form given.";

    /// <summary>The boxes drawn on a check: 1 and 2 the left iris and opening, 3 and 4 the right ones, 5 and 6 the left and right
    /// cheeks and 7 the mouth (the ones the answer gave).</summary>
    public static IReadOnlyList<MarkedBox> Marks(EyeBoxes boxes)
    {
        var marks = new List<MarkedBox>
        {
            new(1, boxes.LeftIris, TouchZonePictures.MarkColors[0]), new(2, boxes.LeftEye, TouchZonePictures.MarkColors[1]),
            new(3, boxes.RightIris, TouchZonePictures.MarkColors[2]), new(4, boxes.RightEye, TouchZonePictures.MarkColors[4])
        };
        if (boxes.LeftCheek is { } left) marks.Add(new(5, left, TouchZonePictures.MarkColors[5]));
        if (boxes.RightCheek is { } right) marks.Add(new(6, right, TouchZonePictures.MarkColors[6]));
        if (boxes.Mouth is { } mouth) marks.Add(new(7, mouth, TouchZonePictures.MarkColors[3]));
        return marks;
    }

    // ---------- reading and checking the answer ----------

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 };

    /// <summary>The four boxes an answer gives (fractions of a <paramref name="width"/> by <paramref name="height"/> close-up),
    /// read leniently: eyes by side with an iris and an eye box each, flat keys such as left_iris, a list of eyes each naming its
    /// side, boxes as named edges or arrays, as fractions, pixels or the 0..1000 grid; or null and why not.</summary>
    public static (EyeBoxes? Boxes, string? Problem) Parse(string? answer, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(answer)) return (null, "the answer was empty");
        var start = answer.IndexOfAny(['{', '[']);
        var end = Math.Max(answer.LastIndexOf('}'), answer.LastIndexOf(']'));
        JsonElement root;
        try
        {
            if (start < 0 || end <= start) return (null, "the answer had no JSON");
            using var document = JsonDocument.Parse(answer[start..(end + 1)], Options);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return (null, "the answer's JSON couldn't be read"); }
        var raw = new List<(string Key, double[] Box)>();
        Walk(root, null, null, raw, 0);
        var boxes = ZoneAnswers.Scale(raw, width, height);
        TouchZoneBox? Find(string key) => boxes.Where(b => b.Key == key).Select(b => b.Box).FirstOrDefault();
        var found = new[] { Find("left_iris"), Find("left_eye"), Find("right_iris"), Find("right_eye") };
        string[] names = ["the left iris", "the left eye's opening", "the right iris", "the right eye's opening"];
        var missing = names.Where((_, i) => found[i] is null).ToArray();
        return missing.Length == 0 ? (new(found[0]!, found[1]!, found[2]!, found[3]!, Find("left_cheek"), Find("right_cheek"), Find("mouth")), null)
            : (null, "it gave no box for " + (missing.Length == 1 ? missing[0] : string.Join(", ", missing[..^1]) + " and " + missing[^1]));
    }

    // Collects boxes keyed "<side>_<part>", the side and part coming from the names on the way down (the left eye, its iris).
    private static void Walk(JsonElement element, string? side, string? part, List<(string Key, double[] Box)> raw, int depth)
    {
        if (depth > 6) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (ZoneAnswers.Numbers(element) is { Length: 4 } numbers) { Add(side, part, numbers, raw); return; }
            foreach (var item in element.EnumerateArray())
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(item, side, part, raw, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        // {"side":"left","iris":...} or {"id":"left_iris","box":[...]}: the object names what it is.
        if (Label(element) is { } label)
        {
            side = SideOf(label) ?? side;
            part = PartOf(label) ?? part;
        }
        if (!Grouped(element) && ZoneAnswers.RawBox(element) is { } box)
        {
            Add(side, part, box, raw);
            return;
        }
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Walk(property.Value, SideOf(property.Name) ?? side, PartOf(property.Name) ?? part, raw, depth + 1);
    }

    private static void Add(string? side, string? part, double[] box, List<(string Key, double[] Box)> raw)
    {
        // The mouth has no side.
        if (part == "mouth") raw.Add(("mouth", box));
        else if (side is not null && part is not null) raw.Add(($"{side}_{part}", box));
    }

    // Whether an object holds an eye's parts or both eyes (named for a side or a part), so it isn't a box itself.
    private static bool Grouped(JsonElement element) => element.EnumerateObject().Any(p =>
        p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && (PartOf(p.Name) is not null || SideOf(p.Name) is not null));

    private static string? Label(JsonElement element)
    {
        foreach (var name in new[] { "side", "id", "name", "label", "which", "eye", "part", "zone" })
            if (ZoneAnswers.Property(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text) return text;
        return null;
    }

    private static string? SideOf(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("left", StringComparison.Ordinal) || lower is "l" ? "left"
            : lower.Contains("right", StringComparison.Ordinal) || lower is "r" ? "right" : null;
    }

    private static string? PartOf(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("iris", StringComparison.Ordinal) || lower.Contains("pupil", StringComparison.Ordinal)) return "iris";
        if (lower.Contains("cheek", StringComparison.Ordinal) || lower.Contains("blush", StringComparison.Ordinal)) return "cheek";
        if (lower.Contains("mouth", StringComparison.Ordinal) || lower.Contains("lips", StringComparison.Ordinal) || lower is "lip") return "mouth";
        return new[] { "eye", "open", "sclera", "white", "socket", "lid" }.Any(word => lower.Contains(word, StringComparison.Ordinal)) ? "eye" : null;
    }

    /// <summary>The boxes with the left eye (the one nearer the picture's left edge) first, and whether they were swapped. The
    /// cheeks are put in order by where they are too.</summary>
    public static (EyeBoxes Boxes, bool Swapped) Ordered(EyeBoxes boxes)
    {
        var swapped = boxes.LeftEye.CenterX > boxes.RightEye.CenterX;
        if (swapped) boxes = boxes.Swapped;
        if (boxes is { LeftCheek: { } left, RightCheek: { } right } && left.CenterX > right.CenterX)
            boxes = boxes with { LeftCheek = right, RightCheek = left };
        return (boxes, swapped);
    }

    /// <summary>What Martlet's checks find wrong with <paramref name="boxes"/> (in order: left eye first) on
    /// <paramref name="closeUp"/>, in words for the next request: the eyes' problems (<see cref="EyeProblems"/>) and then the
    /// cheeks' and mouth's (<see cref="FeatureProblems"/>); empty when they pass.</summary>
    public static IReadOnlyList<string> Problems(EyeBoxes boxes, EyeCloseUp closeUp) =>
        [.. EyeProblems(boxes, closeUp), .. FeatureProblems(boxes, closeUp).Select(p => p.Problem)];

    /// <summary>What Martlet's checks find wrong with the eyes of <paramref name="boxes"/> on <paramref name="closeUp"/>; empty
    /// when they pass.</summary>
    public static IReadOnlyList<string> EyeProblems(EyeBoxes boxes, EyeCloseUp closeUp)
    {
        var problems = new List<string>();
        void Eye(string side, int number, TouchZoneBox iris, TouchZoneBox eye)
        {
            double wide = closeUp.Across(eye.Width), tall = closeUp.Down(eye.Height);
            var size = (closeUp.Across(iris.Width) + closeUp.Down(iris.Height)) / 2;
            if (eye.Covers(iris) < MostlyInside)
                problems.Add($"The {side} iris (box {number}) lies mostly outside the {side} eye's opening (box {number + 1}).");
            else if (iris.Width > eye.Width * 1.2 || iris.Height > eye.Height * 1.3)
                problems.Add($"The {side} iris (box {number}) is larger than the {side} eye's opening (box {number + 1}).");
            if (wide is < MinimumEyeWidth or > MaximumEyeWidth)
                problems.Add($"The {side} eye's opening (box {number + 1}) is {F(wide)} face widths wide, but an eye is {F(MinimumEyeWidth)} to {F(MaximumEyeWidth)}.");
            else if (tall < MinimumEyeHeight || tall < MinimumOpen * wide)
                problems.Add($"The {side} eye's opening (box {number + 1}) is very flat, as if the eye were closed. Mark the opening as it shows.");
            else if (tall > MaximumEyeHeight)
                problems.Add($"The {side} eye's opening (box {number + 1}) is {F(tall)} face widths tall, but an eye is at most {F(MaximumEyeHeight)}.");
            if (size is < MinimumIris or > MaximumIris)
                problems.Add($"The {side} iris (box {number}) is {F(size)} face widths across, but an iris is {F(MinimumIris)} to {F(MaximumIris)}.");
        }
        Eye("left", 1, boxes.LeftIris, boxes.LeftEye);
        Eye("right", 3, boxes.RightIris, boxes.RightEye);
        var (leftX, leftY) = closeUp.InFace(boxes.LeftEye.CenterX, boxes.LeftEye.CenterY);
        var (rightX, rightY) = closeUp.InFace(boxes.RightEye.CenterX, boxes.RightEye.CenterY);
        if (rightX - leftX < MinimumApart || boxes.LeftEye.Covers(boxes.RightEye) > 0.25 || boxes.RightEye.Covers(boxes.LeftEye) > 0.25)
            problems.Add("The two eyes' openings (boxes 2 and 4) overlap: they must be the character's two different eyes.");
        else if (Math.Abs(rightY - leftY) > MaximumTilt)
            problems.Add($"The two eyes (boxes 2 and 4) aren't level: one is {F(Math.Abs(rightY - leftY))} face widths higher, but the face looks straight ahead.");
        return problems;
    }

    /// <summary>The parts of a face measurement besides the eyes: both cheeks together (a blush needs both) and the mouth.</summary>
    public enum FacePart { Cheeks, Mouth }

    /// <summary>What Martlet's checks find wrong with the cheeks and the mouth of <paramref name="boxes"/> on
    /// <paramref name="closeUp"/> (a part the answer left out too), with the part each problem is about; empty when they pass.
    /// Each cheek must be about a cheek's size, on its eye's side of the face and below that eye, but not on it; the mouth
    /// about a mouth's size, below the eyes and between them.</summary>
    public static IReadOnlyList<(FacePart Part, string Problem)> FeatureProblems(EyeBoxes boxes, EyeCloseUp closeUp)
    {
        var problems = new List<(FacePart, string)>();
        var (leftX, leftY) = closeUp.InFace(boxes.LeftEye.CenterX, boxes.LeftEye.CenterY);
        var (rightX, rightY) = closeUp.InFace(boxes.RightEye.CenterX, boxes.RightEye.CenterY);
        double between = (leftX + rightX) / 2, eyeLine = (leftY + rightY) / 2;
        void Cheek(string side, int number, TouchZoneBox? cheek, TouchZoneBox eye, double eyeY, bool left)
        {
            if (cheek is null) return;
            var wide = closeUp.Across(cheek.Width);
            var (x, y) = closeUp.InFace(cheek.CenterX, cheek.CenterY);
            if (wide is < MinimumCheek or > MaximumCheek)
                problems.Add((FacePart.Cheeks, $"The {side} cheek (box {number}) is {F(wide)} face widths wide, but a cheek is {F(MinimumCheek)} to {F(MaximumCheek)}."));
            if (left ? x > between : x < between)
                problems.Add((FacePart.Cheeks, $"The {side} cheek (box {number}) isn't on the {side} side of the face, below the {side} eye."));
            if (eye.Contains(cheek.CenterX, cheek.CenterY) || y <= eyeY)
                problems.Add((FacePart.Cheeks, $"The {side} cheek (box {number}) is on or above the {side} eye; a blush goes on the cheek below the eye."));
            else if (y - eyeY > MaximumCheekDrop)
                problems.Add((FacePart.Cheeks, $"The {side} cheek (box {number}) is {F(y - eyeY)} face widths below the {side} eye, but a cheek is at most {F(MaximumCheekDrop)} below it."));
        }
        if (boxes.LeftCheek is null && boxes.RightCheek is null) problems.Add((FacePart.Cheeks, "It gave no box for the cheeks (5 and 6)."));
        else if (boxes.LeftCheek is null || boxes.RightCheek is null)
            problems.Add((FacePart.Cheeks, $"It gave no box for the {(boxes.LeftCheek is null ? "left cheek (5)" : "right cheek (6)")}."));
        Cheek("left", 5, boxes.LeftCheek, boxes.LeftEye, leftY, left: true);
        Cheek("right", 6, boxes.RightCheek, boxes.RightEye, rightY, left: false);
        if (boxes.Mouth is not { } mouth) problems.Add((FacePart.Mouth, "It gave no box for the mouth (7)."));
        else
        {
            var wide = closeUp.Across(mouth.Width);
            var (x, y) = closeUp.InFace(mouth.CenterX, mouth.CenterY);
            if (wide is < MinimumMouth or > MaximumMouth)
                problems.Add((FacePart.Mouth, $"The mouth (box 7) is {F(wide)} face widths wide, but a mouth is {F(MinimumMouth)} to {F(MaximumMouth)}."));
            if (y < eyeLine + MinimumApart || x < leftX || x > rightX)
                problems.Add((FacePart.Mouth, "The mouth (box 7) isn't below the eyes and between them."));
        }
        return problems;
    }

    /// <summary><paramref name="boxes"/> without the parts that fail Martlet's checks (both cheeks when either fails), and the
    /// problems of the parts left out.</summary>
    public static (EyeBoxes Boxes, IReadOnlyList<string> Dropped) Kept(EyeBoxes boxes, EyeCloseUp closeUp)
    {
        var problems = FeatureProblems(boxes, closeUp);
        if (problems.Any(p => p.Part == FacePart.Cheeks)) boxes = boxes with { LeftCheek = null, RightCheek = null };
        if (problems.Any(p => p.Part == FacePart.Mouth)) boxes = boxes with { Mouth = null };
        return (boxes, [.. problems.Select(p => p.Problem)]);
    }

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // ---------- the hint ----------

    /// <summary>The renderer's face hint from <paramref name="boxes"/> on <paramref name="closeUp"/>: each iris's middle and
    /// radius (the mean of its box's half width and half height), each opening's middle, half width and half height, and each
    /// cheek's (both or none) and the mouth's middle and radius, in face widths from the face's middle with its roll taken out,
    /// rounded to 4 decimals.</summary>
    public static RendererEyes Hint(EyeBoxes boxes, EyeCloseUp closeUp)
    {
        static double Round(double value) => Math.Round(value, 4);
        static double Size(double value) => Math.Clamp(Round(value), 0.0001, RendererEyes.Largest);
        static double Place(double value) => Math.Clamp(Round(value), -RendererEyes.Farthest, RendererEyes.Farthest);
        RendererEye Eye(TouchZoneBox iris, TouchZoneBox eye)
        {
            var (ix, iy) = closeUp.InFace(iris.CenterX, iris.CenterY);
            var (ex, ey) = closeUp.InFace(eye.CenterX, eye.CenterY);
            return new(new(Place(ix), Place(iy), Size((closeUp.Across(iris.Width) + closeUp.Down(iris.Height)) / 4)),
                new(Place(ex), Place(ey), Size(closeUp.Across(eye.Width) / 2), Size(closeUp.Down(eye.Height) / 2)));
        }
        // A cheek or the mouth: its middle, and its radius as the mean of its box's half width and half height.
        RendererFeature? Feature(TouchZoneBox? box)
        {
            if (box is null) return null;
            var (x, y) = closeUp.InFace(box.CenterX, box.CenterY);
            return new(Place(x), Place(y), Size((closeUp.Across(box.Width) + closeUp.Down(box.Height)) / 4));
        }
        var (cheekLeft, cheekRight) = boxes is { LeftCheek: not null, RightCheek: not null } ? (Feature(boxes.LeftCheek), Feature(boxes.RightCheek)) : (null, null);
        return new(Eye(boxes.LeftIris, boxes.LeftEye), Eye(boxes.RightIris, boxes.RightEye), cheekLeft, cheekRight, Feature(boxes.Mouth));
    }

    // ---------- the measurement ----------

    /// <summary>Measures the face on <paramref name="closeUp"/>: one request through <paramref name="ask"/>, and when its answer
    /// can't be read or fails Martlet's checks, one more (a check with its boxes drawn and numbered, or the question again).
    /// The eyes must pass; the cheeks or mouth that still fail after the second answer are left out (the first answer's eyes are
    /// kept when only the second answer's eyes fail). <paramref name="progress"/> gets each step in words.</summary>
    public static async Task<EyeMeasurementResult> RunAsync(EyeCloseUp closeUp, Func<EyeAsk, CancellationToken, Task<(string? Answer, string? Failure)>> ask,
        Action<string>? progress, CancellationToken token)
    {
        var steps = new List<string>();
        var (width, height) = closeUp.Size;
        var requests = 0;

        // What one answer gave: the boxes (left eye first), what the checks found (all of it, and whether the eyes pass), why it
        // couldn't be read, or why the request failed.
        async Task<(EyeBoxes? Boxes, string? Problem, IReadOnlyList<string> Problems, bool EyesPass, string? Failure)> AskAsync(EyeAsk request)
        {
            requests++;
            var (answer, why) = await ask(request, token).ConfigureAwait(false);
            if (answer is null)
            {
                steps.Add($"{request.Step}: no answer" + (why is null ? "" : $" ({why})"));
                return (null, "there was no answer", [], false, why);
            }
            var (boxes, problem) = Parse(answer, width, height);
            if (boxes is null)
            {
                steps.Add($"{request.Step}: {problem}");
                return (null, problem, [], false, null);
            }
            (boxes, var swapped) = Ordered(boxes);
            var eyes = EyeProblems(boxes, closeUp);
            var problems = Problems(boxes, closeUp);
            var parts = 4 + (boxes.LeftCheek is null ? 0 : 1) + (boxes.RightCheek is null ? 0 : 1) + (boxes.Mouth is null ? 0 : 1);
            steps.Add($"{request.Step}: read {parts} boxes" + (swapped ? ", put left and right back the right way round" : "") +
                (problems.Count == 0 ? ", and they pass Martlet's checks" : $"; {problems.Count} problem{(problems.Count == 1 ? "" : "s")}: " + string.Join(" ", problems)));
            return (boxes, null, problems, eyes.Count == 0, null);
        }

        // The eyes passed: keeps the cheeks and the mouth that pass too.
        EyeMeasurementResult Done(EyeBoxes boxes)
        {
            var (kept, dropped) = Kept(boxes, closeUp);
            if (dropped.Count > 0)
                steps.Add("kept the eyes" + (kept.LeftCheek is null ? ", left out the cheeks" : "") + (kept.Mouth is null ? ", left out the mouth" : "") +
                    ": " + string.Join(" ", dropped));
            return new(kept, Hint(kept, closeUp), null, requests, [.. steps], dropped);
        }

        progress?.Invoke("Asking the Thinking model where the eyes, cheeks and mouth are in a close-up of the face...");
        var first = await AskAsync(new(EyeAskKind.Eyes, "eyes", Instructions, Text, closeUp.Picture(), [])).ConfigureAwait(false);
        if (first.Boxes is { } found && first.Problems.Count == 0) return Done(found);
        // The request itself failed: asking again now won't help.
        if (first.Failure is not null) return new(null, null, $"Couldn't ask the Thinking model ({first.Failure}).", requests, [.. steps], []);

        EyeAsk second;
        if (first.Boxes is { } drawn)
        {
            progress?.Invoke("Checking the face: the Thinking model sees its boxes drawn on the close-up...");
            var marks = Marks(drawn);
            second = new(EyeAskKind.Check, "check", Instructions, CheckText(first.Problems), closeUp.Picture(marks), marks);
        }
        else
        {
            progress?.Invoke("The answer couldn't be used. Asking the Thinking model once more...");
            second = new(EyeAskKind.Again, "again", Instructions, AgainText(first.Problem ?? "it had no eyes"), closeUp.Picture(), []);
        }
        var last = await AskAsync(second).ConfigureAwait(false);
        if (last.Boxes is { } corrected && last.EyesPass) return Done(corrected);
        if (first.Boxes is { } earlier && first.EyesPass) return Done(earlier);
        var eyeProblems = last.Boxes is { } failed ? EyeProblems(failed, closeUp) : [];
        var why = last.Failure is not null
            ? (first.Boxes is null ? "Couldn't ask the Thinking model again" : "The Thinking model's eyes didn't pass Martlet's checks, and the check couldn't be asked") +
              $" ({last.Failure})."
            : last.Boxes is not null ? "The Thinking model's eyes didn't pass Martlet's checks: " + eyeProblems[0]
            : $"The Thinking model's answers had no eyes Martlet could use ({last.Problem}).";
        return new(last.Boxes ?? first.Boxes, null, why, requests, [.. steps], last.Boxes is not null ? last.Problems : first.Problems);
    }

    // ---------- saving, per model ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 10
    };

    private sealed record Document(int Version, IReadOnlyList<CharacterEyeMeasurement> Models);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>The folder with the pictures the model's measurement sent and the picture of its boxes.</summary>
    public static string PictureFolder(string dataDirectory, string modelId) =>
        System.IO.Path.Combine(dataDirectory, Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)))[..24].ToLowerInvariant());

    public static CharacterEyeMeasurement? Load(string dataDirectory, string modelId) => LoadAll(dataDirectory).FirstOrDefault(m => m.ModelId == modelId);

    public static IReadOnlyList<CharacterEyeMeasurement> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            return document is { Version: 1, Models: { } models }
                ? models.Where(m => m is { ModelId.Length: > 0, Left.IsValid: true, Right.IsValid: true } && m.Hint.IsValid).ToArray() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    /// <summary>Saves <paramref name="measurement"/> for its model, replacing the one before (at most <see cref="MaximumModels"/>
    /// models are kept, the newest).</summary>
    public static async Task<CharacterEyeMeasurement> SaveAsync(string dataDirectory, CharacterEyeMeasurement measurement, CancellationToken token = default)
    {
        ContractRules.Require(measurement.ModelId.Length > 0 && measurement.Hint is { Clears: false, IsValid: true }, "The eyes' measurement isn't valid.");
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAsync(dataDirectory, LoadAll(dataDirectory).Where(m => m.ModelId != measurement.ModelId).Append(measurement)
                .OrderByDescending(m => m.MeasuredAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray(), token).ConfigureAwait(false);
            return measurement;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Forgets the model's measurement and its pictures; false when none was saved.</summary>
    public static async Task<bool> ForgetAsync(string dataDirectory, string modelId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var all = LoadAll(dataDirectory);
            var kept = all.Where(m => m.ModelId != modelId).ToArray();
            if (kept.Length != all.Count) await WriteAsync(dataDirectory, kept, token).ConfigureAwait(false);
            var folder = PictureFolder(dataDirectory, modelId);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return kept.Length != all.Count;
        }
        finally { Gate.Release(); }
    }

    private static async Task WriteAsync(string dataDirectory, IReadOnlyList<CharacterEyeMeasurement> models, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, models), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The eyes' measurements are too large.", ErrorCode.PayloadTooLarge);
        Directory.CreateDirectory(dataDirectory);
        var temporary = System.IO.Path.Combine(dataDirectory, $"character-eyes.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, Path(dataDirectory), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Empties the model's picture folder before a new measurement, so it holds only what that one sends.</summary>
    public static string ClearPictures(string dataDirectory, string modelId)
    {
        var folder = PictureFolder(dataDirectory, modelId);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ---------- in words ----------

    /// <summary>"Measured with vision at 3:12 PM" (today) or "... on 10/7/2026 3:12 PM", with FIXTURE - NOT AI when a stand-in
    /// answered.</summary>
    public static string Measured(CharacterEyeMeasurement measurement, DateTimeOffset now)
    {
        var at = measurement.MeasuredAt.ToLocalTime();
        return (measurement.By == CharacterEyeMeasurement.ByFixture ? "FIXTURE - NOT AI: measured by a stand-in" : "Measured with vision") +
            (at.Date == now.ToLocalTime().Date ? " at " + at.ToString("t", CultureInfo.CurrentCulture) : " on " + at.ToString("g", CultureInfo.CurrentCulture));
    }

    /// <summary>Where the shown model's eyes come from, and whether the blush uses measured cheeks, in words (Companion › Eyes ›
    /// Where the face is): <paramref name="eyesFrom"/> is the renderer's last answer (null while the character is hidden or
    /// before it answered) and <paramref name="saved"/> the model's measurement.</summary>
    public static string Status(string? eyesFrom, CharacterEyeMeasurement? saved, bool showing, DateTimeOffset now)
    {
        var measured = saved is null ? null : Measured(saved, now);
        var cheeks = saved is null ? "" : saved.HasCheeks ? " The blush goes on the measured cheeks."
            : " The cheeks weren't measured, so the blush uses an estimate. Measure the face to place it.";
        return eyesFrom switch
        {
            RendererEyesFrom.Vision => (measured ?? "Measured with vision") + "." + cheeks,
            RendererEyesFrom.Mesh => "From the model's own meshes." + (measured is null ? "" : $" {measured}, but the model's own eyes come first.") + cheeks,
            RendererEyesFrom.Bones => "From the model's own eye bones and meshes." + (measured is null ? "" : $" {measured}, but the model's own eyes come first.") + cheeks,
            RendererEyesFrom.Estimate => "Estimated: Martlet guesses where the eyes are from the face, so emotes drawn over the eyes may not fit them." +
                (measured is null ? " Measure the face to fit them." : $" {measured}, but the character doesn't use it."),
            _ when measured is not null => measured + (showing ? "." : ". The character uses it when it shows.") + cheeks,
            _ => showing ? "Martlet is reading where the character's eyes are..." : "Show the character to see where its eyes come from."
        };
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

public enum TouchZoneGroup { Head, Torso, Arms, LowerBody, Extras }

/// <summary>One body zone Martlet knows: its ID (the vision model's name for it), label, group, whether it is intimate (only
/// used with Include intimate zones on), what telling the character says, and the default reaction: one gesture or emote from
/// each slot, the first of its names the model has.</summary>
public sealed record TouchZoneKind(string Id, string Label, TouchZoneGroup Group, bool Intimate, string Narration,
    IReadOnlyList<IReadOnlyList<string>> Defaults);

/// <summary>A box as fractions 0..1 of a picture (origin top-left, +y down).</summary>
public sealed record TouchZoneBox(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Area => Width * Height;
    [JsonIgnore] public double CenterX => X + Width / 2;
    [JsonIgnore] public double CenterY => Y + Height / 2;
    [JsonIgnore] public bool Valid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0.002 && Height > 0.002 && X >= -0.01 && Y >= -0.01 && X + Width <= 1.01 && Y + Height <= 1.01;
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    /// <summary>The fraction of <paramref name="other"/>'s area inside this box.</summary>
    public double Covers(TouchZoneBox other)
    {
        var w = Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X);
        var h = Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 || other.Area <= 0 ? 0 : w * h / other.Area;
    }

    /// <summary>This box (a fraction of <paramref name="frame"/>) as a fraction of what <paramref name="frame"/> is a fraction of.</summary>
    public TouchZoneBox Within(TouchZoneBox frame) =>
        new(frame.X + X * frame.Width, frame.Y + Y * frame.Height, Width * frame.Width, Height * frame.Height);

    /// <summary>This box (a fraction of what <paramref name="frame"/> is a fraction of) as a fraction of <paramref name="frame"/>;
    /// the reverse of <see cref="Within"/>.</summary>
    public TouchZoneBox Relative(TouchZoneBox frame) =>
        new((X - frame.X) / frame.Width, (Y - frame.Y) / frame.Height, Width / frame.Width, Height / frame.Height);

    public TouchZoneBox Clamped()
    {
        double x = Math.Clamp(X, 0, 0.99), y = Math.Clamp(Y, 0, 0.99);
        return new(x, y, Math.Clamp(Width, 0.01, 1 - x), Math.Clamp(Height, 0.01, 1 - y));
    }
}

/// <summary>What touching a zone does: the gestures, emotes and motions it plays (their <see cref="CharacterActionSource.Id"/>s;
/// null plays the zone's default, an empty list nothing), whether Martlet notices it (<see cref="Notices"/>: the touch goes to
/// the Thinking model, with what the user says or as a short reply of its own, <see cref="Narration"/> an optional hint in the
/// owner's words) and how long the zone then rests.</summary>
public sealed record CharacterTouchReaction
{
    public const double DefaultCooldown = 4, MaximumCooldown = 600;
    public IReadOnlyList<string>? Actions { get; init; }
    /// <summary>Martlet notices touches on this zone (was "Tell the character", saved as <c>tell</c> before).</summary>
    public bool Notices { get; init; }
    // Settings saved before Martlet notices replaced Tell the character keep working.
    [JsonInclude, JsonPropertyName("tell")]
    private bool? Tell
    {
        get => null;
        init { if (value == true) Notices = true; }
    }
    public string? Narration { get; init; }
    public double CooldownSeconds { get; init; } = DefaultCooldown;
}

/// <summary>One zone found on a model: its box in the snapshot (fractions of the picture), the Live2D drawables or VRM bones that
/// lie in it (so it follows the model as it moves), whether it is used, and its reaction.</summary>
public sealed record CharacterTouchZone
{
    public required string Id { get; init; }
    public string? Label { get; init; }
    public required TouchZoneBox Box { get; init; }
    public IReadOnlyList<string> Drawables { get; init; } = [];
    public IReadOnlyList<string> Bones { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public CharacterTouchReaction Reaction { get; init; } = new();

    [JsonIgnore] public string Name => Label ?? CharacterTouchZones.Kind(Id)?.Label ?? Id;
}

/// <summary>One model's touch zones (Companion › Character › Touch zones). <see cref="Crop"/> is where the snapshot the zones were
/// found in sat on the renderer page (fractions of the page), so a zone's box can be compared with a touch.</summary>
public sealed record CharacterTouchZoneSettings
{
    public const string ByVision = "vision", ByOwner = "owner";
    public required string ModelId { get; init; }
    public string? DetectedBy { get; init; }
    public DateTimeOffset? DetectedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IncludeIntimate { get; init; } = true;
    public TouchZoneBox? Crop { get; init; }
    /// <summary>The snapshot framed the character whole (no zoom, no pan): boxes compare with where a touch lands in that
    /// framing (<see cref="CharacterTouch.WholeX"/>), whatever the view is now.</summary>
    public bool Whole { get; init; }
    public IReadOnlyList<CharacterTouchZone> Zones { get; init; } = [];

    /// <summary>Whether touching <paramref name="zone"/> does anything: it is on, and an intimate zone only with Include intimate zones.</summary>
    public bool Active(CharacterTouchZone zone) => zone.Enabled && (IncludeIntimate || CharacterTouchZones.Kind(zone.Id)?.Intimate != true);
}

/// <summary>A Live2D drawable's bounds on the renderer page (fractions of the page), from the renderer's zones probe.</summary>
public sealed record RendererDrawableBox(string Id, double Left, double Top, double Right, double Bottom);
/// <summary>A VRM humanoid bone's place on the renderer page (fractions of the page).</summary>
public sealed record RendererBonePoint(string Bone, double X, double Y);
/// <summary>The zones probe's reply: where the showing model's drawables (Live2D) or humanoid bones (VRM) are now.</summary>
public sealed record RendererZoneProbe(RendererDrawableBox[]? Drawables = null, RendererBonePoint[]? Bones = null);

/// <summary>Which zone a touch landed in and how it was found ("drawable", "bone", "hair", "box" or "coarse").</summary>
public sealed record TouchZoneMatch(CharacterTouchZone Zone, string How);

/// <summary>Touch zones: the body zones Martlet knows, the vision model's request and how its answer is read, binding zones to a
/// model's drawables or bones, matching a touch to a zone, the reaction it plays, and the per-model settings kept in
/// character-touch-zones.json (snapshots in character-touch-zones\) on this PC.</summary>
public static class CharacterTouchZones
{
    public const string FileName = "character-touch-zones.json";
    public const string SnapshotFolder = "character-touch-zones";
    public const int MaximumModels = 32, MaximumZones = 64, MaximumBytes = 4 * 1024 * 1024, MaximumActions = 3;
    public const int MaximumNarrationLength = 160, MaximumLabelLength = 40;
    // A drawable belongs to a zone when this much of its bounds lies inside the zone's box.
    public const double MostlyInside = 0.6;

    private static readonly string[][] HeadPat = [["lean_in", "tilt"], ["smile", "happy"]];
    private static readonly string[][] Face = [["tilt", "nod"], ["smile", "happy"]];
    private static readonly string[][] Cheek = [["blush"], ["shy", "smile"]];
    private static readonly string[][] Intimate = [["blush"], ["flinch", "surprise", "gasp"]];
    private static readonly string[][] Tickle = [["flinch", "surprise"], ["giggle", "laugh", "chuckle"]];
    private static readonly string[][] Arm = [["tilt", "nod"], ["smile", "happy"]];
    private static readonly string[][] Hand = [["smile", "happy"], ["nod"]];
    private static readonly string[][] Leg = [["surprise", "flinch"], ["pout", "tilt"]];
    private static readonly string[][] Extra = [["tilt"], ["smile", "happy"]];
    private static readonly string[][] Startle = [["surprise", "flinch"], ["blush"]];

    private static TouchZoneKind Z(string id, string label, TouchZoneGroup group, string narration, string[][] defaults, bool intimate = false) =>
        new(id, label, group, intimate, narration, defaults);

    /// <summary>Every zone, in the order the vision model is asked for them. Left and right are the character's own.</summary>
    public static readonly IReadOnlyList<TouchZoneKind> Kinds =
    [
        Z("top_of_head", "Top of head", TouchZoneGroup.Head, "*gently pats your head*", HeadPat),
        Z("hair", "Hair", TouchZoneGroup.Head, "*runs fingers through your hair*", HeadPat),
        Z("forehead", "Forehead", TouchZoneGroup.Head, "*pokes your forehead*", Face),
        Z("face", "Face and eyes", TouchZoneGroup.Head, "*touches your face*", Face),
        Z("cheek_left", "Left cheek", TouchZoneGroup.Head, "*pokes your cheek*", Cheek),
        Z("cheek_right", "Right cheek", TouchZoneGroup.Head, "*pokes your cheek*", Cheek),
        Z("nose", "Nose", TouchZoneGroup.Head, "*boops your nose*", Face),
        Z("lips", "Lips", TouchZoneGroup.Head, "*touches your lips*", Intimate, true),
        Z("chin", "Chin", TouchZoneGroup.Head, "*tilts your chin up*", Face),
        Z("ear_left", "Left ear", TouchZoneGroup.Head, "*touches your ear*", Intimate, true),
        Z("ear_right", "Right ear", TouchZoneGroup.Head, "*touches your ear*", Intimate, true),
        Z("neck", "Neck", TouchZoneGroup.Torso, "*touches your neck*", Intimate, true),
        Z("shoulder_left", "Left shoulder", TouchZoneGroup.Torso, "*taps your shoulder*", Arm),
        Z("shoulder_right", "Right shoulder", TouchZoneGroup.Torso, "*taps your shoulder*", Arm),
        Z("collarbone", "Collarbone", TouchZoneGroup.Torso, "*touches your collarbone*", Startle),
        Z("chest", "Chest", TouchZoneGroup.Torso, "*touches your chest*", Intimate, true),
        Z("breast_left", "Left breast", TouchZoneGroup.Torso, "*touches your chest*", Intimate, true),
        Z("breast_right", "Right breast", TouchZoneGroup.Torso, "*touches your chest*", Intimate, true),
        Z("stomach", "Stomach", TouchZoneGroup.Torso, "*pokes your tummy*", Tickle),
        Z("navel", "Navel", TouchZoneGroup.Torso, "*pokes your belly button*", Tickle),
        Z("waist", "Waist and sides", TouchZoneGroup.Torso, "*tickles your sides*", Intimate, true),
        Z("lower_back", "Lower back", TouchZoneGroup.Torso, "*touches your lower back*", Tickle),
        Z("upper_arm_left", "Left upper arm", TouchZoneGroup.Arms, "*taps your arm*", Arm),
        Z("upper_arm_right", "Right upper arm", TouchZoneGroup.Arms, "*taps your arm*", Arm),
        Z("forearm_left", "Left forearm", TouchZoneGroup.Arms, "*touches your arm*", Arm),
        Z("forearm_right", "Right forearm", TouchZoneGroup.Arms, "*touches your arm*", Arm),
        Z("hand_left", "Left hand", TouchZoneGroup.Arms, "*holds your hand*", Hand),
        Z("hand_right", "Right hand", TouchZoneGroup.Arms, "*holds your hand*", Hand),
        Z("hips", "Hips", TouchZoneGroup.LowerBody, "*touches your hips*", Intimate, true),
        Z("groin", "Groin", TouchZoneGroup.LowerBody, "*touches you between the legs*", Intimate, true),
        Z("buttocks", "Buttocks", TouchZoneGroup.LowerBody, "*pats your bottom*", Intimate, true),
        Z("thigh_left", "Left thigh", TouchZoneGroup.LowerBody, "*touches your thigh*", Leg),
        Z("thigh_right", "Right thigh", TouchZoneGroup.LowerBody, "*touches your thigh*", Leg),
        Z("inner_thigh_left", "Left inner thigh", TouchZoneGroup.LowerBody, "*touches your inner thigh*", Intimate, true),
        Z("inner_thigh_right", "Right inner thigh", TouchZoneGroup.LowerBody, "*touches your inner thigh*", Intimate, true),
        Z("knee_left", "Left knee", TouchZoneGroup.LowerBody, "*taps your knee*", Leg),
        Z("knee_right", "Right knee", TouchZoneGroup.LowerBody, "*taps your knee*", Leg),
        Z("calf_left", "Left calf", TouchZoneGroup.LowerBody, "*touches your leg*", Leg),
        Z("calf_right", "Right calf", TouchZoneGroup.LowerBody, "*touches your leg*", Leg),
        Z("foot_left", "Left foot", TouchZoneGroup.LowerBody, "*tickles your foot*", Tickle),
        Z("foot_right", "Right foot", TouchZoneGroup.LowerBody, "*tickles your foot*", Tickle),
        Z("animal_ears", "Animal ears", TouchZoneGroup.Extras, "*strokes your ears*", HeadPat),
        Z("tail", "Tail", TouchZoneGroup.Extras, "*strokes your tail*", Startle),
        Z("horns", "Horns", TouchZoneGroup.Extras, "*touches your horns*", Extra),
        Z("wings", "Wings", TouchZoneGroup.Extras, "*strokes your wings*", Startle),
        Z("glasses_or_hat", "Glasses or hat", TouchZoneGroup.Extras, "*adjusts your glasses*", Extra),
        Z("skirt_hem", "Skirt hem", TouchZoneGroup.Extras, "*tugs at your skirt*", Intimate),
        Z("held_item", "Held item", TouchZoneGroup.Extras, "*touches what you're holding*", Extra)
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["head"] = "top_of_head", ["top_head"] = "top_of_head", ["crown"] = "top_of_head", ["eyes"] = "face", ["face_eyes"] = "face",
        ["mouth"] = "lips", ["lip"] = "lips", ["sides"] = "waist", ["waist_sides"] = "waist", ["belly"] = "stomach",
        ["butt"] = "buttocks", ["crotch"] = "groin", ["pelvis"] = "groin", ["between_legs"] = "groin", ["genitals"] = "groin",
        ["genital_area"] = "groin", ["pubic_area"] = "groin", ["glasses"] = "glasses_or_hat", ["hat"] = "glasses_or_hat", ["skirt"] = "skirt_hem",
        ["left_cheek"] = "cheek_left", ["right_cheek"] = "cheek_right", ["left_ear"] = "ear_left", ["right_ear"] = "ear_right",
        ["left_shoulder"] = "shoulder_left", ["right_shoulder"] = "shoulder_right", ["left_hand"] = "hand_left",
        ["right_hand"] = "hand_right", ["left_foot"] = "foot_left", ["right_foot"] = "foot_right", ["left_knee"] = "knee_left",
        ["right_knee"] = "knee_right", ["left_thigh"] = "thigh_left", ["right_thigh"] = "thigh_right", ["left_breast"] = "breast_left",
        ["right_breast"] = "breast_right", ["left_upper_arm"] = "upper_arm_left", ["right_upper_arm"] = "upper_arm_right",
        ["left_forearm"] = "forearm_left", ["right_forearm"] = "forearm_right", ["left_calf"] = "calf_left", ["right_calf"] = "calf_right",
        ["left_inner_thigh"] = "inner_thigh_left", ["right_inner_thigh"] = "inner_thigh_right"
    };

    // A's rough zones, when no found zone matched: the found zones to try, else the first one's default reaction.
    private static readonly Dictionary<string, string[]> Coarse = new(StringComparer.Ordinal)
    {
        ["head"] = ["top_of_head", "hair", "forehead"], ["hair"] = ["hair", "top_of_head"], ["face"] = ["face", "forehead", "nose"],
        ["body"] = ["stomach", "navel", "lower_back"], ["arm"] = ["upper_arm_right", "upper_arm_left", "forearm_right", "forearm_left"],
        ["hand"] = ["hand_right", "hand_left"], ["leg"] = ["thigh_right", "thigh_left", "knee_right", "knee_left", "calf_right", "calf_left"],
        ["foot"] = ["foot_right", "foot_left"]
    };

    public static TouchZoneKind? Kind(string id) => Kinds.FirstOrDefault(k => k.Id == id);

    /// <summary>A zone ID as Martlet knows it (lower case, underscores, common names mapped), or null when it isn't one.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var slug = string.Join("_", name.Trim().ToLowerInvariant().Split([' ', '-', '/', '.', ',', '&'], StringSplitOptions.RemoveEmptyEntries));
        slug = slug.Replace("_and_", "_").Replace("_or_", "_or_");
        if (Kind(slug) is not null) return slug;
        if (Aliases.TryGetValue(slug, out var alias)) return alias;
        return slug == "glasses_hat" ? "glasses_or_hat" : null;
    }

    // ---------- the vision model's answer ----------

    /// <summary>The zones in a vision model's answer about a whole <paramref name="width"/> by <paramref name="height"/> picture
    /// (the fixture's stand-in answer and MCP's simulated one), with their boxes as fractions of the picture: JSON with boxes as
    /// arrays or named edges, a bare list, boxes keyed by zone, or Qwen-style <c>bbox_2d</c> grounding in pixels or 0..1000; an
    /// answer cut off part way keeps the zones it finished. Unknown zones and boxes that can't be placed are left out; null when
    /// nothing could be read. Detection itself asks step by step (<see cref="TouchZoneDetection"/>).</summary>
    public static IReadOnlyList<CharacterTouchZone>? Parse(string? answer, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var zones = TouchZoneDetection.ReadBoxes(answer, width, height, Normalize).Take(MaximumZones)
            .Select(z => new CharacterTouchZone { Id = z.Key, Box = z.Value.Clamped(), Enabled = true }).ToArray();
        return zones.Length == 0 ? null : zones;
    }

    // ---------- binding zones to the model ----------

    /// <summary>The zones with the drawables (Live2D) and humanoid bones (VRM) that lie in each: a drawable whose bounds are
    /// mostly (<see cref="MostlyInside"/>) inside a zone's box, a bone whose point is. <paramref name="crop"/> is where the
    /// snapshot sat on the page (null: the boxes are already fractions of the page).</summary>
    public static IReadOnlyList<CharacterTouchZone> Bind(IReadOnlyList<CharacterTouchZone> zones, TouchZoneBox? crop, RendererZoneProbe? probe)
    {
        if (probe is null) return zones;
        return zones.Select(zone =>
        {
            var page = crop is null ? zone.Box : zone.Box.Within(crop);
            var drawables = (probe.Drawables ?? []).Where(d => d.Right > d.Left && d.Bottom > d.Top &&
                    page.Covers(new(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top)) >= MostlyInside)
                .Select(d => d.Id).Distinct(StringComparer.Ordinal).Take(256).ToArray();
            var bones = (probe.Bones ?? []).Where(b => page.Contains(b.X, b.Y)).Select(b => b.Bone).Distinct(StringComparer.Ordinal).Take(32).ToArray();
            return zone with { Drawables = drawables, Bones = bones };
        }).ToArray();
    }

    /// <summary>Newly found zones merged with the saved ones: a zone found again keeps the owner's label, choice and reaction;
    /// one not found again is dropped. <paramref name="whole"/>: the snapshot framed the character whole.</summary>
    public static CharacterTouchZoneSettings Detected(CharacterTouchZoneSettings? saved, string modelId, IReadOnlyList<CharacterTouchZone> found,
        TouchZoneBox? crop, RendererZoneProbe? probe, DateTimeOffset now, bool whole = false)
    {
        var bound = Bind(found, crop, probe);
        var merged = bound.Select(zone => saved?.Zones.FirstOrDefault(z => z.Id == zone.Id) is { } old
            ? zone with { Label = old.Label, Enabled = old.Enabled, Reaction = old.Reaction } : zone).ToList();
        return new()
        {
            ModelId = modelId, DetectedBy = CharacterTouchZoneSettings.ByVision, DetectedAt = now.ToUniversalTime(), UpdatedAt = now.ToUniversalTime(),
            IncludeIntimate = saved?.IncludeIntimate ?? true, Crop = crop, Whole = whole, Zones = merged
        };
    }

    // ---------- matching a touch ----------

    /// <summary>The zone a touch landed in: the topmost touched drawable or the touched bone that belongs to a zone in use (with
    /// several, the smallest whose box holds the point), hair, then the smallest box in use that holds the point, then the
    /// touch's rough zone (a found zone of that kind, else that kind's default zone). Null when nothing fits (a touch only on
    /// zones that aren't in use).</summary>
    public static TouchZoneMatch? Match(CharacterTouchZoneSettings? settings, CharacterTouch touch)
    {
        var active = settings?.Zones.Where(settings.Active).ToArray() ?? [];
        TouchZoneBox Page(CharacterTouchZone zone) => settings?.Crop is { } crop ? zone.Box.Within(crop) : zone.Box;
        // Zones found with the character framed whole compare with where the touch lands in that framing.
        var (x, y) = settings is { Whole: true } && touch is { WholeX: { } wholeX, WholeY: { } wholeY } ? (wholeX, wholeY) : (touch.X, touch.Y);
        CharacterTouchZone Best(IEnumerable<CharacterTouchZone> zones)
        {
            var list = zones.ToArray();
            return list.Where(z => Page(z).Contains(x, y)).OrderBy(z => z.Box.Area).FirstOrDefault() ??
                list.OrderBy(z => z.Box.Area).First();
        }
        foreach (var drawable in touch.Drawables)
        {
            var owners = active.Where(z => z.Drawables.Contains(drawable, StringComparer.Ordinal)).ToArray();
            if (owners.Length > 0) return new(Best(owners), "drawable");
        }
        if (touch.Hair && active.FirstOrDefault(z => z.Id == "hair") is { } hair) return new(hair, "hair");
        if (touch.Bone is { } bone)
        {
            var owners = active.Where(z => z.Bones.Contains(bone, StringComparer.Ordinal)).ToArray();
            if (owners.Length > 0) return new(Best(owners), "bone");
        }
        var boxed = active.Where(z => Page(z).Contains(x, y)).OrderBy(z => z.Box.Area).FirstOrDefault();
        if (boxed is not null) return new(boxed, "box");
        if (!Coarse.TryGetValue(touch.CoarseZone, out var candidates)) return null;
        foreach (var id in candidates)
            if (active.FirstOrDefault(z => z.Id == id) is { } found) return new(found, "coarse");
        // Not found on this model (or not detected yet): the rough zone's default, unless the owner turned that zone off.
        var fallback = candidates[0];
        if (settings?.Zones.FirstOrDefault(z => z.Id == fallback) is { } off && !settings.Active(off)) return null;
        return new(new() { Id = fallback, Box = new(0, 0, 1, 1) }, "coarse");
    }

    // ---------- reactions ----------

    /// <summary>What touching <paramref name="zone"/> plays on the model: the owner's choice, or for each default slot the first of
    /// its names the model has (its own expressions and motions before Martlet's gestures). Only emotes and motions in use.</summary>
    public static IReadOnlyList<CharacterActionSource> Plan(CharacterTouchZone zone, CharacterActionCatalog? catalog) => React(zone, catalog, null, 0).Actions;

    /// <summary>What a touch on <paramref name="zone"/> plays, in this order of precedence: the owner's own pick for the zone, the
    /// persona's <paramref name="temperament"/> for the zone kind or its group (escalated after <paramref name="repeats"/> touches
    /// in a row), else the zone's built-in default reaction. How long the eyes then turn to the mouse pointer always comes from
    /// the temperament.</summary>
    public static TouchReactionPlan React(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament, int repeats)
    {
        var attitude = CharacterTouchTemperaments.Attitude(temperament, zone.Id);
        var entry = CharacterTouchTemperaments.Entry(temperament, zone.Id);
        var look = entry?.LookSeconds ?? 0;
        if (zone.Reaction.Actions is { } chosen)
        {
            var entries = catalog?.Entries.Where(e => e.Action.Enabled).ToArray() ?? [];
            return new(chosen.Select(id => entries.FirstOrDefault(e => e.Source.Id == id).Source).OfType<CharacterActionSource>().Take(MaximumActions).ToArray(),
                0, attitude, TouchReactionPlan.FromOwner, LookSeconds: look);
        }
        if (temperament is not null && entry is not null)
        {
            var (words, escalated) = CharacterTouchTemperaments.Words(temperament, entry, repeats);
            return new(CharacterTouchTemperaments.Resolve(words, catalog), entry.LingerSeconds, attitude, TouchReactionPlan.FromTemperament, escalated, look);
        }
        return new(DefaultPlan(zone, catalog));
    }

    private static IReadOnlyList<CharacterActionSource> DefaultPlan(CharacterTouchZone zone, CharacterActionCatalog? catalog)
    {
        if (catalog is null) return [];
        var entries = catalog.Entries.Where(e => e.Action.Enabled).ToArray();
        var plan = new List<CharacterActionSource>();
        foreach (var slot in Kind(zone.Id)?.Defaults ?? [])
            foreach (var word in slot)
            {
                var hit = entries.Where(e => Named(e, word)).OrderBy(e => e.Source.Kind == CharacterActionKind.Gesture ? 1 : 0)
                    .Select(e => e.Source).FirstOrDefault(s => !plan.Contains(s));
                if (hit is null) continue;
                plan.Add(hit);
                break;
            }
        return plan;
    }

    private static bool Named((CharacterActionSource Source, CharacterAction Action) entry, string word) =>
        string.Equals(entry.Action.Tag, word, StringComparison.OrdinalIgnoreCase) ||
        (entry.Source.Kind == CharacterActionKind.Gesture && string.Equals(entry.Source.Name, word, StringComparison.OrdinalIgnoreCase)) ||
        CharacterActions.Slug(entry.Source.Name) == word;

    /// <summary>The owner's own words for a touch on a zone Martlet notices (a hint that goes with it), or null: none, the zone's
    /// built-in line, or Martlet doesn't notice the zone.</summary>
    public static string? Narration(CharacterTouchZone zone) => zone.Reaction.Notices && zone.Reaction.Narration is { Length: > 0 } own &&
        own != Kind(zone.Id)?.Narration ? own : null;

    /// <summary>Whether a quick tap on <paramref name="zone"/> is a pat (the top of the head, the hair, animal ears) rather than a poke.</summary>
    public static bool Pats(CharacterTouchZone zone) => zone.Id is "top_of_head" or "hair" or "animal_ears";

    /// <summary>Where <paramref name="zone"/> is, as the character hears it ("the top of your head", "your left cheek").</summary>
    public static string Part(CharacterTouchZone zone) => zone.Label is null ? zone.Id switch
    {
        "top_of_head" => "the top of your head",
        "face" => "your face",
        "waist" => "your sides",
        "held_item" => "what you're holding",
        "skirt_hem" => "the hem of your skirt",
        _ => "your " + zone.Name.ToLowerInvariant()
    } : "your " + zone.Name.ToLowerInvariant();

    // ---------- storage ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 10
    };

    private sealed record Document(int Version, IReadOnlyList<CharacterTouchZoneSettings> Models);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>Where the snapshot the model's zones were found in is kept (a PNG).</summary>
    public static string SnapshotPath(string dataDirectory, string modelId) =>
        System.IO.Path.Combine(dataDirectory, SnapshotFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)))[..24].ToLowerInvariant() + ".png");

    public static CharacterTouchZoneSettings? Load(string dataDirectory, string modelId) =>
        LoadAll(dataDirectory).FirstOrDefault(m => m.ModelId == modelId);

    public static IReadOnlyList<CharacterTouchZoneSettings> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            return document is { Version: 1, Models: { } models }
                ? models.Where(m => m is { ModelId: not null, Zones: not null } && m.Zones.All(z => z is { Id: not null, Box: not null })).ToArray() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    /// <summary>Why <paramref name="settings"/> can't be saved, or null.</summary>
    public static string? Problem(CharacterTouchZoneSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelId)) return "The model is unknown.";
        if (settings.Zones.Count > MaximumZones) return $"A model can have at most {MaximumZones} zones.";
        foreach (var zone in settings.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id) || zone.Id.Length > 40) return "A zone needs a short ID.";
            if (!zone.Box.Valid) return $"{zone.Name}'s box must lie inside the picture.";
            if (zone.Label is { Length: > MaximumLabelLength }) return $"A zone's name can be at most {MaximumLabelLength} characters.";
            if (zone.Reaction.Narration is { Length: > MaximumNarrationLength }) return $"What a touch tells the character can be at most {MaximumNarrationLength} characters.";
            if (zone.Reaction.Actions is { Count: > MaximumActions }) return $"A zone plays at most {MaximumActions} emotes or gestures.";
            if (!double.IsFinite(zone.Reaction.CooldownSeconds) || zone.Reaction.CooldownSeconds is < 0 or > CharacterTouchReaction.MaximumCooldown)
                return $"A zone's rest must be 0 to {CharacterTouchReaction.MaximumCooldown:0} seconds.";
        }
        return settings.Zones.Select(z => z.Id).Distinct(StringComparer.Ordinal).Count() == settings.Zones.Count ? null : "Two zones have the same ID.";
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CharacterTouchZoneSettings> SaveAsync(string dataDirectory, CharacterTouchZoneSettings settings, DateTimeOffset now,
        CancellationToken token = default)
    {
        if (Problem(settings) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        settings = settings with { UpdatedAt = now.ToUniversalTime() };
        await Gate.WaitAsync(token);
        try
        {
            var models = LoadAll(dataDirectory).Where(m => m.ModelId != settings.ModelId).Append(settings)
                .OrderByDescending(m => m.UpdatedAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, models), Json);
            ContractRules.Require(bytes.Length <= MaximumBytes, "The touch zones are too large.", ErrorCode.PayloadTooLarge);
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-touch-zones.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return settings;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Where the pictures the vision model saw in the model's last detection are kept, with <see cref="SentFile"/>.</summary>
    public static string SentFolder(string dataDirectory, string modelId) => System.IO.Path.ChangeExtension(SnapshotPath(dataDirectory, modelId), null) + "-sent";

    public const string SentFile = "sent.json";

    /// <summary>What the model's last detection sent, or null before one ran (or when it can't be read).</summary>
    public static TouchZoneSent? LoadSent(string dataDirectory, string modelId)
    {
        try
        {
            var path = System.IO.Path.Combine(SentFolder(dataDirectory, modelId), SentFile);
            return File.Exists(path) && new FileInfo(path).Length <= MaximumBytes ? JsonSerializer.Deserialize<TouchZoneSent>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Clears the model's sent folder before a new detection, so it holds only what that one sends.</summary>
    public static string ClearSent(string dataDirectory, string modelId)
    {
        var folder = SentFolder(dataDirectory, modelId);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static async Task SaveSentAsync(string dataDirectory, string modelId, TouchZoneSent sent, CancellationToken token = default)
    {
        var folder = SentFolder(dataDirectory, modelId);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, SentFile), JsonSerializer.SerializeToUtf8Bytes(sent, Json), token);
    }
    public static async Task SaveSnapshotAsync(string dataDirectory, string modelId, byte[] png, CancellationToken token = default)
    {
        var path = SnapshotPath(dataDirectory, modelId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png, token);
    }
}

/// <summary>One picture the vision model was sent while finding zones: its file in the sent folder, the step, what it asked
/// (Parts, Zones or Check), its size in pixels and bytes, and its media type.</summary>
public sealed record TouchZoneSentPicture(string File, string Step, string Kind, int Width, int Height, int Bytes, string MediaType);

/// <summary>What a detection sent to the vision model: when, whether a FIXTURE stood in for it, how many requests it made, each
/// picture, one line per step and whether it saved zones (so its pictures belong to the snapshot under the boxes)
/// (character-touch-zones\&lt;model&gt;-sent\sent.json).</summary>
public sealed record TouchZoneSent(DateTimeOffset At, bool Fixture, int Requests, IReadOnlyList<TouchZoneSentPicture> Pictures, IReadOnlyList<string> Steps,
    bool Saved = false)
{
    /// <summary>One plain line: how many pictures, how large, and what they showed.</summary>
    public string Describe()
    {
        var largest = Pictures.Count == 0 ? 0 : Pictures.Max(p => Math.Max(p.Width, p.Height));
        var closeUps = Pictures.Where(p => p.Kind == nameof(ZoneAskKind.Zones) && p.Step != TouchZoneDetection.MissingStep)
            .Select(p => p.Step.Replace('_', ' ')).Distinct().ToArray();
        var checks = Pictures.Count(p => p.Kind == nameof(ZoneAskKind.Check));
        var again = Pictures.Any(p => p.Step == TouchZoneDetection.MissingStep);
        var parts = closeUps.Length switch
        {
            0 => "",
            1 => closeUps[0],
            _ => string.Join(", ", closeUps[..^1]) + " and " + closeUps[^1]
        };
        return (Fixture ? "FIXTURE - NOT AI answered these. " : "") +
            $"Thinking saw {Pictures.Count} picture{(Pictures.Count == 1 ? "" : "s")} on {At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}, " +
            $"up to {largest} pixels on the longer side, each on a plain backdrop with a grid of tenths" +
            (parts.Length > 0 ? $": the whole character, then close-ups of the {parts}" : "") +
            (checks > 0 ? $", with its boxes drawn and numbered for {checks} check{(checks == 1 ? "" : "s")}" : "") +
            (again ? ", and the whole character again for the zones the close-ups missed" : "") + ".";
    }
}
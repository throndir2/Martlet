using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>Touch zones: the body zones Martlet knows, the vision model's request and how its answer is read, binding zones to a
/// model's drawables or bones, matching a touch to a zone, the reaction it plays, and the per-model settings kept in
/// character-touch-zones.json (the same on all the owner's computers: <see cref="SharedKey"/>; snapshots in character-touch-zones\
/// on the computer that made them).</summary>
public static partial class CharacterTouchZones
{
    public const string FileName = "character-touch-zones.json";
    public const string SnapshotFolder = "character-touch-zones";
    /// <summary><see cref="MaximumActions"/>: at most this many entries in a zone's reaction list. Zones are kept for the newest
    /// <see cref="MaximumModels"/> models (as many characters as the shared character list holds). <see cref="MaximumBytes"/>
    /// bounds one model's sent and probe files.</summary>
    public const int MaximumModels = Martlet.Core.Characters.CharacterModelLibrary.MaximumModels, MaximumZones = 64, MaximumBytes = 4 * 1024 * 1024,
        MaximumActions = 8;
    /// <summary>character-touch-zones.json is read whole, so it stays bounded: 128 KiB for each of <see cref="MaximumModels"/>
    /// models, the room each of the 32 models it held before had. A model with 32 zones, each with 16 drawables and two areas,
    /// takes about 107 KB.</summary>
    public const int MaximumListBytes = 128 * 1024 * 1024;
    /// <summary>At most this many areas make up one zone.</summary>
    public const int MaximumAreas = 8;
    public const int MaximumNarrationLength = 160, MaximumLabelLength = 40;
    // A drawable belongs to a zone when this much of its bounds lies inside the zone's box; a zone frames a smaller one when this
    // much of the smaller box lies inside its own.
    public const double MostlyInside = 0.6;
    /// <summary>Every intimate zone kind in plain words, left and right together (Include intimate zones and the touch
    /// temperament's Intimate parts name them so).</summary>
    public const string IntimateParts = "mouth, ears, neck, chest and breasts, waist and sides, hips, groin, buttocks and inner thighs";

    private static readonly string[][] HeadPat = [["lean_in", "tilt"], ["smile", "happy"]];
    private static readonly string[][] Face = [["tilt", "nod"], ["smile", "happy"]];
    private static readonly string[][] Eye = [["flinch", "surprise"], ["pout", "tilt"]];
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

    /// <summary>Every zone Martlet knows, in the order the vision model is asked for them. Detect zones looks for
    /// <see cref="TouchZoneDetection.Defaults"/>, the ones the owner added and what is special about the character (which can also
    /// be a zone of its own that isn't here, named as the vision model sees it); Add zone offers the rest. Left and right are the
    /// character's own.</summary>
    public static readonly IReadOnlyList<TouchZoneKind> Kinds =
    [
        Z("top_of_head", "Top of head", TouchZoneGroup.Head, "*gently pats your head*", HeadPat),
        Z("hair", "Hair", TouchZoneGroup.Head, "*runs fingers through your hair*", HeadPat),
        Z("forehead", "Forehead", TouchZoneGroup.Head, "*pokes your forehead*", Face),
        Z("face", "Face", TouchZoneGroup.Head, "*touches your face*", Face),
        Z("eye_left", "Left eye", TouchZoneGroup.Head, "*touches near your eye*", Eye),
        Z("eye_right", "Right eye", TouchZoneGroup.Head, "*touches near your eye*", Eye),
        Z("cheek_left", "Left cheek", TouchZoneGroup.Head, "*pokes your cheek*", Cheek),
        Z("cheek_right", "Right cheek", TouchZoneGroup.Head, "*pokes your cheek*", Cheek),
        Z("nose", "Nose", TouchZoneGroup.Head, "*boops your nose*", Face),
        Z("lips", "Mouth", TouchZoneGroup.Head, "*touches your lips*", Intimate, true),
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
        Z("hip_left", "Left hip", TouchZoneGroup.LowerBody, "*touches your hip*", Intimate, true),
        Z("hip_right", "Right hip", TouchZoneGroup.LowerBody, "*touches your hip*", Intimate, true),
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
        ["left_inner_thigh"] = "inner_thigh_left", ["right_inner_thigh"] = "inner_thigh_right", ["left_eye"] = "eye_left",
        ["right_eye"] = "eye_right", ["left_hip"] = "hip_left", ["right_hip"] = "hip_right",
        // Plain names for the parts of a limb: the lower arm is the forearm, the upper leg the thigh and the lower leg the calf.
        ["left_lower_arm"] = "forearm_left", ["right_lower_arm"] = "forearm_right", ["lower_arm_left"] = "forearm_left",
        ["lower_arm_right"] = "forearm_right", ["left_upper_leg"] = "thigh_left", ["right_upper_leg"] = "thigh_right",
        ["upper_leg_left"] = "thigh_left", ["upper_leg_right"] = "thigh_right", ["left_lower_leg"] = "calf_left",
        ["right_lower_leg"] = "calf_right", ["lower_leg_left"] = "calf_left", ["lower_leg_right"] = "calf_right",
        // What a vision model calls the extras a character can have.
        ["animal_ear"] = "animal_ears", ["cat_ears"] = "animal_ears", ["fox_ears"] = "animal_ears", ["dog_ears"] = "animal_ears",
        ["wolf_ears"] = "animal_ears", ["bunny_ears"] = "animal_ears", ["rabbit_ears"] = "animal_ears", ["kemonomimi"] = "animal_ears",
        ["kemomimi"] = "animal_ears", ["nekomimi"] = "animal_ears", ["cat_ear"] = "animal_ears", ["fox_ear"] = "animal_ears",
        ["dog_ear"] = "animal_ears", ["wolf_ear"] = "animal_ears", ["bunny_ear"] = "animal_ears", ["rabbit_ear"] = "animal_ears",
        ["tails"] = "tail", ["wing"] = "wings", ["horn"] = "horns"
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

    /// <summary>A name as the ID of a zone of its own: lower case, its words (letters and digits in any language) joined with
    /// underscores, at most 32 characters; null when nothing is left.</summary>
    public static string? Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var slug = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '_') slug.Append('_');
        var text = slug.ToString().Trim('_');
        if (text.Length > 32) text = text[..32].TrimEnd('_');
        return text.Length == 0 ? null : text;
    }
}

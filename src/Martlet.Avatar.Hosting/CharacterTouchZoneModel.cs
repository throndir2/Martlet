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

    /// <summary>How far a point is from this box (0 inside it), in the same fractions.</summary>
    public double Distance(double x, double y) =>
        Math.Sqrt(Math.Pow(Math.Max(0, Math.Max(X - x, x - (X + Width))), 2) + Math.Pow(Math.Max(0, Math.Max(Y - y, y - (Y + Height))), 2));

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

/// <summary>What touching a zone does: its reaction list (<see cref="Actions"/>), whether Martlet notices it (<see cref="Notices"/>,
/// on by default: the touch goes to the Thinking model, with what the user says or as a short reply of its own,
/// <see cref="Narration"/> an optional hint in the owner's words) and how long the zone then rests.</summary>
public sealed record CharacterTouchReaction
{
    public const double DefaultCooldown = 4, MaximumCooldown = 600;
    /// <summary>A reaction list entry for one of the active voice's sounds: this prefix and the sound's cue
    /// (<see cref="Martlet.Core.Settings.VoiceTag.Cue"/>), such as <c>sound:laugh</c>.</summary>
    public const string SoundPrefix = "sound:";
    /// <summary>At most this many characters in one reaction list entry.</summary>
    public const int MaximumEntryLength = 160;
    /// <summary>The zone's reaction list: what plays on its own, in this order, each time the zone is touched. Each entry is an
    /// emote, gesture or motion of the model (its <see cref="CharacterActionSource.Id"/>) or a voice sound
    /// (<see cref="SoundPrefix"/> and its cue). An entry the model doesn't have is kept and skipped when it plays. An empty list
    /// plays nothing. Null: not filled yet (a zone saved before reaction lists, or one just found): it plays and gets its
    /// <see cref="CharacterTouchZones.DefaultReactions"/> as soon as the model's emotes are known
    /// (<see cref="CharacterTouchZones.Filled"/>).</summary>
    public IReadOnlyList<string>? Actions { get; init; }
    /// <summary>Whether a reaction list entry is a voice sound (<see cref="SoundPrefix"/>).</summary>
    public static bool IsSound(string entry) => entry.StartsWith(SoundPrefix, StringComparison.Ordinal);
    /// <summary>Martlet notices touches on this zone (on unless the owner turns it off; was "Tell the character", saved as
    /// <c>tell</c> before).</summary>
    public bool Notices { get; init; } = true;
    // Settings saved before Martlet notices replaced Tell the character keep working.
    [JsonInclude, JsonPropertyName("tell")]
    private bool? Tell
    {
        get => null;
        init { if (value is { } tell) Notices = tell; }
    }
    public string? Narration { get; init; }
    public double CooldownSeconds { get; init; } = DefaultCooldown;
    // An early, unreleased version saved a second list (autoplay) that played after the reaction. It is read here and joined to
    // the end of the reaction list (CharacterTouchZones.List; Filled saves it so), and written only until then. Its step
    // (autoplay_seconds) is ignored: the list plays together, as the reaction did.
    [JsonInclude, JsonPropertyName("autoplay")]
    private IReadOnlyList<string>? SavedAutoplay
    {
        get => Later;
        init => Later = value is { Count: > 0 } ? value : null;
    }
    /// <summary>Entries of the old second list that join the end of <see cref="Actions"/>; null once joined.</summary>
    [JsonIgnore] internal IReadOnlyList<string>? Later { get; init; }
}

/// <summary>One area of a zone: its box in the snapshot (fractions of the picture) and the Live2D drawables, VRM humanoid bones
/// and VRM nodes (spring-bone joints, such as a tail's) that lie in it, so it follows the model as it moves. An area whose
/// parts come from the model itself (<see cref="FromModel"/>: a part the model names for the zone, or a part its physics swings,
/// such as a tail that hangs behind the legs at rest) follows those parts wherever they move; its box only says where they
/// were in the snapshot, so it never matches a touch by itself.</summary>
public sealed record CharacterTouchZoneArea
{
    public required TouchZoneBox Box { get; init; }
    public IReadOnlyList<string> Drawables { get; init; } = [];
    public IReadOnlyList<string> Bones { get; init; } = [];
    public IReadOnlyList<string> Nodes { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FromModel { get; init; }
}

/// <summary>One zone found on a model: its box in the snapshot (fractions of the picture), the Live2D drawables or VRM bones that
/// lie in it (so it follows the model as it moves), whether it is used, its reaction, and whether the owner added it. A zone can
/// have several areas (<see cref="Areas"/>: a tail in segments along its length, or boxes the owner drew); its box then holds them
/// all, and its drawables and bones are all of theirs.</summary>
public sealed record CharacterTouchZone
{
    public required string Id { get; init; }
    public string? Label { get; init; }
    public required TouchZoneBox Box { get; init; }
    public IReadOnlyList<string> Drawables { get; init; } = [];
    public IReadOnlyList<string> Bones { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public CharacterTouchReaction Reaction { get; init; } = new();
    /// <summary>The owner added this zone (Add zone): Detect again looks for it too, beside
    /// <see cref="TouchZoneDetection.Defaults"/>, and keeps it where it is when it can't place it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Added { get; init; }
    /// <summary>The zone's areas when it has several (or one whose parts come from the model itself); null for a zone that is
    /// only its box.</summary>
    public IReadOnlyList<CharacterTouchZoneArea>? Areas { get; init; }
    /// <summary>What of the model the zone's areas follow, by its own names (such as 尾巴, the physics settings that swing it, or
    /// the root joint of a VRM's spring-bone chain), when they come from the model itself; null otherwise.</summary>
    public string? Follows { get; init; }

    [JsonIgnore] public string Name => Label ?? CharacterTouchZones.Kind(Id)?.Label ?? Id;

    /// <summary>Every area of the zone: its <see cref="Areas"/>, or for a zone that is only its box, that box (with the zone's
    /// drawables and bones) as its one area.</summary>
    [JsonIgnore]
    public IReadOnlyList<CharacterTouchZoneArea> AllAreas => Areas is { Count: > 0 } areas ? areas : [new() { Box = Box, Drawables = Drawables, Bones = Bones }];

    /// <summary>Whether a box of the zone holds a point of the page (<paramref name="x"/>, <paramref name="y"/>, fractions;
    /// <paramref name="crop"/> is where the snapshot sat on the page, null when the boxes are fractions of the page). An area whose
    /// parts come from the model itself moves away from its box, so its box never holds a touch.</summary>
    public bool Holds(TouchZoneBox? crop, double x, double y) =>
        AllAreas.Any(a => !a.FromModel && (crop is null ? a.Box : a.Box.Within(crop)).Contains(x, y));
}

/// <summary>One model's touch zones (Companion › Touch › Touch zones). <see cref="Crop"/> is where the snapshot the zones were
/// found in sat on the renderer page (fractions of the page), so a zone's box can be compared with a touch.</summary>
public sealed record CharacterTouchZoneSettings
{
    public const string ByVision = "vision", ByOwner = "owner", ByEstimate = "estimate";
    public required string ModelId { get; init; }
    /// <summary>Who placed the zones: the Thinking model (<see cref="ByVision"/>), Martlet's first guess from the character's
    /// picture with no AI (<see cref="ByEstimate"/>), or the owner (<see cref="ByOwner"/>).</summary>
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

/// <summary>A Live2D drawable's bounds on the renderer page (fractions of the page), from the renderer's zones probe, and the ID of
/// the model's part it belongs to (null when the renderer can't say).</summary>
public sealed record RendererDrawableBox(string Id, double Left, double Top, double Right, double Bottom, string? Part = null);
/// <summary>A VRM humanoid bone's place on the renderer page (fractions of the page).</summary>
public sealed record RendererBonePoint(string Bone, double X, double Y);
/// <summary>A Live2D model's own part (a group of drawables and parts): its ID, the name its DisplayInfo file (*.cdi3.json) gives
/// it, in any language (null when it gives none), and the ID of the part it belongs to (null for a top part).</summary>
public sealed record RendererModelPart(string Id, string? Name = null, string? Parent = null);
/// <summary>A part of a Live2D model that swings on its own (a tail, a ponytail, a skirt, ears), from its physics file: the names
/// of the physics settings that swing it (尾巴, Hair Front...; null when they have none), its drawables from the one that moves
/// least (its root) to the one that moves most (its tip), and where it can reach (its bounds at rest and swung both ways,
/// fractions of the page).</summary>
public sealed record RendererChain(string? Name, string[] Drawables, double Left, double Top, double Right, double Bottom)
{
    public const int MaximumChains = 48, MaximumDrawables = 256;

    /// <summary>A name of bounded length, at least one drawable, bounded lists and finite bounds.</summary>
    [JsonIgnore]
    public bool IsValid => (Name is null || Name.Length <= 256) && Drawables is { Length: > 0 and <= MaximumDrawables } &&
        Drawables.All(id => id is { Length: > 0 and <= 256 }) && double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) &&
        double.IsFinite(Bottom);
}
/// <summary>A chain of a VRM's spring-bone joints (hair, a tail, a skirt, ears): its name (its first joint's node's name) and
/// each joint's node and place on the page (fractions), from its root to its tip.</summary>
public sealed record RendererSpring(string? Name, RendererBonePoint[] Joints)
{
    public const int MaximumSprings = 64, MaximumJoints = 64;

    /// <summary>A name of bounded length and at least one joint with a short name and a finite place.</summary>
    [JsonIgnore]
    public bool IsValid => (Name is null || Name.Length <= 256) && Joints is { Length: > 0 and <= MaximumJoints } &&
        Joints.All(j => j is { Bone.Length: > 0 and <= 256 } && double.IsFinite(j.X) && double.IsFinite(j.Y));
}
/// <summary>The zones probe's reply: where the showing model's drawables (Live2D) or humanoid bones (VRM) are now. A whole
/// picture's probe also says where the face was in it (<see cref="Face"/>, from the renderer's face anchor), which the eye
/// measurement crops around, a Live2D model's own parts (<see cref="Parts"/>), so its part names can find and check zones, and
/// the parts of it that swing on their own (a Live2D model's <see cref="Chains"/>, a VRM's <see cref="Springs"/>), so a zone on
/// a tail follows all of the tail.</summary>
public sealed record RendererZoneProbe(RendererDrawableBox[]? Drawables = null, RendererBonePoint[]? Bones = null, RendererFace? Face = null,
    RendererModelPart[]? Parts = null, RendererChain[]? Chains = null, RendererSpring[]? Springs = null);

/// <summary>Which zone a touch landed in and how it was found ("drawable", "node", "bone", "hair", "box" or "coarse"), whether
/// its boxes were compared with where the touched point was in the rest pose (<see cref="CharacterTouch.RestX"/>, traced on the
/// touched mesh), so the match follows the model as it moves (not for a part that swings on its own, such as a tail, found by its
/// drawable or spring-bone joint wherever it swung), and for a zone with several areas, which of them (its index in
/// <see cref="CharacterTouchZone.AllAreas"/>).</summary>
public sealed record TouchZoneMatch(CharacterTouchZone Zone, string How, bool Traced = false, int? Area = null);

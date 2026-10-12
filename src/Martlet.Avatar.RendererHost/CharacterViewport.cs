using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The character's drag surface. Besides right-click (and the Menu key or Shift+F10), assistive technology and Martlet's MCP
/// open its menu through UI Automation's expand/collapse, move the character through its transform (like a drag), which
/// is refused while its place is locked, tap it through its value ("x,y"), which then reads the last tap's hit test, read
/// where Martlet draws over its face ("face"), take a picture of it as it shows ("picture"), read what its idle body does
/// ("pose"), who moves its mouth ("mouth"), where it looks ("look") and where each area of its touch zones is now ("zones"),
/// move its mouth as Martlet's voice does ("voice:ms;level;...") and play a VRMA clip from a file ("animation:{json}").
/// </summary>
internal sealed class CharacterViewport : Grid
{
    /// <summary>Moves the overlay so this surface's top-left lands on the given screen point (device pixels).</summary>
    internal Action<System.Windows.Point>? MoveTo { get; set; }

    /// <summary>The character's place is locked: it can't be moved (Martlet unlocks it).</summary>
    internal bool PlacementLocked { get; set; }

    /// <summary>Taps the character at a point given as fractions 0..1 of this surface (+y down), like a click there held for the
    /// given milliseconds (0: a quick tap).</summary>
    internal Action<double, double, int>? TouchAt { get; set; }

    /// <summary>The last tap's hit test as JSON (its number, point, whether it hit, the coarse zone, hit areas, drawables, bone,
    /// node, hair, mesh and material), or empty before the first tap. UI Automation reads it as this surface's value.</summary>
    internal string LastTouch { get; set; } = "";

    /// <summary>Strokes the locked character along points (fractions 0..1 of this surface), one every so many milliseconds.</summary>
    internal Action<IReadOnlyList<(double X, double Y)>, int>? StrokeAlong { get; set; }

    /// <summary>The last stroke as JSON (its number, samples, how many hit the character, milliseconds and coarse zones crossed),
    /// or empty. Read as the "stroke" field of this surface's value.</summary>
    internal string LastStroke { get; set; } = "";

    /// <summary>The last settled move, zoom or pan as JSON (kind, dx, dy, monitors, sizes, focus zone), or empty. Read as the
    /// "physical" field of this surface's value.</summary>
    internal string LastPhysical { get; set; } = "";

    /// <summary>Asks the page where Martlet draws over the face now; its answer arrives as <see cref="LastFace"/>.</summary>
    internal Action? ReadFace { get; set; }

    /// <summary>The last reading of the face as JSON (see CharacterFaceReading), or empty. Read as the "face" field of this
    /// surface's value.</summary>
    internal string LastFace { get; set; } = "";

    /// <summary>Takes a picture of the character as it shows now, Martlet's drawings over its face included; its result arrives
    /// as <see cref="LastPicture"/>.</summary>
    internal Action? TakePicture { get; set; }

    /// <summary>The last picture as JSON (its number, the PNG file, its size and the crop of the overlay it shows), or empty.
    /// Read as the "picture" field of this surface's value.</summary>
    internal string LastPicture { get; set; } = "";

    /// <summary>Asks the page what the idle body does now; its answer arrives as <see cref="LastPose"/>.</summary>
    internal Action? ReadPose { get; set; }

    /// <summary>The last reading of the idle body as JSON (see CharacterPoseReading), or empty. Read as the "pose" field of this
    /// surface's value.</summary>
    internal string LastPose { get; set; } = "";

    /// <summary>Asks the page who moves the mouth now (the voice or the emotes); its answer arrives as <see cref="LastMouth"/>.</summary>
    internal Action? ReadMouth { get; set; }

    /// <summary>The last reading of the mouth as JSON (see CharacterMouthReading), or empty. Read as the "mouth" field of this
    /// surface's value.</summary>
    internal string LastMouth { get; set; } = "";

    /// <summary>Reads where the character looks now; the reading arrives as <see cref="LastLook"/>.</summary>
    internal Action? ReadLook { get; set; }

    /// <summary>The last reading of where the character looks as JSON (its number, what the eyes are on, the direction and the
    /// point, the usual gaze, and the window the user is using and what the eyes watch in it), or empty. Read as the "look"
    /// field of this surface's value.</summary>
    internal string LastLook { get; set; } = "";

    /// <summary>Moves the mouth with loudness levels (0 to 1), one every so many milliseconds, as Martlet's voice does, without
    /// a sound.</summary>
    internal Action<IReadOnlyList<double>, int>? PlayVoice { get; set; }

    /// <summary>Asks the page where each area of the character's touch zones is now; its answer arrives as <see cref="LastZones"/>.</summary>
    internal Action? ReadZones { get; set; }

    /// <summary>The last reading of the touch zones as JSON (its number, whether the renderer has zones, whether it draws them, and
    /// each area's zone, place among the zone's areas, box now and what it follows), or empty. Read as the "zones" field of this
    /// surface's value.</summary>
    internal string LastZones { get; set; } = "";

    /// <summary>Gives the character a VRMA clip from a file on this PC (its full path, or null to play the clip kept by the name),
    /// keeps it by a name (null: the file's name) and plays it once when asked, the hips in place or not; the answer arrives as
    /// <see cref="LastAnimation"/>.</summary>
    internal Action<string?, string?, bool, bool>? PlayAnimation { get; set; }

    /// <summary>The last answer about a VRMA clip as JSON (see CharacterAnimationReading), or empty. Read as the "animation" field
    /// of this surface's value.</summary>
    internal string LastAnimation { get; set; } = "";

    /// <summary>The most levels one "voice:" value plays (20 seconds at 50 ms).</summary>
    internal const int MaximumVoiceLevels = 400;

    /// <summary>The value UI Automation reads: the last tap's hit test with the last stroke, physical change, face reading,
    /// picture, pose reading, mouth reading, look reading, touch zones reading and clip answer added.</summary>
    internal string Reading()
    {
        if (LastStroke.Length == 0 && LastPhysical.Length == 0 && LastFace.Length == 0 && LastPicture.Length == 0 &&
            LastPose.Length == 0 && LastMouth.Length == 0 && LastLook.Length == 0 && LastZones.Length == 0 &&
            LastAnimation.Length == 0) return LastTouch;
        var node = (LastTouch.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(LastTouch) as System.Text.Json.Nodes.JsonObject : null) ?? [];
        if (LastStroke.Length > 0) node["stroke"] = System.Text.Json.Nodes.JsonNode.Parse(LastStroke);
        if (LastPhysical.Length > 0) node["physical"] = System.Text.Json.Nodes.JsonNode.Parse(LastPhysical);
        if (LastFace.Length > 0) node["face"] = System.Text.Json.Nodes.JsonNode.Parse(LastFace);
        if (LastPicture.Length > 0) node["picture"] = System.Text.Json.Nodes.JsonNode.Parse(LastPicture);
        if (LastPose.Length > 0) node["pose"] = System.Text.Json.Nodes.JsonNode.Parse(LastPose);
        if (LastMouth.Length > 0) node["mouth"] = System.Text.Json.Nodes.JsonNode.Parse(LastMouth);
        if (LastLook.Length > 0) node["look"] = System.Text.Json.Nodes.JsonNode.Parse(LastLook);
        if (LastZones.Length > 0) node["zones"] = System.Text.Json.Nodes.JsonNode.Parse(LastZones);
        if (LastAnimation.Length > 0) node["animation"] = System.Text.Json.Nodes.JsonNode.Parse(LastAnimation);
        return node.ToJsonString();
    }

    /// <summary>Parses an "animation:{json}" value: <c>path</c> (the full path of a .vrma file on this PC, or none to play a kept
    /// clip), <c>name</c> (needed without a path), <c>play</c> and <c>inPlace</c> (both true when left out).</summary>
    internal static (string? Name, string? Path, bool Play, bool InPlace) AnimationRequest(string value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value["animation:".Length..]);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) throw Invalid();
            string? Text(string property) => root.TryGetProperty(property, out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
                ? text.GetString() : null;
            bool Flag(string property) => !root.TryGetProperty(property, out var flag) || flag.ValueKind != System.Text.Json.JsonValueKind.False;
            var (name, path) = (Text("name"), Text("path"));
            if ((path is null && name is null) || (path is not null && !System.IO.Path.IsPathFullyQualified(path))) throw Invalid();
            return (name, path, Flag("play"), Flag("inPlace"));
        }
        catch (System.Text.Json.JsonException) { throw Invalid(); }

        static ArgumentException Invalid() => new(
            "Give a clip with \"animation:{\\\"path\\\": full path of a .vrma file}\" or play a kept one with \"animation:{\\\"name\\\": ...}\".", nameof(value));
    }

    /// <summary>Parses a "voice:ms;level;level;..." value: 10 to 1000 ms a step and 1 to <see cref="MaximumVoiceLevels"/> levels
    /// from 0 to 1 (invariant numbers).</summary>
    internal static (IReadOnlyList<double> Levels, int StepMs) VoiceLevels(string value)
    {
        var steps = value["voice:".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (steps.Length < 2 || steps.Length > MaximumVoiceLevels + 1 || !int.TryParse(steps[0], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms is < 10 or > 1000)
            throw Invalid();
        var levels = new List<double>();
        foreach (var step in steps.Skip(1))
            levels.Add(double.TryParse(step, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var level) && level is >= 0 and <= 1 ? level : throw Invalid());
        return (levels, ms);

        static ArgumentException Invalid() => new(
            $"Move the mouth with \"voice:ms;level;level;...\": 10 to 1000 ms a step and 1 to {MaximumVoiceLevels} levels from 0 to 1.", nameof(value));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(CharacterViewport owner) : FrameworkElementAutomationPeer(owner), IExpandCollapseProvider, ITransformProvider,
        IValueProvider
    {
        protected override string GetClassNameCore() => nameof(CharacterViewport);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        public override object GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.ExpandCollapse or PatternInterface.Transform or PatternInterface.Value => this,
            _ => base.GetPattern(patternInterface)
        };

        // Value: reads the last tap (with the last stroke, move or zoom, face reading, picture, pose reading, mouth reading and
        // look reading); setting "x,y" (invariant fractions of the surface) taps the character there, "stroke:ms;x,y;x,y;..."
        // strokes the locked character along those points, "voice:ms;level;..." moves its mouth as Martlet's voice does (without
        // a sound), "face" reads where Martlet draws over the face now, "picture" takes a picture of the character as it shows
        // now, "pose" reads what its idle body does now, "mouth" who moves its mouth now, "look" where it looks now and "zones"
        // where each area of its touch zones is now (they change nothing).
        public string Value => owner.Reading();
        public bool IsReadOnly => owner.TouchAt is null;

        public void SetValue(string value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (value == "look")
            {
                (owner.ReadLook ?? throw new InvalidOperationException("Where the character looks can't be read yet."))();
                return;
            }
            if (value == "face")
            {
                (owner.ReadFace ?? throw new InvalidOperationException("The character's face can't be read yet."))();
                return;
            }
            if (value == "picture")
            {
                (owner.TakePicture ?? throw new InvalidOperationException("The character's picture can't be taken yet."))();
                return;
            }
            if (value == "pose")
            {
                (owner.ReadPose ?? throw new InvalidOperationException("The character's pose can't be read yet."))();
                return;
            }
            if (value == "mouth")
            {
                (owner.ReadMouth ?? throw new InvalidOperationException("The character's mouth can't be read yet."))();
                return;
            }
            if (value == "zones")
            {
                (owner.ReadZones ?? throw new InvalidOperationException("The character's touch zones can't be read yet."))();
                return;
            }
            if (value?.StartsWith("animation:", StringComparison.Ordinal) == true)
            {
                if (owner.PlayAnimation is not { } play) throw new InvalidOperationException("The character can't play a clip yet.");
                var (name, path, now, inPlace) = AnimationRequest(value);
                play(name, path, now, inPlace);
                return;
            }
            if (value?.StartsWith("voice:", StringComparison.Ordinal) == true)
            {
                if (owner.PlayVoice is not { } play) throw new InvalidOperationException("The character's mouth can't be moved yet.");
                var (levels, stepMs) = VoiceLevels(value);
                play(levels, stepMs);
                return;
            }
            if (value?.StartsWith("stroke:", StringComparison.Ordinal) == true)
            {
                if (owner.StrokeAlong is not { } along) throw new InvalidOperationException("The character can't be stroked yet.");
                var steps = value[7..].Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (steps.Length < 3 || steps.Length > 201 || !int.TryParse(steps[0], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms is < 10 or > 2000)
                    throw new ArgumentException("Stroke with \"stroke:ms;x,y;x,y;...\": 10 to 2000 ms a step and 2 to 200 points.", nameof(value));
                along([.. steps.Skip(1).Select(Point)], ms);
                return;
            }
            if (owner.TouchAt is not { } touch) throw new InvalidOperationException("The character can't be tapped yet.");
            var parts = (value ?? "").Split(',');
            var held = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out held) ||
                held is < 0 or > Martlet.Avatar.Hosting.CharacterTouch.MaximumHeldMilliseconds))
                throw new ArgumentException("Tap with \"x,y\" (fractions 0 to 1 of the character's surface), or press and hold with \"x,y,ms\".", nameof(value));
            var (x, y) = Point(parts.Length == 3 ? parts[0] + "," + parts[1] : value ?? "");
            touch(x, y, held);
        }

        private static (double X, double Y) Point(string value)
        {
            var parts = value.Split(',');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ||
                x is < 0 or > 1 || y is < 0 or > 1)
                throw new ArgumentException("Give points as \"x,y\": fractions 0 to 1 of the character's surface.", nameof(value));
            return (x, y);
        }

        public bool CanMove => !owner.PlacementLocked && owner.MoveTo is not null;
        public bool CanResize => false;
        public bool CanRotate => false;

        public void Move(double x, double y)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (!CanMove) throw new InvalidOperationException("The character's position is locked. Unlock it in Martlet.");
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
            owner.MoveTo!(new System.Windows.Point(x, y));
        }

        public void Resize(double width, double height) =>
            throw new InvalidOperationException("Zoom the character to change its size.");

        public void Rotate(double degrees) => throw new InvalidOperationException("The character can't be rotated.");

        public ExpandCollapseState ExpandCollapseState =>
            owner.ContextMenu is { IsOpen: true } ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (owner.ContextMenu is not { IsOpen: false } menu) return;
            menu.PlacementTarget = owner;
            // Opened without a click, the overlay usually isn't the foreground window and can't hold the mouse capture that
            // closes the menu on a click elsewhere; it stays open until a choice, Collapse or Esc (and is reset on closing).
            menu.StaysOpen = true;
            menu.IsOpen = true;
        }

        public void Collapse()
        {
            if (owner.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        }
    }
}

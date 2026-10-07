using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The character's drag surface. Besides right-click (and the Menu key or Shift+F10), assistive technology and Martlet's MCP
/// open its menu through UI Automation's expand/collapse, move the character through its transform (like a drag), which
/// is refused while its place is locked, and tap it through its value ("x,y"), which then reads the last tap's hit test.
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

    /// <summary>The value UI Automation reads: the last tap's hit test with the last stroke and physical change added.</summary>
    internal string Reading()
    {
        if (LastStroke.Length == 0 && LastPhysical.Length == 0) return LastTouch;
        var node = (LastTouch.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(LastTouch) as System.Text.Json.Nodes.JsonObject : null) ?? [];
        if (LastStroke.Length > 0) node["stroke"] = System.Text.Json.Nodes.JsonNode.Parse(LastStroke);
        if (LastPhysical.Length > 0) node["physical"] = System.Text.Json.Nodes.JsonNode.Parse(LastPhysical);
        return node.ToJsonString();
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

        // Value: reads the last tap (with the last stroke and move or zoom); setting "x,y" (invariant fractions of the surface)
        // taps the character there, and "stroke:ms;x,y;x,y;..." strokes the locked character along those points.
        public string Value => owner.Reading();
        public bool IsReadOnly => owner.TouchAt is null;

        public void SetValue(string value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
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

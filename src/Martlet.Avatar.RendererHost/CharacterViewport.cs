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

    /// <summary>Taps the character at a point given as fractions 0..1 of this surface (+y down), like a click there.</summary>
    internal Action<double, double>? TouchAt { get; set; }

    /// <summary>The last tap's hit test as JSON (its number, point, whether it hit, the coarse zone, hit areas, drawables, bone,
    /// node, hair, mesh and material), or empty before the first tap. UI Automation reads it as this surface's value.</summary>
    internal string LastTouch { get; set; } = "";

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

        // Value: reads the last tap; setting "x,y" (invariant fractions of the surface) taps the character there.
        public string Value => owner.LastTouch;
        public bool IsReadOnly => owner.TouchAt is null;

        public void SetValue(string value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (owner.TouchAt is not { } touch) throw new InvalidOperationException("The character can't be tapped yet.");
            var parts = (value ?? "").Split(',');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ||
                x is < 0 or > 1 || y is < 0 or > 1)
                throw new ArgumentException("Tap with \"x,y\": fractions 0 to 1 of the character's surface.", nameof(value));
            touch(x, y);
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

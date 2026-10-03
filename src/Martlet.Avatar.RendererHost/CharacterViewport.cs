using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The character's drag surface. Besides right-click (and the Menu key or Shift+F10), assistive technology and Martlet's MCP
/// open its menu through UI Automation's expand/collapse, and move the character through its transform (like a drag), which
/// is refused while its place is locked.
/// </summary>
internal sealed class CharacterViewport : Grid
{
    /// <summary>Moves the overlay so this surface's top-left lands on the given screen point (device pixels).</summary>
    internal Action<System.Windows.Point>? MoveTo { get; set; }

    /// <summary>The character's place is locked: it can't be moved (Martlet unlocks it).</summary>
    internal bool PlacementLocked { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(CharacterViewport owner) : FrameworkElementAutomationPeer(owner), IExpandCollapseProvider, ITransformProvider
    {
        protected override string GetClassNameCore() => nameof(CharacterViewport);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        public override object GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.ExpandCollapse or PatternInterface.Transform => this,
            _ => base.GetPattern(patternInterface)
        };

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

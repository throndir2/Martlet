using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Martlet.Avatar.RendererHost;

/// <summary>
/// The character's drag surface. Besides right-click (and the Menu key or Shift+F10), assistive technology and Martlet's MCP
/// open its menu through UI Automation's expand/collapse.
/// </summary>
internal sealed class CharacterViewport : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(CharacterViewport owner) : FrameworkElementAutomationPeer(owner), IExpandCollapseProvider
    {
        protected override string GetClassNameCore() => nameof(CharacterViewport);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

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

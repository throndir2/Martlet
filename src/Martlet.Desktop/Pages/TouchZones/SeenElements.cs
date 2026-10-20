using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Martlet.Desktop;

// The zone map's picture and each zone's box on it take part in UI Automation (a Canvas and a Border have no automation peer of
// their own), so Martlet's MCP sees the boxes (TouchZoneRect-<n>, with their bounds) on the picture (TouchZonesPicture).

/// <summary>A <see cref="Canvas"/> that UI Automation sees.</summary>
internal sealed class SeenCanvas : Canvas
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}

/// <summary>A <see cref="Border"/> that UI Automation sees.</summary>
internal sealed class SeenBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}

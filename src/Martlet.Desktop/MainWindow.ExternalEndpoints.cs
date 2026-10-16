using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>Devices › Cloud providers and servers: every endpoint outside this PC and your paired computers that Martlet sends work
/// to, with what each one is used for (<see cref="ExternalEndpoints"/>). A host PC uses no conversation jobs itself, so its saved
/// Thinking, Listening and Speaking routes are left out. Read-only; never a key.</summary>
public partial class MainWindow
{
    private ExternalEndpointSources EndpointSources()
    {
        var directory = store?.DataDirectory;
        var companion = Role != DeviceRole.Host;
        return new()
        {
            Routes = companion ? homeSettings?.Setup?.Routes ?? [] : [],
            Fallback = companion ? homeSettings?.ThinkingFallback : null,
            ThinkingPool = directory is null ? null : ThinkingPoolSettings.Load(directory),
            Pools = directory is null ? [] : [.. PoolAreas.All.Select(area => WorkSharingRoster.Shared.Pool(directory, area)).OfType<PoolList>()],
            Tools = [.. mcpTools.Configuration.Servers.Where(s => s.Transport == McpTransportKind.Http)
                .Select(s => new ExternalTool(s.Name, s.Url?.AbsoluteUri, s.Disabled))]
        };
    }

    private void RenderExternalEndpoints()
    {
        var endpoints = ExternalEndpoints.Find(EndpointSources());
        ExternalEndpointsStatus.Text = ExternalEndpoints.Summary(endpoints);
        ExternalEndpointsPanel.Children.Clear();
        foreach (var endpoint in endpoints)
        {
            var row = NetworkRowFrame();
            row.Children.Add(NetworkRowText("ExternalEndpoint-" + endpoint.Id, endpoint.Name, endpoint.Describe()));
            ExternalEndpointsPanel.Children.Add(NetworkRowCard(row, warning: false));
        }
    }
}

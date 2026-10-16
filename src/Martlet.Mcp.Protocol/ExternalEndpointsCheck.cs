using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>external_endpoints_status: Devices › Cloud providers and servers from a data directory, with the production
/// <see cref="ExternalEndpoints"/>. Reads settings.json (the Thinking, Listening and Speaking routes and Thinking's fallback),
/// thinking-pool.json (without migrating or saving it), pools.json, pools-local.json and mcp.json. Never a key; read-only.</summary>
internal static class ExternalEndpointsCheck
{
    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var (tools, toolsState) = Tools(dataDirectory);
        var sources = new ExternalEndpointSources
        {
            Routes = loaded.Settings?.Setup?.Routes ?? [],
            Fallback = loaded.Settings?.ThinkingFallback,
            ThinkingPool = ThinkingPoolSettings.Read(dataDirectory, save: false).Settings,
            Pools = [.. PoolAreas.All.Select(area => PoolSettings.Load(dataDirectory, area.Shared) is var saved && saved.Has(area.Id) ? saved.Pool(area.Id) : null)
                .OfType<PoolList>()],
            Tools = tools
        };
        var endpoints = ExternalEndpoints.Find(sources);
        return new
        {
            dataDirectory,
            settings = loaded.Settings is null ? "none" : "loaded",
            mcpJson = toolsState,
            summary = ExternalEndpoints.Summary(endpoints),
            endpoints = endpoints.Select(e => new
            {
                id = e.Id, name = e.Name, kind = e.Cloud ? "cloud" : "server", address = e.Address, text = e.Describe(),
                uses = e.Uses.Select(u => new { purpose = u.Purpose, page = u.Page, model = u.Model, on = u.On })
            })
        };
    }

    /// <summary>mcp.json's servers reached over HTTP, and the file's state (none, loaded, unreadable or invalid).</summary>
    private static (IReadOnlyList<ExternalTool> Tools, string State) Tools(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "mcp.json");
        if (!File.Exists(path)) return ([], "none");
        try
        {
            if (new FileInfo(path).Length > McpConfiguration.MaxFileBytes) return ([], "invalid");
            var configuration = McpConfiguration.Parse(File.ReadAllText(path), secrets: _ => "");
            return ([.. configuration.Servers.Where(s => s.Transport == McpTransportKind.Http)
                .Select(s => new ExternalTool(s.Name, s.Url?.AbsoluteUri, s.Disabled))], "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ([], "unreadable"); }
        catch (McpConfigurationException) { return ([], "invalid"); }
    }
}

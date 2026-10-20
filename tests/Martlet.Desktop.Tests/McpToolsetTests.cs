using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Mcp.Client;

namespace Martlet.Desktop.Tests;

// A reply's tools never wait for MCP servers and keep the same names while servers start, stop and restart (#938, #939).
public sealed class McpToolsetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-mcp-toolset-" + Guid.NewGuid().ToString("N"));

    public McpToolsetTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // What a request carries for each tool.
    private static IEnumerable<string> Encoded(DesktopToolset toolset) =>
        toolset.Definitions.Select(d => d.Name + "|" + d.Description + "|" + d.ParametersJson);

    private static McpServerStatus Ready(string server, params string[] tools) => new(new McpServerDefinition { Name = server, Command = "x" },
        McpServerState.Ready, [.. tools.Select(t => new McpTool(t, null, $"The {t} tool.", new JsonObject()))], null, null, null, null);

    [Fact]
    public async Task Tool_names_start_with_their_server_and_never_change_when_another_server_starts_or_stops()
    {
        await using var service = new McpToolService(directory);
        var both = DesktopToolset.Build(service, [Ready("alpha", "search", "read"), Ready("beta", "search")])!;
        var alone = DesktopToolset.Build(service, [Ready("alpha", "search", "read")])!;

        Assert.Equal(["alpha_search", "alpha_read", "beta_search"], both.Definitions.Select(d => d.Name));
        Assert.Equal(["alpha_search", "alpha_read"], alone.Definitions.Select(d => d.Name));
        Assert.Equal(Encoded(both).Take(2), Encoded(alone));
    }

    // A stdio MCP server in Windows PowerShell: it answers initialize and tools/list on its first launch, and never answers on
    // a later one (a server that starts slowly).
    private const string Server = """
        $count = 0
        if (Test-Path $args[0]) { $count = [int](Get-Content $args[0]) }
        Set-Content $args[0] ($count + 1)
        if ($count -gt 0) { Start-Sleep -Seconds 600; exit }
        while ($null -ne ($line = [Console]::In.ReadLine())) {
          $m = $line | ConvertFrom-Json
          if ($null -eq $m.id) { continue }
          if ($m.method -eq 'initialize') { $r = @{ protocolVersion = '2025-06-18'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'fake'; version = '1' } } }
          elseif ($m.method -eq 'tools/list') { $r = @{ tools = @(@{ name = 'search'; description = 'Searches.'; inputSchema = @{ type = 'object'; properties = @{} } }) } }
          else { $r = @{} }
          [Console]::Out.WriteLine((@{ jsonrpc = '2.0'; id = $m.id; result = $r } | ConvertTo-Json -Depth 10 -Compress))
          [Console]::Out.Flush()
        }
        """;

    [Fact]
    public async Task A_reply_never_waits_for_a_starting_server_and_offers_its_last_tools_while_it_restarts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = Path.Combine(directory, "server.ps1");
        var launches = Path.Combine(directory, "launches.txt");
        await File.WriteAllTextAsync(script, Server);
        await File.WriteAllTextAsync(Path.Combine(directory, McpToolService.FileName), JsonSerializer.Serialize(new
        {
            mcpServers = new { fake = new { command = "powershell.exe", args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, launches } } }
        }));
        await using var service = new McpToolService(directory);

        // The first reply starts the server and doesn't wait for it: its tools aren't known yet.
        var clock = Stopwatch.StartNew();
        Assert.Null(service.Prepare());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{clock.Elapsed}");
        for (var i = 0; i < 600 && service.Hub.Status.Single().State != McpServerState.Ready; i++) await Task.Delay(100);
        var status = service.Hub.Status.Single();
        Assert.True(status.State == McpServerState.Ready, $"{status.State}: {status.Error} {status.Diagnostics}");
        var ready = service.Prepare()!;
        Assert.Equal(["fake_search"], ready.Definitions.Select(d => d.Name));

        // It restarts and this time never answers: a reply still gets the same tools, at once.
        service.Hub.Restart("fake");
        Assert.Equal(McpServerState.Starting, service.Hub.Status.Single().State);
        clock.Restart();
        var starting = service.Prepare()!;
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{clock.Elapsed}");
        Assert.Equal(Encoded(ready), Encoded(starting));
    }
}

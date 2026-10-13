using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>docs/MCP.md and the server's tools agree: every tool the server defines is named in the page, and the page names no
/// tool the server does not have (a renamed or removed tool).</summary>
public sealed partial class McpDocsAgreementTests
{
    [Fact]
    public async Task McpDocsNameEveryToolAndNoToolTheServerDoesNotHave()
    {
        var tools = await ToolNamesAsync();
        Assert.True(tools.Count > 150, $"Only {tools.Count} tools listed.");
        var named = BacktickedNames().Matches(await File.ReadAllTextAsync(McpDocs())).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var undocumented = tools.Where(tool => !named.Contains(tool)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(undocumented.Length == 0, "docs/MCP.md does not name these tools (write each as `name`): " + string.Join(", ", undocumented));

        var unknown = named.Where(name => !tools.Contains(name) && LooksLikeTool(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unknown.Length == 0, "docs/MCP.md names tools the server does not have: " + string.Join(", ", unknown));
    }

    /// <summary>The names MCP's own tools use (ui_click, doctor_status, network_selftest, hearing_check): Martlet's conversation tools
    /// and settings keys in the page do not look like this.</summary>
    private static bool LooksLikeTool(string name) =>
        name.StartsWith("ui_", StringComparison.Ordinal) || name.StartsWith("martlet_", StringComparison.Ordinal) ||
        name.StartsWith("doctor_", StringComparison.Ordinal) || name.StartsWith("logs_", StringComparison.Ordinal) ||
        name.EndsWith("_status", StringComparison.Ordinal) || name.EndsWith("_check", StringComparison.Ordinal) ||
        name.EndsWith("_selftest", StringComparison.Ordinal);

    private static async Task<HashSet<string>> ToolNamesAsync()
    {
        using var reader = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""" + "\n");
        using var writer = new StringWriter();
        await new McpServer(new DesktopAutomation(false), allowChanges: false, allTools: true).RunAsync(reader, writer, CancellationToken.None);
        using var document = JsonDocument.Parse(writer.ToString());
        return document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static string McpDocs()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "docs", "MCP.md");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("docs/MCP.md was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`")]
    private static partial Regex BacktickedNames();
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>The MCP docs (docs/MCP.md, the index, and one page for each martlet_guide area in docs/mcp/) and the server's tools
/// agree: every tool the server defines is named on a page, and no page names a tool the server does not have (a renamed or removed
/// tool).</summary>
public sealed partial class McpDocsAgreementTests
{
    [Fact]
    public async Task McpDocsNameEveryToolAndNoToolTheServerDoesNotHave()
    {
        var tools = await ToolNamesAsync();
        Assert.True(tools.Count > 150, $"Only {tools.Count} tools listed.");
        var pages = McpDocs();
        Assert.True(pages.Length > 5, $"Only {pages.Length} MCP docs pages found.");
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages)
            named.UnionWith(BacktickedNames().Matches(await File.ReadAllTextAsync(page)).Select(match => match.Groups[1].Value));

        var undocumented = tools.Where(tool => !named.Contains(tool)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(undocumented.Length == 0, "docs/MCP.md and docs/mcp/ do not name these tools (write each as `name` on the page of its area): " + string.Join(", ", undocumented));

        var unknown = named.Where(name => !tools.Contains(name) && LooksLikeTool(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unknown.Length == 0, "docs/MCP.md or docs/mcp/ names tools the server does not have: " + string.Join(", ", unknown));
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

    /// <summary>docs/MCP.md and every page in docs/mcp/.</summary>
    private static string[] McpDocs()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "docs", "MCP.md");
            var pages = Path.Combine(folder.FullName, "docs", "mcp");
            if (File.Exists(path))
                return [path, .. Directory.Exists(pages) ? Directory.GetFiles(pages, "*.md").Order(StringComparer.Ordinal).ToArray() : []];
        }
        throw new FileNotFoundException("docs/MCP.md was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`")]
    private static partial Regex BacktickedNames();
}

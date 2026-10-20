using Martlet.Mcp.Client;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Tools' page model (Pages\Tools), without WPF.</summary>
public sealed class ToolsPageModelTests
{
    [Fact]
    public void ServerStateSaysWhetherTheServerRuns()
    {
        var server = new McpServerDefinition { Name = "files" };
        Assert.Equal("Off.", ToolsPageModel.ServerState(server with { Disabled = true }, null));
        Assert.StartsWith("Not started yet:", ToolsPageModel.ServerState(server, null), StringComparison.Ordinal);
        Assert.Null(ToolsPageModel.FromDirectory(server));
        Assert.Equal("From the MCP directory: io.example/files 1.2; 1 secret value is kept in Windows Credential Manager.",
            ToolsPageModel.FromDirectory(server with { Registry = "io.example/files", RegistryVersion = "1.2", Secrets = ["TOKEN"] }));
    }

    [Fact]
    public void TerminalWordsSayWhatChanged()
    {
        var off = new TerminalSettings();
        Assert.Equal("The terminal is on, and Martlet asks before each command.", ToolsPageModel.Saved(off, off with { Enabled = true }));
        Assert.Equal("The terminal is off.", ToolsPageModel.Saved(off with { Enabled = true }, off));
        Assert.Equal("Terminal commands run without asking.", ToolsPageModel.Saved(off, off with { AskFirst = false }));
        Assert.Equal("Terminal settings saved.", ToolsPageModel.Saved(off, off with { TimeLimitSeconds = 60 }));
        Assert.Equal("15 seconds", ToolsPageModel.TimeLimit(15));
        Assert.Equal("1 minute", ToolsPageModel.TimeLimit(60));
        Assert.StartsWith("Your home folder (", ToolsPageModel.Folder(off), StringComparison.Ordinal);
        Assert.Equal(@"C:\Work", ToolsPageModel.Folder(off with { Folder = @"C:\Work" }));
    }
}

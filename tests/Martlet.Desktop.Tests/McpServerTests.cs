using System.Diagnostics;
using System.Text.Json;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task NegotiatesAndRunsRealOfflineFixtureOverStdio()
    {
        var messages = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"fixture","arguments":{"scenario":"complete"}}}""");
        Assert.Equal(3, messages.Length);
        Assert.Equal("2025-06-18", messages[0].GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Contains(messages[1].GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "ui_click");
        var result = ToolResult(messages[2]);
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.True(result.GetProperty("report").GetRawText().Contains("fixture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeniesUnapprovedUiEffectsAndUnrelatedProcesses()
    {
        var messages = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ui_click","arguments":{"id":"LiveSend"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ui_connect","arguments":{"pid":-1}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ui_snapshot"}}""");
        Assert.All(messages, message => Assert.True(message.GetProperty("result").GetProperty("isError").GetBoolean()));
        Assert.Contains("--allow-ui-effects", messages[0].GetRawText());
        Assert.Contains("positive", messages[1].GetRawText());
        Assert.Contains("Connect", messages[2].GetRawText());
    }

    [Fact]
    public async Task RejectsMalformedRequestWithoutEndingSession()
    {
        var messages = await SendAsync(
            """{"jsonrpc":123,"id":1,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal(-32600, messages[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(2, messages[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task AttachesToRealDesktopAndInvokesOfflineFixture()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Martlet.Desktop.exe");
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Tests." + Guid.NewGuid().ToString("N"));
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--data-directory", directory },
            UseShellExecute = false
        })!;
        try
        {
            var automation = new DesktopAutomation(false);
            Exception? lastError = null;
            var connected = false;
            for (var attempt = 0; attempt < 50 && !connected; attempt++)
            {
                await Task.Delay(100);
                try
                {
                    await Task.Run(() => automation.Connect(process.Id));
                    connected = true;
                }
                catch (InvalidOperationException error) { lastError = error; }
            }
            Assert.True(connected, lastError?.Message);
            await Task.Run(() => automation.ClickAsync("StartFixture"));
            var fixture = "";
            for (var attempt = 0; attempt < 50 && !fixture.Contains("Scenario: complete", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(100);
                var snapshot = JsonSerializer.Serialize(await Task.Run(automation.Snapshot));
                fixture = snapshot;
            }
            Assert.Contains("FIXTURE - NOT AI", fixture);
            Assert.Contains("Scenario: complete", fixture);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement ToolResult(JsonElement message)
    {
        var text = message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> SendAsync(params string[] requests)
    {
        using var reader = new StringReader(string.Join('\n', requests) + "\n");
        using var writer = new StringWriter();
        await new McpServer(new DesktopAutomation(false)).RunAsync(reader, writer, CancellationToken.None);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }
}

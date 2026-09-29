using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Martlet.Mcp;
using Xunit.Abstractions;

namespace Martlet.Desktop.Tests;

public sealed class McpServerTests(ITestOutputHelper output)
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AttachesToRealDesktopAndInvokesOfflineFixture(bool churn) => WithDesktop(async (_, automation) =>
    {
        var churnTask = churn ? ChurnWindows() : Task.CompletedTask;
        try
        {
            await Task.Run(() => automation.ClickAsync("NavSettings"));
            await Task.Delay(300);
            await Task.Run(() => automation.ClickAsync("StartFixture"));
            var fixture = "";
            for (var attempt = 0; attempt < 50 && !fixture.Contains("Scenario: complete", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(100);
                fixture = JsonSerializer.Serialize(await Task.Run(automation.Snapshot));
            }
            Assert.Contains("FIXTURE - NOT AI", fixture);
            Assert.Contains("Scenario: complete", fixture);
            if (churn)
            {
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    var snapshot = JsonSerializer.SerializeToElement(await Task.Run(automation.Snapshot));
                    Assert.Single(snapshot.GetProperty("windows").EnumerateArray());
                    await Task.Delay(5);
                }
            }
        }
        finally { await churnTask; }
    });

    [Fact]
    public Task PreservesDialogsAndRejectsWrongOwnerHiddenMainAndExitedProcess() => WithDesktop(async (process, automation) =>
    {
        var pid = process.Id;
        process.Refresh();
        var mainHandle = process.MainWindowHandle;
        Assert.NotEqual(0, mainHandle);
        await Task.Run(() =>
        {
            Assert.Throws<ArgumentException>(() => automation.Connect(Environment.ProcessId));
            Assert.Throws<InvalidOperationException>(() => DesktopAutomation.WindowForProcess(Environment.ProcessId, mainHandle));
        });

        await Task.Run(() => automation.ClickAsync("NavCompanion"));
        await Task.Delay(300);
        await Task.Run(() => automation.ClickAsync("OpenSetup"));
        var snapshot = await WaitForWindowCount(automation, 2);
        Assert.Single(snapshot.GetProperty("controls").EnumerateArray(),
            control => control.GetProperty("id").GetString() == "SetupClose");
        var setupHandle = await Task.Run(() => (nint)DesktopAutomation.WindowForProcess(pid, mainHandle)
            .FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SetupWindow"))
            .Current.NativeWindowHandle);
        Assert.NotEqual(0, setupHandle);

        Assert.True(ShowWindowAsync(mainHandle, 0));
        await WaitForVisibility(mainHandle, false);
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => DesktopAutomation.WindowForProcess(pid, mainHandle));
            Assert.Throws<InvalidOperationException>(() => automation.Snapshot());
            Assert.Throws<InvalidOperationException>(() => new DesktopAutomation(false).Connect(pid));
        });
        Assert.True(ShowWindowAsync(mainHandle, 4));
        await WaitForVisibility(mainHandle, true);
        await Task.Run(() => automation.ClickAsync("SetupClose"));
        await WaitForVisibility(setupHandle, false);
        await WaitForWindowCount(automation, 1);

        process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => DesktopAutomation.WindowForProcess(pid, mainHandle));
            Assert.Throws<ArgumentException>(() => automation.Snapshot());
        });
    });

    private async Task WithDesktop(Func<Process, DesktopAutomation, Task> action)
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
            await action(process, automation);
        }
        catch
        {
            process.Refresh();
            output.WriteLine($"Owned fixture: pid={process.Id}, exited={process.HasExited}, exitCode={(process.HasExited ? process.ExitCode : null)}, hwnd={(process.HasExited ? 0 : process.MainWindowHandle)}");
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task ChurnWindows()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? failure = null;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    for (var iteration = 0; iteration < 200; iteration++)
                    {
                        var window = new Window
                        {
                            Title = "MCP owned churn fixture", Width = 100, Height = 100,
                            ShowActivated = false, ShowInTaskbar = false
                        };
                        try
                        {
                            window.Show();
                            await Task.Delay(5);
                        }
                        finally { window.Close(); }
                    }
                }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
            if (failure is null) finished.SetResult();
            else finished.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task;
    }

    private static async Task WaitForVisibility(nint handle, bool visible)
    {
        for (var attempt = 0; attempt < 50 && IsWindowVisible(handle) != visible; attempt++)
            await Task.Delay(20);
        Assert.Equal(visible, IsWindowVisible(handle));
    }

    private static async Task<JsonElement> WaitForWindowCount(DesktopAutomation automation, int count)
    {
        var snapshot = JsonSerializer.SerializeToElement(await Task.Run(automation.Snapshot));
        for (var attempt = 0; attempt < 50 && snapshot.GetProperty("windows").GetArrayLength() != count; attempt++)
        {
            await Task.Delay(100);
            snapshot = JsonSerializer.SerializeToElement(await Task.Run(automation.Snapshot));
        }
        Assert.Equal(count, snapshot.GetProperty("windows").GetArrayLength());
        return snapshot;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

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

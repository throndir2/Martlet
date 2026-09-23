using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Martlet.Readiness;

namespace Martlet.Launcher.FixtureChild;

public static class FixtureMarker;

internal static class Program
{
    private const string PurposeVariable = "MARTLET_READINESS_PURPOSE";
    private const string NonceVariable = "MARTLET_READINESS_NONCE";
    private const string VersionVariable = "MARTLET_READINESS_VERSION";
    private const string ProfileVariable = "MARTLET_READINESS_PROFILE";
    private const string PayloadVariable = "MARTLET_READINESS_PAYLOAD_SHA256";

    private static async Task<int> Main(string[] args)
    {
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "fixture-started.pid"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (args is ["--fixture-grandchild"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args is ["--fixture-report"])
        {
            await DesktopReadinessReporter.CreateFromEnvironment()
                .ReportInitializedAsync();
            return 0;
        }

        try
        {
            var purpose = Environment.GetEnvironmentVariable(PurposeVariable);
            var modes = File.ReadAllLines(Path.Combine(
                    AppContext.BaseDirectory, "fixture-mode.txt"))
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1],
                    StringComparer.Ordinal);
            var key = purpose == DesktopReadinessPurpose.ActivationProbe.ToString()
                ? "activation"
                : purpose == DesktopReadinessPurpose.DesktopLaunch.ToString()
                    ? "launch"
                    : "";
            if (!modes.TryGetValue(key, out var mode))
                return 80;
            return await RunAsync(mode);
        }
        catch
        {
            return 81;
        }
    }

    private static async Task<int> RunAsync(string mode)
    {
        if (mode.StartsWith("raw-", StringComparison.Ordinal))
        {
            await RawReportAsync(mode);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        switch (mode)
        {
            case "initialized-exit":
                await ReportInitializedAsync();
                await Task.Delay(50);
                return 0;
            case "initialized-hang":
                await ReportInitializedAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "initialized-crash":
                await ReportInitializedAsync();
                await Task.Delay(50);
                return 72;
            case "degraded":
                await DesktopReadinessReporter.CreateFromEnvironment()
                    .ReportDegradedAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "failed":
                await DesktopReadinessReporter.CreateFromEnvironment()
                    .ReportFailedAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "silent-hang":
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "late":
                await Task.Delay(1000);
                await ReportInitializedAsync();
                return 0;
            case "crash":
                return 73;
            case "wrong-nonce":
                Environment.SetEnvironmentVariable(NonceVariable, new string('0', 64));
                await ReportInitializedAsync();
                return 0;
            case "wrong-version":
                Environment.SetEnvironmentVariable(VersionVariable, "0.9.9.9");
                await ReportInitializedAsync();
                return 0;
            case "wrong-profile":
                Environment.SetEnvironmentVariable(ProfileVariable, Guid.NewGuid().ToString("D"));
                await ReportInitializedAsync();
                return 0;
            case "wrong-payload":
                Environment.SetEnvironmentVariable(PayloadVariable, new string('0', 64));
                await ReportInitializedAsync();
                return 0;
            case "wrong-process":
                StartChild("--fixture-report");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "spawn-child-silent":
                var child = StartChild("--fixture-grandchild");
                await File.WriteAllTextAsync(
                    Path.Combine(Path.GetTempPath(), "grandchild.pid"),
                    child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            default:
                return 82;
        }
    }

    private static async Task RawReportAsync(string mode)
    {
        string Env(string suffix) => Environment.GetEnvironmentVariable("MARTLET_READINESS_" + suffix)!;
        var message = new Dictionary<string, object>
        {
            ["formatVersion"] = mode == "raw-format" ? 99 : 1,
            ["protocolVersion"] = mode == "raw-protocol" ? 99 : 1,
            ["nonce"] = Env("NONCE"),
            ["purpose"] = mode == "raw-purpose" ? 0 : 1,
            ["version"] = Env("VERSION"),
            ["profileId"] = Guid.Parse(Env("PROFILE")),
            ["settingsRevision"] = mode == "raw-settings" ? new string('c', 64) : Env("SETTINGS_REVISION"),
            ["payloadSha256"] = Env("PAYLOAD_SHA256"),
            ["executableSha256"] = mode == "raw-executable" ? new string('0', 64) : Env("EXECUTABLE_SHA256"),
            ["processId"] = Environment.ProcessId,
            ["deadlineUtc"] = DateTimeOffset.Parse(Env("DEADLINE_UTC"),
                System.Globalization.CultureInfo.InvariantCulture).AddSeconds(mode == "raw-deadline" ? 1 : 0),
            ["state"] = mode == "raw-state" ? 99 : 0
        };
        var json = JsonSerializer.Serialize(message);
        if (mode == "raw-duplicate") json = json[..^1] + ",\"state\":0}";
        if (mode == "raw-unknown") json = json[..^1] + ",\"extra\":0}";
        if (mode == "raw-noncanonical") json += " ";
        var bytes = Encoding.UTF8.GetBytes(json);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length,
            mode == "raw-oversize" ? 4097 : mode == "raw-zero" ? 0 : bytes.Length);
        await using var pipe = new NamedPipeClientStream(".", Env("PIPE"), PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(length, timeout.Token);
        if (mode is not ("raw-oversize" or "raw-zero"))
            await pipe.WriteAsync(mode == "raw-truncated" ? bytes[..8] : bytes, timeout.Token);
    }

    private static async Task ReportInitializedAsync()
    {
        if (Environment.GetEnvironmentVariable("PATH") is not null ||
            Environment.GetEnvironmentVariable("COMSPEC") is not null)
            throw new InvalidOperationException();
        await DesktopReadinessReporter.CreateFromEnvironment()
            .ReportInitializedAsync();
    }

    private static Process StartChild(string argument)
    {
        var path = Environment.ProcessPath ??
            throw new InvalidOperationException();
        return Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            ArgumentList = { argument }
        }) ?? throw new InvalidOperationException();
    }
}

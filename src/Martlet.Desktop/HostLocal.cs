using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Desktop;

/// <summary>Drives martlet-host on this PC's Docker Desktop without a console: starts Docker, builds the host image,
/// runs setup unattended (--yes) and pairs this desktop by itself, streaming output to a <see cref="HostRunWindow"/>.
/// The owner's click in Martlet is the confirmation, as for SSH hosts (<see cref="HostRemote"/>).</summary>
internal static partial class HostLocal
{
    [GeneratedRegex(@"martlet-pair-v1\.[A-Za-z0-9_-]+")]
    private static partial Regex PairingCodePattern();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiPattern();

    private static string Docker
    {
        get
        {
            var bundled = Path.Combine(Path.GetDirectoryName(MachineInfo.DockerDesktopPath)!, "resources", "bin", "docker.exe");
            return File.Exists(bundled) ? bundled : "docker";
        }
    }

    /// <summary>Starts Docker Desktop when needed and waits (up to ten minutes) until its engine answers.</summary>
    internal static async Task EnsureDockerAsync(Action<string> status, IProgress<string> output, CancellationToken token)
    {
        if (!MachineInfo.DockerDesktopInstalled())
            throw new InvalidOperationException("Docker Desktop isn't installed on this PC yet. Install it, start it once, then try again.");
        status("Checking Docker Desktop...");
        if (await RunAsync(["info", "--format", "{{.ServerVersion}}"], null, token) == 0) return;
        if (!MachineInfo.DockerDesktopRunning())
        {
            output.Report("Starting Docker Desktop...");
            Process.Start(new ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose();
        }
        status("Waiting for Docker Desktop to start. The first start can take a few minutes; accept Docker's terms if it asks.");
        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            if (await RunAsync(["info", "--format", "{{.ServerVersion}}"], null, token) == 0)
            {
                output.Report("Docker Desktop is running.");
                return;
            }
        }
        throw new InvalidOperationException("Docker Desktop did not start within ten minutes. Open it, make sure it says it is running, then try again.");
    }

    /// <summary>Builds martlet-host:&lt;version&gt; from this version's source (falling back to main) unless it exists.</summary>
    internal static async Task EnsureImageAsync(HostSetupTarget target, Action<string> status, IProgress<string> output, CancellationToken token)
    {
        var image = HostSetupCommands.Image(target);
        if (await RunAsync(["image", "inspect", image], null, token) == 0) return;
        status($"Building Martlet's host image {image}. The first time this takes a few minutes...");
        foreach (var reference in new[] { "v" + target.Version, "main" })
        {
            output.Report($"$ docker build -t {image} -f deploy/host/Dockerfile {HostSetupCommands.Repository}#{reference}");
            if (await RunAsync(["build", "-t", image, "-f", "deploy/host/Dockerfile", $"{HostSetupCommands.Repository}#{reference}"],
                    output, token) == 0)
                return;
        }
        throw new InvalidOperationException("The host image could not be built. The output shows why.");
    }

    /// <summary>Runs one martlet-host command unattended in a container on this PC; returns its exit code.</summary>
    internal static Task<int> EngineAsync(HostSetupTarget target, IReadOnlyList<string> engine, IProgress<string> output,
        CancellationToken token, Task<string?>? moreInput = null)
    {
        HostSetupCommands.Validate(target, HostAction.Status);
        var setup = engine.Count > 0 && engine[0] == "setup";
        var args = new List<string> { "run", "--rm", "-i", "--log-driver", "none", "-u", "0", "-v", HostSetupCommands.DockerSocket };
        if (setup)
        {
            args.AddRange(["-e", "MARTLET_HOST_ADDRESS=" + target.Address]);
            if (target.HostId is { } id) args.AddRange(["-e", "MARTLET_HOST_ID=" + id]);
        }
        args.Add(HostSetupCommands.Image(target));
        args.Add("--yes");
        args.AddRange(engine);
        output.Report("$ martlet-host " + string.Join(' ', engine) + "  (on this PC, Docker Desktop)");
        return RunAsync(args, output, token, "end\n", moreInput);
    }

    /// <summary>Pairs this desktop with this PC's host: runs "pair --device-id ... --name ...", reads the one-use code from
    /// its output (never shown), redeems it and lets the host restart its gateway.</summary>
    internal static async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(HostSetupTarget target, string deviceId,
        string name, IProgress<string> output, CancellationToken token)
    {
        var label = new string(name.Where(c => c is >= ' ' and <= '~').Take(64).ToArray()).Trim();
        if (label.Length == 0 || label[0] == '-') label = "Martlet desktop";
        var withdraw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(Audio2FaceHostPairing, string)>? pairing = null;
        var sink = new LineSink(line =>
        {
            var match = PairingCodePattern().Match(line);
            if (!match.Success) { output.Report(line); return; }
            output.Report("pairing-code: (read by Martlet; not shown)");
            if (pairing is not null) return;
            output.Report($"Pairing this PC as {deviceId}...");
            pairing = RedeemAsync(match.Value);
        });

        async Task<(Audio2FaceHostPairing, string)> RedeemAsync(string code)
        {
            try { return await HostPairingCode.Parse(code).PairAsync(deviceId, token); }
            catch
            {
                withdraw.TrySetResult("cancel\n");
                throw;
            }
        }

        int exit;
        try { exit = await EngineAsync(target, ["pair", "--device-id", deviceId, "--name", label], sink, token, withdraw.Task); }
        finally { withdraw.TrySetResult(null); }
        if (pairing is null) throw new InvalidOperationException($"This PC's host did not show a pairing code (exit {exit}). See the output.");
        var result = await pairing;
        if (exit != 0) output.Report($"Paired, but the host reported exit {exit} while restarting its gateway. Check the host.");
        return result;
    }

    private static async Task<int> RunAsync(IEnumerable<string> args, IProgress<string>? output, CancellationToken token,
        string? input = null, Task<string?>? moreInput = null)
    {
        var start = new ProcessStartInfo(Docker)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        void Line(string? text)
        {
            if (text is not null && output is not null) output.Report(AnsiPattern().Replace(text, ""));
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException("Docker could not be started: " + error.Message);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var feeding = FeedAsync(process.StandardInput, input, moreInput, process.WaitForExitAsync(CancellationToken.None));
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        await feeding;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task FeedAsync(StreamWriter stdin, string? input, Task<string?>? more, Task exited)
    {
        try
        {
            if (input is not null) { await stdin.WriteAsync(input); await stdin.FlushAsync(); }
            if (more is not null && await Task.WhenAny(more, exited) == more && await more is { } last)
            {
                await stdin.WriteAsync(last);
                await stdin.FlushAsync();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { stdin.Close(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }
}

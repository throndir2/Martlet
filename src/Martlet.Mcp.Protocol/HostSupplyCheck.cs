using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Nodes;

namespace Martlet.Mcp;

/// <summary>host_supply_check: sets up a native Ubuntu host that has no internet access the way Martlet does when it finds
/// one (deploy/host/README.md, "Computers without internet"), with the production <see cref="HostSupplier"/>,
/// <see cref="HostCheckout"/> and this checkout's real engine, in one disposable ubuntu:24.04 container on an internal
/// Docker network (a private LAN address, no route out; never pulled, removed afterwards). This PC downloads the .NET SDK
/// and the gateway's NuGet packages for real (cached in cacheDirectory); the container gets them only from this PC.</summary>
internal static class HostSupplyCheck
{
    internal const string Image = "ubuntu:24.04";
    private const string User = "martlet";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(25);

    internal static async Task<object> RunAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var cache = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("cacheDirectory", out var given) &&
            given.GetString() is { Length: > 0 } directory
                ? Path.GetFullPath(directory)
                : Path.Combine(Path.GetTempPath(), "Martlet", "host-supply-check");
        var root = FindCheckout();
        var (found, _) = await DockerAsync(["image", "inspect", "--format", "{{.Id}}", Image], null, TimeSpan.FromSeconds(30), cancellation);
        if (found != 0)
            return new
            {
                exitCode = 2,
                notRun = $"Docker isn't running here or the {Image} image isn't on this PC (this check never pulls it; run: docker pull {Image})."
            };

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Limit);
        var token = limit.Token;
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }

        var name = "martlet-supply-check-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var log = new List<string>();
        var output = new Progress(log);
        try
        {
            var archive = Path.Combine(cache, "checkout.tar.gz");
            var count = await ArchiveCheckoutAsync(root, archive, token);
            Step("checkout-archived", true, $"{count} files of this checkout in {HostSupply.Megabytes(new FileInfo(archive).Length)} (as GitHub's source archive)");

            var needs = HostSupply.ReadNeeds(archive);
            var pinned = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "global.json"), token))
                .RootElement.GetProperty("sdk").GetProperty("version").GetString();
            Step("needs-read", needs.DotnetSdk == pinned && needs.Packages.Count > 0,
                $".NET SDK {needs.DotnetSdk} (global.json {pinned}); {needs.Packages.Count} NuGet packages: " +
                string.Join(", ", needs.Packages.Select(p => p.Name["nuget/".Length..^".nupkg".Length])));

            var (created, networkText) = await DockerAsync(["network", "create", "--internal", name], null, TimeSpan.FromSeconds(30), token);
            if (created != 0) throw new InvalidOperationException("docker network create failed: " + networkText.Trim());
            var (started, startText) = await DockerAsync(["run", "-d", "--pull", "never", "--network", name, "--name", name,
                // The gateway keeps its state only on a local Linux filesystem: the home folder is a disposable ext4 volume, not overlayfs.
                "-v", $"{name}:/home/{User}", Image, "sleep", "infinity"], null, TimeSpan.FromSeconds(60), token);
            if (started != 0) throw new InvalidOperationException("docker run failed: " + startText.Trim());
            var address = (await DockerAsync(["container", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", name],
                null, TimeSpan.FromSeconds(30), token)).Output.Trim();
            var (prepared, preparedText) = await DockerAsync(["exec", "-i", name, "bash", "-s"], Fixture(address), TimeSpan.FromSeconds(60), token);
            if (prepared != 0) throw new InvalidOperationException("Preparing the container failed: " + Tail(preparedText));

            var host = new ContainerChannel(name);
            var (_, probe) = await host.RunAsync(HostCheckout.InternetProbe, token);
            Step("host-is-offline", probe.Contains("internet=no") && address.Length > 0,
                $"LAN address {address} on an internal Docker network; HostCheckout.InternetProbe: {string.Join(" ", probe)}");

            var setupEnvironment = $"MARTLET_HOST_ADDRESS={address} MARTLET_HOST_ID=supply-check-host ";
            var online = HostCheckout.Command("--yes setup", setupEnvironment, needs.DotnetSdk, refresh: true, update: false, supplied: false,
                closeStdin: true);
            var (onlineExit, onlineLines) = await host.RunAsync(online, token);
            var onlineText = string.Join("\n", onlineLines);
            Step("online-setup-stops-plainly", onlineExit == 1 && onlineText.Contains("Stopped: git is not installed here") &&
                !onlineText.Contains("martlet-host: not found"),
                $"the online command (no git, no sudo, no internet) ended with exit {onlineExit}: {LastLine(onlineLines)}");

            var supplier = new HostSupplier(Martlet.Desktop.HostSupplies.OpenAsync, cache, output);
            await supplier.SupplyAsync(host, archive, token);
            var sent = log.LastOrDefault(l => l.StartsWith("Sending ", StringComparison.Ordinal)) ?? "nothing sent";
            var (_, stateLines) = await host.RunAsync(HostSupply.StateScript, token);
            var state = HostSupply.ReadState(stateLines);
            Step("supplied", state.Source is { Length: 128 } && state.Files.Keys.Any(k => k.StartsWith("dotnet-sdk-", StringComparison.Ordinal)) &&
                needs.Packages.All(p => state.Files.ContainsKey(p.Name)) && !state.Files.ContainsKey(HostSupply.SourceArchive),
                $"{sent}; ~/Martlet unpacked from this PC's archive; {state.Files.Count} files kept in ~/{HostSupply.RemoteDirectory}");

            var setup = HostCheckout.Command("--yes setup", setupEnvironment, needs.DotnetSdk, refresh: true, update: false, supplied: true,
                closeStdin: true);
            var (setupExit, setupLines) = await host.RunAsync(setup, token);
            var setupText = string.Join("\n", setupLines);
            Step("setup-without-internet", setupExit == 0 && setupText.Contains("Gateway healthy.") && setupText.Contains("Host ready."),
                $"martlet-host --yes setup with MARTLET_SUPPLY: exit {setupExit}; " +
                (setupExit == 0 ? "built the gateway from the files sent, created its identity, gateway healthy" : Tail(setupText)));

            log.Clear();
            await supplier.SupplyAsync(host, archive, token);
            var (_, againLines) = await host.RunAsync(HostSupply.StateScript, token);
            var again = HostSupply.ReadState(againLines);
            Step("again-sends-nothing", log.Any(l => l.Contains("already has the files")) && again.Sdks.Contains(needs.DotnetSdk) &&
                !again.Files.Keys.Any(k => k.StartsWith("dotnet-sdk-", StringComparison.Ordinal)),
                $"second pass: {log.FirstOrDefault(l => l.Contains("already has") || l.StartsWith("Sending ", StringComparison.Ordinal)) ?? "?"}; " +
                $"the installed SDK's archive was removed ({again.Files.Count} files kept)");

            var update = HostCheckout.Command("--yes update", "", needs.DotnetSdk, refresh: true, update: true, supplied: true, closeStdin: true);
            var (updateExit, updateLines) = await host.RunAsync(update, token);
            var updateText = string.Join("\n", updateLines);
            Step("update-without-internet", updateExit == 0 && updateText.Contains("Host updated."),
                $"martlet-host --yes update with MARTLET_SUPPLY: exit {updateExit}; " +
                (updateExit == 0 ? "rebuilt the gateway offline and restarted it" : Tail(updateText)));
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or
            HttpRequestException or JsonException || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            Step("completed", false, error is OperationCanceledException ? $"did not finish within {Limit.TotalMinutes} minutes" : error.Message);
        }
        finally
        {
            try
            {
                await DockerAsync(["rm", "-f", name], null, TimeSpan.FromSeconds(30), CancellationToken.None);
                await DockerAsync(["volume", "rm", "-f", name], null, TimeSpan.FromSeconds(30), CancellationToken.None);
                await DockerAsync(["network", "rm", name], null, TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return new
        {
            exitCode = passed ? 0 : 1,
            report = new { passed, total = steps.Count, image = Image, cache, steps },
            notCovered = new[]
            {
                "Martlet's SSH runner: the check reaches the container with docker exec (HostRemote.SupplyIfOfflineAsync's SSH channel is not run)",
                "systemd: a FIXTURE systemctl runs the gateway unit's ExecStart; a FIXTURE ip reports the container's LAN address",
                "Docker hosts and roles on a computer without internet access (Martlet stops with an explanation instead)"
            }
        };
    }

    /// <summary>Runs as root once: the host account and the FIXTURE tools a container lacks.</summary>
    private static string Fixture(string address) => $$"""
        set -e
        useradd -m -s /bin/bash {{User}} 2>/dev/null || useradd -s /bin/bash {{User}}
        cp -rn /etc/skel/. /home/{{User}}/ 2>/dev/null || true
        chown -R {{User}}:{{User}} /home/{{User}}
        chmod 700 /home/{{User}}
        cat > /usr/local/bin/ip <<'IP'
        #!/bin/sh
        # FIXTURE: the container's LAN address, as iproute2 prints it.
        printf '2: eth0    inet {{address}}/16 brd 0.0.0.0 scope global eth0\n'
        IP
        cat > /usr/local/bin/loginctl <<'LOGIN'
        #!/bin/sh
        # FIXTURE: lingering is on.
        echo Linger=yes
        LOGIN
        cat > /usr/local/bin/systemctl <<'UNIT'
        #!/bin/bash
        # FIXTURE: systemctl --user without systemd; runs the gateway unit's ExecStart in the background, detached like a
        # systemd service (it must not inherit the engine lock on fd 9).
        unit="$HOME/.config/systemd/user/martlet-host-gateway.service"; pid="/tmp/martlet-gateway-$(id -u).pid"
        stop() { [ -f "$pid" ] && kill "$(cat "$pid")" 2>/dev/null; rm -f "$pid"; sleep 1; }
        case " $* " in
          *" is-active "*) exit 3 ;;
          *" stop "*) stop ;;
          *" restart "*|*" start "*)
            stop; line="$(sed -n 's/^ExecStart=//p' "$unit")"; vars="$(sed -n 's/^Environment=//p' "$unit")"
            setsid env $vars $line >/tmp/martlet-gateway.log 2>&1 </dev/null 9>&- & echo $! > "$pid" ;;
        esac
        exit 0
        UNIT
        chmod 755 /usr/local/bin/ip /usr/local/bin/loginctl /usr/local/bin/systemctl
        """.Replace("\r\n", "\n");

    /// <summary>This checkout's tracked and new files (as they are now) in one top-level folder, like GitHub's archive.</summary>
    private static async Task<int> ArchiveCheckoutAsync(string root, string archive, CancellationToken token)
    {
        var (listed, list) = await ProcessAsync("git", ["-C", root, "ls-files", "-z", "--cached", "--others", "--exclude-standard"], null,
            TimeSpan.FromSeconds(60), token);
        if (listed != 0) throw new InvalidOperationException("git ls-files failed: " + list.Trim());
        var (staged, stage) = await ProcessAsync("git", ["-C", root, "ls-files", "-s"], null, TimeSpan.FromSeconds(60), token);
        if (staged != 0) throw new InvalidOperationException("git ls-files -s failed: " + stage.Trim());
        var executable = stage.Split('\n').Where(l => l.StartsWith("100755 ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf('\t') + 1)..].Trim()).ToHashSet(StringComparer.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var count = 0;
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            foreach (var path in list.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                await using var data = File.OpenRead(full);
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                if (executable.Contains(path)) mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "Martlet/" + path) { DataStream = data, Mode = mode }, token);
                count++;
            }
        return count;
    }

    private static string FindCheckout()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var source = output.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Run host_supply_check from a Martlet source checkout's build.");
        var root = Path.GetFullPath(Path.Combine(source, ".."));
        return File.Exists(Path.Combine(root, "global.json")) && File.Exists(Path.Combine(root, "deploy", "host", "martlet-host"))
            ? root : throw new InvalidOperationException($"No Martlet checkout around {source}.");
    }

    private static string LastLine(IReadOnlyList<string> lines) => lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no output)";

    private static string Tail(string text) => text.Length <= 900 ? text.Trim() : "..." + text[^900..].Trim();

    private sealed class Progress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }

    /// <summary>The host account in the container, through docker exec.</summary>
    private sealed class ContainerChannel(string container) : IHostSupplyChannel
    {
        // SSH sessions set HOME and USER (docker exec doesn't), and Ubuntu's pam_umask gives them umask 002 (user private groups).
        private IReadOnlyList<string> Exec(bool input, string command) =>
            ["exec", .. (input ? new[] { "-i" } : []), "-u", User, "-e", $"HOME=/home/{User}", "-e", $"USER={User}", "-e", $"LOGNAME={User}",
                "-w", $"/home/{User}", container, "sh", "-c", "umask 002; " + command];

        public async Task<(int ExitCode, IReadOnlyList<string> Lines)> RunAsync(string command, CancellationToken token)
        {
            var (exit, text) = await DockerAsync(Exec(false, command), null, TimeSpan.FromMinutes(15), token);
            return (exit, text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        public async Task<int> SendAsync(string command, Func<Stream, CancellationToken, Task> write, CancellationToken token) =>
            (await ProcessAsync("docker", Exec(true, command), write, TimeSpan.FromMinutes(15), token)).Exit;
    }

    private static Task<(int Exit, string Output)> DockerAsync(IReadOnlyList<string> arguments, string? input, TimeSpan limit,
        CancellationToken cancellation) =>
        ProcessAsync("docker", arguments, input is null ? null : (stream, token) => stream.WriteAsync(new UTF8Encoding(false).GetBytes(input), token).AsTask(),
            limit, cancellation);

    private static async Task<(int Exit, string Output)> ProcessAsync(string program, IReadOnlyList<string> arguments,
        Func<Stream, CancellationToken, Task>? input, TimeSpan limit, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return (-1, $"{program} was not found."); }
        if (process is null) return (-1, $"{program} did not start.");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(limit);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                if (input is not null) await input(process.StandardInput.BaseStream, timeout.Token);
            }
            catch (IOException) { }
            finally { process.StandardInput.Close(); }
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                if (cancellation.IsCancellationRequested) throw;
                return (-1, $"{program} did not finish in time.");
            }
            return (process.ExitCode, await stdout + await stderr);
        }
    }
}

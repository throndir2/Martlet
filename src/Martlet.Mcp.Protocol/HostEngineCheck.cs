using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Nodes;

namespace Martlet.Mcp;

/// <summary>host_engine_check: runs this checkout's real martlet-host engine (deploy\host\martlet-host) in one disposable
/// ubuntu:24.04 container (no network, never pulled, removed afterwards; it never touches Martlet's own host containers or
/// volumes) and checks that a host makes one change at a time: a change holds the engine lock, read-only commands still
/// run, status names the holder, an automatic run (no terminal, no --yes) stops at once with exit 75 and MARTLET-BUSY,
/// an attended run waits and gives up after MARTLET_LOCK_WAIT, a waiting run continues when the holder dies (SIGKILL), no
/// stale lock remains and the engine journal records it. The desktop's own reader (<see cref="HostEngineBusy"/>) then
/// reads the engine's real busy line.</summary>
internal static class HostEngineCheck
{
    internal const string Image = "ubuntu:24.04";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(100);

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var engine = FindEngine();
        var (found, _) = await DockerAsync(["image", "inspect", "--format", "{{.Id}}", Image], null, TimeSpan.FromSeconds(30), cancellation);
        if (found != 0)
            return new
            {
                exitCode = 2,
                notRun = $"Docker isn't running here or the {Image} image isn't on this PC (this check never pulls it; run: docker pull {Image})."
            };

        var name = "martlet-engine-check-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var script = new StringBuilder()
            .Append("mkdir -p /m/deploy/host\nbase64 -d > /m/deploy/host/martlet-host <<'MARTLET_ENGINE'\n")
            .Append(Convert.ToBase64String(await File.ReadAllBytesAsync(engine, cancellation), Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n"))
            .Append("\nMARTLET_ENGINE\nchmod 755 /m/deploy/host/martlet-host\n")
            .Append(Scenario.Replace("\r\n", "\n"))
            .ToString();
        int exit;
        string output;
        try
        {
            (exit, output) = await DockerAsync(["run", "--rm", "-i", "--pull", "never", "--network", "none", "--name", name, Image,
                "timeout", ((int)Limit.TotalSeconds - 10).ToString(System.Globalization.CultureInfo.InvariantCulture), "bash", "-s"],
                script, Limit, cancellation);
        }
        finally
        {
            // Normally gone already (--rm); this only matters when the run was cut off.
            try { await DockerAsync(["rm", "-f", name], null, TimeSpan.FromSeconds(20), CancellationToken.None); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        var steps = new List<object>();
        var passed = true;
        var automatic = new List<string>();
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var parts = line.Split('\t', 4);
            if (parts is ["STEP", var step, var ok, var detail])
            {
                steps.Add(new { name = step, ok = ok == "1", detail });
                passed &= ok == "1";
            }
            else if (parts is ["OUT", "automatic", var text]) automatic.Add(text);
        }
        var read = HostEngineBusy.Read(HostEngineBusy.ExitCode, automatic);
        var readOk = read?.StartsWith("leaving the Martlet network (", StringComparison.Ordinal) == true &&
            HostEngineBusy.Read(1, automatic) is null && HostEngineBusy.Read(HostEngineBusy.ExitCode, ["Stopped: something else"]) is null;
        steps.Add(new { name = "desktop-reads-busy", ok = readOk, detail = $"HostEngineBusy.Read: {read ?? "(nothing)"}" });
        passed &= readOk;
        const int expected = 10;
        if (steps.Count < expected)
        {
            passed = false;
            steps.Add(new { name = "complete", ok = false, detail = $"{steps.Count} of {expected} steps reported (docker exit {exit}): {Tail(output)}" });
        }
        return new
        {
            exitCode = passed ? 0 : 1,
            report = new { passed, total = steps.Count, image = Image, engine = "deploy/host/martlet-host", steps }
        };
    }

    private static string FindEngine()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var source = output.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Run host_engine_check from a Martlet source checkout's build.");
        var engine = Path.GetFullPath(Path.Combine(source, "..", "deploy", "host", "martlet-host"));
        return File.Exists(engine) ? engine : throw new InvalidOperationException($"deploy\\host\\martlet-host was not found next to {source}.");
    }

    private static string Tail(string text) => text.Length <= 600 ? text.Trim() : "..." + text[^600..].Trim();

    private static async Task<(int Exit, string Output)> DockerAsync(IReadOnlyList<string> arguments, string? input, TimeSpan limit,
        CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return (-1, "docker was not found."); }
        if (process is null) return (-1, "docker did not start.");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(limit);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                if (cancellation.IsCancellationRequested) throw;
                return (-1, "docker did not finish in time.");
            }
            return (process.ExitCode, await stdout + await stderr);
        }
    }

    /// <summary>The scenario, in bash, against the engine in native mode with a fixture setup under /tmp. Each check prints
    /// "STEP name 0|1 detail"; the automatic run's output is echoed as "OUT automatic line" for the desktop's reader.</summary>
    private const string Scenario = """
        set -u
        E=/m/deploy/host/martlet-host
        export HOME=/tmp/h MARTLET_HOST_CONFIG=/tmp/c MARTLET_HOST_DATA=/tmp/d MARTLET_HOST_MODE=native
        unset MARTLET_ASSUME_YES MARTLET_LOCK_WAIT MARTLET_ENGINE_LOCKED
        step() { printf 'STEP\t%s\t%s\t%s\n' "$1" "$2" "$3"; }
        capture() { tr -d '\r' < "$2" | sed 's/\x1b\[[0-9;]*m//g' | sed "s/^/OUT\t$1\t/"; }
        has() { grep -qF -- "$2" "$1"; }
        mkdir -p "$HOME" /tmp/c /tmp/d/private/state /tmp/d/gateway "${E%/*}/roles/fixture-role"
        : > /tmp/d/gateway/Martlet.Gateway.Host.Linux.dll
        printf 'host_id=check-host\nlan_ip=192.168.1.20\nport=9443\n' > /tmp/c/host.env
        printf 'title=Fixture role (FIXTURE, installs nothing)\nrequires=docker\n' > "${E%/*}/roles/fixture-role/role.conf"
        if ! command -v flock >/dev/null; then step flock-present 0 "flock is missing in this image"; exit 0; fi
        step flock-present 1 "$(flock --version 2>&1 | head -n1)"

        # A change that holds the lock and never finishes by itself: network-reset waiting for a typed yes (a console left open).
        mkfifo /tmp/hold.in
        ( exec 3<>/tmp/hold.in; exec "$E" network-reset <&3 >/tmp/hold.out 2>&1 ) &
        HOLDER=$!
        for _ in $(seq 1 100); do [[ -s /tmp/c/engine.holder ]] && break; sleep 0.1; done
        words="$(sed -n 2p /tmp/c/engine.holder 2>/dev/null)"; how="$(sed -n 4p /tmp/c/engine.holder 2>/dev/null)"
        mode="$(stat -c %a /tmp/c/engine.lock 2>/dev/null)"
        [[ "$words" == "leaving the Martlet network" && "$how" == automatic && "$mode" == 600 ]] && ok=1 || ok=0
        step holder-recorded "$ok" "holds the lock: '$words' ($how), engine.lock mode $mode"

        timeout 15 "$E" roles </dev/null >/tmp/roles.out 2>&1; rc=$?
        [[ $rc == 0 ]] && has /tmp/roles.out fixture-role && ok=1 || ok=0
        step read-only-not-blocked "$ok" "martlet-host roles exit $rc while another change runs"

        timeout 15 "$E" status </dev/null >/tmp/status.out 2>&1; rc=$?
        has /tmp/status.out "Busy now: leaving the Martlet network" && ok=1 || ok=0
        step status-names-holder "$ok" "$(grep -m1 -F 'Busy now:' /tmp/status.out || echo "no busy line (exit $rc)")"

        start=$(date +%s)
        timeout 15 "$E" update </dev/null >/tmp/auto.out 2>&1; rc=$?
        took=$(( $(date +%s) - start ))
        [[ $rc == 75 && $took -le 3 ]] && grep -q '^MARTLET-BUSY leaving the Martlet network' /tmp/auto.out && ok=1 || ok=0
        step automatic-run-does-not-queue "$ok" "update without a terminal or --yes: exit $rc after ${took} s, nothing changed"
        capture automatic /tmp/auto.out

        start=$(date +%s)
        MARTLET_LOCK_WAIT=3 timeout 20 "$E" --yes update </dev/null >/tmp/gaveup.out 2>&1; rc=$?
        took=$(( $(date +%s) - start ))
        [[ $rc == 75 && $took -ge 3 ]] && has /tmp/gaveup.out "Waiting for it to finish before updating this host" &&
          grep -q '^MARTLET-BUSY ' /tmp/gaveup.out && ok=1 || ok=0
        step attended-run-waits-then-gives-up "$ok" "--yes update with MARTLET_LOCK_WAIT=3: waited, exit $rc after ${took} s"

        MARTLET_LOCK_WAIT=60 timeout 70 "$E" --yes remove fixture-missing-role </dev/null >/tmp/wait.out 2>&1 &
        WAITER=$!
        for _ in $(seq 1 50); do has /tmp/wait.out "Waiting for it to finish" && break; sleep 0.1; done
        waiting=0; has /tmp/wait.out "Waiting for it to finish before removing fixture-missing-role" && waiting=1
        kill -9 "$HOLDER" 2>/dev/null; wait "$HOLDER" 2>/dev/null
        wait "$WAITER"; rc=$?
        [[ $waiting == 1 && $rc == 1 ]] && has /tmp/wait.out "That finished. Continuing with removing fixture-missing-role." &&
          has /tmp/wait.out "Unknown role 'fixture-missing-role'" && ok=1 || ok=0
        step waiting-run-continues-after-holder-dies "$ok" "holder killed (SIGKILL); the waiting remove continued and ended exit $rc (unknown role)"

        timeout 15 "$E" update </dev/null >/tmp/after.out 2>&1; rc=$?
        [[ $rc != 75 ]] && ! grep -q '^MARTLET-BUSY' /tmp/after.out && has /tmp/after.out "changes the gateway configuration" && ok=1 || ok=0
        step no-stale-lock "$ok" "the next automatic update got past the lock (exit $rc at the fixture's unapproved configuration)"

        log=/tmp/c/logs/engine.log
        has "$log" "busy, stopped without changing anything: leaving the Martlet network" && has "$log" "busy, waiting: leaving the Martlet network" &&
          has "$log" "lock free after waiting" && ok=1 || ok=0
        step journal-records-waits "$ok" "$(grep -c 'busy' "$log" 2>/dev/null || echo 0) busy lines in logs/engine.log"
        """;
}

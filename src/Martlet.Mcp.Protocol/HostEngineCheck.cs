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
/// an attended run waits and gives up after MARTLET_LOCK_WAIT, a waiting run continues when the holder dies (SIGKILL), so
/// does a run without a terminal or --yes that was told to wait (Update hosts now), no
/// stale lock remains and the engine journal records it, and an add whose image build fails says to run it again. The
/// desktop's own reader (<see cref="HostEngineBusy"/>) then reads the engine's real busy line.</summary>
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
        var holder = new List<string>();
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var parts = line.Split('\t', 4);
            if (parts is ["STEP", var step, var ok, var detail])
            {
                steps.Add(new { name = step, ok = ok == "1", detail });
                passed &= ok == "1";
            }
            else if (parts is ["OUT", "automatic", var text]) automatic.Add(text);
            else if (parts is ["OUT", "holder", var waiting]) holder.Add(waiting);
        }
        var read = HostEngineBusy.Read(HostEngineBusy.ExitCode, automatic);
        var readOk = read?.StartsWith("leaving the Martlet network (", StringComparison.Ordinal) == true &&
            HostEngineBusy.Read(1, automatic) is null && HostEngineBusy.Read(HostEngineBusy.ExitCode, ["Stopped: something else"]) is null;
        steps.Add(new { name = "desktop-reads-busy", ok = readOk, detail = $"HostEngineBusy.Read: {read ?? "(nothing)"}" });
        passed &= readOk;
        var holderRead = HostEngineBusy.Read(HostEngineBusy.ExitCode, holder);
        var holderOk = holderRead?.StartsWith("installing chatterbox (martlet-host-add-", StringComparison.Ordinal) == true;
        steps.Add(new { name = "desktop-reads-holder-busy", ok = holderOk, detail = $"HostEngineBusy.Read: {holderRead ?? "(nothing)"}" });
        passed &= holderOk;
        const int expected = 17;
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
        # Update hosts now: no terminal and no --yes like an automatic update, but told to wait (MARTLET_LOCK_WAIT).
        MARTLET_LOCK_WAIT=60 timeout 70 "$E" update </dev/null >/tmp/asked.out 2>&1 &
        ASKED=$!
        for _ in $(seq 1 50); do has /tmp/wait.out "Waiting for it to finish" && has /tmp/asked.out "Waiting for it to finish" && break; sleep 0.1; done
        waiting=0; has /tmp/wait.out "Waiting for it to finish before removing fixture-missing-role" && waiting=1
        asked=0; has /tmp/asked.out "Waiting for it to finish before updating this host" && asked=1
        kill -9 "$HOLDER" 2>/dev/null; wait "$HOLDER" 2>/dev/null
        wait "$WAITER"; rc=$?
        wait "$ASKED"; arc=$?
        [[ $waiting == 1 && $rc == 1 ]] && has /tmp/wait.out "That finished. Continuing with removing fixture-missing-role." &&
          has /tmp/wait.out "Unknown role 'fixture-missing-role'" && ok=1 || ok=0
        step waiting-run-continues-after-holder-dies "$ok" "holder killed (SIGKILL); the waiting remove continued and ended exit $rc (unknown role)"
        [[ $asked == 1 && $arc == 1 ]] && has /tmp/asked.out "That finished. Continuing with updating this host." &&
          has /tmp/asked.out "changes the gateway configuration" && ! grep -q '^MARTLET-BUSY' /tmp/asked.out && ok=1 || ok=0
        step asked-update-waits-then-runs "$ok" "update without a terminal or --yes but with MARTLET_LOCK_WAIT=60 (Update hosts now): waited instead of stopping, then ran (exit $arc at the fixture's unapproved configuration)"

        timeout 15 "$E" update </dev/null >/tmp/after.out 2>&1; rc=$?
        [[ $rc != 75 ]] && ! grep -q '^MARTLET-BUSY' /tmp/after.out && has /tmp/after.out "changes the gateway configuration" && ok=1 || ok=0
        step no-stale-lock "$ok" "the next automatic update got past the lock (exit $rc at the fixture's unapproved configuration)"

        log=/tmp/c/logs/engine.log
        has "$log" "busy, stopped without changing anything: leaving the Martlet network" && has "$log" "busy, waiting: leaving the Martlet network" &&
          has "$log" "lock free after waiting" && ok=1 || ok=0
        step journal-records-waits "$ok" "$(grep -c 'busy' "$log" 2>/dev/null || echo 0) busy lines in logs/engine.log"

        # The Docker method against a fake docker CLI (its state in /tmp/fake; nothing real is touched): setup must not
        # replace the network holder (martlet-host-net) while an engine session (an add) runs in its namespace, and an
        # engine left in a replaced holder's namespace stops at once instead of probing a dead loopback.
        mkdir -p /tmp/fakebin /tmp/fake /var/run
        : > /var/run/docker.sock; : > /tmp/fake/calls; : > /tmp/fake/engine
        printf 'holder-1' > /tmp/fake/holder; printf 'holder-1' > /tmp/fake/engine-net
        cat > /tmp/fakebin/docker <<'FAKE'
        #!/bin/bash
        S=/tmp/fake
        case "$1" in
          info|volume) exit 0 ;;
          ps) [[ -f $S/engine ]] && printf 'e1 martlet-host-add-20261003-021239-9052\n'; exit 0 ;;
          rm|run) echo "$*" >> $S/calls; [[ "$1 $2" == "rm -f" ]] && printf 'holder-2' > $S/holder; exit 0 ;;
          logs) echo "Network holder ready."; exit 0 ;;
          container)
            [[ "$2" == inspect ]] || exit 1
            shift 2; fmt=""; [[ "$1" == -f ]] && fmt="$2"
            case "$fmt" in
              *PortBindings*) echo "192.168.1.20:9443 martlet-host:old" ;;
              *Config.Image*) echo "martlet-host:new" ;;
              *Config.Cmd*) echo "--yes add chatterbox" ;;
              *MARTLET_ENGINE*) echo "container:$(cat $S/engine-net) inner" ;;
              *NetworkMode*) echo "container:$(cat $S/engine-net)" ;;
              *.Id*) cat $S/holder; echo ;;
            esac
            exit 0 ;;
          *) exit 0 ;;
        esac
        FAKE
        chmod 755 /tmp/fakebin/docker
        D() { env PATH="/tmp/fakebin:$PATH" MARTLET_HOST_MODE=docker MARTLET_HOST_ADDRESS=192.168.1.20 MARTLET_HOST_ID=check-host "$@"; }

        D timeout 15 "$E" setup </dev/null >/tmp/holder-auto.out 2>&1; rc=$?
        [[ $rc == 75 ]] && grep -q '^MARTLET-BUSY installing chatterbox (martlet-host-add-' /tmp/holder-auto.out &&
          ! grep -q '^rm ' /tmp/fake/calls && ok=1 || ok=0
        step holder-kept-while-engine-runs "$ok" "automatic setup while an add runs in the holder: exit $rc, holder not replaced ($(grep -c '^rm ' /tmp/fake/calls) removals)"
        capture holder /tmp/holder-auto.out

        D MARTLET_LOCK_WAIT=60 timeout 40 "$E" --yes setup </dev/null >/tmp/holder-wait.out 2>&1 &
        WAITER=$!
        for _ in $(seq 1 60); do has /tmp/holder-wait.out "before replacing the network holder" && break; sleep 0.1; done
        waiting=0; has /tmp/holder-wait.out "This host is busy: installing chatterbox" && ! grep -q '^rm ' /tmp/fake/calls && waiting=1
        rm -f /tmp/fake/engine
        wait "$WAITER"; rc=$?
        order="$(awk '/^rm -f martlet-host-net/{print "remove-holder"} /^run -d --name martlet-host-net /{print "new-holder"}
          /^run --rm .*--name martlet-host-setup-/{print "engine"}' /tmp/fake/calls | tr '\n' ' ')"
        [[ $waiting == 1 && $rc == 0 && "$order" == "remove-holder new-holder engine " ]] &&
          has /tmp/holder-wait.out "That finished. Continuing with setting up this host." && ok=1 || ok=0
        step setup-waits-then-replaces-holder "$ok" "--yes setup waited for the add, then replaced the holder and ran its engine (exit $rc; $order)"

        D MARTLET_ENGINE=inner MARTLET_ENGINE_NAME=martlet-host-remove-check timeout 15 "$E" --yes remove fixture-role </dev/null >/tmp/stranded.out 2>&1; rc=$?
        [[ $rc == 1 ]] && has /tmp/stranded.out "was replaced while this ran" && has /tmp/stranded.out "Nothing was changed" && ok=1 || ok=0
        step stranded-engine-stops "$ok" "engine in holder-1's namespace after holder-2 replaced it: exit $rc, $(grep -m1 -oE 'was replaced[^,]*' /tmp/stranded.out || echo 'no stranded message')"

        printf 'holder-2' > /tmp/fake/engine-net
        D MARTLET_ENGINE=inner MARTLET_ENGINE_NAME=martlet-host-remove-check timeout 15 "$E" --yes remove fixture-role </dev/null >/tmp/attached.out 2>&1; rc=$?
        [[ $rc == 1 ]] && ! has /tmp/attached.out "was replaced" && has /tmp/attached.out "Run 'martlet-host setup' first." && ok=1 || ok=0
        step attached-engine-continues "$ok" "engine in the current holder's namespace passed the check (exit $rc at the fixture's missing setup)"

        # A role whose image build fails (a fake docker whose compose up fails like a pip download that timed out) stops
        # with what to do next instead of only Compose's exit code.
        mkdir -p /tmp/buildfail "${E%/*}/roles/fixture-build"
        printf 'title=Fixture build (FIXTURE, builds nothing)\nrequires=docker\nport=50999\n' > "${E%/*}/roles/fixture-build/role.conf"
        printf 'services: {}\n' > "${E%/*}/roles/fixture-build/compose.yaml"
        cat > /tmp/buildfail/docker <<'FAKE'
        #!/bin/bash
        [[ "$1" == run ]] && { cat > /dev/null; exit 0; }
        [[ "$1" == compose && " $* " == *" up "* ]] || exit 0
        echo "pip._vendor.urllib3.exceptions.ReadTimeoutError: HTTPSConnectionPool(host='pypi.nvidia.com', port=443): Read timed out." >&2
        exit 17
        FAKE
        chmod 755 /tmp/buildfail/docker
        PATH="/tmp/buildfail:$PATH" timeout 30 "$E" --yes add fixture-build </dev/null >/tmp/buildfail.out 2>&1; rc=$?
        [[ $rc == 1 ]] && has /tmp/buildfail.out "Stopped: Building or starting fixture-build failed" &&
          has /tmp/buildfail.out "run 'martlet-host add fixture-build' again" && has "$log" "stopped: Building or starting fixture-build failed" && ok=1 || ok=0
        step build-failure-says-run-again "$ok" "add whose compose up fails (exit 17): exit $rc, $(grep -m1 -oE 'Stopped: Building or starting [^.]*' /tmp/buildfail.out || tail -n1 /tmp/buildfail.out)"
        """;
}

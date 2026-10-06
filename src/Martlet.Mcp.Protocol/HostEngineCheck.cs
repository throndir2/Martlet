using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Nodes;

namespace Martlet.Mcp;

/// <summary>host_engine_check: runs this checkout's real martlet-host engine (deploy\host\martlet-host) in one disposable
/// ubuntu:24.04 container (no network, never pulled, removed afterwards; it never touches Martlet's own host containers or
/// volumes) and checks that a host runs changes side by side and makes only colliding ones wait: a change holds and records
/// its locks, read-only commands still run, status names the holders, an automatic run (no terminal, no --yes) stops at
/// once with exit 75 and MARTLET-BUSY, an attended run waits and gives up after MARTLET_LOCK_WAIT, adds of different roles
/// run side by side while the same role or exclusive group waits, setup and update wait for every change and hold back
/// later ones, a waiting run continues when the holder dies (SIGKILL), so does a run without a terminal or --yes that was
/// told to wait (Update hosts now), no stale lock or record remains and the engine journal records it, an add whose
/// image build fails says to run it again, and a role's variants (like the stt role's whisper and Parakeet engines) keep their
/// own choices, GPU option and prepare step and switch only once the chosen one is prepared. The desktop's own reader
/// (<see cref="HostEngineBusy"/>) then reads the engine's real busy line.</summary>
internal static class HostEngineCheck
{
    internal const string Image = "ubuntu:24.04";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(150);

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
        const int expected = 31;
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

        # A change that holds its locks and never finishes by itself: network-reset waiting for a typed yes (a console left
        # open). It holds engine.lock shared and the gateway lock.
        mkfifo /tmp/hold.in
        ( exec 3<>/tmp/hold.in; exec "$E" network-reset <&3 >/tmp/hold.out 2>&1 ) &
        HOLDER=$!
        for _ in $(seq 1 100); do [[ -s /tmp/c/engine.holder ]] && break; sleep 0.1; done
        words="$(sed -n 2p /tmp/c/engine.holder 2>/dev/null)"; how="$(sed -n 4p /tmp/c/engine.holder 2>/dev/null)"
        mode="$(stat -c %a /tmp/c/engine.lock 2>/dev/null)"
        record="$(ls /tmp/c/engine.holders 2>/dev/null | head -n1)"
        scope="$(sed -n 5p "/tmp/c/engine.holders/$record" 2>/dev/null)"; state="$(sed -n 6p "/tmp/c/engine.holders/$record" 2>/dev/null)"
        [[ "$words" == "leaving the Martlet network" && "$how" == automatic && "$mode" == 600 && "$scope" == gateway && "$state" == running ]] && ok=1 || ok=0
        step holder-recorded "$ok" "holds its locks: '$words' ($how), engine.lock mode $mode, record in engine.holders: $scope, $state"

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

        start=$(date +%s)
        timeout 15 "$E" --yes remove fixture-missing-role </dev/null >/tmp/alongside.out 2>&1; rc=$?
        took=$(( $(date +%s) - start ))
        [[ $rc == 1 && $took -le 3 ]] && has /tmp/alongside.out "Unknown role 'fixture-missing-role'" && ! has /tmp/alongside.out "Waiting for it" &&
          ok=1 || ok=0
        step role-change-runs-alongside "$ok" "--yes remove of a role while network-reset runs: did not wait, exit $rc after ${took} s (unknown role)"

        # A change that already changed the host (a remove that stopped its role) and still has to publish that through the
        # gateway never gives up with MARTLET-BUSY ("nothing was changed"), even past MARTLET_LOCK_WAIT: it keeps waiting.
        mkdir -p "${E%/*}/roles/fixture-pub" /tmp/c/roles /tmp/d/roles/fixture-pub /tmp/nativebin
        printf '#!/bin/sh\nexit 0\n' > /tmp/nativebin/docker; chmod 755 /tmp/nativebin/docker
        printf 'title=Fixture pub (FIXTURE, installs nothing)\nrequires=docker\n' > "${E%/*}/roles/fixture-pub/role.conf"
        printf 'kind=fixture\nendpoint=http://127.0.0.1:50998/\nmodel=fixture-pub\n' > /tmp/c/roles/fixture-pub.role
        : > /tmp/d/roles/fixture-pub/.env
        ( export PATH="/tmp/nativebin:$PATH"; MARTLET_LOCK_WAIT=2 exec timeout 70 "$E" --yes remove fixture-pub </dev/null >/tmp/pub.out 2>&1 ) &
        PUB=$!
        for _ in $(seq 1 50); do has /tmp/pub.out "publishing it through the gateway" && break; sleep 0.1; done
        sleep 4
        pubwaiting=0; kill -0 "$PUB" 2>/dev/null && has /tmp/pub.out "publishing it through the gateway" && ! grep -q '^MARTLET-BUSY' /tmp/pub.out && pubwaiting=1

        MARTLET_LOCK_WAIT=60 timeout 70 "$E" --yes machine </dev/null >/tmp/wait.out 2>&1 &
        WAITER=$!
        for _ in $(seq 1 50); do has /tmp/wait.out "Waiting for it to finish" && break; sleep 0.1; done
        # Update hosts now: no terminal and no --yes like an automatic update, but told to wait (MARTLET_LOCK_WAIT).
        MARTLET_LOCK_WAIT=60 timeout 70 "$E" update </dev/null >/tmp/asked.out 2>&1 &
        ASKED=$!
        for _ in $(seq 1 50); do has /tmp/asked.out "Waiting for it to finish" && break; sleep 0.1; done
        waiting=0; has /tmp/wait.out "This host is busy: leaving the Martlet network (" &&
          has /tmp/wait.out "Waiting for it to finish before collecting the hardware report" && waiting=1
        asked=0; has /tmp/asked.out "Waiting for it to finish before updating this host" && asked=1
        kill -9 "$HOLDER" 2>/dev/null; wait "$HOLDER" 2>/dev/null
        wait "$WAITER"; rc=$?
        wait "$PUB"; prc=$?
        wait "$ASKED"; arc=$?
        [[ $pubwaiting == 1 && $prc != 75 && ! -e /tmp/c/roles/fixture-pub.role ]] && has /tmp/pub.out "That finished. Continuing with removing fixture-pub." &&
          ! grep -q '^MARTLET-BUSY' /tmp/pub.out && ok=1 || ok=0
        step changed-host-never-claims-nothing-changed "$ok" "a remove that stopped its role waited past MARTLET_LOCK_WAIT=2 to publish (still waiting after 4 s, no MARTLET-BUSY), then unpublished it (exit $prc at the fixture's missing gateway)"
        [[ $waiting == 1 && $rc == 0 ]] && has /tmp/wait.out "That finished. Continuing with collecting the hardware report." && ok=1 || ok=0
        step gateway-change-waits-then-runs "$ok" "machine (restarts the gateway) waited for network-reset's gateway lock; holder killed (SIGKILL), it continued and ended exit $rc"
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

        # Role changes side by side. A fake docker CLI (/tmp/nativebin; every call succeeds, nothing real runs) gets adds past
        # their requirements; each fixture role then asks to accept its terms and, with nobody answering (a fifo), stays there.
        mkdir -p /tmp/nativebin
        printf '#!/bin/sh\nexit 0\n' > /tmp/nativebin/docker; chmod 755 /tmp/nativebin/docker
        for r in fixture-a fixture-b voice-x voice-y; do
          mkdir -p "${E%/*}/roles/$r"
          printf 'title=Fixture %s (FIXTURE, installs nothing)\nrequires=docker\nterms=FIXTURE terms of %s\n' "$r" "$r" > "${E%/*}/roles/$r/role.conf"
        done
        printf 'exclusive=fixture-voice\n' >> "${E%/*}/roles/voice-x/role.conf"
        printf 'exclusive=fixture-voice\n' >> "${E%/*}/roles/voice-y/role.conf"
        held() { mkfifo "/tmp/$1.in"; ( export PATH="/tmp/nativebin:$PATH"; exec 3<>"/tmp/$1.in"; exec "$E" add "$1" <&3 >"/tmp/$1.out" 2>&1 ) & }
        later() { local out="$1"; shift; ( export PATH="/tmp/nativebin:$PATH"; MARTLET_LOCK_WAIT=60 exec timeout 70 "$E" --yes "$@" </dev/null >"$out" 2>&1 ) & }
        held fixture-a; A=$!
        held fixture-b; B=$!
        for _ in $(seq 1 100); do has /tmp/fixture-a.out "FIXTURE terms of fixture-a" && has /tmp/fixture-b.out "FIXTURE terms of fixture-b" && break; sleep 0.1; done
        PATH="/tmp/nativebin:$PATH" timeout 15 "$E" status </dev/null >/tmp/status2.out 2>&1
        busy="$(grep -m1 -F 'Busy now:' /tmp/status2.out || echo 'no busy line')"
        has /tmp/fixture-a.out "FIXTURE terms of fixture-a" && has /tmp/fixture-b.out "FIXTURE terms of fixture-b" &&
          ! grep -q 'Waiting for it' /tmp/fixture-a.out /tmp/fixture-b.out && [[ "$busy" == *"installing fixture-a ("* && "$busy" == *"installing fixture-b ("* ]] && ok=1 || ok=0
        step role-changes-run-side-by-side "$ok" "two adds of different roles both reached their terms, neither waited; $busy"

        held voice-x; V=$!
        for _ in $(seq 1 100); do has /tmp/voice-x.out "FIXTURE terms of voice-x" && break; sleep 0.1; done
        later /tmp/same.out remove fixture-a; SAME=$!
        later /tmp/group.out add voice-y; GROUP=$!
        for _ in $(seq 1 100); do has /tmp/same.out "Waiting for it" && has /tmp/group.out "Waiting for it" && break; sleep 0.1; done
        has /tmp/same.out "This host is busy: installing fixture-a (" && has /tmp/same.out "Waiting for it to finish before removing fixture-a" && ok=1 || ok=0
        step same-role-waits "$ok" "$(grep -m1 -F 'This host is busy' /tmp/same.out || echo 'remove fixture-a did not wait')"
        has /tmp/group.out "This host is busy: installing voice-x (" && has /tmp/group.out "Waiting for it to finish before installing voice-y" && ok=1 || ok=0
        step same-group-waits "$ok" "$(grep -m1 -F 'This host is busy' /tmp/group.out || echo 'add voice-y did not wait for voice-x (exclusive=fixture-voice)')"

        PATH="/tmp/nativebin:$PATH" timeout 15 "$E" update </dev/null >/tmp/auto2.out 2>&1; rc=$?
        busy="$(grep -m1 '^MARTLET-BUSY ' /tmp/auto2.out || true)"
        [[ $rc == 75 && "$busy" == *"installing fixture-a ("* && "$busy" == *"installing fixture-b ("* && "$busy" == *"installing voice-x ("* ]] && ok=1 || ok=0
        step update-waits-for-changes-running "$ok" "automatic update while three adds run: exit $rc; ${busy:-no MARTLET-BUSY line}"

        later /tmp/w.out update; W=$!
        for _ in $(seq 1 100); do has /tmp/w.out "Waiting for it to finish before updating this host" && break; sleep 0.1; done
        later /tmp/late.out remove fixture-late; LATE=$!
        for _ in $(seq 1 100); do has /tmp/late.out "Waiting for it" && break; sleep 0.1; done
        has /tmp/late.out "This host is busy: updating this host (waiting for the changes running now" && ok=1 || ok=0
        step later-change-waits-for-update "$ok" "$(grep -m1 -F 'This host is busy' /tmp/late.out || echo 'remove fixture-late did not wait for the waiting update')"

        kill -9 "$A" "$B" "$V" 2>/dev/null; wait "$A" "$B" "$V" 2>/dev/null
        wait "$SAME"; src=$?; wait "$GROUP"; grc=$?; wait "$W"; wrc=$?; wait "$LATE"; lrc=$?
        has /tmp/same.out "That finished. Continuing with removing fixture-a." && has /tmp/same.out "Role fixture-a is not installed." &&
          has /tmp/group.out "That finished. Continuing with installing voice-y." &&
          has /tmp/w.out "That finished. Continuing with updating this host." && has /tmp/late.out "That finished. Continuing with removing fixture-late." &&
          has /tmp/late.out "Unknown role 'fixture-late'" && ok=1 || ok=0
        step waiting-changes-continue "$ok" "adds killed (SIGKILL): remove fixture-a (exit $src), add voice-y (exit $grc), update (exit $wrc), then remove fixture-late (exit $lrc) each continued"

        PATH="/tmp/nativebin:$PATH" timeout 15 "$E" status </dev/null >/tmp/status3.out 2>&1
        left="$(ls /tmp/c/engine.holders 2>/dev/null | wc -l)"
        [[ "$left" == 0 ]] && ! has /tmp/status3.out "Busy now:" && ok=1 || ok=0
        step ended-changes-leave-no-records "$ok" "$left records left in engine.holders after finished and killed changes; status $(grep -c 'Busy now:' /tmp/status3.out) busy lines"

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

        # Role variants (like the stt role's whisper and Parakeet engines): a variant's own choices, GPU option and prepare step
        # apply only when it is chosen, describe lists them with their condition, and switching keeps the previous variant
        # running until the chosen one is prepared. A fake docker records each compose call with its profile and stops at up -d.
        mkdir -p /tmp/variant "${E%/*}/roles/fixture-variant"
        printf '%s\n' 'title=Fixture variant (FIXTURE, installs nothing)' requires=docker port=50997 \
          'choice=FX_ENGINE|Engine|alpha beta|alpha' model_from=FX_MODEL profile_from=FX_ENGINE profile_legacy=alpha \
          '[FX_ENGINE=alpha]' 'gpu=optional|compose.gpu.yaml' 'choice=FX_MODEL|Alpha model|a1 a2|a1' 'choice_by_vram=FX_MODEL|0@a1 4000@a2' \
          prepare=alpha '[end]' '[FX_ENGINE=beta]' 'choice=FX_MODEL|Beta model|b1 b2|b1' prepare=beta '[end]' > "${E%/*}/roles/fixture-variant/role.conf"
        printf 'services: {}\n' > "${E%/*}/roles/fixture-variant/compose.yaml"
        printf 'services: {}\n' > "${E%/*}/roles/fixture-variant/compose.gpu.yaml"
        cat > /tmp/variant/docker <<'FAKE'
        #!/bin/bash
        [[ "$1" == run ]] && { cat > /dev/null; exit 0; }
        [[ "$1" == compose && "$2" != version ]] || exit 0
        a="$*"; echo "${COMPOSE_PROFILES:-}|${a#*compose.yaml }" >> /tmp/variant/calls
        [[ " $* " == *" up -d "* ]] && exit 17
        exit 0
        FAKE
        chmod 755 /tmp/variant/docker
        V() { : > /tmp/variant/calls; printf '%s\n' "$@" end | PATH="/tmp/variant:$PATH" timeout 30 "$E" --yes add fixture-variant >/tmp/variant/out 2>&1; }
        venv() { grep -E '^(FX_|COMPOSE_PROFILES|MARTLET_ACCELERATOR)' /tmp/d/roles/fixture-variant/.env | tr '\n' ' '; }
        calls() { tr '\n' ';' < /tmp/variant/calls; }

        PATH="/tmp/variant:$PATH" timeout 15 "$E" describe fixture-variant </dev/null >/tmp/variant/describe 2>&1
        has /tmp/variant/describe 'role.choice=FX_ENGINE|Engine|alpha beta|alpha' &&
          has /tmp/variant/describe 'role.choice_when=FX_ENGINE=alpha|FX_MODEL|Alpha model|a1 a2|a1' &&
          has /tmp/variant/describe 'role.choice_when=FX_ENGINE=beta|FX_MODEL|Beta model|b1 b2|b1' &&
          has /tmp/variant/describe 'role.suggested_when=FX_ENGINE=alpha|FX_MODEL' && has /tmp/variant/describe 'role.gpu_when=FX_ENGINE=alpha' &&
          has /tmp/variant/describe 'role.accelerator=gpu cpu' && ! grep -q '^role.choice=FX_MODEL' /tmp/variant/describe && ok=1 || ok=0
        step variant-describe "$ok" "describe lists $(grep -c '_when=' /tmp/variant/describe) variant entries: $(grep -oE '^role\.[a-z_]+_when=[^|]*' /tmp/variant/describe | sort -u | tr '\n' ' ')"

        V choice.FX_ENGINE=beta choice.accelerator=gpu choice.FX_MODEL=b2
        env="$(venv)"; overlay=none; [[ -e /tmp/d/roles/fixture-variant/compose.gpu.yaml ]] && overlay=added
        [[ "$env" == "FX_ENGINE=beta FX_MODEL=b2 COMPOSE_PROFILES=beta " && $overlay == none ]] &&
          [[ "$(calls)" == "beta|run --rm --no-deps -T -e MARTLET_PREPARE=1 beta;beta|up -d;" ]] && ok=1 || ok=0
        step variant-own-choices "$ok" "add with engine beta (and an ignored gpu answer): $env; GPU overlay $overlay; $(calls)"

        sed -i '/^COMPOSE_PROFILES=/d; s/^FX_ENGINE=beta/FX_ENGINE=alpha/' /tmp/d/roles/fixture-variant/.env
        V choice.FX_ENGINE=beta
        [[ "$(calls)" == "beta|run --rm --no-deps -T -e MARTLET_PREPARE=1 beta;beta|up --no-start;alpha|down;beta|up -d;" ]] &&
          has /tmp/variant/out "while alpha still runs" && ok=1 || ok=0
        step variant-switch-after-prepare "$ok" "an install from before the role had variants (profile_legacy alpha) moved to beta: $(calls)"

        # Run again after that switch stopped at up -d (as a failed build or download would): .env names beta already, yet the
        # other variant is still stopped before beta starts, so a leftover alpha never keeps the role's port.
        V choice.FX_ENGINE=beta
        [[ "$(calls)" == "beta|run --rm --no-deps -T -e MARTLET_PREPARE=1 beta;alpha|down;beta|up -d;" ]] && ok=1 || ok=0
        step variant-retry-stops-the-other "$ok" "the same switch run again: $(calls)"

        V choice.FX_ENGINE=alpha choice.FX_MODEL=b1; refused=0; has /tmp/variant/out "Choose one of: a1 a2" && refused=1
        V choice.FX_ENGINE=alpha
        [[ $refused == 1 && "$(venv)" == "FX_ENGINE=alpha MARTLET_ACCELERATOR=cpu FX_MODEL=a1 COMPOSE_PROFILES=alpha " ]] &&
          [[ "$(calls)" == "alpha|run --rm --no-deps -T -e MARTLET_PREPARE=1 alpha;alpha|up --no-start;beta|down;alpha|up -d;" ]] && ok=1 || ok=0
        step variant-gpu-and-suggestion "$ok" "a beta model for alpha refused: $refused; alpha automatic on this GPU-less host: $(venv); $(calls)"
        """;
}

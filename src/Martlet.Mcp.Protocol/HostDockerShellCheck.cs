using System.Text;
using Martlet.Core.Nodes;

namespace Martlet.Mcp;

/// <summary>host_docker_shell_check: runs the production shell that finds Docker and builds the host image on a Docker host
/// over SSH (<see cref="HostCheckout.Docker"/> and <see cref="HostCheckout.DockerImage"/>, as the desktop's DockerShell joins
/// them) in one disposable ubuntu:24.04 container shaped like a NAS: Docker is only on the login shell's PATH
/// (/etc/profile.d) and there is no git. FIXTURE docker and curl CLIs stand in for Docker and GitHub (no network, never
/// pulled, removed afterwards).</summary>
internal static class HostDockerShellCheck
{
    private const string Image = HostEngineCheck.Image;
    private const string Version = "1.2.3";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var (found, _) = await HostSupplyCheck.DockerAsync(["image", "inspect", "--format", "{{.Id}}", Image], null, TimeSpan.FromSeconds(30), cancellation);
        if (found != 0)
            return new
            {
                exitCode = 2,
                notRun = $"Docker isn't running here or the {Image} image isn't on this PC (this check never pulls it; run: docker pull {Image})."
            };

        var image = $"martlet-host:{Version}";
        var command = HostCheckout.Docker(batch: false) + $"({HostCheckout.DockerImage("$D", image, Version)}) && $D run --rm -i {image} --yes status";
        var script = new StringBuilder()
            .Append("cat > /fixture-find <<'MARTLET_FIND'\n").Append(HostCheckout.DockerPath).Append("command -v docker\nMARTLET_FIND\n")
            .Append("cat > /fixture-run <<'MARTLET_RUN'\n").Append(command).Append("\nMARTLET_RUN\n")
            .Append(Scenario.Replace("\r\n", "\n"));
        var name = "martlet-docker-shell-check-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        int exit;
        string output;
        try
        {
            (exit, output) = await HostSupplyCheck.DockerAsync(["run", "--rm", "-i", "--pull", "never", "--network", "none", "--name", name, Image,
                "timeout", ((int)Limit.TotalSeconds - 10).ToString(System.Globalization.CultureInfo.InvariantCulture), "bash", "-s"],
                script.ToString(), Limit, cancellation);
        }
        finally
        {
            try { await HostSupplyCheck.DockerAsync(["rm", "-f", name], null, TimeSpan.FromSeconds(20), CancellationToken.None); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        var steps = new List<object>();
        var passed = true;
        foreach (var parts in output.Split('\n').Select(l => l.TrimEnd('\r').Split('\t', 4)))
        {
            if (parts is not ["STEP", var step, var ok, var detail]) continue;
            steps.Add(new { name = step, ok = ok == "1", detail });
            passed &= ok == "1";
        }
        const int expected = 6;
        if (steps.Count < expected)
        {
            passed = false;
            steps.Add(new { name = "complete", ok = false, detail = $"{steps.Count} of {expected} steps reported (docker exit {exit}): {output.Trim()}" });
        }
        return new
        {
            exitCode = passed ? 0 : 1,
            report = new { passed, total = steps.Count, image = Image, command, steps },
            notCovered = new[]
            {
                "Martlet's SSH runner and a real NAS: the commands run with sh -c in a container with the plain PATH of an SSH command",
                "a real docker build: a FIXTURE docker checks the Dockerfile and the unpacked source, and a FIXTURE curl serves the archive"
            }
        };
    }

    /// <summary>Runs as root in the container. Each STEP line is: STEP, name, 1 or 0, detail (tab-separated).</summary>
    private const string Scenario = """
        set -u
        step() { printf 'STEP\t%s\t%s\t%s\n' "$1" "$2" "$3"; }
        plain() { env -i HOME=/root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin sh -c "$(cat "$1")" 2>&1; }
        mkdir -p /opt/nas/bin /fixture/Martlet-main/deploy/host /fixture/Martlet-main/src /var/log/fixture
        echo 'FROM scratch' > /fixture/Martlet-main/deploy/host/Dockerfile
        touch /fixture/Martlet-main/src/Martlet.slnx
        tar -czf /fixture/main.tar.gz -C /fixture Martlet-main
        # FIXTURE docker, only on a login shell's PATH (like TerraMaster TOS): records each call; build checks the Dockerfile and
        # the unpacked source; image inspect finds the image once it was built.
        cat > /opt/nas/bin/docker <<'DOCKER'
        #!/bin/sh
        echo "$*" >> /var/log/fixture/docker
        case "$1" in
          info) exit 0 ;;
          image) [ -e /fixture/has-image ] ;;
          build) shift; context=; file=; while [ $# -gt 0 ]; do case "$1" in -f) file=$2; shift ;; -t) shift ;; *) context=$1 ;; esac; shift; done
                 [ -f "$file" ] && [ -f "$context/src/Martlet.slnx" ] && echo "built from $context" >> /var/log/fixture/docker && touch /fixture/has-image ;;
          run) echo 'engine ran' ;;
        esac
        DOCKER
        echo 'PATH=$PATH:/opt/nas/bin' > /etc/profile.d/nas-docker.sh
        # FIXTURE curl: GitHub has no archive for the release tag here (404), and main's archive unless the host is offline.
        cat > /usr/local/bin/curl <<'CURL'
        #!/bin/sh
        for url; do :; done
        echo "$url" >> /var/log/fixture/curl
        case "$url" in */heads/main.tar.gz) [ -e /fixture/offline ] && exit 6; cat /fixture/main.tar.gz ;; *) exit 22 ;; esac
        CURL
        chmod 755 /opt/nas/bin/docker /usr/local/bin/curl

        if ! env -i PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin sh -c 'command -v docker' >/dev/null &&
           ! command -v git >/dev/null; then step plain-shell-has-no-docker 1 'an SSH command shell finds no docker, and there is no git'
        else step plain-shell-has-no-docker 0 'the container already has docker on the plain PATH, or git'; fi

        found=$(plain /fixture-find | tail -n 1)
        [ "$found" = /opt/nas/bin/docker ] && step docker-found-on-login-path 1 "HostCheckout.DockerPath finds $found" ||
          step docker-found-on-login-path 0 "found: ${found:-nothing}"

        out=$(plain /fixture-run); code=$?
        built=$(sed -n 's/^build -t martlet-host:1.2.3 -f \(.*\)\/deploy\/host\/Dockerfile \(.*\)$/\1 \2/p' /var/log/fixture/docker)
        dir=${built%% *}
        if [ $code = 0 ] && [ -n "$dir" ] && [ "$built" = "$dir $dir" ] && grep -q "^built from $dir$" /var/log/fixture/docker &&
           [ "$(cat /var/log/fixture/curl | tr '\n' ' ')" = 'https://github.com/throndir2/Martlet/archive/refs/tags/v1.2.3.tar.gz https://github.com/throndir2/Martlet/archive/refs/heads/main.tar.gz ' ] &&
           echo "$out" | grep -q 'Building the host from the source archive of Martlet 1.2.3.' && echo "$out" | grep -q '^engine ran$'
        then step builds-from-source-archive 1 "no git: the tag's archive is missing, so main's archive was unpacked in $dir and built, then the engine ran"
        else step builds-from-source-archive 0 "exit $code; docker: $(tr '\n' '|' < /var/log/fixture/docker) curl: $(tr '\n' '|' < /var/log/fixture/curl 2>/dev/null) output: $(echo "$out" | tr '\n' '|')"; fi

        [ -n "$dir" ] && [ ! -e "$dir" ] && step source-folder-removed 1 "$dir is gone after the build" ||
          step source-folder-removed 0 "the unpacked source is still there: ${dir:-unknown}"

        : > /var/log/fixture/docker; : > /var/log/fixture/curl
        out=$(plain /fixture-run); code=$?
        [ $code = 0 ] && ! grep -q '^build' /var/log/fixture/docker && [ ! -s /var/log/fixture/curl ] && echo "$out" | grep -q '^engine ran$' &&
          step image-there-builds-nothing 1 'with the image there, nothing is downloaded or built and the engine runs' ||
          step image-there-builds-nothing 0 "exit $code; docker: $(tr '\n' '|' < /var/log/fixture/docker)"

        rm -f /fixture/has-image; touch /fixture/offline; : > /var/log/fixture/docker
        out=$(plain /fixture-run); code=$?
        [ $code != 0 ] && ! grep -q '^build' /var/log/fixture/docker && ! echo "$out" | grep -q 'engine ran' &&
          step download-fails-stops 1 "without the archive nothing is built and the engine doesn't run (exit $code)" ||
          step download-fails-stops 0 "exit $code; docker: $(tr '\n' '|' < /var/log/fixture/docker) output: $(echo "$out" | tr '\n' '|')"
        """;
}

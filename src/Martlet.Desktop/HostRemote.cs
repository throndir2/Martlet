using System.Text.RegularExpressions;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Desktop;

/// <summary>What Martlet found on a Linux computer before changing anything there.</summary>
internal sealed record HostProbe(bool Docker, bool DockerAccess, string Sudo, string? OperatingSystem, string? Architecture,
    string? Address, string? Hostname)
{
    internal bool SudoNeedsPassword => Sudo == "password";
}

/// <summary>A role's inputs as the host's role.conf declares them (martlet-host describe).</summary>
internal sealed record HostRoleInputs(string Title, string Requires, string Terms, bool Installed,
    IReadOnlyList<HostRoleSecret> Secrets, IReadOnlyList<HostRoleChoice> Choices);

internal sealed record HostRoleSecret(string Name, string Prompt, bool Stored);

internal sealed record HostRoleChoice(string Variable, string Label, IReadOnlyList<string> Options, string Default);

/// <summary>Drives martlet-host on SSH hosts through <see cref="HostShell"/>: probe, setup, pair, roles and status, all
/// unattended (--yes). The owner's click in Martlet is the confirmation; secrets and choices go over stdin.</summary>
internal sealed partial class HostRemote(HostShell shell)
{
    internal HostShell Shell => shell;

    [GeneratedRegex(@"martlet-pair-v1\.[A-Za-z0-9_-]+")]
    private static partial Regex PairingCodePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex IdentifierPattern();

    private const string ProbeScript =
        "printf 'probe.docker=%s\\n' \"$(command -v docker >/dev/null 2>&1 && echo yes || echo no)\"; " +
        "printf 'probe.docker_access=%s\\n' \"$(docker info >/dev/null 2>&1 && echo yes || echo no)\"; " +
        "printf 'probe.sudo=%s\\n' \"$(command -v sudo >/dev/null 2>&1 || { echo none; exit 0; }; sudo -n true 2>/dev/null && echo nopassword || echo password)\"; " +
        "printf 'probe.os=%s\\n' \"$( (. /etc/os-release 2>/dev/null && printf '%s' \"$PRETTY_NAME\") || uname -s)\"; " +
        "printf 'probe.arch=%s\\n' \"$(uname -m)\"; " +
        "printf 'probe.address=%s\\n' \"$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \\([0-9.]*\\).*/\\1/p')\"; " +
        "printf 'probe.hostname=%s\\n' \"$(hostname -s 2>/dev/null || hostname)\"";

    /// <summary>Connects (asking for the password and host key trust the first time) and reads what the computer has.</summary>
    internal async Task<(HostProbe Probe, string HostKey)> ProbeAsync(HostShellTarget target, string? pinnedHostKey, CancellationToken token)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var sink = new LineSink(line =>
        {
            var split = line.IndexOf('=');
            if (line.StartsWith("probe.", StringComparison.Ordinal) && split > 0) values[line[6..split]] = line[(split + 1)..].Trim();
        });
        var result = await shell.RunAsync(target, new() { Command = ProbeScript, PinnedHostKey = pinnedHostKey }, sink, token);
        string? Value(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        var address = Value("address");
        return (new HostProbe(Value("docker") == "yes", Value("docker_access") == "yes", Value("sudo") ?? "none", Value("os"),
            Value("arch"), HostSetupCommands.IsPrivate(address) ? address : null, Value("hostname")), result.HostKey);
    }

    /// <summary>Whether a martlet-host run needs sudo: Docker hosts only when this account cannot use Docker directly;
    /// native Ubuntu may install packages or enable lingering.</summary>
    internal static bool NeedsSudo(HostSetupMethod method, HostProbe probe) =>
        method == HostSetupMethod.SshNative ? probe.Sudo != "none" : !probe.DockerAccess && probe.Sudo != "none";

    /// <summary>Plain-language reason a computer cannot be set up with this method yet, or null.</summary>
    internal static string? Blocker(HostSetupMethod method, HostProbe probe, string target) => method switch
    {
        HostSetupMethod.SshDocker when !probe.Docker =>
            $"Docker is not installed on {target}. Docker is the one thing a Linux computer needs before Martlet can set it up: " +
            "install Docker Engine there (on Ubuntu: sudo apt-get install docker.io docker-compose-v2), then press Add this computer again.",
        HostSetupMethod.SshDocker when !probe.DockerAccess && probe.Sudo == "none" =>
            $"The account on {target} cannot use Docker and has no sudo. Add it to the docker group (sudo usermod -aG docker <user>) and try again.",
        HostSetupMethod.SshNative when probe.OperatingSystem?.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) != true =>
            $"{target} runs {probe.OperatingSystem ?? "an unknown system"}; the native method needs Ubuntu. Choose Docker instead.",
        _ when probe.Architecture is { } arch && arch != "x86_64" =>
            $"{target} is {arch}; Martlet hosts need an x86_64 (64-bit Intel/AMD) computer.",
        _ => null
    };

    /// <summary>Runs one martlet-host command unattended and streams its output. <paramref name="input"/> carries answers
    /// (secret.&lt;name&gt;=..., choice.&lt;VAR&gt;=...); it is ended with the "end" line here.</summary>
    internal async Task<HostShellResult> RunAsync(HostSetupTarget target, string engine, bool setup, bool sudo,
        IReadOnlyDictionary<string, string>? input, string? pinnedHostKey, IProgress<string> output, CancellationToken token,
        Task<string?>? moreInput = null)
    {
        var ssh = HostShellTarget.Parse(target.SshTarget);
        var text = string.Concat((input ?? new Dictionary<string, string>()).Select(pair =>
        {
            if (pair.Value.Contains('\n') || pair.Value.Contains('\r')) throw new InvalidOperationException("Answers must be a single line.");
            return $"{pair.Key}={pair.Value}\n";
        })) + "end\n";
        output.Report($"$ martlet-host {engine}  (on {ssh}, {(target.Method == HostSetupMethod.SshDocker ? "Docker" : "native Ubuntu")})");
        return await shell.RunAsync(ssh, new()
        {
            Command = HostSetupCommands.RemoteShell(target, engine, setup), Input = text, MoreInput = moreInput, Sudo = sudo,
            PinnedHostKey = pinnedHostKey
        }, output, token);
    }

    /// <summary>Pairs this PC with an SSH host without any console: runs "pair --device-id ... --name ..." there, reads the
    /// one-use code from its output (never shown), redeems it with the same client as the Pair button and lets the host
    /// restart its gateway.</summary>
    internal async Task<(Audio2FaceHostPairing Pairing, string Secret, string HostKey)> PairAsync(HostSetupTarget target, string deviceId,
        string name, bool sudo, string? pinnedHostKey, IProgress<string> output, CancellationToken token)
    {
        if (!IdentifierPattern().IsMatch(deviceId)) throw new InvalidOperationException("Invalid device ID for this PC.");
        var label = new string(name.Where(c => c is >= ' ' and <= '~' and not '\'').Take(64).ToArray()).Trim();
        if (label.Length == 0 || label[0] == '-') label = "Martlet desktop";
        var withdraw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(Audio2FaceHostPairing Pairing, string Secret)>? pairing = null;
        var sink = new LineSink(line =>
        {
            var match = PairingCodePattern().Match(line);
            if (!match.Success)
            {
                output.Report(line);
                return;
            }
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

        HostShellResult result;
        try
        {
            result = await RunAsync(target, $"pair --device-id {deviceId} --name {HostShell.Quote(label)}", false, sudo, null,
                pinnedHostKey, sink, token, withdraw.Task);
        }
        finally { withdraw.TrySetResult(null); }
        if (pairing is null)
            throw new InvalidOperationException($"{target.SshTarget} did not show a pairing code (exit {result.ExitCode}). See the output.");
        var (paired, secret) = await pairing;
        if (result.ExitCode != 0)
            output.Report($"Paired, but the host reported exit {result.ExitCode} while restarting its gateway. Check host status.");
        return (paired, secret, result.HostKey);
    }

    /// <summary>Reads a role's terms, secrets and choices from the host (martlet-host describe).</summary>
    internal async Task<HostRoleInputs> DescribeAsync(HostSetupTarget target, string role, bool sudo, string? pinnedHostKey,
        IProgress<string> output, CancellationToken token)
    {
        var lines = new List<string>();
        var sink = new LineSink(line =>
        {
            if (line.StartsWith("role.", StringComparison.Ordinal)) lines.Add(line);
            else output.Report(line);
        });
        var result = await RunAsync(target, "describe " + role, false, sudo, null, pinnedHostKey, sink, token);
        if (result.ExitCode != 0 || lines.Count == 0)
            throw new InvalidOperationException($"Could not read the {role} role from {target.SshTarget} (exit {result.ExitCode}). See the output.");
        return ParseRole(lines);
    }

    internal static HostRoleInputs ParseRole(IEnumerable<string> lines)
    {
        string title = "", requires = "", terms = "";
        var installed = false;
        var secrets = new List<HostRoleSecret>();
        var choices = new List<HostRoleChoice>();
        foreach (var line in lines)
        {
            var split = line.IndexOf('=');
            if (split < 0) continue;
            var value = line[(split + 1)..];
            switch (line[..split])
            {
                case "role.title": title = value; break;
                case "role.requires": requires = value; break;
                case "role.terms": terms = value; break;
                case "role.installed": installed = value == "yes"; break;
                case "role.secret" when value.Split('|') is [var name, .. var middle, var state] && middle.Length > 0:
                    secrets.Add(new(name, string.Join('|', middle), state == "stored"));
                    break;
                case "role.choice" when value.Split('|') is [var variable, var label, var options, var fallback]:
                    choices.Add(new(variable, label, options.Split(' ', StringSplitOptions.RemoveEmptyEntries), fallback));
                    break;
            }
        }
        return new(title, requires, terms, installed, secrets, choices);
    }
}

/// <summary>Synchronous progress sink: runs on the runner's reading thread, in order (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class LineSink(Action<string> action) : IProgress<string>
{
    public void Report(string value) => action(value);
}

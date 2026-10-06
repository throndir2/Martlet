using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>What Martlet found on a Linux computer before changing anything there.</summary>
internal sealed record HostProbe(bool Docker, bool DockerAccess, string Sudo, string? OperatingSystem, string? Architecture,
    string? Address, string? Hostname)
{
    internal bool SudoNeedsPassword => Sudo == "password";
}

/// <summary>A role's inputs as the host's role.conf declares them (martlet-host describe). <paramref name="SecretWhen"/> and
/// <paramref name="TermsWhen"/> belong to one variant of the role: they apply only when choice <c>Variable</c> is <c>Value</c>,
/// as do choices with a <see cref="HostRoleChoice.When"/> (a variant's own models) and, with <see cref="GpuWhen"/>, the
/// GPU or CPU option and graphics card (the stt role's whisper engine; Parakeet runs on the CPU).
/// <see cref="Stops"/> names the installed roles adding it stops first (another voice engine: one runs per host).</summary>
internal sealed record HostRoleInputs(string Title, string Requires, string Terms, bool Installed,
    IReadOnlyList<HostRoleSecret> Secrets, IReadOnlyList<HostRoleChoice> Choices, bool GpuOrCpu = false)
{
    public IReadOnlyDictionary<string, (string Variable, string Value)> SecretWhen { get; init; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);
    public IReadOnlyList<(string Variable, string Value, string Text)> TermsWhen { get; init; } = [];
    /// <summary>The variant whose GPU option this is (role.gpu_when), or null when every variant has it.</summary>
    public (string Variable, string Value)? GpuWhen { get; init; }
    public IReadOnlyList<string> Stops { get; init; } = [];
    /// <summary>The host's NVIDIA cards when it has several (role.gpu), so the owner can put this role on one of them.</summary>
    public IReadOnlyList<HostCard> Gpus { get; init; } = [];
    /// <summary>The card the installed role runs on now (a UUID, or "all"), or null.</summary>
    public string? GpuCurrent { get; init; }
    /// <summary>What the installed role runs with now (role.choice_current), by choice variable; empty when it isn't installed
    /// or the host's martlet-host predates reporting it.</summary>
    public IReadOnlyDictionary<string, string> Current { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Whether the installed role runs on the GPU or the CPU now (role.accelerator_current), or null.</summary>
    public string? AcceleratorCurrent { get; init; }
}

/// <summary>One NVIDIA card on a host with several: its UUID, name, memory and the other roles pinned to it.</summary>
internal sealed record HostCard(string Id, string Name, int MemoryMb, IReadOnlyList<string> UsedBy)
{
    internal string Describe() =>
        $"{Name} ({(MemoryMb / 1024d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} GB)" +
        (UsedBy.Count > 0 ? $", used by {string.Join(", ", UsedBy)}" : ", free");
}

internal sealed record HostRoleSecret(string Name, string Prompt, bool Stored);

/// <summary>A role choice; <paramref name="Suggested"/> when the host suggests the default by its GPU memory; <paramref name="When"/>
/// when only one variant has it (choice <c>Variable</c> is <c>Value</c>), such as the stt role's whisper or Parakeet models.</summary>
internal sealed record HostRoleChoice(string Variable, string Label, IReadOnlyList<string> Options, string Default, bool Suggested = false,
    (string Variable, string Value)? When = null)
{
    /// <summary>The dialog field's key: <c>choice.VAR</c>, or <c>choice.VAR@WHEN=VALUE</c> for one variant's (the answer is
    /// still <c>choice.VAR</c>; see <see cref="HostInputDialog.Cleaned"/>).</summary>
    internal string Key => "choice." + Variable + (When is { } when ? $"@{when.Variable}={when.Value}" : "");
}

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
            $"Docker is not installed on {target}. Install Docker Engine there, then try again.",
        HostSetupMethod.SshDocker when !probe.DockerAccess && probe.Sudo == "none" =>
            $"The account on {target} can't use Docker. Use an account with Docker access or sudo.",
        HostSetupMethod.SshNative when probe.OperatingSystem?.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) != true =>
            $"{target} runs {probe.OperatingSystem ?? "an unknown system"}. Choose the Docker method instead.",
        _ when probe.Architecture is { } arch && arch != "x86_64" =>
            $"{target} is not a 64-bit Intel or AMD computer.",
        _ => null
    };

    /// <summary>Runs one martlet-host command unattended and streams its output. <paramref name="input"/> carries answers
    /// (secret.&lt;name&gt;=..., choice.&lt;VAR&gt;=...); it is ended with the "end" line here. <paramref name="supplied"/>:
    /// a native host without internet access that <see cref="SupplyIfOfflineAsync"/> sent its files to.</summary>
    internal async Task<HostShellResult> RunAsync(HostSetupTarget target, string engine, bool setup, bool sudo,
        IReadOnlyDictionary<string, string>? input, string? pinnedHostKey, IProgress<string> output, CancellationToken token,
        Task<string?>? moreInput = null, bool supplied = false)
    {
        var ssh = HostShellTarget.Parse(target.SshTarget);
        var text = string.Concat((input ?? new Dictionary<string, string>()).Select(pair =>
        {
            if (pair.Value.Contains('\n') || pair.Value.Contains('\r')) throw new InvalidOperationException("Answers must be a single line.");
            return $"{pair.Key}={pair.Value}\n";
        })) + "end\n";
        output.Report($"$ martlet-host {engine}  (on {ssh}, {(target.Method == HostSetupMethod.SshDocker ? "Docker" : "native Ubuntu")}" +
            $"{(supplied ? ", files from this PC" : "")})");
        return await shell.RunAsync(ssh, new()
        {
            Command = HostSetupCommands.RemoteShell(target, engine, setup, supplied: supplied), Input = text, MoreInput = moreInput,
            Sudo = sudo, PinnedHostKey = pinnedHostKey
        }, output, token);
    }

    /// <summary>Whether the computer reaches the internet (GitHub over HTTPS within 8 seconds); null when it can't tell.</summary>
    internal async Task<bool?> InternetAsync(HostShellTarget target, string? pinnedHostKey, CancellationToken token)
    {
        string? answer = null;
        var sink = new LineSink(line =>
        {
            if (line.StartsWith("internet=", StringComparison.Ordinal)) answer = line["internet=".Length..].Trim();
        });
        await shell.RunAsync(target, new() { Command = HostCheckout.InternetProbe, PinnedHostKey = pinnedHostKey }, sink, token);
        return answer switch { "yes" => true, "no" => false, _ => null };
    }

    /// <summary>Before a command that downloads (setup, update, add a role): when the computer has no internet access,
    /// sends a native Ubuntu host what setup and update need from this PC (<see cref="HostSupplier"/>, cached under
    /// <paramref name="dataDirectory"/>) and returns true, so the engine runs with those files. What Martlet can't send
    /// yet (Docker hosts, roles) stops with a plain explanation instead of failing halfway.</summary>
    internal async Task<bool> SupplyIfOfflineAsync(HostSetupTarget target, HostVerb verb, string dataDirectory, string? pinnedHostKey,
        IProgress<string> output, CancellationToken token)
    {
        if (target.Method is not (HostSetupMethod.SshNative or HostSetupMethod.SshDocker) ||
            verb is not (HostVerb.Setup or HostVerb.Update or HostVerb.Add)) return false;
        var ssh = HostShellTarget.Parse(target.SshTarget);
        if (await InternetAsync(ssh, pinnedHostKey, token) != false) return false;
        if (OfflineBlocker(target.Method, verb, ssh.ToString()) is { } blocker) throw new InvalidOperationException(blocker);
        output.Report($"{ssh} can't reach the internet, so Martlet downloads what it needs on this PC and sends it over SSH.");
        var supplier = new HostSupplier(HostSupplies.OpenAsync, HostSupplies.Cache(dataDirectory), output);
        try
        {
            var source = await supplier.SourceAsync(target.Version, token);
            await supplier.SupplyAsync(new SupplyChannel(shell, ssh, pinnedHostKey, output), source, token);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or FileNotFoundException ||
            error is TaskCanceledException && !token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"This PC couldn't download what {ssh} needs: {error.Message}", error);
        }
        return true;
    }

    /// <summary>Why a computer without internet access can't run <paramref name="verb"/> yet, or null when Martlet sends
    /// what it needs.</summary>
    internal static string? OfflineBlocker(HostSetupMethod method, HostVerb verb, string target) => (method, verb) switch
    {
        (_, HostVerb.Add) =>
            $"{target} can't reach the internet. Martlet sends a computer without internet access what its gateway needs, " +
            "but not yet the Docker images and models a role needs. Connect it to the internet to add this role.",
        (HostSetupMethod.SshDocker, _) =>
            $"{target} can't reach the internet. Martlet sends what a computer without internet access needs only with the " +
            "native Ubuntu method: choose Another computer over SSH, native Ubuntu install.",
        _ => null
    };

    /// <summary><see cref="HostSupplier"/>'s way to the host: Martlet's SSH runner.</summary>
    private sealed class SupplyChannel(HostShell shell, HostShellTarget target, string? pinnedHostKey, IProgress<string> output)
        : IHostSupplyChannel
    {
        public async Task<(int ExitCode, IReadOnlyList<string> Lines)> RunAsync(string command, CancellationToken token)
        {
            var lines = new List<string>();
            var result = await shell.RunAsync(target, new() { Command = command, PinnedHostKey = pinnedHostKey },
                new LineSink(line => { lock (lines) lines.Add(line); }), token);
            lock (lines) return (result.ExitCode, lines.ToList());
        }

        public async Task<int> SendAsync(string command, Func<Stream, CancellationToken, Task> write, CancellationToken token) =>
            (await shell.RunAsync(target, new() { Command = command, Write = write, PinnedHostKey = pinnedHostKey }, output, token)).ExitCode;
    }

    /// <summary>Pairs this PC with an SSH host without any console: runs "pair --device-id ... --name ..." there, reads the
    /// one-use code from its output (never shown), redeems it with the same client as the Pair button and lets the host
    /// restart its gateway.</summary>
    internal async Task<(Audio2FaceHostPairing Pairing, string Secret, string HostKey)> PairAsync(HostSetupTarget target, string deviceId,
        string name, bool sudo, string? pinnedHostKey, IProgress<string> output, CancellationToken token)
    {
        if (!IdentifierPattern().IsMatch(deviceId)) throw new InvalidOperationException("This PC's device ID is invalid.");
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
            throw new InvalidOperationException($"{target.SshTarget} couldn't create a pairing code. See the output.");
        var (paired, secret) = await pairing;
        if (result.ExitCode != 0)
            output.Report($"Paired, but the host reported exit {result.ExitCode} while restarting. Check host status.");
        return (paired, secret, result.HostKey);
    }

    /// <summary>Lets another desktop pair with an SSH host: runs "pair" there, which shows the host's address and a short
    /// one-use code; <paramref name="shown"/> receives both (the code never reaches <paramref name="output"/>). The host waits
    /// until that desktop redeems it (the code has no deadline); canceling withdraws the code. Returns the exit code.</summary>
    internal async Task<int> PairOtherAsync(HostSetupTarget target, bool sudo, string? pinnedHostKey, Action<string, string> shown,
        IProgress<string> output, CancellationToken token, string codeNote)
    {
        var withdraw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => withdraw.TrySetResult("cancel\n"));
        var sink = HostLocal.ShownCodeSink(target.Address, shown, output, codeNote);
        try { return (await RunAsync(target, "pair", false, sudo, null, pinnedHostKey, sink, token, withdraw.Task)).ExitCode; }
        finally { withdraw.TrySetResult(null); }
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
            throw new InvalidOperationException($"Couldn't read {role} from {target.SshTarget}. See the output.");
        return ParseRole(lines);
    }

    internal static HostRoleInputs ParseRole(IEnumerable<string> lines)
    {
        string title = "", requires = "", terms = "";
        var installed = false;
        var gpuOrCpu = false;
        var suggested = new HashSet<string>(StringComparer.Ordinal);
        var suggestedWhen = new HashSet<(string, string, string)>();
        var secrets = new List<HostRoleSecret>();
        var choices = new List<HostRoleChoice>();
        var secretWhen = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var termsWhen = new List<(string, string, string)>();
        (string, string)? gpuWhen = null;
        IReadOnlyList<string> stops = [];
        var gpus = new List<HostCard>();
        string? gpuCurrent = null;
        string? acceleratorCurrent = null;
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        static (string Variable, string Value)? Condition(string text) =>
            text.Split('=') is [var variable, var value] && variable.Length > 0 && value.Length > 0 ? (variable, value) : null;
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
                case "role.accelerator": gpuOrCpu = true; break;
                case "role.suggested": suggested.Add(value); break;
                case "role.stops": stops = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); break;
                case "role.gpu" when value.Split('|') is [var id, var name, var mb, var users] && id.StartsWith("GPU-", StringComparison.Ordinal) &&
                                     int.TryParse(mb, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var memory):
                    gpus.Add(new(id, name, memory, users.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
                    break;
                case "role.gpu_current": gpuCurrent = value; break;
                case "role.accelerator_current" when value is "gpu" or "cpu": acceleratorCurrent = value; break;
                case "role.choice_current" when value.Split('|') is [var variable, var chosen] && variable.Length > 0 && chosen.Length > 0:
                    current[variable] = chosen;
                    break;
                case "role.secret" when value.Split('|') is [var name, .. var middle, var state] && middle.Length > 0:
                    secrets.Add(new(name, string.Join('|', middle), state == "stored"));
                    break;
                case "role.secret_when" when value.Split('|') is [var name, var when] && Condition(when) is { } condition:
                    secretWhen[name] = condition;
                    break;
                case "role.terms_when" when value.IndexOf('|') is > 0 and var bar && Condition(value[..bar]) is { } condition:
                    termsWhen.Add((condition.Variable, condition.Value, value[(bar + 1)..]));
                    break;
                case "role.choice" when value.Split('|') is [var variable, var label, var options, var fallback]:
                    choices.Add(new(variable, label, options.Split(' ', StringSplitOptions.RemoveEmptyEntries), fallback));
                    break;
                case "role.choice_when" when value.Split('|') is [var when, var variable, var label, var options, var fallback] &&
                                             Condition(when) is { } condition:
                    choices.Add(new(variable, label, options.Split(' ', StringSplitOptions.RemoveEmptyEntries), fallback, When: condition));
                    break;
                case "role.suggested_when" when value.Split('|') is [var when, var variable] && Condition(when) is { } condition:
                    suggestedWhen.Add((condition.Variable, condition.Value, variable));
                    break;
                case "role.gpu_when" when Condition(value) is { } condition:
                    gpuWhen = condition;
                    break;
            }
        }
        return new(title, requires, terms, installed, secrets,
            choices.Select(c => c with
            {
                Suggested = c.When is { } when ? suggestedWhen.Contains((when.Variable, when.Value, c.Variable)) : suggested.Contains(c.Variable)
            }).ToList(), gpuOrCpu)
        {
            SecretWhen = secretWhen, TermsWhen = termsWhen, Stops = stops, Gpus = gpus, GpuCurrent = gpuCurrent, GpuWhen = gpuWhen,
            Current = current, AcceleratorCurrent = acceleratorCurrent
        };
    }
}

/// <summary>Synchronous progress sink: runs on the runner's reading thread, in order (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class LineSink(Action<string> action) : IProgress<string>
{
    public void Report(string value) => action(value);
}

/// <summary>Passes martlet-host output on and keeps the line the engine prints when the host was busy with another change
/// (<see cref="Martlet.Core.Nodes.HostEngineBusy"/>), so a caller can tell "busy, nothing changed" from a failure.</summary>
internal sealed class EngineOutput(IProgress<string> output) : IProgress<string>
{
    private volatile string? busyLine;

    public void Report(string value)
    {
        if ((value ?? "").TrimStart().StartsWith(Martlet.Core.Nodes.HostEngineBusy.Marker, StringComparison.Ordinal)) busyLine = value;
        output.Report(value!);
    }

    /// <summary>What the host was busy with when the run ended with <paramref name="exitCode"/>, or null when it wasn't.</summary>
    internal string? Busy(int exitCode) => Martlet.Core.Nodes.HostEngineBusy.Read(exitCode, busyLine is { } line ? [line] : []);
}

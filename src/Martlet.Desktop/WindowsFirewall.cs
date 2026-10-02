using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Martlet.Desktop;

/// <summary>
/// Opens the Martlet host gateway port for this PC's private network when this PC becomes a host, together with the port
/// other Martlet desktops use to find this PC and ask to use its hosts (<see cref="Nearby"/>, TCP and UDP 9444).
/// Reading needs no rights; the change runs once through Windows' own administrator prompt (UAC).
/// </summary>
internal static class WindowsFirewall
{
    internal const string RuleName = "Martlet-Host-Gateway";
    internal const int Port = 9443;
    internal const string NearbyTcpRule = "Martlet-Nearby-TCP";
    internal const string NearbyUdpRule = "Martlet-Nearby-UDP";

    internal sealed record State(bool RuleExists, string? Category, int? InterfaceIndex, bool DockerBlocked, bool NearbyRulesExist = false);

    internal enum Outcome { Applied, Declined, Failed }

    internal static string ProbeScript(string address)
    {
        if (!HostSetupCommands.IsPrivate(address)) throw new InvalidOperationException("Enter this PC's private network address.");
        return "$ErrorActionPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue';" +
            $"$r=[bool](Get-NetFirewallRule -Name '{RuleName}');" +
            $"$p=Get-NetIPAddress -IPAddress '{address}' | Get-NetConnectionProfile | Select-Object -First 1;" +
            "$b=[bool](Get-NetFirewallApplicationFilter -Program '*com.docker.backend.exe' | Get-NetFirewallRule | " +
            "Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' -and " +
            "($_.Profile -match 'Private|Any') });" +
            $"$n=[bool](Get-NetFirewallRule -Name '{NearbyTcpRule}') -and [bool](Get-NetFirewallRule -Name '{NearbyUdpRule}');" +
            "\"$r|$($p.NetworkCategory)|$($p.InterfaceIndex)|$b|$n\"";
    }

    internal static State Parse(string output)
    {
        var parts = (output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "").Split('|');
        if (parts.Length != 5) return new(false, null, null, false);
        return new(parts[0] == "True", parts[1].Length == 0 ? null : parts[1],
            int.TryParse(parts[2], out var index) ? index : null, parts[3] == "True", parts[4] == "True");
    }

    /// <summary>The elevated change: inbound rules for the local subnet on private/domain networks (the gateway's TCP port
    /// unless <paramref name="gateway"/> is false, and Nearby's TCP and UDP port), and, only when the user agreed, marking
    /// the network as Private.</summary>
    internal static string ApplyScript(int? makePrivateInterface, bool gateway = true)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';");
        if (gateway)
        {
            script.Append($"Remove-NetFirewallRule -Name '{RuleName}' -ErrorAction SilentlyContinue;");
            script.Append($"New-NetFirewallRule -Name '{RuleName}' -DisplayName 'Martlet host' ");
            script.Append("-Description 'Lets Martlet desktops on your private network reach this PC as a host. Added by Martlet.' ");
            script.Append($"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {Port} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null;");
        }
        foreach (var (name, protocol) in new[] { (NearbyTcpRule, "TCP"), (NearbyUdpRule, "UDP") })
        {
            script.Append($"Remove-NetFirewallRule -Name '{name}' -ErrorAction SilentlyContinue;");
            script.Append($"New-NetFirewallRule -Name '{name}' -DisplayName 'Martlet: find this PC ({protocol})' ");
            script.Append("-Description 'Lets Martlet on your other computers find this PC and ask to use its hosts (each request needs your Allow here). Added by Martlet.' ");
            script.Append($"-Direction Inbound -Action Allow -Protocol {protocol} -LocalPort {Nearby.Port} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null;");
        }
        if (makePrivateInterface is { } index)
            script.Append($"Set-NetConnectionProfile -InterfaceIndex {index} -NetworkCategory Private;");
        return script.ToString();
    }

    internal static string Encoded(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    internal static async Task<State> ProbeAsync(string address, CancellationToken token)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Encoded(ProbeScript(address))}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        var output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return Parse(output);
    }

    internal static async Task<Outcome> ApplyAsync(int? makePrivateInterface, CancellationToken token, bool gateway = true)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {Encoded(ApplyScript(makePrivateInterface, gateway))}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return Outcome.Failed;
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0 ? Outcome.Applied : Outcome.Failed;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return Outcome.Declined; }
    }
}

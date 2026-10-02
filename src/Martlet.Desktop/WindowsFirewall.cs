using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Martlet.Desktop;

/// <summary>
/// Opens the Martlet host gateway port for this PC's private network when this PC becomes a host.
/// Reading needs no rights; the change runs once through Windows' own administrator prompt (UAC).
/// </summary>
internal static class WindowsFirewall
{
    internal const string RuleName = "Martlet-Host-Gateway";
    internal const int Port = 9443;

    internal sealed record State(bool RuleExists, string? Category, int? InterfaceIndex, bool DockerBlocked);

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
            "\"$r|$($p.NetworkCategory)|$($p.InterfaceIndex)|$b\"";
    }

    internal static State Parse(string output)
    {
        var parts = (output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "").Split('|');
        if (parts.Length != 4) return new(false, null, null, false);
        return new(parts[0] == "True", parts[1].Length == 0 ? null : parts[1],
            int.TryParse(parts[2], out var index) ? index : null, parts[3] == "True");
    }

    /// <summary>The elevated change: one inbound TCP rule for the local subnet on private/domain networks,
    /// and, only when the user agreed, marking the host's network as Private.</summary>
    internal static string ApplyScript(int? makePrivateInterface)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';");
        script.Append($"Remove-NetFirewallRule -Name '{RuleName}' -ErrorAction SilentlyContinue;");
        script.Append($"New-NetFirewallRule -Name '{RuleName}' -DisplayName 'Martlet host' ");
        script.Append("-Description 'Lets Martlet desktops on your private network reach this PC as a host. Added by Martlet.' ");
        script.Append($"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {Port} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null;");
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

    internal static async Task<Outcome> ApplyAsync(int? makePrivateInterface, CancellationToken token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {Encoded(ApplyScript(makePrivateInterface))}")
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

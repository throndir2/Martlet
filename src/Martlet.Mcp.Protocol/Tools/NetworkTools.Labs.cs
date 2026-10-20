using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>The live labs this server started (signin_lab and role_lab), each a Martlet.NodeLinkCheck process.</summary>
    private static System.Diagnostics.Process? signInLab;

    private static System.Diagnostics.Process? roleLab;

    /// <summary>Starts a live lab (Martlet.NodeLinkCheck <paramref name="mode"/> &lt;data directory&gt;) and returns the process and
    /// its first line, which says whether it is ready. The lab keeps its pairing secrets in the lab credential folder.</summary>
    private static async Task<(System.Diagnostics.Process Process, JsonElement Ready)> StartLabAsync(string mode, string directory, string name,
        CancellationToken cancellation, params string[] extra)
    {
        if (Environment.GetEnvironmentVariable(Martlet.Credentials.Windows.LabCredentialNative.Variable) is not { Length: > 0 })
            throw new InvalidOperationException("Run with Invoke-MartletMcp.ps1 -LabCredentials: the lab keeps its pairing secret in a lab folder, never Windows Credential Manager.");
        Directory.CreateDirectory(directory);
        var start = new System.Diagnostics.ProcessStartInfo(NodeLinkCheckProgram())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        foreach (var argument in extra) start.ArgumentList.Add(argument);
        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start the {name}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        if (line is null || !line.Contains("\"ready\":true", StringComparison.Ordinal))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The {name} didn't start: " + (line ?? await process.StandardError.ReadToEndAsync(cancellation)));
        }
        return (process, JsonSerializer.Deserialize<JsonElement>(line));
    }

    /// <summary>Ends a live lab: closing its standard input stops it.</summary>
    private static void StopLab(System.Diagnostics.Process? lab)
    {
        if (lab is not { HasExited: false } running) return;
        running.StandardInput.Close();
        if (!running.WaitForExit(10_000)) running.Kill(entireProcessTree: true);
    }

    /// <summary>Starts, reads, asks or stops the live lab for switching computers between companion and host PC
    /// (Martlet.NodeLinkCheck role-lab) for a disposable data directory.</summary>
    private static async Task<object> RoleLabAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var status = Path.Combine(directory, "role-lab.json");
        switch (OptionalString(arguments, "action"))
        {
            case "start":
            {
                if (roleLab is { HasExited: false }) throw new InvalidOperationException("The role lab is already running; stop it first.");
                var (process, ready) = await StartLabAsync("role-lab", directory, "role lab", cancellation);
                roleLab = process;
                return ready;
            }
            case "status":
                return await ReadLabStatusAsync(status, roleLab, cancellation);
            case "ask":
            {
                if (roleLab is not { HasExited: false }) throw new InvalidOperationException("Start the role lab first.");
                var role = OptionalString(arguments, "role");
                if (role is not ("host" or "companion")) throw new ArgumentException("role is host or companion.");
                var ask = Path.Combine(directory, "role-lab.ask");
                await File.WriteAllTextAsync(ask + ".tmp", role, cancellation);
                File.Move(ask + ".tmp", ask, overwrite: true);
                return new { asked = role };
            }
            case "add_host":
            {
                if (roleLab is not { HasExited: false }) throw new InvalidOperationException("Start the role lab first.");
                var hostId = OptionalString(arguments, "hostId") ?? "lab-nas";
                if (hostId is not { Length: > 0 and <= 64 } || !char.IsAsciiLetterOrDigit(hostId[0]) ||
                    !hostId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                    throw new ArgumentException("hostId is letters, digits, '.', '_' or '-'.");
                var adding = Path.Combine(directory, "role-lab.host");
                await File.WriteAllTextAsync(adding + ".tmp", hostId, cancellation);
                File.Move(adding + ".tmp", adding, overwrite: true);
                return new { adding = hostId };
            }
            case "stop":
                StopLab(roleLab);
                roleLab = null;
                return new { stopped = true };
            default:
                throw new InvalidOperationException("action is start, status, ask, add_host or stop.");
        }
    }

    /// <summary>A live lab's status file, read again when the lab is swapping it in at that moment; whether the lab still runs
    /// when there is none yet.</summary>
    private static async Task<object> ReadLabStatusAsync(string path, System.Diagnostics.Process? lab, CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(path, cancellation))
                    : new { ready = false, running = lab is { HasExited: false } };
            }
            catch (IOException) when (attempt < 5) { await Task.Delay(50, cancellation); }
        }
    }

    /// <summary>Starts, reads or stops the live sign-in lab (Martlet.NodeLinkCheck signin-lab) for a disposable data directory.</summary>
    private static async Task<object> SignInLabAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var status = Path.Combine(directory, "signin-lab.json");
        switch (OptionalString(arguments, "action"))
        {
            case "start":
            {
                if (signInLab is { HasExited: false }) throw new InvalidOperationException("The sign-in lab is already running; stop it first.");
                var mode = OptionalString(arguments, "mode") ?? "owner";
                if (mode is not ("owner" or "friend" or "account")) throw new ArgumentException("mode is owner, friend or account.");
                var extra = new List<string> { "--mode", mode };
                if (OptionalBool(arguments, "signInDesktop") == true)
                    extra.Add(mode == "friend" ? "--sign-in-desktop" : throw new ArgumentException("signInDesktop goes with mode friend."));
                if (OptionalBool(arguments, "shareWithFriend") == true)
                    extra.Add(mode == "owner" ? "--share-with-friend" : throw new ArgumentException("shareWithFriend goes with mode owner."));
                if (OptionalBool(arguments, "household") == true)
                    extra.Add(mode == "account" ? "--household" : throw new ArgumentException("household goes with mode account."));
                if (OptionalBool(arguments, "joinDesktop") == true)
                    extra.Add(mode == "account" && !extra.Contains("--household") ? "--join-desktop"
                        : throw new ArgumentException("joinDesktop goes with mode account, without household."));
                var (process, ready) = await StartLabAsync("signin-lab", directory, "sign-in lab", cancellation, [.. extra]);
                signInLab = process;
                return ready;
            }
            case "status":
                return await ReadLabStatusAsync(status, signInLab, cancellation);
            case "stop":
                StopLab(signInLab);
                signInLab = null;
                return new { stopped = true };
            default:
                throw new InvalidOperationException("action is start, status or stop.");
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Martlet.Gateway.Host.Linux;
using Martlet.Gateway.Persistence;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: SupportedOSPlatform("linux")]

namespace Martlet.Gateway.Host.Linux.Native.Tests;

public sealed class NativeTests : IDisposable
{
    private readonly string root;
    private readonly LinuxFileSystem fs;

    public NativeTests()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "NOT RUN: requires separately authorized Linux x86_64/glibc, ext4 and non-root service identity.");
        var parent = Environment.GetEnvironmentVariable("MARTLET_LINUX_TEST_PARENT");
        Assert.False(string.IsNullOrEmpty(parent),
            "NOT RUN: explicitly authorize an existing private ext4 parent; no account/permission repair occurs.");
        fs = new LinuxFileSystem();
        root = parent!.TrimEnd('/') + "/martlet-host-native-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Actual_approved_foreground_process_health_SIGTERM_and_reopen()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var origin = new GatewayOrigin($"https://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
        port.Stop();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, hostId = "native-fixture", stateDirectory = root + "/state",
            storageBackend = "linuxServicePermissions",
            binding = new { mode = "loopback", origin = origin.CanonicalOrigin },
            serviceUid = fs.UserId, serviceGid = fs.GroupId
        });
        using (var stream = new FileStream(root + "/host.json", new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
            stream.Write(bytes);
        var config = HostConfiguration.Parse(bytes);
        GatewayHostIdentity identity;
        // The approval test seam is not a human-presence claim; actual terminal I/O has a separate PTY case.
        await using (var owner = DurableGatewayHost.CreateNewForBinding(config.StateDirectory, config.HostId,
            config.Binding, GatewayStorageBackend.LinuxServicePermissions, [], new QuietAudit(), LocalGatewayDecision.Enable))
        {
            identity = owner.Identity!;
            using var directory = new LinuxControlDirectory(root + "/host.json", fs);
            directory.WriteApproval(ServiceApproval.Create(config, identity));
            await owner.CloseCleanlyAsync();
        }
        using var process = Start(typeof(HostApplication).Assembly.Location, "serve", "--config", root + "/host.json");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("serving:", line);
            using var output = new StringWriter();
            Assert.Equal(0, await HostApplication.RunAsync(["health", "--config", root + "/host.json"], output));
            Assert.Equal(0, Native.kill(process.Id, 15));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(130, process.ExitCode);
            Assert.Contains("closed cleanly", await process.StandardOutput.ReadToEndAsync());
            Assert.Empty(await process.StandardError.ReadToEndAsync());
        }
        finally { await StopOwned(process); }
        await using var reopened = DurableGatewayHost.OpenExistingForBinding(config.StateDirectory, config.Binding,
            GatewayStorageBackend.LinuxServicePermissions, identity, [], new QuietAudit(), LocalGatewayDecision.Enable);
        Assert.Equal(identity, reopened.Identity);
        await reopened.CloseCleanlyAsync();
    }

    /// <summary>signin.json with the real martlet-host process: the owner account set up over stdin (authenticator code
    /// computed from the secret it prints), status and an invite, then the approved service serves sign-in over pinned TLS and
    /// a computer signs in with a recovery code; the file stays 0600 for the service owner and never holds the password.</summary>
    [Fact]
    public async Task Actual_process_keeps_signin_json_and_serves_sign_in_over_pinned_tls()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var origin = new GatewayOrigin($"https://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
        port.Stop();
        WriteConfig(origin);
        var config = root + "/host.json";
        var assembly = typeof(HostApplication).Assembly.Location;
        var (initExit, initOutput) = await RunAsync(assembly, null, "owner-init", "--config", config);
        Assert.True(initExit == 0, initOutput);

        const string password = "native owner passphrase";
        string ownerOutput;
        using (var owner = Start(assembly, "owner-signin-owner", "--config", config, "--user", "owner"))
        {
            try
            {
                await owner.StandardInput.WriteLineAsync(password);
                await owner.StandardInput.FlushAsync();
                var seen = new StringBuilder();
                string? secret = null;
                while (secret is null && await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
                {
                    seen.AppendLine(line);
                    if (line.StartsWith("secret: ", StringComparison.Ordinal)) secret = line["secret: ".Length..].Trim();
                }
                Assert.True(secret is not null, seen.ToString());
                await owner.StandardInput.WriteLineAsync(Martlet.Core.Access.Totp.Code(secret!, DateTimeOffset.UtcNow));
                owner.StandardInput.Close();
                ownerOutput = seen + await owner.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(owner.ExitCode == 0, ownerOutput);
            }
            finally { await StopOwned(owner); }
        }
        var recovery = ownerOutput.Split('\n').Select(l => l.Trim()).Where(l => l.Length == 11 && l[5] == '-').ToArray();
        Assert.Equal(10, recovery.Length);
        var signin = root + "/signin.json";
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(signin));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(signin));
        Assert.DoesNotContain(recovery[0], await File.ReadAllTextAsync(signin));

        var (statusExit, status) = await RunAsync(assembly, null, "owner-signin-status", "--config", config);
        Assert.True(statusExit == 0, status);
        Assert.Contains("\"user\":\"owner\"", status);
        Assert.Contains("\"recoveryCodesLeft\":10", status);
        var (inviteExit, inviteOutput) = await RunAsync(assembly, null, "owner-invite", "--config", config, "--address", "home.example.net:9443");
        Assert.True(inviteExit == 0, inviteOutput);
        var invite = Martlet.Core.Network.NetworkInvite.Parse(inviteOutput.Split('\n')
            .Single(l => l.StartsWith(Martlet.Core.Network.NetworkInvite.Prefix, StringComparison.Ordinal)));
        Assert.Equal(origin.CanonicalOrigin, invite.Origin);
        Assert.Equal(["home.example.net:9443"], invite.Addresses);

        using var process = Start(assembly, "serve", "--config", config);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("serving:", line);
            using var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate).PublicKey.ExportSubjectPublicKeyInfo())) == invite.SpkiFingerprint;
            using var http = new HttpClient(handler) { BaseAddress = new Uri(invite.Origin + "/") };
            using (var list = await http.GetAsync("martlet/v1/signin"))
                Assert.Contains("\"owner\"", await list.Content.ReadAsStringAsync());
            using var begin = await http.PostAsync("martlet/v1/signin/begin", Json(new { protocol_version = new { major = 2, minor = 0 }, provider = "owner" }));
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
            var attempt = JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement.GetProperty("attempt_id").GetString();
            using var complete = await http.PostAsync("martlet/v1/signin/complete", Json(new
            {
                protocol_version = new { major = 2, minor = 0 }, attempt_id = attempt, device_id = "native-laptop", display_name = "NATIVE",
                proof = new { user = "owner", password, code = recovery[0] }
            }));
            var body = await complete.Content.ReadAsStringAsync();
            Assert.True(complete.StatusCode == HttpStatusCode.Created, body);
            Assert.Contains("\"signed_in\"", body);
            var (afterExit, after) = await RunAsync(assembly, null, "owner-signin-status", "--config", config);
            Assert.True(afterExit == 0, after);
            Assert.Contains("native-laptop", after);
            Assert.Contains("\"recoveryCodesLeft\":9", after);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(signin));
            Assert.Equal(0, Native.kill(process.Id, 15));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(130, process.ExitCode);
        }
        finally { await StopOwned(process); }

        static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private void WriteConfig(GatewayOrigin origin)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, hostId = "native-fixture", stateDirectory = root + "/state",
            storageBackend = "linuxServicePermissions",
            binding = new { mode = "loopback", origin = origin.CanonicalOrigin },
            serviceUid = fs.UserId, serviceGid = fs.GroupId
        });
        using var stream = new FileStream(root + "/host.json", new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        stream.Write(bytes);
    }

    private static async Task<(int Exit, string Output)> RunAsync(string assembly, string? input, params string[] args)
    {
        using var process = Start(assembly, args);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return (process.ExitCode, output + error);
        }
        finally { await StopOwned(process); }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("flow-stop")]
    [InlineData("cancel")]
    public async Task Actual_owned_PTY_input_private_screen_and_restoration(string scenario)
    {
        var size = new Native.Size { Rows = 24, Columns = 100 };
        var name = new byte[256];
        Assert.Equal(0, Native.openpty(out var master, out var slave, name, IntPtr.Zero, ref size));
        using var masterOwner = new LinuxDescriptor(fs, master);
        using var slaveOwner = new LinuxDescriptor(fs, slave);
        Assert.Equal(0, Native.fcntl(master, 2, 1));
        Assert.Equal(0, Native.fcntl(slave, 2, 1));
        var path = Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));
        using var process = Start(typeof(NativeTests).Assembly.Location, "--tty-probe", path, scenario);
        var captured = new StringBuilder();
        try
        {
            await Until("fixture> ");
            Write("yes\n");
            await Until("PRIVATE ONE-USE");
            if (scenario != "cancel") Write(scenario == "flow-stop" ? "\x13" : "x");
            await Until("RESTORED");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\x1b[?1049h", captured.ToString());
            Assert.Contains("\x1b[2J\x1b[H\x1b[?1049l", captured.ToString());
            Assert.Empty(await process.StandardOutput.ReadToEndAsync());
            Assert.Empty(await process.StandardError.ReadToEndAsync());
        }
        finally { await StopOwned(process); }

        void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Assert.Equal(bytes.Length, fs.Write(master, bytes, 0, bytes.Length));
        }
        async Task Until(string marker)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bytes = new byte[4096];
            while (!captured.ToString().Contains(marker, StringComparison.Ordinal))
            {
                deadline.Token.ThrowIfCancellationRequested();
                var poll = new Native.PollFd { Fd = master, Events = 1 };
                Assert.True(Native.poll(ref poll, 1, 0) >= 0);
                if ((poll.Returned & 1) != 0)
                {
                    var count = fs.Read(master, bytes, 0, bytes.Length);
                    Assert.True(count > 0);
                    captured.Append(Encoding.UTF8.GetString(bytes, 0, count));
                    Assert.True(captured.Length < 16384);
                }
                else await Task.Delay(25, deadline.Token);
            }
        }
    }

    private static Process Start(string assembly, params string[] args)
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Assert.False(string.IsNullOrEmpty(root), "NOT RUN: select the authorized runtime with DOTNET_ROOT.");
        var start = new ProcessStartInfo(Path.Combine(root!, "dotnet"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add(assembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["TERM"] = "xterm-256color";
        return Process.Start(start)!;
    }

    private static async Task StopOwned(Process process)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(root + "/state"))
        {
            foreach (var name in new[] { "owner.lock", "authority.bin", "running", "staging.bin", "pending.bin" })
                File.Delete(root + "/state/" + name);
            Directory.Delete(root + "/state", recursive: false);
        }
        foreach (var name in new[] { "host.json", "service-approval.json", "service-approval.staging", "logs.json", "agent.token", "commands.json",
                     "signin.json", "signin.staging", "network.json", "exposure.json", "machine.json" })
            File.Delete(root + "/" + name);
        Directory.Delete(root, recursive: false);
    }
}

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args is not ["--tty-probe", var path, var scenario] ||
            scenario is not ("normal" or "flow-stop" or "cancel")) return 2;
        if (Native.setsid() < 0) return 3;
        var fd = Native.open(path, 2 | 0x100 | 0x80000 | 0x20000);
        if (fd < 0) return 4;
        try
        {
            if (Native.ioctl(fd, 0x540e, 0) != 0 || Native.tcsetpgrp(fd, Native.getpgrp()) != 0) return 5;
            for (var target = 0; target <= 2; target++) if (Native.dup2(fd, target) != target) return 6;
            using var terminal = new LinuxTerminal();
            if (scenario == "flow-stop" && Native.tcflow(fd, 0) != 0) return 9;
            if (await terminal.ReadAsync("fixture> ", 3, CancellationToken.None) != "yes") return 7;
            using var cancel = new CancellationTokenSource();
            if (scenario == "cancel") cancel.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await terminal.DiscloseAsync(new()
                {
                    HostId = "tty-fixture", Origin = "https://127.0.0.1:9443",
                    SpkiFingerprint = "sha256:" + new string('a', 64), PairingId = "synthetic",
                    Token = new("SYNTHETIC-NOT-A-CREDENTIAL"), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10)
                }, cancel.Token);
            }
            catch (OperationCanceledException) when (scenario == "cancel" && cancel.IsCancellationRequested) { }
            var termios = new byte[60];
            if (Native.tcgetattr(fd, termios) != 0 || (BitConverter.ToUInt32(termios, 12) & (2u | 8u)) != (2u | 8u)) return 8;
            Console.WriteLine("RESTORED");
            return 0;
        }
        finally { Native.close(fd); }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Size { internal ushort Rows, Columns, X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct PollFd { internal int Fd; internal short Events, Returned; }
    [DllImport("libutil.so.1", SetLastError = true)] internal static extern int openpty(out int master, out int slave, byte[] name, IntPtr termios, ref Size size);
    [DllImport("libc", SetLastError = true)] internal static extern int poll(ref PollFd poll, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] internal static extern int setsid();
    [DllImport("libc", SetLastError = true)] internal static extern int getpgrp();
    [DllImport("libc", SetLastError = true)] internal static extern int tcsetpgrp(int fd, int pg);
    [DllImport("libc", SetLastError = true)] internal static extern int ioctl(int fd, uint request, int arg);
    [DllImport("libc", SetLastError = true)] internal static extern int dup2(int fd, int target);
    [DllImport("libc", SetLastError = true)] internal static extern int tcgetattr(int fd, byte[] termios);
    [DllImport("libc", SetLastError = true)] internal static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int fd, int command, int value);
    [DllImport("libc", SetLastError = true)] internal static extern int tcflow(int fd, int action);
}

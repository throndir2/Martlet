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
        foreach (var name in new[] { "host.json", "service-approval.json", "service-approval.staging" })
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

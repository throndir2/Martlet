using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal interface IHostTerminal : IDisposable
{
    void Check();
    void CheckDisclosure();
    ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation);
    ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation);
}

internal sealed class LinuxTerminal : IHostTerminal
{
    private readonly LinuxFileSystem fs;
    private readonly LinuxDescriptor terminal;
    private readonly LinuxFileIdentity identity;
    private bool disposed;

    internal LinuxTerminal()
    {
        fs = new LinuxFileSystem();
        if (Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
            throw new HostTerminalException();
        var name = new byte[4096];
        if (Native.ttyname_r(0, name, (nuint)name.Length) != 0) throw new HostTerminalException();
        var end = Array.IndexOf(name, (byte)0);
        if (end <= 0) throw new HostTerminalException();
        var path = Encoding.UTF8.GetString(name, 0, end);
        if (!path.StartsWith("/dev/", StringComparison.Ordinal)) throw new HostTerminalException();
        var fd = Native.open(path, LinuxFileSystem.ReadWrite | LinuxFileSystem.CloseOnExec |
            LinuxFileSystem.NoFollow | LinuxFileSystem.NonBlocking | 0x100);
        if (fd < 0) throw new HostTerminalException();
        terminal = new(fs, fd);
        try
        {
            identity = fs.Stat(fd);
            Check();
        }
        catch { terminal.Dispose(); throw; }
    }

    public void Check()
    {
        if (disposed || Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected ||
            (identity.Mode & 0xf000) != 0x2000 || identity.User != fs.UserId ||
            (identity.Mode & 0x6) != 0 || Native.isatty(terminal.Value) != 1 ||
            Native.tcgetsid(terminal.Value) != Native.getsid(0) ||
            Native.tcgetpgrp(terminal.Value) != Native.getpgrp())
            throw new HostTerminalException();
        foreach (var fd in new[] { 0, 1, 2, terminal.Value })
        {
            var current = fs.Stat(fd);
            if (Native.isatty(fd) != 1 || !identity.SameFile(current) ||
                current.User != fs.UserId || (current.Mode & 0xf000) != 0x2000 || (current.Mode & 0x6) != 0)
                throw new HostTerminalException();
        }
    }

    public void CheckDisclosure()
    {
        Check();
        if (Environment.GetEnvironmentVariable("TERM") is not ("xterm" or "xterm-256color") ||
            Native.ioctl(terminal.Value, 0x5413, out var size) != 0 || size.Columns < 80 || size.Rows < 20)
            throw new HostTerminalException();
    }

    private void Write(string text, CancellationToken cancellation = default)
    {
        Check();
        var bytes = Encoding.UTF8.GetBytes(text);
        var offset = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (offset < bytes.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                Check();
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
                    throw new HostTerminalException();
                var remaining = bytes.AsSpan(offset).ToArray();
                nint count;
                try { count = Native.write(terminal.Value, remaining, (nuint)remaining.Length); }
                finally { CryptographicOperations.ZeroMemory(remaining); }
                if (count > 0 && count <= bytes.Length - offset) { offset += (int)count; continue; }
                if (count >= 0 || Marshal.GetLastPInvokeError() is not (4 or 11))
                    throw new HostTerminalException();
                Thread.Sleep(10);
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async ValueTask<int> KeyAsync(CancellationToken cancellation)
    {
        var buffer = new byte[1];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Check();
            var poll = new Native.PollFd { Fd = terminal.Value, Events = 1 };
            var result = Native.poll(ref poll, 1, 0);
            if (result < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4) continue;
                throw new HostTerminalException();
            }
            if ((poll.Returned & (8 | 16 | 32)) != 0) throw new HostTerminalException();
            if ((poll.Returned & 1) != 0)
            {
                var count = Native.read(terminal.Value, buffer, 1);
                if (count == 1) return buffer[0];
                if (count == 0) return 4;
                if (Marshal.GetLastPInvokeError() is not (4 or 11)) throw new HostTerminalException();
            }
            await Task.Delay(50, cancellation);
        }
    }

    private byte[] Raw()
    {
        Check();
        var saved = new byte[60];
        if (Native.tcgetattr(terminal.Value, saved) != 0) throw new HostTerminalException();
        var current = saved.ToArray();
        BitConverter.GetBytes(BitConverter.ToUInt32(current, 0) & ~(0x400u | 0x800u | 0x1000u)).CopyTo(current, 0);
        BitConverter.GetBytes(BitConverter.ToUInt32(current, 12) & ~(2u | 8u)).CopyTo(current, 12);
        current[17 + 6] = 1;
        current[17 + 5] = 0;
        if (Native.tcsetattr(terminal.Value, 0, current) != 0) throw new HostTerminalException();
        if (Native.tcflow(terminal.Value, 1) != 0)
        {
            Restore(saved);
            throw new HostTerminalException();
        }
        return saved;
    }

    private void Restore(byte[] saved)
    {
        if (Native.tcsetattr(terminal.Value, 0, saved) != 0) throw new HostTerminalException();
    }

    public async ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation)
    {
        var saved = Raw();
        try
        {
            Write(prompt, cancellation);
            var text = new StringBuilder(maximum);
            while (true)
            {
                var key = await KeyAsync(cancellation);
                if (key == 4) { Write("\r\n", cancellation); return null; }
                if (key is 10 or 13) { Write("\r\n", cancellation); return text.ToString(); }
                if (key is 8 or 127)
                {
                    if (text.Length > 0) { text.Length--; Write("\b \b", cancellation); }
                    continue;
                }
                if (key is < 32 or > 126 || text.Length >= maximum) throw new HostInputException();
                text.Append((char)key);
                Write(((char)key).ToString(), cancellation);
            }
        }
        finally { Restore(saved); }
    }

    public async ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation)
    {
        CheckDisclosure();
        var saved = Raw();
        try
        {
            Write("\x1b[?1049h\x1b[2J\x1b[H", cancellation);
            Write($"PRIVATE ONE-USE INVITATION (protocol 2)\r\nHost: {card.HostId}\r\nOrigin: {card.Origin}\r\nSPKI pin: {card.SpkiFingerprint}\r\nPairing ID: {card.PairingId}\r\nToken: {card.Token.Reveal()}\r\nDeadline UTC: {card.ExpiresAt:O}\r\nPairing is permanent until revoked. No clipboard or log output.\r\nPress a key to erase. Never record/share this terminal.\r\n", cancellation);
            var remaining = card.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                using var expiry = new CancellationTokenSource(remaining);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, expiry.Token);
                try { await KeyAsync(linked.Token); }
                catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellation.IsCancellationRequested) { }
            }
        }
        finally
        {
            try { Write("\x1b[2J\x1b[H\x1b[?1049l"); }
            finally { Restore(saved); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        terminal.Dispose();
        disposed = true;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PollFd { internal int Fd; internal short Events, Returned; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Size { internal ushort Rows, Columns, X, Y; }
        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int ttyname_r(int fd, byte[] buffer, nuint size);
        [DllImport("libc", SetLastError = true)] internal static extern int isatty(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int tcgetsid(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int getsid(int pid);
        [DllImport("libc", SetLastError = true)] internal static extern int tcgetpgrp(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int getpgrp();
        [DllImport("libc", SetLastError = true)] internal static extern int tcgetattr(int fd, byte[] termios);
        [DllImport("libc", SetLastError = true)] internal static extern int tcsetattr(int fd, int action, byte[] termios);
        [DllImport("libc", SetLastError = true)] internal static extern int ioctl(int fd, uint request, out Size size);
        [DllImport("libc", SetLastError = true)] internal static extern int poll(ref PollFd poll, nuint count, int timeout);
        [DllImport("libc", SetLastError = true)] internal static extern nint read(int fd, byte[] bytes, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern nint write(int fd, byte[] bytes, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern int tcflow(int fd, int action);
    }
}

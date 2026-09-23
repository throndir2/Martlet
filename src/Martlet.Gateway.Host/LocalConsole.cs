using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Gateway.Host;

internal interface ILocalConsole
{
    bool IsInteractive { get; }
    void Write(string text);
    ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation);
    ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation);
}

internal sealed class LocalConsole : ILocalConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
    public void Write(string text) => Console.WriteLine(text);

    public async ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation)
    {
        if (!IsInteractive) throw new HostConsoleException();
        Console.Write(prompt);
        var input = new StringBuilder(maximum);
        while (true)
        {
            var key = await KeyAsync(cancellation);
            if (key.Key == ConsoleKey.Z && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.WriteLine();
                return null;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return input.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (key.KeyChar is < ' ' or > '~' || input.Length == maximum)
                throw new HostInputException();
            input.Append(key.KeyChar);
            // Fields are non-secret. Approvals still require the exact lower-case word "yes".
            Console.Write(key.KeyChar);
        }
    }

    private async ValueTask<ConsoleKeyInfo> KeyAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsInteractive) throw new HostConsoleException();
            if (Console.KeyAvailable)
                return Console.ReadKey(intercept: true);
            await Task.Delay(50, cancellation);
        }
    }

    public async ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows() || !IsInteractive)
            throw new HostConsoleException();
        var original = Native.GetStdHandle(-11);
        if (!Native.GetConsoleMode(original, out _))
            throw new HostConsoleException();
        using var screen = Native.CreateConsoleScreenBuffer(0xC0000000, 3, IntPtr.Zero, 1, IntPtr.Zero);
        if (screen.IsInvalid)
            throw new HostConsoleException();
        if (!Native.GetConsoleScreenBufferInfo(screen, out var info) || info.Size.X < 80 || info.Size.Y < 20)
            throw new HostConsoleException();
        try
        {
            if (!Native.SetConsoleActiveScreenBuffer(screen.DangerousGetHandle()))
                throw new HostConsoleException();
            var text = $"PRIVATE ONE-USE INVITATION (protocol 2)\r\nHost: {card.HostId}\r\nOrigin: {card.Origin}\r\nSPKI pin: {card.SpkiFingerprint}\r\nPairing ID: {card.PairingId}\r\nToken: {card.Token.Reveal()}\r\nInvitation deadline (UTC): {card.ExpiresAt:O}\r\nPairing itself is permanent until revoked.\r\nEnter only into a verified pinned local client, never a URL/shell/log.\r\nPress any key to erase this screen. No copying to clipboard is performed.\r\n";
            if (!Native.WriteConsoleW(screen, text, (uint)text.Length, out var written, IntPtr.Zero) || written != text.Length)
                throw new HostConsoleException();
            var remaining = card.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                using var timeout = new CancellationTokenSource(remaining);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
                try { await KeyAsync(linked.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested) { }
            }
        }
        finally
        {
            var erased = Native.FillConsoleOutputCharacterW(screen, ' ', (uint)(info.Size.X * info.Size.Y), default, out _);
            var restored = Native.SetConsoleActiveScreenBuffer(original);
            if (!erased || !restored)
                throw new HostConsoleException();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Coord { internal short X; internal short Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal short Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ScreenInfo
        {
            internal Coord Size, CursorPosition;
            internal ushort Attributes;
            internal Rect Window;
            internal Coord MaximumWindowSize;
        }
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll")]
        internal static extern SafeFileHandle CreateConsoleScreenBuffer(uint access, uint share, IntPtr security, uint flags, IntPtr data);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleScreenBufferInfo(SafeFileHandle screen, out ScreenInfo info);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleActiveScreenBuffer(IntPtr screen);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteConsoleW(SafeFileHandle screen, string text, uint count, out uint written, IntPtr reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FillConsoleOutputCharacterW(SafeFileHandle screen, char character, uint count, Coord coordinate, out uint written);
    }
}

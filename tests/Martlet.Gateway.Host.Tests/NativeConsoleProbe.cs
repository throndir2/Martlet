using System.Runtime.InteropServices;
using System.Text;
using Martlet.Gateway.Host;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Gateway.Host.Tests;

internal sealed class NativeConsoleProbe : ILocalConsole
{
    private static string stage = "setup";
    private readonly LocalConsole console = new();
    private readonly Queue<string> input = new(["yes", "start", "yes", "pair", "fixture-device", "Fixture", "voice", "yes", "stop", "yes"]);
    public bool IsInteractive => console.IsInteractive;
    public void Write(string text) => console.Write(text);
    public async ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation)
    {
        var expected = input.Dequeue();
        stage = "read-remaining-" + input.Count;
        SendKeys(expected + "\r");
        var actual = await console.ReadAsync(prompt, maximum, cancellation);
        if (actual != expected)
            stage = $"script-mismatch-{expected}-{Fields.Display(actual ?? "", 64)}";
        Assert.Equal(expected, actual);
        return actual;
    }

    public async ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation)
    {
        stage = "disclosure";
        var showing = console.DiscloseAsync(card, cancellation).AsTask();
        // Disclose runs synchronously through screen activation/write until its first key wait.
        try
        {
            Assert.False(showing.IsCompleted);
            stage = "active-screen";
            using var active = Open("CONOUT$");
            Assert.Contains(card.Token.Reveal(), ReadScreen(active));
            SendKeys("x");
            await showing;
            stage = "restored-screen";
            Assert.DoesNotContain(card.Token.Reveal(), ReadScreen(active));
            using var restored = Open("CONOUT$");
            var contents = ReadScreen(restored);
            Assert.DoesNotContain(card.Token.Reveal(), contents);
            Assert.Contains("host>", contents);
        }
        finally
        {
            if (!showing.IsCompleted)
            {
                SendKeys("x");
                await showing;
            }
        }
    }

    internal static async Task<int> Run(string state, string origin)
    {
        var output = GetStdHandle(-11);
        // This subprocess allocates its own console; it never reads the user's existing console.
        FreeConsole();
        if (!AllocConsole()) return 8;
        var exit = 8;
        try
        {
            using var input = Open("CONIN$");
            using var screen = Open("CONOUT$");
            Assert.True(SetStdHandle(-10, input.DangerousGetHandle()));
            Assert.True(SetStdHandle(-11, screen.DangerousGetHandle()));
            Assert.True(SetStdHandle(-12, screen.DangerousGetHandle()));
            var probe = new NativeConsoleProbe();
            stage = "interactive";
            Assert.True(probe.IsInteractive);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            stage = "host";
            var hostResult = await HostApplication.RunAsync(
                ["init", "--state", state, "--host-id", "fixture-host", "--origin", origin], probe, timeout.Token);
            if (hostResult != 0)
                stage += "-exit-" + hostResult;
            Assert.Equal(0, hostResult);
            stage = "bounded-input";
            SendKeys("abcd");
            await Assert.ThrowsAsync<HostInputException>(() => probe.console.ReadAsync("bounded: ", 3, timeout.Token).AsTask());
            SendKeys("\u001b");
            stage = "control-input";
            await Assert.ThrowsAsync<HostInputException>(() => probe.console.ReadAsync("control: ", 3, timeout.Token).AsTask());
            SendKeys("\u001a");
            stage = "eof-input";
            Assert.Null(await probe.console.ReadAsync("EOF: ", 3, timeout.Token));
            exit = 0;
        }
        catch (Exception)
        {
            // The owning test receives only a bounded result, never a secret-bearing assertion payload.
            exit = 8;
        }
        finally
        {
            FreeConsole();
            using var stream = new FileStream(new SafeFileHandle(output, ownsHandle: false), FileAccess.Write);
            using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(exit == 0 ? "CONSOLE-PASS" : "CONSOLE-FAIL:" + stage);
        }
        return exit;
    }

    private static SafeFileHandle Open(string name)
    {
        var handle = CreateFileW(name, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return handle;
    }
    private static string ReadScreen(SafeFileHandle handle)
    {
        var text = new StringBuilder(32768);
        Assert.True(ReadConsoleOutputCharacterW(handle, text, 32768, default, out var count));
        return text.ToString(0, (int)count);
    }
    private static void SendKeys(string value)
    {
        var records = value.Select(c => new InputRecord
        {
            EventType = 1, KeyDown = 1, RepeatCount = 1, Character = c,
            VirtualKey = c switch { '\r' => 13, '\u001a' => 90, '\u001b' => 27, _ => 0 },
            ControlKeyState = c == '\u001a' ? 8u : 0
        }).ToArray();
        Assert.True(WriteConsoleInputW(GetStdHandle(-10), records, (uint)records.Length, out var written));
        Assert.Equal((uint)records.Length, written);
    }
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] internal ushort EventType;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort RepeatCount;
        [FieldOffset(10)] internal ushort VirtualKey;
        [FieldOffset(14)] internal char Character;
        [FieldOffset(16)] internal uint ControlKeyState;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { internal short X, Y; }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacterW(SafeFileHandle handle, StringBuilder text, uint count, Coord start, out uint read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInputW(IntPtr input, InputRecord[] records, uint count, out uint written);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Martlet.Companion.Platform;
using Martlet.Platform.MacOS.Native;

namespace Martlet.Platform.MacOS;

/// <summary>Global push-to-talk through Carbon's <c>RegisterEventHotKey</c>: key down and key up in any app, including
/// full-screen games, with no Accessibility or Input Monitoring permission. macOS swallows the combination, so the
/// focused app does not see it. Registration runs on the main thread; Pressed/Released are raised there.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacPushToTalkHotkey : IPushToTalkHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint KeyboardClass = 0x6B657962;     // 'keyb'
    private const uint HotKeyPressed = 5, HotKeyReleased = 6;
    private const uint DirectObject = 0x2D2D2D2D;      // '----'
    private const uint TypeHotKeyId = 0x686B6964;      // 'hkid'
    private const uint Signature = 0x4D72746C;         // 'Mrtl'

    private static readonly Dictionary<uint, MacPushToTalkHotkey> Registered = [];
    private static nint handler;
    private static uint nextId;

    private uint id;
    private nint hotKey;
    private bool down;

    [StructLayout(LayoutKind.Sequential)] private struct EventTypeSpec { public uint EventClass, EventKind; }
    [StructLayout(LayoutKind.Sequential)] private struct EventHotKeyId { public uint Signature, Id; }

    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(nint target, delegate* unmanaged<nint, nint, nint, int> handler,
        nuint count, EventTypeSpec* types, nint userData, nint* handlerRef);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, nint target,
        uint options, nint* hotKeyRef);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(nint hotKeyRef);
    [DllImport(Carbon)] private static extern uint GetEventKind(nint eventRef);
    [DllImport(Carbon)] private static extern int GetEventParameter(nint eventRef, uint name, uint desiredType, uint* actualType,
        nuint bufferSize, nuint* actualSize, void* data);

    public FeatureStatus Status { get; private set; } =
        FeatureStatus.Yes($"Global push-to-talk works in any app without extra permissions; {MacHotkeyMap.Suggested} is a good choice on a Mac.");

    public event EventHandler? Pressed;
    public event EventHandler? Released;

    public Task<FeatureStatus> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
    {
        if (!MacHotkeyMap.TryMap(gesture, out var keyCode, out var modifiers, out var problem))
            return Task.FromResult(FeatureStatus.No(problem));
        return Task.FromResult(MainThread.Invoke(() =>
        {
            Unregister();
            EnsureHandler();
            var newId = ++nextId;
            nint reference;
            var status = RegisterEventHotKey(keyCode, modifiers, new() { Signature = Signature, Id = newId },
                GetApplicationEventTarget(), 0, &reference);
            if (status != 0)
                return Status = FeatureStatus.No(status == -9878
                    ? $"{gesture} is already used by another app or by macOS. Choose a different push-to-talk key."
                    : $"macOS refused {gesture} as a push-to-talk key (error {status}).");
            id = newId;
            hotKey = reference;
            Registered[id] = this;
            return Status = FeatureStatus.Yes(MacHotkeyMap.Note(gesture));
        }));
    }

    public void Unregister() => MainThread.Invoke(() =>
    {
        if (hotKey == 0) return false;
        UnregisterEventHotKey(hotKey);
        hotKey = 0;
        Registered.Remove(id);
        if (down)
        {
            down = false;
            Released?.Invoke(this, EventArgs.Empty);
        }
        return true;
    });

    public void Dispose() => Unregister();

    private static void EnsureHandler()
    {
        if (handler != 0) return;
        var types = stackalloc EventTypeSpec[2];
        types[0] = new() { EventClass = KeyboardClass, EventKind = HotKeyPressed };
        types[1] = new() { EventClass = KeyboardClass, EventKind = HotKeyReleased };
        nint reference;
        var status = InstallEventHandler(GetApplicationEventTarget(), &OnEvent, 2, types, 0, &reference);
        if (status != 0) throw new InvalidOperationException($"macOS refused the push-to-talk event handler (error {status}).");
        handler = reference;
    }

    [UnmanagedCallersOnly]
    private static int OnEvent(nint callRef, nint eventRef, nint userData)
    {
        try
        {
            EventHotKeyId hk;
            if (GetEventParameter(eventRef, DirectObject, TypeHotKeyId, null, (nuint)sizeof(EventHotKeyId), null, &hk) != 0 ||
                hk.Signature != Signature || !Registered.TryGetValue(hk.Id, out var key))
                return -9874; // eventNotHandledErr
            var pressed = GetEventKind(eventRef) == HotKeyPressed;
            if (pressed == key.down) return 0; // one Pressed per hold
            key.down = pressed;
            (pressed ? key.Pressed : key.Released)?.Invoke(key, EventArgs.Empty);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Push-to-talk hot key handler failed: {exception.Message}");
            return 0;
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Martlet.Companion.Platform;
using Martlet.Platform.MacOS.Native;

namespace Martlet.Platform.MacOS;

/// <summary>Martlet's menu bar item (NSStatusItem). Unlike Avalonia's tray icon it shows the state as text next to the
/// icon area ("Martlet ● Listening"), readable at a glance while a game is in front. The item appears on the first
/// <see cref="Show"/> (after the app has started) and its menu raises <see cref="Command"/> on the main thread.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacTrayStatus : ITrayStatus
{
    private static readonly Dictionary<long, (MacTrayStatus Owner, TrayCommand Command)> Actions = [];
    private static nint target;
    private static long nextTag;

    private nint item;

    public FeatureStatus Status { get; } = FeatureStatus.Yes("Menu bar item with the current state.");

    public event EventHandler<TrayCommand>? Command;

    public void Show(TrayState state, string tooltip) => MainThread.Post(() =>
    {
        if (item == 0) Create();
        WithString(MacTrayText.Title(state), text => ObjC.SendVoid(Button(), "setTitle:", text));
        WithString(tooltip, text => ObjC.SendVoid(Button(), "setToolTip:", text));
    });

    public void Dispose() => MainThread.Post(() =>
    {
        if (item == 0) return;
        ObjC.SendVoid(ObjC.Send(ObjC.Class("NSStatusBar"), "systemStatusBar"), "removeStatusItem:", item);
        ObjC.Release(item);
        item = 0;
        foreach (var tag in Actions.Where(a => a.Value.Owner == this).Select(a => a.Key).ToArray()) Actions.Remove(tag);
    });

    private void Create()
    {
        // NSVariableStatusItemLength (-1): as wide as the title.
        item = ObjC.Retain(ObjC.SendDouble(ObjC.Send(ObjC.Class("NSStatusBar"), "systemStatusBar"), "statusItemWithLength:", -1));
        EnsureTarget();
        var menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), "alloc"), "initWithTitle:", Empty());
        foreach (var (title, command) in MacTrayText.Menu)
        {
            if (title is null)
            {
                ObjC.SendVoid(menu, "addItem:", ObjC.Send(ObjC.Class("NSMenuItem"), "separatorItem"));
                continue;
            }
            var tag = ++nextTag;
            Actions[tag] = (this, command);
            var text = ObjC.NewString(title);
            var key = ObjC.NewString(command == TrayCommand.Quit ? "q" : "");
            var menuItem = ObjC.Send(ObjC.Send(ObjC.Class("NSMenuItem"), "alloc"), "initWithTitle:action:keyEquivalent:",
                text, ObjC.Sel("martletInvoke:"), key);
            ObjC.Release(text);
            ObjC.Release(key);
            ObjC.SendVoid(menuItem, "setTag:", (nint)tag);
            ObjC.SendVoid(menuItem, "setTarget:", target);
            ObjC.SendVoid(menu, "addItem:", menuItem);
            ObjC.Release(menuItem);
        }
        ObjC.SendVoid(item, "setMenu:", menu);
        ObjC.Release(menu);
    }

    private nint Button() => ObjC.Send(item, "button");

    private static nint Empty() => ObjC.Send(ObjC.Class("NSString"), "string");

    private static void WithString(string value, Action<nint> use)
    {
        var text = ObjC.NewString(value);
        try { use(text); }
        finally { ObjC.Release(text); }
    }

    private static void EnsureTarget()
    {
        if (target != 0) return;
        var cls = ObjC.objc_getClass("MartletMenuTarget");
        if (cls == 0)
        {
            cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "MartletMenuTarget", 0);
            ObjC.class_addMethod(cls, ObjC.Sel("martletInvoke:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&Invoke, "v@:@");
            ObjC.objc_registerClassPair(cls);
        }
        target = ObjC.Send(ObjC.Send(cls, "alloc"), "init");
    }

    [UnmanagedCallersOnly]
    private static void Invoke(nint self, nint selector, nint sender)
    {
        try
        {
            if (Actions.TryGetValue(ObjC.SendLong(sender, "tag"), out var action))
                action.Owner.Command?.Invoke(action.Owner, action.Command);
        }
        catch (Exception exception) { Console.Error.WriteLine($"Menu bar action failed: {exception.Message}"); }
    }
}

/// <summary>The menu bar item's words; pure, unit tested.</summary>
public static class MacTrayText
{
    public static string Title(TrayState state) => state switch
    {
        TrayState.Listening => "Martlet ● Listening",
        TrayState.Thinking => "Martlet … Thinking",
        TrayState.Speaking => "Martlet ♪ Speaking",
        TrayState.Problem => "Martlet ⚠",
        _ => "Martlet"
    };

    /// <summary>Menu entries in order; a null title is a separator.</summary>
    public static IReadOnlyList<(string? Title, TrayCommand Command)> Menu { get; } =
    [
        ("Show Martlet", TrayCommand.ShowWindow),
        ("Show or Hide Character", TrayCommand.ToggleCharacter),
        (null, default),
        ("Quit Martlet", TrayCommand.Quit)
    ];
}

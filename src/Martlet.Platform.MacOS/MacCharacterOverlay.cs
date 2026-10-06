using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Martlet.Companion.Platform;
using Martlet.Platform.MacOS.Native;

namespace Martlet.Platform.MacOS;

/// <summary>Turns the companion's character window (an Avalonia window, by its NSWindow handle) into an overlay for games:
/// floating level, on every Space and beside full-screen apps, transparent, never key or main (so clicking it never takes
/// focus from the game) and click-through everywhere except the character. Call on the main (UI) thread.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacCharacterOverlay : ICharacterOverlay
{
    private readonly Dictionary<nint, OverlayWindow> windows = [];

    public FeatureStatus Status { get; } = FeatureStatus.Yes(
        "The character floats over full-screen games on every Space without taking focus; clicks pass through except on the character.");

    public FeatureStatus Attach(NativeWindow window, OverlayBehavior behavior)
    {
        if (MacOverlayRules.AttachProblem(window.Handle, window.Descriptor) is { } problem) return FeatureStatus.No(problem);
        Detach(window);
        windows[window.Handle] = new OverlayWindow(window.Handle, behavior);
        return FeatureStatus.Yes();
    }

    public void SetInteractiveRegions(NativeWindow window, IReadOnlyList<PixelRect> regions)
    {
        if (windows.TryGetValue(window.Handle, out var overlay)) overlay.SetRegions(regions);
    }

    public void Detach(NativeWindow window)
    {
        if (windows.Remove(window.Handle, out var overlay)) overlay.Dispose();
    }

    private sealed unsafe class OverlayWindow : IDisposable
    {
        private static readonly Dictionary<nint, nint> SubclassFor = [];
        private readonly nint window;
        private readonly bool clickThrough;
        private OverlayRect[] regions = [];
        private GCHandle self;
        private nint timer;
        private bool takingMouse = true;

        [StructLayout(LayoutKind.Sequential)]
        private struct TimerContext { public nint Version, Info, Retain, Release, CopyDescription; }

        public OverlayWindow(nint nsWindow, OverlayBehavior behavior)
        {
            window = nsWindow;
            if (behavior.HasFlag(OverlayBehavior.NonActivating)) MakeNonActivating(window);
            if (behavior.HasFlag(OverlayBehavior.AlwaysOnTop)) ObjC.SendVoid(window, "setLevel:", (nint)MacOverlayRules.FloatingLevel);
            if (behavior.HasFlag(OverlayBehavior.AllSpacesAndFullScreen))
                ObjC.SendVoid(window, "setCollectionBehavior:", (nint)MacOverlayRules.CollectionBehavior);
            if (behavior.HasFlag(OverlayBehavior.Transparent))
            {
                ObjC.SendVoidBool(window, "setOpaque:", false);
                ObjC.SendVoid(window, "setBackgroundColor:", ObjC.Send(ObjC.Class("NSColor"), "clearColor"));
                ObjC.SendVoidBool(window, "setHasShadow:", false);
            }
            ObjC.SendVoidBool(window, "setHidesOnDeactivate:", false);
            clickThrough = behavior.HasFlag(OverlayBehavior.ClickThroughExceptCharacter);
            if (!clickThrough) return;
            SetIgnoresMouse(true);
            self = GCHandle.Alloc(this);
            var context = new TimerContext { Info = GCHandle.ToIntPtr(self) };
            // 20 checks a second: two property reads each, quick enough that the character feels clickable at once.
            timer = CF.CFRunLoopTimerCreate(0, CF.CFAbsoluteTimeGetCurrent() + 0.05, 0.05, 0, 0, &OnTimer, (nint)(&context));
            CF.CFRunLoopAddTimer(CF.CFRunLoopGetMain(), timer, CF.RunLoopCommonModes);
        }

        public void SetRegions(IReadOnlyList<PixelRect> pixels)
        {
            var scale = ObjC.SendReturnDouble(window, "backingScaleFactor");
            regions = [.. pixels.Select(p => MacOverlayRules.ToPoints(p, scale))];
        }

        public void Dispose()
        {
            if (timer != 0)
            {
                CF.CFRunLoopTimerInvalidate(timer);
                CF.CFRelease(timer);
                timer = 0;
            }
            if (self.IsAllocated) self.Free();
            if (clickThrough) SetIgnoresMouse(false);
        }

        private void SetIgnoresMouse(bool ignore)
        {
            takingMouse = !ignore;
            ObjC.SendVoidBool(window, "setIgnoresMouseEvents:", ignore);
        }

        private void Track()
        {
            var mouse = ObjC.SendPoint(ObjC.Class("NSEvent"), "mouseLocation");
            var frame = ObjC.SendRect(window, "frame");
            var over = MacOverlayRules.IsOverCharacter(regions, frame.Origin.X, frame.Origin.Y, frame.Size.Width, frame.Size.Height,
                mouse.X, mouse.Y);
            var buttonDown = ObjC.SendLong(ObjC.Class("NSEvent"), "pressedMouseButtons") != 0;
            var take = MacOverlayRules.TakesMouse(over, buttonDown, takingMouse);
            if (take != takingMouse) SetIgnoresMouse(!take);
        }

        [UnmanagedCallersOnly]
        private static void OnTimer(nint timer, nint info)
        {
            try { (GCHandle.FromIntPtr(info).Target as OverlayWindow)?.Track(); }
            catch (Exception exception) { Console.Error.WriteLine($"Character overlay tracking failed: {exception.Message}"); }
        }

        /// <summary>Swaps the window to a runtime subclass of its own class (keeping all of Avalonia's behavior) that can never
        /// become key or main: the non-activating-panel behavior without replacing Avalonia's NSWindow with an NSPanel.</summary>
        private static void MakeNonActivating(nint window)
        {
            var current = ObjC.object_getClass(window);
            var currentName = Marshal.PtrToStringUTF8(ObjC.class_getName(current)) ?? "";
            if (currentName.StartsWith("MartletOverlay_", StringComparison.Ordinal)) SubclassFor[current] = current;
            if (!SubclassFor.TryGetValue(current, out var subclass))
            {
                var name = $"MartletOverlay_{currentName}";
                subclass = ObjC.objc_getClass(name);
                if (subclass == 0)
                {
                    subclass = ObjC.objc_allocateClassPair(current, name, 0);
                    ObjC.class_addMethod(subclass, ObjC.Sel("canBecomeKeyWindow"), (nint)(delegate* unmanaged<nint, nint, byte>)&No, "c@:");
                    ObjC.class_addMethod(subclass, ObjC.Sel("canBecomeMainWindow"), (nint)(delegate* unmanaged<nint, nint, byte>)&No, "c@:");
                    ObjC.objc_registerClassPair(subclass);
                }
                SubclassFor[current] = subclass;
            }
            if (current != subclass) ObjC.object_setClass(window, subclass);
            // AppKit's switch for windows whose clicks must not activate their app; private, so only when present.
            if (ObjC.SendBool(window, "respondsToSelector:", ObjC.Sel("_setPreventsActivation:")))
                ObjC.SendVoidBool(window, "_setPreventsActivation:", true);
        }

        [UnmanagedCallersOnly]
        private static byte No(nint self, nint selector) => 0;
    }
}

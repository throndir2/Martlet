using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Martlet.Platform.Linux.Native;

// The libX11/libXext boundary: only what the overlay, the push-to-talk grab and X11 capture need.
internal static unsafe partial class Xlib
{
    private const string X11 = "libX11.so.6";
    private const string Xext = "libXext.so.6";

    public const int KeyPress = 2, KeyRelease = 3, ClientMessage = 33;
    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    public const int GrabModeAsync = 1, PropModeReplace = 0, ZPixmap = 2, ShapeInput = 2, ShapeSet = 0, Unsorted = 0;
    public const long SubstructureNotifyMask = 1L << 19, SubstructureRedirectMask = 1L << 20;
    public const int IsUnmapped = 0;
    public const long InputHint = 1;
    public const nuint XA_ATOM = 4, XA_CARDINAL = 6;
    public const byte BadAccess = 10;

    [LibraryImport(X11)] public static partial int XInitThreads();
    [LibraryImport(X11)] public static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11)] public static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(X11)] public static partial nuint XDefaultRootWindow(IntPtr display);
    [LibraryImport(X11)] public static partial int XDefaultScreen(IntPtr display);
    [LibraryImport(X11)] public static partial int XConnectionNumber(IntPtr display);
    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(X11)] public static partial int XChangeProperty(IntPtr display, nuint window, nuint property, nuint type, int format, int mode, void* data, int count);
    [LibraryImport(X11)] public static partial int XSendEvent(IntPtr display, nuint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, nint mask, XEvent* e);
    [LibraryImport(X11)] public static partial int XFlush(IntPtr display);
    [LibraryImport(X11)] public static partial int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [LibraryImport(X11)] public static partial int XGetWindowAttributes(IntPtr display, nuint window, out XWindowAttributes attributes);
    [LibraryImport(X11)] public static partial int XSetWMHints(IntPtr display, nuint window, XWMHints* hints);
    [LibraryImport(X11)] public static partial nuint XGetSelectionOwner(IntPtr display, nuint selection);
    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)] public static partial nuint XStringToKeysym(string name);
    [LibraryImport(X11)] public static partial byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [LibraryImport(X11)] public static partial int XGrabKey(IntPtr display, int keycode, uint modifiers, nuint window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
    [LibraryImport(X11)] public static partial int XUngrabKey(IntPtr display, int keycode, uint modifiers, nuint window);
    [LibraryImport(X11)] public static partial int XPending(IntPtr display);
    [LibraryImport(X11)] public static partial int XNextEvent(IntPtr display, XEvent* e);
    [LibraryImport(X11)] public static partial int XPeekEvent(IntPtr display, XEvent* e);
    [LibraryImport(X11)] public static partial int XkbSetDetectableAutoRepeat(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool detectable, out int supported);
    [LibraryImport(X11)] public static partial XImage* XGetImage(IntPtr display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);
    [LibraryImport(X11)] public static partial int XFree(void* data);
    [LibraryImport(X11)] public static partial IntPtr XSetErrorHandler(IntPtr handler);

    [LibraryImport(Xext)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [LibraryImport(Xext)] public static partial void XShapeCombineRectangles(IntPtr display, nuint window, int destKind, int xOffset, int yOffset, XRectangle* rectangles, int count, int op, int ordering);
    [LibraryImport(Xext)] public static partial void XShapeCombineMask(IntPtr display, nuint window, int destKind, int xOffset, int yOffset, nuint mask, int op);

    [LibraryImport("libc", SetLastError = true)] public static partial int poll(PollFd* fds, nuint count, int timeout);

    public static void DestroyImage(XImage* image)
    {
        if (image == null) return;
        if (image->DestroyImage != null) image->DestroyImage(image);
        else { if (image->Data != null) XFree(image->Data); XFree(image); }
    }

    // Xlib's error handler is process-wide (Avalonia installs its own). Ours records errors on Martlet's own displays (a
    // refused grab answers BadAccess) and passes every other error to the handler that was there before.
    private static readonly object HandlerGate = new();
    private static IntPtr previousHandler;
    private static bool handlerInstalled;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> LastErrors = new();

    public static void TrackErrors(IntPtr display)
    {
        lock (HandlerGate)
        {
            if (!handlerInstalled)
            {
                previousHandler = XSetErrorHandler((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, XErrorEvent*, int>)&OnError);
                handlerInstalled = true;
            }
        }
        LastErrors[display] = 0;
    }

    public static void ForgetErrors(IntPtr display) => LastErrors.TryRemove(display, out _);

    /// <summary>The last X error code on a tracked display since the previous call, 0 for none.</summary>
    public static byte TakeError(IntPtr display) => LastErrors.TryGetValue(display, out var code) && LastErrors.TryUpdate(display, 0, code) ? code : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(IntPtr display, XErrorEvent* error)
    {
        if (LastErrors.ContainsKey(display)) { LastErrors[display] = error->ErrorCode; return 0; }
        var previous = previousHandler;
        return previous == IntPtr.Zero ? 0 : ((delegate* unmanaged[Cdecl]<IntPtr, XErrorEvent*, int>)previous)(display, error);
    }
}

[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] public int Type;
    [FieldOffset(0)] public XKeyEvent Key;
    [FieldOffset(0)] public XClientMessageEvent Client;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XKeyEvent
{
    public int Type;
    public nuint Serial;
    public int SendEvent;
    public IntPtr Display;
    public nuint Window, Root, Subwindow, Time;
    public int X, Y, XRoot, YRoot;
    public uint State, Keycode;
    public int SameScreen;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XClientMessageEvent
{
    public int Type;
    public nuint Serial;
    public int SendEvent;
    public IntPtr Display;
    public nuint Window, MessageType;
    public int Format;
    public fixed long Data[5];
}

[StructLayout(LayoutKind.Sequential)]
internal struct XErrorEvent
{
    public int Type;
    public IntPtr Display;
    public nuint ResourceId, Serial;
    public byte ErrorCode, RequestCode, MinorCode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XWindowAttributes
{
    public int X, Y, Width, Height, BorderWidth, Depth;
    public IntPtr Visual;
    public nuint Root;
    public int Class, BitGravity, WinGravity, BackingStore;
    public nuint BackingPlanes, BackingPixel;
    public int SaveUnder;
    public nuint Colormap;
    public int MapInstalled, MapState;
    public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
    public int OverrideRedirect;
    public IntPtr Screen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XWMHints
{
    public nint Flags;
    public int Input, InitialState;
    public nuint IconPixmap, IconWindow;
    public int IconX, IconY;
    public nuint IconMask, WindowGroup;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XRectangle
{
    public short X, Y;
    public ushort Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XImage
{
    public int Width, Height, XOffset, Format;
    public byte* Data;
    public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
    public nuint RedMask, GreenMask, BlueMask;
    public IntPtr ObData;
    public IntPtr CreateImage;
    public delegate* unmanaged[Cdecl]<XImage*, int> DestroyImage;
    public IntPtr GetPixel, PutPixel, SubImage, AddPixel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int Fd;
    public short Events, Revents;
}

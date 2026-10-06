using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Native;
using static Martlet.Platform.Linux.Native.Xlib;

namespace Martlet.Platform.Linux.Capture;

/// <summary>One look at the whole X11 screen (every monitor, as the X server arranges them) through XGetImage of the root
/// window, on a short-lived X connection. Under a Wayland session this would only see X11 windows, so it is not used there.</summary>
internal static unsafe class X11Capture
{
    public static ScreenShot? Capture(ScreenCaptureRequest request)
    {
        if (!OperatingSystem.IsLinux() || !NativeLibraries.HasX11) return null;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return null;
        TrackErrors(display);
        XImage* image = null;
        try
        {
            var root = XDefaultRootWindow(display);
            if (XGetWindowAttributes(display, root, out var attributes) == 0 || attributes.Width <= 0 || attributes.Height <= 0) return null;
            image = XGetImage(display, root, 0, 0, (uint)attributes.Width, (uint)attributes.Height, nuint.MaxValue, ZPixmap);
            if (image == null || image->Data == null || image->BitsPerPixel != 32) return null;
            var format = Layout(image->RedMask, image->BlueMask, image->ByteOrder);
            if (format is null) return null;
            var length = image->BytesPerLine * image->Height;
            return FrameEncoder.Encode(new ReadOnlySpan<byte>(image->Data, length), image->Width, image->Height, image->BytesPerLine,
                format.Value, request);
        }
        finally
        {
            if (image != null)
            {
                if (image->Data != null) new Span<byte>(image->Data, image->BytesPerLine * image->Height).Clear();
                DestroyImage(image);
            }
            ForgetErrors(display);
            XCloseDisplay(display);
        }
    }

    /// <summary>The byte layout of a 32-bit ZPixmap from its channel masks (LSBFirst = 0 is little-endian).</summary>
    internal static SpaVideoFormat? Layout(nuint redMask, nuint blueMask, int byteOrder)
    {
        if (byteOrder != 0) return null;
        return (redMask, blueMask) switch
        {
            (0xFF0000, 0xFF) => SpaVideoFormat.BGRx,
            (0xFF, 0xFF0000) => SpaVideoFormat.RGBx,
            _ => null
        };
    }
}

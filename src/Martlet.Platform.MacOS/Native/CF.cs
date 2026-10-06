using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Martlet.Platform.MacOS.Native;

/// <summary>The few CoreFoundation calls the Keychain, power and run-loop code needs. Every <c>Create</c> result is +1 and
/// released by the caller.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe class CF
{
    public const string Path = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly nint Lib = NativeLibrary.Load(Path);

    public static readonly nint BooleanTrue = Constant(Lib, "kCFBooleanTrue");
    public static readonly nint BooleanFalse = Constant(Lib, "kCFBooleanFalse");
    public static readonly nint RunLoopCommonModes = Constant(Lib, "kCFRunLoopCommonModes");
    private static readonly nint KeyCallBacks = NativeLibrary.GetExport(Lib, "kCFTypeDictionaryKeyCallBacks");
    private static readonly nint ValueCallBacks = NativeLibrary.GetExport(Lib, "kCFTypeDictionaryValueCallBacks");

    [DllImport(Path)] public static extern void CFRelease(nint cf);
    [DllImport(Path)] private static extern nint CFStringCreateWithCharacters(nint alloc, char* chars, nint length);
    [DllImport(Path)] public static extern nint CFStringGetLength(nint str);
    [DllImport(Path)] public static extern void CFStringGetCharacters(nint str, CFRange range, char* buffer);
    [DllImport(Path)] private static extern nint CFDataCreate(nint alloc, byte* bytes, nint length);
    [DllImport(Path)] public static extern nint CFDataGetLength(nint data);
    [DllImport(Path)] public static extern byte* CFDataGetBytePtr(nint data);
    [DllImport(Path)] private static extern nint CFDictionaryCreateMutable(nint alloc, nint capacity, nint keyCallBacks, nint valueCallBacks);
    [DllImport(Path)] public static extern void CFDictionarySetValue(nint dict, nint key, nint value);
    [DllImport(Path)] public static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(Path)] public static extern double CFDateGetAbsoluteTime(nint date);
    [DllImport(Path)] public static extern nint CFGetTypeID(nint cf);
    [DllImport(Path)] public static extern nint CFDateGetTypeID();
    [DllImport(Path)] public static extern nint CFRunLoopGetMain();
    [DllImport(Path)] public static extern double CFAbsoluteTimeGetCurrent();
    [DllImport(Path)] public static extern nint CFRunLoopTimerCreate(nint alloc, double fireDate, double interval, nuint flags, nint order,
        delegate* unmanaged<nint, nint, void> callout, nint context);
    [DllImport(Path)] public static extern void CFRunLoopAddTimer(nint runLoop, nint timer, nint mode);
    [DllImport(Path)] public static extern void CFRunLoopTimerInvalidate(nint timer);

    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange(nint location, nint length)
    {
        public nint Location = location, Length = length;
    }

    /// <summary>The value of an exported <c>const CFTypeRef</c> (the export is the variable's address).</summary>
    public static nint Constant(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);

    public static nint String(ReadOnlySpan<char> value)
    {
        fixed (char* p = value) return CFStringCreateWithCharacters(0, p, value.Length);
    }

    public static nint Data(ReadOnlySpan<byte> value)
    {
        fixed (byte* p = value) return CFDataCreate(0, p, value.Length);
    }

    public static nint MutableDictionary() => CFDictionaryCreateMutable(0, 0, KeyCallBacks, ValueCallBacks);

    public static string? ReadString(nint str)
    {
        if (str == 0) return null;
        var length = CFStringGetLength(str);
        var chars = new char[length];
        fixed (char* p = chars) CFStringGetCharacters(str, new(0, length), p);
        return new string(chars);
    }

    /// <summary>CFAbsoluteTime counts seconds from 2001-01-01 UTC.</summary>
    public static DateTimeOffset FromAbsoluteTime(double seconds) =>
        new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
}

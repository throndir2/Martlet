using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Martlet.Platform.MacOS.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint(double x, double y)
{
    public double X = x, Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height)
{
    public double Width = width, Height = height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect(double x, double y, double width, double height)
{
    public CGPoint Origin = new(x, y);
    public CGSize Size = new(width, height);
}

/// <summary>The Objective-C runtime and typed <c>objc_msgSend</c> calls. Each overload casts the one trampoline to the
/// exact C prototype, as Apple requires on arm64 (no variadic calls).</summary>
[SupportedOSPlatform("macos")]
internal static unsafe class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint Lib = NativeLibrary.Load(LibObjC);
    private static readonly nint MsgSend = NativeLibrary.GetExport(Lib, "objc_msgSend");
    // Structs over 16 bytes come back through a hidden pointer on x86_64 only; arm64 has no _stret variant.
    private static readonly nint MsgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? NativeLibrary.GetExport(Lib, "objc_msgSend_stret") : 0;

    [DllImport(LibObjC, CharSet = CharSet.Ansi)] public static extern nint objc_getClass(string name);
    [DllImport(LibObjC, CharSet = CharSet.Ansi)] public static extern nint sel_registerName(string name);
    [DllImport(LibObjC, CharSet = CharSet.Ansi)] public static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);
    [DllImport(LibObjC)] public static extern void objc_registerClassPair(nint cls);
    [DllImport(LibObjC, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);
    [DllImport(LibObjC)] public static extern nint object_getClass(nint obj);
    [DllImport(LibObjC)] public static extern nint object_setClass(nint obj, nint cls);
    [DllImport(LibObjC)] public static extern nint class_getName(nint cls);

    public static nint Class(string name) =>
        objc_getClass(name) is var cls && cls != 0 ? cls : throw new PlatformNotSupportedException($"Objective-C class {name} is missing.");

    public static nint Sel(string name) => sel_registerName(name);

    public static nint Send(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, nint>)MsgSend)(receiver, Sel(selector));
    public static nint Send(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a);
    public static nint Send(nint receiver, string selector, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a, b);
    public static nint Send(nint receiver, string selector, nint a, nint b, nint c) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a, b, c);
    public static nint SendDouble(nint receiver, string selector, double a) =>
        ((delegate* unmanaged<nint, nint, double, nint>)MsgSend)(receiver, Sel(selector), a);
    public static nint SendULongPtr(nint receiver, string selector, ulong a, nint b) =>
        ((delegate* unmanaged<nint, nint, ulong, nint, nint>)MsgSend)(receiver, Sel(selector), a, b);

    public static void SendVoid(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, void>)MsgSend)(receiver, Sel(selector));
    public static void SendVoid(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(receiver, Sel(selector), a);
    public static void SendVoidBool(nint receiver, string selector, bool a) =>
        ((delegate* unmanaged<nint, nint, byte, void>)MsgSend)(receiver, Sel(selector), a ? (byte)1 : (byte)0);
    public static void SendVoidDouble(nint receiver, string selector, double a) =>
        ((delegate* unmanaged<nint, nint, double, void>)MsgSend)(receiver, Sel(selector), a);

    public static bool SendBool(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, byte>)MsgSend)(receiver, Sel(selector)) != 0;
    public static bool SendBool(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, byte>)MsgSend)(receiver, Sel(selector), a) != 0;
    public static long SendLong(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, long>)MsgSend)(receiver, Sel(selector));
    public static double SendReturnDouble(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, double>)MsgSend)(receiver, Sel(selector));

    /// <summary>A 16-byte all-double struct comes back in registers on both architectures.</summary>
    public static CGPoint SendPoint(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, CGPoint>)MsgSend)(receiver, Sel(selector));

    public static CGRect SendRect(nint receiver, string selector)
    {
        if (MsgSendStret == 0) return ((delegate* unmanaged<nint, nint, CGRect>)MsgSend)(receiver, Sel(selector));
        CGRect rect;
        ((delegate* unmanaged<CGRect*, nint, nint, void>)MsgSendStret)(&rect, receiver, Sel(selector));
        return rect;
    }

    /// <summary>A +1 NSString (the caller releases it).</summary>
    public static nint NewString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return Send(Send(Class("NSString"), "alloc"), "initWithUTF8String:", (nint)p);
    }

    public static string? ReadString(nint nsString)
    {
        if (nsString == 0) return null;
        var utf8 = Send(nsString, "UTF8String");
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static void Release(nint obj)
    {
        if (obj != 0) SendVoid(obj, "release");
    }

    public static nint Retain(nint obj) => obj == 0 ? 0 : Send(obj, "retain");

    public static bool IsMainThread() => SendBool(Class("NSThread"), "isMainThread");
}

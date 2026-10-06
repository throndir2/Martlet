using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Martlet.Platform.MacOS.Native;

/// <summary>Runs work on the main thread through libdispatch (<c>dispatch_async_f</c> / <c>dispatch_sync_f</c> on the main
/// queue). AppKit and Carbon calls must happen there; Avalonia's UI thread on macOS is the main thread.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MainThread
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private static readonly nint MainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");

    [DllImport(LibSystem)] private static extern void dispatch_async_f(nint queue, nint context, delegate* unmanaged<nint, void> work);
    [DllImport(LibSystem)] private static extern void dispatch_sync_f(nint queue, nint context, delegate* unmanaged<nint, void> work);

    public static void Post(Action action) => dispatch_async_f(MainQueue, GCHandle.ToIntPtr(GCHandle.Alloc(action)), &Run);

    /// <summary>Runs <paramref name="func"/> on the main thread and returns its result (directly when already there).</summary>
    public static T Invoke<T>(Func<T> func)
    {
        if (ObjC.IsMainThread()) return func();
        T result = default!;
        Exception? error = null;
        Action work = () =>
        {
            try { result = func(); }
            catch (Exception exception) { error = exception; }
        };
        dispatch_sync_f(MainQueue, GCHandle.ToIntPtr(GCHandle.Alloc(work)), &Run);
        if (error is not null) throw new InvalidOperationException(error.Message, error);
        return result;
    }

    [UnmanagedCallersOnly]
    private static void Run(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        try { (handle.Target as Action)?.Invoke(); }
        catch (Exception exception) { Console.Error.WriteLine($"Main-thread work failed: {exception.Message}"); }
        finally { handle.Free(); }
    }
}

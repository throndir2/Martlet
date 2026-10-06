using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Martlet.Discord.Calls;

/// <summary>Windows' own OCR (Windows.Media.Ocr, in the user's profile languages) on a BGRA picture, entirely on this PC: no
/// download, no network, nothing kept. Reached through the Windows Runtime's ABI directly so Martlet needs no Windows SDK
/// projection. Returns nothing where Windows has no OCR language or the API is missing.</summary>
public sealed class WindowsCallTextReader : ICallTextReader
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    /// <summary>Whether this Windows can read text (an OCR engine exists for the user's languages); checked once.</summary>
    public static bool Available => available.Value;
    private static readonly Lazy<bool> available = new(() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && WinRtOcr.ProbeAsync().GetAwaiter().GetResult());

    public Task<IReadOnlyList<TextLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return Task.FromResult<IReadOnlyList<TextLine>>([]);
        // Its own worker thread in the multithreaded apartment: never the UI thread.
        return WinRtOcr.ReadAsync(bgra, width, height, Limit, token);
    }
}

[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class WinRtOcr
{
    internal static Task<IReadOnlyList<TextLine>> ReadAsync(byte[] bgra, int width, int height, TimeSpan limit, CancellationToken token) =>
        Task.Run(() => Read(bgra, width, height, limit, token), token);

    private static readonly Guid OcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");
    private static readonly Guid SoftwareBitmapFactory = new("C99FEB69-2D62-4D47-A6B3-4FDB6A07FDF8");
    private static readonly Guid MemoryBuffer = new("FBC4DD2A-245B-11E4-AF98-689423260CF8");
    private static readonly Guid MemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
    private static readonly Guid Closable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid AsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private const int Bgra8 = 87, Premultiplied = 0, WriteAccess = 2;

    internal static Task<bool> ProbeAsync() => Task.Run(Probe);

    internal static bool Probe()
    {
        Apartment();
        nint statics = 0, engine = 0;
        try
        {
            statics = Factory("Windows.Media.Ocr.OcrEngine", OcrEngineStatics);
            Check(Call(statics, 10, &engine));
            return engine != 0;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException) { return false; }
        finally
        {
            Release(engine);
            Release(statics);
        }
    }

    internal static IReadOnlyList<TextLine> Read(byte[] bgra, int width, int height, TimeSpan limit, CancellationToken token)
    {
        Apartment();
        nint statics = 0, engine = 0, bitmaps = 0, bitmap = 0, operation = 0, info = 0, result = 0, lines = 0;
        try
        {
            statics = Factory("Windows.Media.Ocr.OcrEngine", OcrEngineStatics);
            uint maximum;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(statics, 6))(statics, &maximum));
            if (width > maximum || height > maximum) return [];
            Check(Call(statics, 10, &engine));
            if (engine == 0) return [];
            bitmaps = Factory("Windows.Graphics.Imaging.SoftwareBitmap", SoftwareBitmapFactory);
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint*, int>)Slot(bitmaps, 7))(
                bitmaps, Bgra8, width, height, Premultiplied, &bitmap));
            Fill(bitmap, bgra, width, height);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(engine, 6))(engine, bitmap, &operation));
            info = Query(operation, AsyncInfo);
            var started = Environment.TickCount64;
            int status;
            while (true)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));
                if (status != 0) break;
                if (token.IsCancellationRequested || Environment.TickCount64 - started > limit.TotalMilliseconds)
                {
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 9))(info);
                    token.ThrowIfCancellationRequested();
                    return [];
                }
                Thread.Sleep(5);
            }
            if (status != 1) return [];
            Check(Call(operation, 8, &result));
            Check(Call(result, 6, &lines));
            return Lines(lines);
        }
        finally
        {
            Release(lines);
            Release(result);
            if (info != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 10))(info);
            Release(info);
            Release(operation);
            Release(bitmap);
            Release(bitmaps);
            Release(engine);
            Release(statics);
        }
    }

    // Copies the picture into the bitmap's own buffer, row by row (its stride can be wider).
    private static void Fill(nint bitmap, byte[] bgra, int width, int height)
    {
        nint buffer = 0, memory = 0, reference = 0, access = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(bitmap, 15))(bitmap, WriteAccess, &buffer));
            Plane plane;
            Check(((delegate* unmanaged[Stdcall]<nint, int, Plane*, int>)Slot(buffer, 7))(buffer, 0, &plane));
            memory = Query(buffer, MemoryBuffer);
            Check(Call(memory, 6, &reference));
            access = Query(reference, MemoryBufferByteAccess);
            byte* data;
            uint capacity;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)Slot(access, 3))(access, &data, &capacity));
            var row = width * 4;
            if (plane.Stride < row || plane.StartIndex + (long)plane.Stride * (height - 1) + row > capacity)
                throw new InvalidOperationException("The bitmap's buffer is smaller than the picture.");
            fixed (byte* source = bgra)
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(source + y * row, data + plane.StartIndex + y * plane.Stride, row, row);
        }
        finally
        {
            Release(access);
            Close(reference);
            Release(reference);
            Release(memory);
            Close(buffer);
            Release(buffer);
        }
    }

    private static List<TextLine> Lines(nint view)
    {
        var found = new List<TextLine>();
        uint count;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(view, 7))(view, &count));
        for (uint i = 0; i < Math.Min(count, 64u); i++)
        {
            nint line = 0, words = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(view, 6))(view, i, &line));
                var text = String(line, 7);
                Check(Call(line, 6, &words));
                uint wordCount;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(words, 7))(words, &wordCount));
                float left = float.MaxValue, top = float.MaxValue, right = 0, bottom = 0;
                for (uint j = 0; j < Math.Min(wordCount, 64u); j++)
                {
                    nint word = 0;
                    try
                    {
                        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(words, 6))(words, j, &word));
                        Rect box;
                        Check(((delegate* unmanaged[Stdcall]<nint, Rect*, int>)Slot(word, 6))(word, &box));
                        left = Math.Min(left, box.X);
                        top = Math.Min(top, box.Y);
                        right = Math.Max(right, box.X + box.Width);
                        bottom = Math.Max(bottom, box.Y + box.Height);
                    }
                    finally { Release(word); }
                }
                if (text.Length > 0 && right > left)
                    found.Add(new(text, new((int)left, (int)top, (int)Math.Ceiling(right - left), (int)Math.Ceiling(bottom - top))));
            }
            finally
            {
                Release(words);
                Release(line);
            }
        }
        return found;
    }

    private static string String(nint instance, int slot)
    {
        nint text = 0;
        Check(Call(instance, slot, &text));
        try
        {
            if (text == 0) return "";
            uint length;
            var raw = WindowsGetStringRawBuffer(text, &length);
            return new string(raw, 0, (int)Math.Min(length, 1024u));
        }
        finally { if (text != 0) WindowsDeleteString(text); }
    }

    private static nint Factory(string name, Guid iid)
    {
        nint text = 0, factory = 0;
        Check(WindowsCreateString(name, (uint)name.Length, &text));
        try { Check(RoGetActivationFactory(text, &iid, &factory)); }
        finally { WindowsDeleteString(text); }
        return factory;
    }

    private static nint Query(nint instance, Guid iid)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    private static void Close(nint instance)
    {
        if (instance == 0) return;
        nint closable = 0;
        var iid = Closable;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &closable) < 0 || closable == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable);
        Release(closable);
    }

    private static int Call(nint instance, int slot, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, result);

    private static void* Slot(nint instance, int slot)
    {
        if (instance == 0) throw new InvalidOperationException("A Windows Runtime object is missing.");
        return (*(void***)instance)[slot];
    }

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static void Check(int result)
    {
        if (result < 0) throw new COMException("Windows OCR failed.", result);
    }

    // The thread joins the multithreaded apartment (already joined, or another apartment, is fine).
    private static void Apartment() => CoInitializeEx(0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Plane { public int StartIndex, Width, Height, Stride; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public float X, Y, Width, Height; }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, nint* text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint text);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(nint text, uint* length);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint model);
}

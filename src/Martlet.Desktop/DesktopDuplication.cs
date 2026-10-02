using System.Runtime.InteropServices;

namespace Martlet.Desktop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public static NativeRect Intersect(NativeRect a, NativeRect b) => new()
    {
        Left = Math.Max(a.Left, b.Left), Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right), Bottom = Math.Min(a.Bottom, b.Bottom)
    };
}

/// <summary>Reads one monitor through the DXGI Desktop Duplication API: exactly what the monitor shows, including
/// full-screen DirectX games, without hooking or injecting anything into the game (nothing an anti-cheat objects to).
/// Protected video is blacked out by Windows. The duplication stays open while watching so each look only copies the
/// newest frame; <see cref="Release"/> frees it. Calls are serialized; pixels are downscaled on the way out.</summary>
internal sealed class DesktopDuplication
{
    private const int WaitTimeout = unchecked((int)0x887A0027), AccessLost = unchecked((int)0x887A0026),
        Unsupported = unchecked((int)0x887A0004);
    private const uint FormatBgra = 87, FormatBgrx = 88, UsageStaging = 3, CpuRead = 0x20000, MapRead = 1;
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Output1Id = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private nint device, context, duplication, staging, monitor, failedMonitor;
    private NativeRect desktop;
    private int textureWidth, textureHeight;
    private bool hasFrame;
    private long failedAt;

    /// <summary>Why duplication is unavailable right now (null when it works), for the watch status.</summary>
    internal string? Problem { get; private set; }
    /// <summary>Windows masked protected content (DRM video) out of the last frame.</summary>
    internal bool ProtectedContent { get; private set; }

    /// <summary>Downscaled BGRA pixels of <paramref name="area"/> (desktop coordinates on <paramref name="target"/>),
    /// or null when duplication is unavailable and the caller should fall back to GDI.</summary>
    internal byte[]? Grab(nint target, NativeRect area, int width, int height)
    {
        lock (gate)
        {
            if (monitor != target) ReleaseLocked();
            // After a refusal, retry the same monitor only every 30 s rather than on every look.
            if (duplication == 0 && failedMonitor == target && Environment.TickCount64 - failedAt < RetryAfterFailure.TotalMilliseconds)
                return null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (duplication == 0 && !OpenLocked(target)) return null;
                var result = AcquireLocked();
                if (result == AccessLost)
                {
                    // A mode switch (a game entering or leaving full screen), a desktop switch or a resolution change.
                    ReleaseDuplicationLocked();
                    continue;
                }
                if (result < 0)
                {
                    var reason = Problem ?? "the screen could not be captured";
                    ReleaseLocked();
                    return Refuse(target, reason);
                }
                // A brand-new duplication that has not produced its first image yet: try again next look.
                if (!hasFrame) return null;
                return ReadLocked(target, area, width, height);
            }
            ReleaseLocked();
            return Refuse(target, "the display mode kept changing");
        }
    }

    internal void Release()
    {
        lock (gate) ReleaseLocked();
    }

    private bool OpenLocked(nint target)
    {
        nint factory = 0, adapter = 0, output = 0, output1 = 0;
        string? refusal = null;
        try
        {
            var id = FactoryId;
            if (CreateDXGIFactory1(ref id, out factory) < 0) { refusal = "Windows screen capture is unavailable"; return false; }
            // The device must live on the GPU that drives this monitor (laptops with two GPUs).
            for (uint a = 0; output == 0 && Call<Enumerate>(factory, 12)(factory, a, out var candidate) >= 0; a++)
            {
                for (uint o = 0; output == 0 && Call<Enumerate>(candidate, 7)(candidate, o, out var screen) >= 0; o++)
                {
                    if (Call<GetOutputDescription>(screen, 7)(screen, out var description) >= 0 && description.Monitor == target)
                    {
                        output = screen;
                        desktop = description.DesktopCoordinates;
                        if (description.Rotation > 1) { refusal = "rotated monitors aren't supported for full-screen capture"; return false; }
                    }
                    else Marshal.Release(screen);
                }
                if (output != 0) adapter = candidate;
                else Marshal.Release(candidate);
            }
            if (output == 0) { refusal = "the monitor couldn't be found"; return false; }
            if (D3D11CreateDevice(adapter, 0, 0, 0x20, 0, 0, 7, out device, out _, out context) < 0)
                { refusal = "Windows graphics capture is unavailable"; return false; }
            if (Marshal.QueryInterface(output, in Output1Id, out output1) < 0) { refusal = "this version of Windows doesn't support full-screen capture"; return false; }
            var duplicated = Call<DuplicateOutput>(output1, 22)(output1, device, out duplication);
            if (duplicated < 0)
            {
                duplication = 0;
                refusal = duplicated == Unsupported
                    ? "full-screen capture is unavailable on this display; try borderless or windowed mode"
                    : "Windows refused screen capture";
                return false;
            }
            monitor = target;
            Problem = null;
            return true;
        }
        finally
        {
            if (output1 != 0) Marshal.Release(output1);
            if (output != 0) Marshal.Release(output);
            if (adapter != 0) Marshal.Release(adapter);
            if (factory != 0) Marshal.Release(factory);
            if (duplication == 0)
            {
                ReleaseLocked();
                Refuse(target, refusal ?? "Windows refused screen capture");
            }
        }
    }

    // Copies the newest frame into the staging texture. A fresh duplication's first frame can be black on some drivers,
    // so it waits briefly for a second one (a game presents many per second); later looks take whatever is newest and
    // keep the previous copy when nothing changed (the screen was still).
    private int AcquireLocked()
    {
        var fresh = !hasFrame;
        var copies = 0;
        for (var i = 0; i < (fresh ? 4 : 1); i++)
        {
            var result = Call<AcquireNextFrame>(duplication, 8)(duplication, fresh ? 150u : 0u, out var info, out var resource);
            if (result == WaitTimeout) continue;
            if (result < 0) return result;
            nint texture = 0;
            try
            {
                if (Marshal.QueryInterface(resource, in Texture2DId, out texture) < 0 || !EnsureStagingLocked(texture)) return Unsupported;
                Call<CopyResource>(context, 47)(context, staging, texture);
                hasFrame = true;
                copies++;
                ProtectedContent = info.ProtectedContentMaskedOut != 0;
            }
            finally
            {
                if (texture != 0) Marshal.Release(texture);
                if (resource != 0) Marshal.Release(resource);
                Call<NoArguments>(duplication, 14)(duplication);
            }
            if (!fresh || copies >= 2 && info.LastPresentTime != 0) break;
        }
        return 0;
    }

    private bool EnsureStagingLocked(nint texture)
    {
        Call<GetTextureDescription>(texture, 10)(texture, out var description);
        if (description.Format is not (FormatBgra or FormatBgrx))
        {
            Problem = "the display uses an unsupported format";
            return false;
        }
        if (staging != 0 && textureWidth == description.Width && textureHeight == description.Height) return true;
        if (staging != 0) Marshal.Release(staging);
        staging = 0;
        hasFrame = false;
        var copy = new TextureDescription
        {
            Width = description.Width, Height = description.Height, MipLevels = 1, ArraySize = 1, Format = description.Format,
            SampleCount = 1, Usage = UsageStaging, CpuAccessFlags = CpuRead
        };
        if (Call<CreateTexture2D>(device, 5)(device, ref copy, 0, out staging) < 0) return false;
        textureWidth = (int)description.Width;
        textureHeight = (int)description.Height;
        return true;
    }

    private byte[]? ReadLocked(nint target, NativeRect area, int width, int height)
    {
        var x = Math.Clamp(area.Left - desktop.Left, 0, textureWidth);
        var y = Math.Clamp(area.Top - desktop.Top, 0, textureHeight);
        var areaWidth = Math.Min(area.Width, textureWidth - x);
        var areaHeight = Math.Min(area.Height, textureHeight - y);
        if (areaWidth <= 0 || areaHeight <= 0) return null;
        if (Call<Map>(context, 14)(context, staging, 0, MapRead, 0, out var mapped) < 0)
        {
            ReleaseLocked();
            return Refuse(target, "the screen could not be read");
        }
        try
        {
            var pixels = new byte[width * height * 4];
            Downscale((row, column, into, count) => Marshal.Copy(mapped.Data + row * (nint)mapped.RowPitch + column * 4, into, 0, count * 4),
                x, y, areaWidth, areaHeight, pixels, width, height);
            return pixels;
        }
        finally
        {
            Call<Unmap>(context, 15)(context, staging, 0);
        }
    }

    internal delegate void RowReader(int row, int column, byte[] into, int count);

    /// <summary>Area-averages a source region into <paramref name="destination"/> (BGRA, opaque), sampling at most 3x3
    /// source pixels per output pixel so a 4K frame costs a few milliseconds.</summary>
    internal static void Downscale(RowReader read, int x, int y, int areaWidth, int areaHeight, byte[] destination, int width, int height)
    {
        var row = new byte[areaWidth * 4];
        var sums = new int[width * 3];
        var counts = new int[width];
        try
        {
            for (var dy = 0; dy < height; dy++)
            {
                Array.Clear(sums);
                Array.Clear(counts);
                int top = dy * areaHeight / height, bottom = Math.Max(top + 1, (dy + 1) * areaHeight / height);
                var rowStep = Math.Max(1, (bottom - top + 2) / 3);
                for (var sy = top; sy < bottom && sy < areaHeight; sy += rowStep)
                {
                    read(y + sy, x, row, areaWidth);
                    for (var dx = 0; dx < width; dx++)
                    {
                        int left = dx * areaWidth / width, right = Math.Max(left + 1, (dx + 1) * areaWidth / width);
                        var columnStep = Math.Max(1, (right - left + 2) / 3);
                        for (var sx = left; sx < right && sx < areaWidth; sx += columnStep)
                        {
                            var i = sx * 4;
                            sums[dx * 3] += row[i];
                            sums[dx * 3 + 1] += row[i + 1];
                            sums[dx * 3 + 2] += row[i + 2];
                            counts[dx]++;
                        }
                    }
                }
                for (var dx = 0; dx < width; dx++)
                {
                    var o = (dy * width + dx) * 4;
                    var n = Math.Max(1, counts[dx]);
                    destination[o] = (byte)(sums[dx * 3] / n);
                    destination[o + 1] = (byte)(sums[dx * 3 + 1] / n);
                    destination[o + 2] = (byte)(sums[dx * 3 + 2] / n);
                    destination[o + 3] = 255;
                }
            }
        }
        finally
        {
            Array.Clear(row);
        }
    }

    private byte[]? Refuse(nint target, string reason)
    {
        Problem = reason;
        failedMonitor = target;
        failedAt = Environment.TickCount64;
        return null;
    }

    private void ReleaseDuplicationLocked()
    {
        if (duplication != 0) Marshal.Release(duplication);
        duplication = 0;
        hasFrame = false;
    }

    private void ReleaseLocked()
    {
        ReleaseDuplicationLocked();
        foreach (var pointer in new[] { staging, context, device })
            if (pointer != 0) Marshal.Release(pointer);
        staging = context = device = monitor = 0;
        textureWidth = textureHeight = 0;
        ProtectedContent = false;
    }

    private static T Call<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private delegate int Enumerate(nint self, uint index, out nint item);
    private delegate int GetOutputDescription(nint self, out OutputDescription description);
    private delegate int DuplicateOutput(nint self, nint device, out nint duplication);
    private delegate int AcquireNextFrame(nint self, uint timeoutMilliseconds, out FrameInfo info, out nint resource);
    private delegate int NoArguments(nint self);
    private delegate void GetTextureDescription(nint self, out TextureDescription description);
    private delegate int CreateTexture2D(nint self, ref TextureDescription description, nint initialData, out nint texture);
    private delegate void CopyResource(nint self, nint destination, nint source);
    private delegate int Map(nint self, nint resource, uint subresource, uint mapType, uint flags, out MappedSubresource mapped);
    private delegate void Unmap(nint self, nint resource, uint subresource);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public NativeRect DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch, DepthPitch;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id, out nint factory);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels,
        uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
}

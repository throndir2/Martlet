using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Martlet.Platform.Linux.Native;

// The libpipewire-0.3 boundary for taking one frame from a ScreenCast portal stream.
internal static unsafe partial class PipeWire
{
    public const string Lib = "libpipewire-0.3.so.0";

    public const int DirectionInput = 0;
    public const int FlagAutoconnect = 1 << 0, FlagMapBuffers = 1 << 2;
    public const int StreamStateError = -1;
    public const uint ParamFormat = 4;

    [LibraryImport(Lib)] public static partial void pw_init(int* argc, byte*** argv);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr pw_thread_loop_new(string name, IntPtr props);
    [LibraryImport(Lib)] public static partial IntPtr pw_thread_loop_get_loop(IntPtr loop);
    [LibraryImport(Lib)] public static partial int pw_thread_loop_start(IntPtr loop);
    [LibraryImport(Lib)] public static partial void pw_thread_loop_stop(IntPtr loop);
    [LibraryImport(Lib)] public static partial void pw_thread_loop_destroy(IntPtr loop);
    [LibraryImport(Lib)] public static partial void pw_thread_loop_lock(IntPtr loop);
    [LibraryImport(Lib)] public static partial void pw_thread_loop_unlock(IntPtr loop);
    [LibraryImport(Lib)] public static partial IntPtr pw_context_new(IntPtr loop, IntPtr props, nuint userDataSize);
    [LibraryImport(Lib)] public static partial void pw_context_destroy(IntPtr context);
    [LibraryImport(Lib)] public static partial IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, nuint userDataSize);
    [LibraryImport(Lib)] public static partial int pw_core_disconnect(IntPtr core);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr pw_properties_new_string(string args);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr pw_stream_new(IntPtr core, string name, IntPtr props);
    [LibraryImport(Lib)] public static partial void pw_stream_add_listener(IntPtr stream, void* hook, PwStreamEvents* events, IntPtr data);
    [LibraryImport(Lib)] public static partial int pw_stream_connect(IntPtr stream, int direction, uint targetId, int flags, IntPtr* parameters, uint count);
    [LibraryImport(Lib)] public static partial PwBuffer* pw_stream_dequeue_buffer(IntPtr stream);
    [LibraryImport(Lib)] public static partial int pw_stream_queue_buffer(IntPtr stream, PwBuffer* buffer);
    [LibraryImport(Lib)] public static partial int pw_stream_disconnect(IntPtr stream);
    [LibraryImport(Lib)] public static partial void pw_stream_destroy(IntPtr stream);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PwStreamEvents
{
    public uint Version;
    public IntPtr Destroy, StateChanged, ControlInfo, IoChanged, ParamChanged, AddBuffer, RemoveBuffer, Process, Drained;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PwBuffer
{
    public SpaBuffer* Buffer;
    public IntPtr UserData;
    public ulong Size, Requested;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaBuffer
{
    public uint MetaCount, DataCount;
    public IntPtr Metas;
    public SpaData* Datas;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaData
{
    public uint Type, Flags;
    public long Fd;
    public uint MapOffset, MaxSize;
    public byte* Data;
    public SpaChunk* Chunk;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpaChunk
{
    public uint Offset, Size;
    public int Stride, Flags;
}

/// <summary>SPA video formats Martlet accepts (spa/param/video/raw.h), all 32 bits per pixel.</summary>
internal enum SpaVideoFormat : uint { RGBx = 7, BGRx = 8, xRGB = 9, xBGR = 10, RGBA = 11, BGRA = 12, ARGB = 13, ABGR = 14 }

/// <summary>Builds and reads the few SPA POD values the screen grab needs: the EnumFormat offer (raw video, 32-bit RGB
/// formats in shared memory, any size) and the negotiated format. Little-endian, every value padded to 8 bytes.</summary>
internal static class SpaPod
{
    private const uint TypeId = 3, TypeRectangle = 10, TypeFraction = 11, TypeObject = 15, TypeChoice = 19;
    private const uint ObjectFormat = 0x40003, ParamEnumFormat = 3;
    private const uint KeyMediaType = 1, KeyMediaSubtype = 2, KeyVideoFormat = 0x20001, KeyVideoSize = 0x20003, KeyVideoFramerate = 0x20004;
    private const uint MediaTypeVideo = 2, MediaSubtypeRaw = 1, ChoiceRange = 1, ChoiceEnum = 3;

    public static readonly SpaVideoFormat[] Offered =
        [SpaVideoFormat.BGRx, SpaVideoFormat.BGRA, SpaVideoFormat.RGBx, SpaVideoFormat.RGBA, SpaVideoFormat.xRGB, SpaVideoFormat.xBGR];

    public static byte[] EnumFormat()
    {
        var props = new List<byte>();
        Prop(props, KeyMediaType, Pod(TypeId, U32(MediaTypeVideo)));
        Prop(props, KeyMediaSubtype, Pod(TypeId, U32(MediaSubtypeRaw)));
        Prop(props, KeyVideoFormat, Choice(ChoiceEnum, TypeId, 4,
            [.. U32((uint)Offered[0]), .. Offered.SelectMany(f => U32((uint)f))]));
        Prop(props, KeyVideoSize, Choice(ChoiceRange, TypeRectangle, 8, [.. U32(1920), .. U32(1080), .. U32(1), .. U32(1), .. U32(16384), .. U32(16384)]));
        Prop(props, KeyVideoFramerate, Choice(ChoiceRange, TypeFraction, 8, [.. U32(60), .. U32(1), .. U32(0), .. U32(1), .. U32(360), .. U32(1)]));
        return Pod(TypeObject, [.. U32(ObjectFormat), .. U32(ParamEnumFormat), .. props]);
    }

    /// <summary>The negotiated pixel format and size from a Format param, or null when it is not 32-bit raw video.</summary>
    public static (SpaVideoFormat Format, int Width, int Height)? ParseVideoFormat(ReadOnlySpan<byte> pod)
    {
        if (pod.Length < 16 || Read(pod, 4) != TypeObject) return null;
        var size = (int)Read(pod, 0);
        if (size + 8 > pod.Length) return null;
        var body = pod.Slice(8, size);
        uint? format = null;
        (uint W, uint H)? dimensions = null;
        for (var offset = 8; offset + 16 <= body.Length;)
        {
            var key = Read(body, offset);
            var valueSize = (int)Read(body, offset + 8);
            var valueType = Read(body, offset + 12);
            if (offset + 16 + valueSize > body.Length) break;
            var value = body.Slice(offset + 16, valueSize);
            var choice = valueType == TypeChoice && value.Length >= 16;
            var type = choice ? Read(value, 12) : valueType;
            var first = choice ? value[16..] : value;
            if (key == KeyVideoFormat && type == TypeId && first.Length >= 4) format = Read(first, 0);
            if (key == KeyVideoSize && type == TypeRectangle && first.Length >= 8) dimensions = (Read(first, 0), Read(first, 4));
            offset += 16 + Pad(valueSize);
        }
        if (format is not { } f || !Enum.IsDefined((SpaVideoFormat)f) || dimensions is not { } d || d.W == 0 || d.H == 0 ||
            d.W > 16384 || d.H > 16384) return null;
        return ((SpaVideoFormat)f, (int)d.W, (int)d.H);
    }

    private static void Prop(List<byte> target, uint key, byte[] pod)
    {
        target.AddRange(U32(key));
        target.AddRange(U32(0));
        target.AddRange(pod);
    }

    private static byte[] Choice(uint choiceType, uint childType, uint childSize, byte[] values) =>
        Pod(TypeChoice, [.. U32(choiceType), .. U32(0), .. U32(childSize), .. U32(childType), .. values]);

    private static byte[] Pod(uint type, byte[] body)
    {
        var pod = new byte[8 + Pad(body.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(pod, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4), type);
        body.CopyTo(pod, 8);
        return pod;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static uint Read(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
    private static int Pad(int size) => (size + 7) & ~7;
}

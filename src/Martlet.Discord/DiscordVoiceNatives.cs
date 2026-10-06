using System.Runtime.InteropServices;
using System.Text;

namespace Martlet.Discord;

/// <summary>What the voice natives did offline: whether libdave.dll loaded, the DAVE protocol version it supports, and whether
/// a DAVE session could be created and produce its MLS key package (what a voice connection sends first), plus a frame
/// encryptor and decryptor. No network: this proves the library works on this PC, not that a call connected.</summary>
public sealed record DiscordVoiceNativeReport(bool LibDaveLoaded, string? LibDavePath, int DaveProtocolVersion, bool DaveSession,
    int KeyPackageBytes, bool FrameCrypto, bool OpusManaged, string? Problem)
{
    public bool Ok => LibDaveLoaded && DaveSession && KeyPackageBytes > 0 && FrameCrypto && OpusManaged;
}

/// <summary>Checks Discord voice's native library (libdave, Discord's end-to-end voice encryption, which NetCord calls) on this
/// PC, offline. Opus is managed (Concentus) and transport encryption uses .NET's AES-GCM, so libdave is the only native.</summary>
public static class DiscordVoiceNatives
{
    private const string LibDave = "libdave";

    /// <summary>Loads libdave from <paramref name="directory"/> (default: beside this app) and runs an offline DAVE session.</summary>
    public static DiscordVoiceNativeReport Check(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, "libdave.dll");
        var opus = OpusWorks();
        if (!File.Exists(path)) return new(false, null, 0, false, 0, false, opus, "libdave.dll isn't beside Martlet.");
        if (!NativeLibrary.TryLoad(path, out var library))
            return new(false, path, 0, false, 0, false, opus, "libdave.dll didn't load (needs Windows x64).");
        try
        {
            // libdave logs to standard output by default, which would corrupt a stdio host such as Martlet.Mcp.
            Call<SetLogSink>(library, "daveSetLogSinkCallback")(QuietSink);
            var version = Call<MaxVersion>(library, "daveMaxSupportedProtocolVersion")();
            var create = Call<SessionCreate>(library, "daveSessionCreate");
            var init = Call<SessionInit>(library, "daveSessionInit");
            var keyPackage = Call<SessionKeyPackage>(library, "daveSessionGetMarshalledKeyPackage");
            var free = Call<Free>(library, "daveFree");
            var destroy = Call<Destroy>(library, "daveSessionDestroy");
            var session = create(0, [0], 0, 0);
            if (session == 0) return new(true, path, version, false, 0, false, opus, "libdave couldn't create a DAVE session.");
            var bytes = 0;
            try
            {
                init(session, version, 1, Encoding.UTF8.GetBytes("1\0"));
                keyPackage(session, out var buffer, out var length);
                bytes = (int)length;
                if (buffer != 0) free(buffer);
            }
            finally { destroy(session); }
            var encryptor = Call<Create>(library, "daveEncryptorCreate")();
            var decryptor = Call<Create>(library, "daveDecryptorCreate")();
            var crypto = encryptor != 0 && decryptor != 0;
            if (encryptor != 0) Call<Destroy>(library, "daveEncryptorDestroy")(encryptor);
            if (decryptor != 0) Call<Destroy>(library, "daveDecryptorDestroy")(decryptor);
            return new(true, path, version, true, bytes, crypto, opus,
                bytes > 0 && crypto ? null : "libdave loaded but its DAVE session didn't work.");
        }
        catch (Exception error) when (error is EntryPointNotFoundException or SEHException or InvalidOperationException)
        {
            return new(true, path, 0, false, 0, false, opus, "libdave.dll is missing DAVE functions: " + error.Message);
        }
        finally { NativeLibrary.Free(library); }
    }

    /// <summary>Whether NetCord will find libdave beside this app (it loads it by the name "libdave").</summary>
    public static bool Present(string? directory = null) => File.Exists(Path.Combine(directory ?? AppContext.BaseDirectory, LibDave + ".dll"));

    private static bool OpusWorks()
    {
        try
        {
            var frame = new short[DiscordVoiceAudio.FrameSamples * DiscordVoiceAudio.DiscordChannels];
            for (var i = 0; i < DiscordVoiceAudio.FrameSamples; i++)
                frame[2 * i] = frame[2 * i + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / DiscordVoiceAudio.DiscordRate));
            var encoded = new DiscordOpusEncoder().Encode(frame);
            return new DiscordOpusDecoder().Decode(encoded).Length == frame.Length;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static T Call<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static readonly LogSink QuietSink = (_, _, _, _) => { };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LogSink(int severity, nint file, int line, nint message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLogSink(LogSink sink);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ushort MaxVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SessionCreate(nint context, byte[] authSessionId, nint callback, nint userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SessionInit(nint session, ushort version, ulong groupId, byte[] selfUserId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SessionKeyPackage(nint session, out nint keyPackage, out nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create();
}

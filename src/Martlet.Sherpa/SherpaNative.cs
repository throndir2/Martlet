using System.Reflection;
using System.Runtime.InteropServices;

namespace Martlet.Sherpa;

/// <summary>The few sherpa-onnx C API calls Martlet uses, bound to the native libraries that ship in Martlet's folder (sherpa-onnx
/// 1.13.8, Apache-2.0, with ONNX Runtime, MIT). Configuration structs are written field by field at the offsets of the
/// v1.13.8 <c>c-api.h</c> layout for 64-bit Windows; every other field stays zero, which the C API maps to its defaults.</summary>
internal static class SherpaNative
{
    internal const string Library = "sherpa-onnx-c-api";
    private static readonly object gate = new();
    private static IntPtr handle;
    private static string? loadedFrom;

    static SherpaNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SherpaNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        name == Library ? Volatile.Read(ref handle) is var loaded && loaded != IntPtr.Zero ? loaded
            : throw new DllNotFoundException("The sherpa-onnx runtime is not loaded.") : IntPtr.Zero;

    /// <summary>Loads ONNX Runtime and sherpa-onnx from <paramref name="directory"/> once per process. ONNX Runtime goes
    /// first, so sherpa-onnx binds to that exact copy rather than any other onnxruntime.dll on the search path.</summary>
    internal static void Load(string directory)
    {
        lock (gate)
        {
            if (handle != IntPtr.Zero)
            {
                if (!string.Equals(loadedFrom, directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different sherpa-onnx runtime is already loaded; restart Martlet.");
                return;
            }
            NativeLibrary.Load(Path.Combine(directory, "onnxruntime.dll"));
            var loaded = NativeLibrary.Load(Path.Combine(directory, "sherpa-onnx-c-api.dll"));
            loadedFrom = directory;
            Volatile.Write(ref handle, loaded);
        }
    }

    internal static bool Loaded => Volatile.Read(ref handle) != IntPtr.Zero;

    // ---------- speaker embeddings ----------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxCreateSpeakerEmbeddingExtractor(IntPtr config);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroySpeakerEmbeddingExtractor(IntPtr extractor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SherpaOnnxSpeakerEmbeddingExtractorDim(IntPtr extractor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxSpeakerEmbeddingExtractorCreateStream(IntPtr extractor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxOnlineStreamAcceptWaveform(IntPtr stream, int sampleRate, float[] samples, int count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxOnlineStreamInputFinished(IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SherpaOnnxSpeakerEmbeddingExtractorIsReady(IntPtr extractor, IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(IntPtr extractor, IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(IntPtr embedding);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroyOnlineStream(IntPtr stream);

    // ---------- speaker diarization (segmentation) ----------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxCreateOfflineSpeakerDiarization(IntPtr config);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroyOfflineSpeakerDiarization(IntPtr diarizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SherpaOnnxOfflineSpeakerDiarizationGetSampleRate(IntPtr diarizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxOfflineSpeakerDiarizationProcess(IntPtr diarizer, float[] samples, int count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SherpaOnnxOfflineSpeakerDiarizationResultGetNumSegments(IntPtr result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxOfflineSpeakerDiarizationResultSortByStartTime(IntPtr result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxOfflineSpeakerDiarizationDestroySegment(IntPtr segments);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxOfflineSpeakerDiarizationDestroyResult(IntPtr result);

    // ---------- offline speech recognition ----------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxCreateOfflineRecognizer(IntPtr config);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroyOfflineRecognizer(IntPtr recognizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxCreateOfflineStream(IntPtr recognizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroyOfflineStream(IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxAcceptWaveformOffline(IntPtr stream, int sampleRate, float[] samples, int count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);

    // ---------- audio tagging ----------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxCreateAudioTagging(IntPtr config);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxDestroyAudioTagging(IntPtr tagger);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxAudioTaggingCreateOfflineStream(IntPtr tagger);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SherpaOnnxAudioTaggingCompute(IntPtr tagger, IntPtr stream, int topK);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SherpaOnnxAudioTaggingFreeResults(IntPtr results);
}

/// <summary>A zeroed native configuration struct whose fields are written at fixed offsets; strings are copied to native
/// UTF-8 and freed with the struct. sherpa-onnx copies the configuration while creating its object.</summary>
internal sealed class NativeConfig : IDisposable
{
    private readonly List<IntPtr> strings = [];
    internal IntPtr Pointer { get; }

    internal NativeConfig(int size)
    {
        Pointer = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, Pointer, size);
    }

    internal NativeConfig Text(int offset, string value)
    {
        var text = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(text);
        Marshal.WriteIntPtr(Pointer, offset, text);
        return this;
    }

    internal NativeConfig Int(int offset, int value)
    {
        Marshal.WriteInt32(Pointer, offset, value);
        return this;
    }

    internal NativeConfig Float(int offset, float value)
    {
        Marshal.WriteInt32(Pointer, offset, BitConverter.SingleToInt32Bits(value));
        return this;
    }

    public void Dispose()
    {
        foreach (var text in strings) Marshal.FreeCoTaskMem(text);
        strings.Clear();
        Marshal.FreeHGlobal(Pointer);
    }
}

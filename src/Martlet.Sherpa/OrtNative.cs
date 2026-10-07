using System.Runtime.InteropServices;
using System.Text;

namespace Martlet.Sherpa;

/// <summary>The few ONNX Runtime C API calls the end-of-turn judge uses, bound to the onnxruntime.dll that ships beside Martlet with
/// sherpa-onnx (ONNX Runtime 1.28.2, MIT). The C API is a table of function pointers (<c>OrtApi</c>) that only grows at its end;
/// the indexes below are the positions in <c>onnxruntime_c_api.h</c> of v1.28.2, asked for at API version 16, so any runtime from
/// 1.16 on serves them. No managed ONNX Runtime package is used or shipped.</summary>
internal static unsafe class OrtNative
{
    private const uint ApiVersion = 16;
    private const int LoggingError = 3;
    private const int GraphOptimizationAll = 99;
    private const int ArenaAllocator = 1;
    private const int MemTypeDefault = 0;
    private const int ElementFloat = 1;

    // Positions in the OrtApi table (v1.28.2 onnxruntime_c_api.h).
    private const int GetErrorMessageAt = 2, CreateEnvAt = 3, DisableTelemetryEventsAt = 6, CreateSessionFromArrayAt = 8, RunAt = 9,
        CreateSessionOptionsAt = 10,
        SetSessionExecutionModeAt = 13, SetSessionGraphOptimizationLevelAt = 23, SetIntraOpNumThreadsAt = 24,
        SetInterOpNumThreadsAt = 25, CreateTensorWithDataAsOrtValueAt = 49, GetTensorMutableDataAt = 51, CreateCpuMemoryInfoAt = 69,
        ReleaseStatusAt = 93, ReleaseMemoryInfoAt = 94, ReleaseSessionAt = 95, ReleaseValueAt = 96, ReleaseSessionOptionsAt = 100;

    private static readonly object gate = new();
    private static IntPtr* api;
    private static IntPtr env;
    private static string? loadedFrom;

    /// <summary>Loads ONNX Runtime from <paramref name="directory"/> once per process (the same copy sherpa-onnx uses) and creates
    /// the environment.</summary>
    internal static void Load(string directory)
    {
        lock (gate)
        {
            if (api != null)
            {
                if (!string.Equals(loadedFrom, directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different ONNX Runtime is already loaded; restart Martlet.");
                return;
            }
            var library = NativeLibrary.Load(Path.Combine(directory, "onnxruntime.dll"));
            var getBase = (delegate* unmanaged[Cdecl]<IntPtr*>)NativeLibrary.GetExport(library, "OrtGetApiBase");
            var apiBase = getBase();
            var getApi = (delegate* unmanaged[Cdecl]<uint, IntPtr*>)apiBase[0];
            var table = getApi(ApiVersion);
            if (table == null) throw new SherpaException("This ONNX Runtime doesn't offer the API the end-of-turn judge needs.");
            IntPtr created;
            var name = Utf8("martlet-turn");
            fixed (byte* logId = name)
                Check(table, ((delegate* unmanaged[Cdecl]<int, byte*, IntPtr*, IntPtr>)table[CreateEnvAt])(LoggingError, logId, &created));
            env = created;
            // ONNX Runtime's Windows telemetry events stay off, as for every model Martlet runs.
            Check(table, ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)table[DisableTelemetryEventsAt])(created));
            api = table;
            loadedFrom = directory;
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static void Check(IntPtr* table, IntPtr status)
    {
        if (status == IntPtr.Zero) return;
        var message = Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)table[GetErrorMessageAt])(status));
        ((delegate* unmanaged[Cdecl]<IntPtr, void>)table[ReleaseStatusAt])(status);
        throw new SherpaException("ONNX Runtime: " + (message ?? "unknown error"));
    }

    private static void Check(IntPtr status) => Check(api, status);

    /// <summary>One loaded model with one float input and one float output, run on the processor.</summary>
    internal sealed class Session : IDisposable
    {
        private readonly object runGate = new();
        private readonly byte[] inputName, outputName;
        private IntPtr session, memory;

        internal Session(byte[] model, int threads, string input, string output)
        {
            if (api == null) throw new InvalidOperationException("Load ONNX Runtime first.");
            inputName = Utf8(input);
            outputName = Utf8(output);
            IntPtr options;
            Check(((delegate* unmanaged[Cdecl]<IntPtr*, IntPtr>)api[CreateSessionOptionsAt])(&options));
            try
            {
                Check(((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)api[SetSessionExecutionModeAt])(options, 0));
                Check(((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)api[SetIntraOpNumThreadsAt])(options, threads));
                Check(((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)api[SetInterOpNumThreadsAt])(options, 1));
                Check(((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)api[SetSessionGraphOptimizationLevelAt])(options, GraphOptimizationAll));
                IntPtr created;
                fixed (byte* data = model)
                    Check(((delegate* unmanaged[Cdecl]<IntPtr, void*, nuint, IntPtr, IntPtr*, IntPtr>)api[CreateSessionFromArrayAt])(
                        env, data, (nuint)model.Length, options, &created));
                session = created;
            }
            finally { ((delegate* unmanaged[Cdecl]<IntPtr, void>)api[ReleaseSessionOptionsAt])(options); }
            IntPtr info;
            Check(((delegate* unmanaged[Cdecl]<int, int, IntPtr*, IntPtr>)api[CreateCpuMemoryInfoAt])(ArenaAllocator, MemTypeDefault, &info));
            memory = info;
        }

        /// <summary>Runs the model on <paramref name="data"/> shaped <paramref name="shape"/> and returns the output's first value.</summary>
        internal float RunFirst(float[] data, long[] shape)
        {
            lock (runGate)
            {
                ObjectDisposedException.ThrowIf(session == IntPtr.Zero, this);
                IntPtr value = IntPtr.Zero, result = IntPtr.Zero;
                try
                {
                    fixed (float* values = data)
                    fixed (long* dimensions = shape)
                    {
                        Check(((delegate* unmanaged[Cdecl]<IntPtr, void*, nuint, long*, nuint, int, IntPtr*, IntPtr>)api[CreateTensorWithDataAsOrtValueAt])(
                            memory, values, (nuint)(data.Length * sizeof(float)), dimensions, (nuint)shape.Length, ElementFloat, &value));
                        fixed (byte* input = inputName)
                        fixed (byte* output = outputName)
                        {
                            IntPtr* inputs = stackalloc IntPtr[] { (IntPtr)input };
                            IntPtr* outputs = stackalloc IntPtr[] { (IntPtr)output };
                            IntPtr* tensors = stackalloc IntPtr[] { value };
                            Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, IntPtr*, nuint, IntPtr*, nuint, IntPtr*, IntPtr>)api[RunAt])(
                                session, IntPtr.Zero, inputs, tensors, 1, outputs, 1, &result));
                        }
                        float* answer;
                        Check(((delegate* unmanaged[Cdecl]<IntPtr, float**, IntPtr>)api[GetTensorMutableDataAt])(result, &answer));
                        return answer[0];
                    }
                }
                finally
                {
                    if (result != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<IntPtr, void>)api[ReleaseValueAt])(result);
                    if (value != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<IntPtr, void>)api[ReleaseValueAt])(value);
                }
            }
        }

        public void Dispose()
        {
            lock (runGate)
            {
                if (session != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<IntPtr, void>)api[ReleaseSessionAt])(session);
                if (memory != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<IntPtr, void>)api[ReleaseMemoryInfoAt])(memory);
                session = memory = IntPtr.Zero;
            }
        }
    }
}

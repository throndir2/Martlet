using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Martlet.VoiceActivity;

internal sealed class OnnxVadInferenceSession : IVoiceActivityInferenceSession
{
    private readonly object optionsGate = new();
    private readonly float[] context = new float[64];
    private readonly float[] state = new float[256];
    private VoiceActivityInferenceAccess? access;
    private VoiceActivityInferenceAccess? workAccess;
    private InferenceSession? session;
    private SessionOptions? loadOptions;
    private RunResources? run;
    private byte[]? modelSnapshot;
    private int ownerThread, cancellationRequested, scoredWindows;
    private bool loadAttempted, loading, scoring, loaded, failed, disposed, cleanupFaulted, cancellationFaulted;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Load(string modelPath, VoiceActivityInferenceAccess access)
    {
        lock (optionsGate)
        {
            VadCheck.Require(!loadAttempted && !disposed && access is not null, VoiceActivityFailureCode.InvalidState);
            ownerThread = Environment.CurrentManagedThreadId;
            loadAttempted = loading = true;
            this.access = access;
            workAccess = new(Check);
        }
        try
        {
            Check();
            var manifest = SileroVadModel.ReadManifest(workAccess!);
            modelSnapshot = SileroVadModel.Load(modelPath, workAccess!);
            Check();
            NativeModule.Ensure(manifest, workAccess!);
            Check();
            lock (optionsGate)
            {
                Call(() => loadOptions = new SessionOptions(), VoiceActivityFailureCode.NativeRuntimeUnavailable);
                Call(() => loadOptions!.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL);
                Call(() => loadOptions!.IntraOpNumThreads = 1);
                Call(() => loadOptions!.InterOpNumThreads = 1);
                Call(() => loadOptions!.EnableCpuMemArena = true);
                Call(() => loadOptions!.EnableMemoryPattern = true);
                Call(() => loadOptions!.EnableProfiling = false);
                Call(() => loadOptions!.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL);
                Call(() => loadOptions!.AppendExecutionProvider_CPU(useArena: 1));
            }
            Call(() => session = new InferenceSession(modelSnapshot!, loadOptions!));
            Check();
            var created = session ?? throw Failure(VoiceActivityFailureCode.InferenceFailed);
            var inputs = Describe(created.InputMetadata);
            Check();
            var outputs = Describe(created.OutputMetadata);
            Check();
            int initializers = created.OverridableInitializerMetadata.Count;
            Check();
            ModelSchema.Validate(inputs, outputs, initializers);
            Check();
            loaded = true;
        }
        catch (VoiceActivityException) { failed = true; Check(); throw; }
        catch (Exception) { failed = true; Check(); throw Failure(VoiceActivityFailureCode.InferenceFailed); }
        finally
        {
            lock (optionsGate) loading = false;
        }
        Check();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public float Score(ReadOnlySpan<float> window)
    {
        lock (optionsGate)
        {
            RequireOwner();
            VadCheck.Require(loaded && !failed && !disposed && !cleanupFaulted && !cancellationFaulted &&
                !loading && !scoring && run is null, VoiceActivityFailureCode.InvalidState);
            scoring = true;
        }
        float score;
        try
        {
            Check();
            ModelSchema.ValidateWindow(window);
            VadCheck.Require(scoredWindows < VoiceActivityOptions.HardMaximumScores,
                VoiceActivityFailureCode.PayloadTooLarge);
            var current = new RunResources();
            run = current;
            context.CopyTo(current.Input, 0);
            window.CopyTo(current.Input.AsSpan(64));
            state.CopyTo(current.State, 0);
            lock (optionsGate)
            {
                Call(() => current.Options = new RunOptions());
                Call(() => current.Options!.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL);
                Call(() => current.InputValue = OrtValue.CreateTensorValueFromMemory<float>(current.Input, [1, 576]));
                Call(() => current.StateValue = OrtValue.CreateTensorValueFromMemory<float>(current.State, [2, 1, 128]));
                Call(() => current.SampleRateValue = OrtValue.CreateTensorValueFromMemory<long>(current.SampleRate, []));
            }

            Check();
            ++scoredWindows;
            Call(() => current.Outputs = session!.Run(current.Options!,
                new[] { "input", "state", "sr" },
                new[] { current.InputValue!, current.StateValue!, current.SampleRateValue! },
                new[] { "output", "stateN" }));
            var results = current.Outputs ?? throw Failure(VoiceActivityFailureCode.InvalidScore);
            VadCheck.Require(results.Count == 2, VoiceActivityFailureCode.InvalidScore);
            var output = results[0];
            var nextState = results[1];
            Check();
            VadCheck.Require(output.OnnxType == OnnxValueType.ONNX_TYPE_TENSOR &&
                nextState.OnnxType == OnnxValueType.ONNX_TYPE_TENSOR, VoiceActivityFailureCode.InvalidScore);
            Check();
            var outputInfo = output.GetTensorTypeAndShape();
            Check();
            var stateInfo = nextState.GetTensorTypeAndShape();
            Check();
            VadCheck.Require(outputInfo.ElementDataType == TensorElementType.Float &&
                stateInfo.ElementDataType == TensorElementType.Float &&
                outputInfo.ElementCount == 1 && stateInfo.ElementCount == 256, VoiceActivityFailureCode.InvalidScore);
            Check();
            var scores = output.GetTensorDataAsSpan<float>();
            Check();
            var returnedState = nextState.GetTensorDataAsSpan<float>();
            Check();
            score = ModelSchema.ValidateResult(outputInfo.Shape, scores, stateInfo.Shape, returnedState);
            returnedState.CopyTo(current.NextState);
            Check();
            current.NextState.CopyTo(state, 0);
            Array.Copy(current.Input, 512, context, 0, 64);
            Check();
        }
        catch (VoiceActivityException) { failed = true; Check(); throw; }
        catch (Exception) { failed = true; Check(); throw Failure(VoiceActivityFailureCode.InferenceFailed); }
        finally
        {
            try { RetireRun(); }
            finally { lock (optionsGate) scoring = false; }
        }
        Check();
        return score;
    }

    public void RequestCancellation()
    {
        // Publication cannot lose a request that arrives before an options object exists.
        Interlocked.Exchange(ref cancellationRequested, 1);
        lock (optionsGate)
        {
            ObserveReleaseAccess();
            if (disposed) return;
            try
            {
                if (loading && loadOptions is not null)
                {
                    ObserveReleaseAccess();
                    loadOptions.SetLoadCancellationFlag(true);
                    ObserveReleaseAccess();
                }
                if (scoring && run?.Options is not null)
                {
                    ObserveReleaseAccess();
                    run.Options.Terminate = true;
                    ObserveReleaseAccess();
                }
            }
            catch (Exception)
            {
                cancellationFaulted = true;
                ObserveReleaseAccess();
                throw Failure(VoiceActivityFailureCode.CancellationFailed);
            }
            ObserveReleaseAccess();
        }
    }

    public void Dispose()
    {
        lock (optionsGate)
        {
            ObserveReleaseAccess();
            if (disposed) return;
            VadCheck.Require(ownerThread == 0 || ownerThread == Environment.CurrentManagedThreadId,
                VoiceActivityFailureCode.CleanupFailed);
            VadCheck.Require(!loading && !scoring && !cleanupFaulted && !cancellationFaulted,
                VoiceActivityFailureCode.CleanupFailed);
            try
            {
                RetireRun();
                if (session is not null) { Release(session); session = null; }
                if (loadOptions is not null) { Release(loadOptions); loadOptions = null; }
                if (modelSnapshot is not null) CryptographicOperations.ZeroMemory(modelSnapshot);
                modelSnapshot = null;
                Array.Clear(context);
                Array.Clear(state);
                disposed = true;
            }
            catch (Exception)
            {
                cleanupFaulted = true;
                throw Failure(VoiceActivityFailureCode.CleanupFailed);
            }
            ObserveReleaseAccess();
        }
    }

    private void RetireRun()
    {
        lock (optionsGate)
        {
            if (run is null) return;
            VadCheck.Require(!cleanupFaulted && !cancellationFaulted, VoiceActivityFailureCode.CleanupFailed);
            try
            {
                if (run.Outputs is not null) { Release(run.Outputs); run.Outputs = null; }
                if (run.SampleRateValue is not null) { Release(run.SampleRateValue); run.SampleRateValue = null; }
                if (run.StateValue is not null) { Release(run.StateValue); run.StateValue = null; }
                if (run.InputValue is not null) { Release(run.InputValue); run.InputValue = null; }
                if (run.Options is not null) { Release(run.Options); run.Options = null; }
                // Never clear or drop the retained buffers until every native owner returned from Dispose.
                Array.Clear(run.Input);
                Array.Clear(run.State);
                Array.Clear(run.NextState);
                Array.Clear(run.SampleRate);
                run = null;
            }
            catch (Exception)
            {
                cleanupFaulted = true;
                throw Failure(VoiceActivityFailureCode.CleanupFailed);
            }
        }
    }

    private void RequireOwner() =>
        VadCheck.Require(ownerThread == Environment.CurrentManagedThreadId, VoiceActivityFailureCode.InvalidState);

    private void Check()
    {
        if (access is null) throw Failure(VoiceActivityFailureCode.InvalidState);
        SileroVadModel.Check(access);
        VadCheck.Require(Volatile.Read(ref cancellationRequested) == 0, VoiceActivityFailureCode.Canceled);
    }

    private void ObserveReleaseAccess()
    {
        if (access is not null) SileroVadModel.CheckForRelease(access);
    }

    private void Call(Action action, VoiceActivityFailureCode code = VoiceActivityFailureCode.InferenceFailed)
    {
        Check();
        try { action(); }
        catch (Exception) { Check(); throw Failure(code); }
        Check();
    }

    private void Release(IDisposable resource)
    {
        ObserveReleaseAccess();
        try { resource.Dispose(); }
        catch (Exception) { ObserveReleaseAccess(); throw Failure(VoiceActivityFailureCode.CleanupFailed); }
        ObserveReleaseAccess();
    }

    private ModelTensorSchema[] Describe(IReadOnlyDictionary<string, NodeMetadata> nodes)
    {
        Check();
        VadCheck.Require(nodes.Count <= 3, VoiceActivityFailureCode.ModelSchemaMismatch);
        var result = new ModelTensorSchema[nodes.Count];
        int index = 0;
        foreach (var (name, node) in nodes)
        {
            Check();
            bool plain = node.OnnxValueType == OnnxValueType.ONNX_TYPE_TENSOR;
            var type = !plain ? ModelElementType.Other : node.ElementDataType switch
            {
                TensorElementType.Float => ModelElementType.Float32,
                TensorElementType.Int64 => ModelElementType.Int64,
                _ => ModelElementType.Other
            };
            result[index++] = new(name, plain, type,
                plain ? (int[])node.Dimensions.Clone() : [],
                plain ? (string[])node.SymbolicDimensions.Clone() : []);
            Check();
        }
        Check();
        return result;
    }

    private static VoiceActivityException Failure(VoiceActivityFailureCode code) => new(code);

    private sealed class RunResources
    {
        internal readonly float[] Input = new float[576], State = new float[256], NextState = new float[256];
        internal readonly long[] SampleRate = [16000];
        internal RunOptions? Options;
        internal OrtValue? InputValue, StateValue, SampleRateValue;
        internal IDisposableReadOnlyCollection<OrtValue>? Outputs;
    }

    // Only the verified module and its immutable filesystem leases are process-owned.
    // No permission, model bytes, options, session, PCM, context or recurrent state is cached here.
    private static class NativeModule
    {
        private static readonly object Gate = new();
        private static readonly List<SileroVadModel.LocalFileLease> Files = [];
        private static Assembly? assembly;
        private static string? modulePath;
        private static VadNativeArtifact[]? artifacts;
        private static IntPtr handle;
        private static bool resolverInstalled, poisoned;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Ensure(VadModelManifest manifest, VoiceActivityInferenceAccess access)
        {
            SileroVadModel.Check(access);
            VadCheck.Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                VoiceActivityFailureCode.UnsupportedPlatform);
            lock (Gate)
            {
                SileroVadModel.Check(access);
                var actualAssembly = typeof(InferenceSession).Assembly;
                string? directory = Path.GetDirectoryName(typeof(OnnxVadInferenceSession).Assembly.Location);
                if (string.IsNullOrEmpty(directory)) throw Failure(VoiceActivityFailureCode.NativeRuntimeUnavailable);
                string managedPath = Path.Combine(directory, "Microsoft.ML.OnnxRuntime.dll");
                string nativeDirectory = Path.Combine(directory, "runtimes", "win-x64", "native");
                string expectedModule = Path.Combine(nativeDirectory, "onnxruntime.dll");
                VadCheck.Require(string.Equals(actualAssembly.Location, managedPath, StringComparison.Ordinal),
                    VoiceActivityFailureCode.NativeRuntimeUnavailable);
                VadCheck.Require(!poisoned, VoiceActivityFailureCode.NativeRuntimeUnavailable);
                if (handle != IntPtr.Zero)
                {
                    VadCheck.Require(ReferenceEquals(assembly, actualAssembly) &&
                        string.Equals(modulePath, expectedModule, StringComparison.Ordinal) &&
                        artifacts is not null && artifacts.SequenceEqual(manifest.NativeFiles),
                        VoiceActivityFailureCode.NativeRuntimeUnavailable);
                    SileroVadModel.Check(access);
                    return;
                }

                var acquired = new List<SileroVadModel.LocalFileLease>();
                try
                {
                    acquired.Add(SileroVadModel.OpenLocalFile(managedPath, access,
                        VoiceActivityFailureCode.NativeRuntimeUnavailable));
                    SileroVadModel.Check(access);
                    long managedBytes = acquired[0].Stream.Length;
                    SileroVadModel.Check(access);
                    VadCheck.Require(managedBytes is > 0 and <= 33_554_432,
                        VoiceActivityFailureCode.NativeRuntimeUnavailable);
                    foreach (var artifact in manifest.NativeFiles)
                    {
                        var file = SileroVadModel.OpenLocalFile(Path.Combine(nativeDirectory, artifact.Name), access,
                            VoiceActivityFailureCode.NativeRuntimeUnavailable);
                        acquired.Add(file);
                        SileroVadModel.VerifyNativeFile(file, artifact, access);
                    }
                    SileroVadModel.Check(access);
                    NativeLibrary.SetDllImportResolver(actualAssembly, Resolve);
                    resolverInstalled = true;
                    assembly = actualAssembly;
                    modulePath = expectedModule;
                    artifacts = manifest.NativeFiles.ToArray();
                    Files.AddRange(acquired);
                    acquired.Clear();
                    SileroVadModel.Check(access);
                    handle = NativeLibrary.Load(expectedModule, actualAssembly,
                        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
                    SileroVadModel.Check(access);
                    VadCheck.Require(handle != IntPtr.Zero, VoiceActivityFailureCode.NativeRuntimeUnavailable);
                }
                catch (Exception)
                {
                    if (resolverInstalled && handle == IntPtr.Zero) poisoned = true;
                    foreach (var file in acquired) file.Close(access);
                    SileroVadModel.Check(access);
                    throw Failure(VoiceActivityFailureCode.NativeRuntimeUnavailable);
                }
                SileroVadModel.Check(access);
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly requestedAssembly, DllImportSearchPath? searchPath)
        {
            lock (Gate)
            {
                VadCheck.Require(ReferenceEquals(assembly, requestedAssembly) &&
                    (libraryName is "onnxruntime" or "onnxruntime.dll") && handle != IntPtr.Zero && !poisoned,
                    VoiceActivityFailureCode.NativeRuntimeUnavailable);
                return handle;
            }
        }
    }
}

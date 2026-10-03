using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Martlet.Audio.Windows;

/// <summary>Whether this Windows can hear what the PC plays without Martlet's own sound (process loopback), found by setting
/// the stream up and closing it without starting it: nothing is recorded.</summary>
public sealed record PcAudioProbe(bool WithoutMartlet, CaptureSourceFormat? Format, string? Problem);

/// <summary>The outputs as hearing what this PC plays sees them (their sessions' state only, never their sound): the output you
/// hear (Windows' default) and another output an app other than Martlet is streaming to right now, such as a voice changer's or
/// a microphone app's virtual cable, or null.</summary>
public sealed record PcOutputs(string? Output, string? Elsewhere);

/// <summary>What this PC plays, for Companion › Listening › Hear what this PC plays, chosen each time it opens. While only the
/// output you hear has apps streaming to it: every app's sound except Martlet's own (a Windows process loopback that leaves out
/// Martlet and the processes it started), so Martlet never hears its own voice and keeps hearing the PC while it speaks. A
/// process loopback mixes every output, virtual ones included, so while another app streams to another output (Voicemod's or
/// NVIDIA Broadcast's virtual cable carries your own voice from the microphone, never played aloud) it hears only the output
/// you hear (Windows' default): Martlet's voice included, so listening holds off while Martlet speaks. That is also the
/// fallback where Windows can't leave Martlet out. Opened and polled on the capture's worker thread; nothing is played, kept
/// or sent here.</summary>
public sealed class WasapiPcAudioSourceFactory(int? martletProcessId = null) : IPcAudioSourceFactory
{
    // Windows converts every app's sound to this; the capture normalizer takes it to 16 kHz mono like a microphone.
    private static readonly WaveFormat ProcessFormat = new(48_000, 16, 2);
    private static readonly TimeSpan Activation = TimeSpan.FromSeconds(5);
    private readonly int processId = martletProcessId ?? Environment.ProcessId;
    // Process loopback that failed once is not tried again until Martlet restarts (the fallback works the same way).
    private int processLoopback = -1;

    /// <summary>Why the last process loopback couldn't open (the fallback is in use), or null.</summary>
    public string? Problem { get; private set; }

    public IPcAudioSource Open(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputs = Outputs(processId);
        if (outputs.Elsewhere is null && Volatile.Read(ref processLoopback) != 0)
        {
            var source = new ProcessSource();
            try
            {
                source.Initialize(processId, cancellationToken);
                Volatile.Write(ref processLoopback, 1);
                Problem = null;
                return source;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                try { source.Dispose(); }
                catch (Exception) { throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false); }
                Problem = Describe(error);
                Volatile.Write(ref processLoopback, 0);
            }
            catch (OperationCanceledException)
            {
                source.Dispose();
                throw;
            }
        }
        return new EndpointSource(new WasapiLoopbackReferenceFactory().Open(null, cancellationToken), outputs.Output ?? "your speakers");
    }

    /// <summary>The output you hear and another output an app other than Martlet (<paramref name="martletProcessId"/>, this
    /// process when null) streams to right now. Reads the outputs' sessions only; when they can't be read, every other output
    /// counts as in use, so a virtual cable is never heard by mistake.</summary>
    public static PcOutputs Outputs(int? martletProcessId = null)
    {
        var martlet = martletProcessId ?? Environment.ProcessId;
        string? output = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var heard = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            output = heard.FriendlyName;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (device.ID == heard.ID) continue;
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateActive && !session.IsSystemSoundsSession &&
                            session.GetProcessID != (uint)martlet)
                            return new(output, device.FriendlyName);
                    }
                }
            }
            return new(output, null);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(output, "another output (Windows didn't say which apps play where)");
        }
    }

    /// <summary>Sets up a process loopback that leaves out <paramref name="processId"/> (this process when null) and closes it
    /// again without starting it.</summary>
    public static PcAudioProbe Probe(int? processId = null, CancellationToken cancellationToken = default)
    {
        var source = new ProcessSource();
        try
        {
            source.Initialize(processId ?? Environment.ProcessId, cancellationToken);
            return new(true, source.Format, null);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new(false, null, Describe(error)); }
        finally { source.Dispose(); }
    }

    private static string Describe(Exception error) => error switch
    {
        AggregateException { InnerException: { } inner } => Describe(inner),
        COMException native => $"Windows refused it (0x{native.HResult:X8}).",
        CaptureDeviceException failure => failure.Message,
        PlatformNotSupportedException unsupported => unsupported.Message,
        _ => error.GetType().Name + ": " + error.Message
    };

    private sealed class ProcessSource : IPcAudioSource
    {
        private Task<AudioClient>? activation;
        private AudioClient? client;
        private AudioCaptureClient? capture;
        private EventWaitHandle? signal;
        private byte[]? scratch;
        private bool initialized, started, disposed;

        public bool WithoutMartlet => true;
        public CaptureSourceFormat Format { get; } = WasapiCaptureDeviceFactory.DescribeFormat(ProcessFormat);

        public void Initialize(int processId, CancellationToken token)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                throw new PlatformNotSupportedException("Leaving Martlet out needs Windows 10 version 2004 or later.");
            activation = AudioClient.ActivateProcessLoopbackAsync((uint)processId, ProcessLoopbackMode.ExcludeTargetProcessTree);
            if (!activation.Wait((int)Activation.TotalMilliseconds, token))
                throw new CaptureDeviceException(ErrorCode.AudioDeviceUnavailable);
            client = activation.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                2_000_000, 0, ProcessFormat, Guid.Empty);
            initialized = true;
            // The stream needs an event to start; Read polls it like the other loopbacks and never waits on it.
            signal = new(false, EventResetMode.AutoReset);
            client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
            capture = client.AudioCaptureClient;
            scratch = new byte[Format.MaximumPacketBytes];
            token.ThrowIfCancellationRequested();
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                client!.Start();
                started = true;
            }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public CapturePacket Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (capture!.GetNextPacketSize() == 0) return new(0);
                var pointer = capture.GetBuffer(out var frames, out var flags, out _, out _);
                try
                {
                    if (frames <= 0) return new(0);
                    var bytes = frames * Format.BlockAlignment;
                    if (bytes > destination.Length || bytes > scratch!.Length) throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                    if ((flags & AudioClientBufferFlags.Silent) != 0 || pointer == IntPtr.Zero)
                        destination[..bytes].Clear();
                    else
                    {
                        Marshal.Copy(pointer, scratch, 0, bytes);
                        scratch.AsSpan(0, bytes).CopyTo(destination);
                        CryptographicOperations.ZeroMemory(scratch.AsSpan(0, bytes));
                    }
                    return new(bytes);
                }
                finally { capture.ReleaseBuffer(frames); }
            }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public void Stop()
        {
            if (disposed) return;
            try
            {
                try
                {
                    if (started) client!.Stop();
                }
                finally
                {
                    if (initialized) client!.Reset();
                    started = false;
                }
            }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public void Dispose()
        {
            if (disposed) return;
            Exception? failure = null;
            // An activation that finishes after it was given up on still hands over a client: release it then.
            if (client is null && activation is { IsCompleted: false } late)
                late.ContinueWith(done => done.Result.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            try { client?.Dispose(); client = null; capture = null; }
            catch (Exception ex) { failure = ex; }
            try { signal?.Dispose(); signal = null; }
            catch (Exception ex) { failure = ex; }
            if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
            scratch = null;
            if (failure is not null) throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            disposed = true;
        }
    }

    // The output you hear (Windows' default), Martlet's voice included: listening to it holds off while Martlet speaks.
    private sealed class EndpointSource(IEchoReference loopback, string output) : IPcAudioSource
    {
        public bool WithoutMartlet => false;
        public string? Output => output;
        public CaptureSourceFormat Format => loopback.Format;
        public void Start() => loopback.Start();
        public CapturePacket Read(Span<byte> destination) => loopback.Read(destination) with { Discontinuity = false };
        public void Stop() => loopback.Stop();
        public void Dispose() => loopback.Dispose();
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Martlet.Platform.Linux.Native;
using static Martlet.Platform.Linux.Native.PipeWire;

namespace Martlet.Platform.Linux.Capture;

/// <summary>One frame from a PipeWire node the ScreenCast portal opened: connects to the portal's PipeWire remote, offers
/// 32-bit RGB in shared memory (no DMA-BUF modifiers, so the compositor sends mappable buffers), copies the first frame
/// with pixels and disconnects. Runs the PipeWire loop on its own thread.</summary>
internal sealed partial class PipeWireFrameGrab
{
    public sealed record Frame(byte[] Pixels, int Width, int Height, int Stride, SpaVideoFormat Format);

    private static int initialized;
    private readonly TaskCompletionSource<Frame> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr stream, loop, context, core, hook, events, podMemory;
    private GCHandle self;
    private (SpaVideoFormat Format, int Width, int Height)? format;

    /// <summary>Takes ownership of <paramref name="fd"/>.</summary>
    public static async Task<Frame> GrabAsync(int fd, uint node, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var grab = new PipeWireFrameGrab();
        try
        {
            grab.Connect(fd, node);
            return await grab.result.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally { grab.Close(); }
    }

    private unsafe void Connect(int fd, uint node)
    {
        if (Interlocked.Exchange(ref initialized, 1) == 0) pw_init(null, null);
        self = GCHandle.Alloc(this);
        hook = (IntPtr)NativeMemory.AllocZeroed(128);
        var callbacks = (PwStreamEvents*)NativeMemory.AllocZeroed((nuint)sizeof(PwStreamEvents));
        events = (IntPtr)callbacks;
        callbacks->Version = 0;
        callbacks->StateChanged = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, byte*, void>)&OnStateChanged;
        callbacks->ParamChanged = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, byte*, void>)&OnParamChanged;
        callbacks->Process = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnProcess;
        var pod = SpaPod.EnumFormat();
        podMemory = (IntPtr)NativeMemory.Alloc((nuint)pod.Length);
        pod.CopyTo(new Span<byte>((void*)podMemory, pod.Length));

        loop = pw_thread_loop_new("martlet-screen", IntPtr.Zero);
        if (loop == IntPtr.Zero) { close(fd); throw new InvalidOperationException("PipeWire could not start a loop."); }
        context = pw_context_new(pw_thread_loop_get_loop(loop), IntPtr.Zero, 0);
        if (context == IntPtr.Zero) { close(fd); throw new InvalidOperationException("PipeWire could not create a context."); }
        if (pw_thread_loop_start(loop) != 0) { close(fd); throw new InvalidOperationException("PipeWire could not start its thread."); }
        pw_thread_loop_lock(loop);
        try
        {
            core = pw_context_connect_fd(context, fd, IntPtr.Zero, 0);
            if (core == IntPtr.Zero) throw new InvalidOperationException("PipeWire refused the portal's connection.");
            stream = pw_stream_new(core, "Martlet screen look", pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen"));
            if (stream == IntPtr.Zero) throw new InvalidOperationException("PipeWire could not create a stream.");
            pw_stream_add_listener(stream, (void*)hook, callbacks, GCHandle.ToIntPtr(self));
            var parameters = podMemory;
            var connected = pw_stream_connect(stream, DirectionInput, node, FlagAutoconnect | FlagMapBuffers, &parameters, 1);
            if (connected < 0) throw new InvalidOperationException($"PipeWire could not connect to the screen (error {connected}).");
        }
        finally { pw_thread_loop_unlock(loop); }
    }

    private unsafe void Close()
    {
        if (loop != IntPtr.Zero)
        {
            pw_thread_loop_lock(loop);
            if (stream != IntPtr.Zero) { pw_stream_disconnect(stream); pw_stream_destroy(stream); stream = IntPtr.Zero; }
            if (core != IntPtr.Zero) { pw_core_disconnect(core); core = IntPtr.Zero; }
            pw_thread_loop_unlock(loop);
            pw_thread_loop_stop(loop);
            if (context != IntPtr.Zero) { pw_context_destroy(context); context = IntPtr.Zero; }
            pw_thread_loop_destroy(loop);
            loop = IntPtr.Zero;
        }
        if (hook != IntPtr.Zero) { NativeMemory.Free((void*)hook); hook = IntPtr.Zero; }
        if (events != IntPtr.Zero) { NativeMemory.Free((void*)events); events = IntPtr.Zero; }
        if (podMemory != IntPtr.Zero) { NativeMemory.Free((void*)podMemory); podMemory = IntPtr.Zero; }
        if (self.IsAllocated) self.Free();
    }

    [LibraryImport("libc")] private static partial int close(int fd);

    private static PipeWireFrameGrab? From(IntPtr data) => GCHandle.FromIntPtr(data).Target as PipeWireFrameGrab;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStateChanged(IntPtr data, int old, int state, byte* error)
    {
        if (state != StreamStateError || From(data) is not { } grab) return;
        var message = error == null ? "unknown error" : Marshal.PtrToStringUTF8((IntPtr)error);
        grab.result.TrySetException(new InvalidOperationException($"The screen stream failed: {message}"));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnParamChanged(IntPtr data, uint id, byte* param)
    {
        if (id != ParamFormat || param == null || From(data) is not { } grab) return;
        var size = *(uint*)param;
        grab.format = SpaPod.ParseVideoFormat(new ReadOnlySpan<byte>(param, (int)Math.Min(size + 8, 1 << 16)));
        if (grab.format is null) grab.result.TrySetException(new InvalidOperationException("The screen stream sent an unsupported pixel format."));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnProcess(IntPtr data)
    {
        if (From(data) is not { } grab || grab.stream == IntPtr.Zero) return;
        var buffer = pw_stream_dequeue_buffer(grab.stream);
        if (buffer == null) return;
        try
        {
            if (grab.result.Task.IsCompleted || grab.format is not { } format || buffer->Buffer == null || buffer->Buffer->DataCount == 0) return;
            var plane = buffer->Buffer->Datas[0];
            if (plane.Data == null || plane.Chunk == null || plane.Chunk->Size == 0) return;
            var stride = plane.Chunk->Stride > 0 ? plane.Chunk->Stride : format.Width * 4;
            var length = (long)stride * format.Height;
            if (stride < format.Width * 4 || plane.Chunk->Offset + length > plane.MaxSize) return;
            grab.result.TrySetResult(new Frame(FrameEncoder.Copy(plane.Data + plane.Chunk->Offset, (int)length),
                format.Width, format.Height, stride, format.Format));
        }
        catch (Exception error) { grab.result.TrySetException(error); }
        finally { pw_stream_queue_buffer(grab.stream, buffer); }
    }
}

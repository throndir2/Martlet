namespace Martlet.Avatar.Hosting;

/// <summary>
/// Writes whole renderer messages to one pipe, one after another, in the order they are given. A write is never cancelled
/// part way: on an anonymous pipe a cancelled write can stop in the middle of a message, and the reader is then out of step
/// for good. A caller that can't wait abandons only its wait (for example with <c>WaitAsync</c>); its message still goes out
/// whole, and the next message goes after it. At most <c>capacity</c> messages wait: when the other side stops reading, a new
/// message is refused at once and nothing of it is written. A write that fails breaks the writer, because part of a message
/// can then be in the pipe: every later message is refused.
/// </summary>
public sealed class RendererPipeWriter
{
    private readonly Stream pipe;
    private readonly int capacity;
    private readonly object gate = new();
    private Task last = Task.CompletedTask;
    private int pending;
    private Exception? fault;

    public RendererPipeWriter(Stream pipe, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.pipe = pipe;
        this.capacity = capacity;
    }

    /// <summary>Messages given but not yet completely written.</summary>
    public int Pending { get { lock (gate) return pending; } }

    /// <summary>Why the writer is broken (a write failed), or null while it works.</summary>
    public Exception? Fault { get { lock (gate) return fault; } }

    /// <summary>Queues one whole message. The task completes when all of it is in the pipe, or faults when the write fails.</summary>
    /// <exception cref="InvalidDataException">The message exceeds <see cref="RendererProtocol.MaximumMessageBytes"/>.</exception>
    /// <exception cref="IOException">The writer is broken, or <c>capacity</c> messages already wait; nothing was written.</exception>
    public Task WriteAsync(RendererMessage message) => WriteFrameAsync(RendererProtocol.Frame(message));

    /// <summary>Queues a frame made by <see cref="RendererProtocol.Frame"/>, as <see cref="WriteAsync"/> does. It never writes
    /// on the calling thread, so a caller may queue while it holds a lock.</summary>
    public Task WriteFrameAsync(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task before;
        lock (gate)
        {
            if (fault is { } broken) throw new IOException("An earlier renderer message couldn't be written whole, so this pipe can't be used.", broken);
            if (pending >= capacity) throw new IOException("The other side isn't reading renderer messages; this one wasn't sent.");
            pending++;
            before = last;
            last = done.Task;
        }
        _ = WriteAfterAsync(before, frame, done);
        return done.Task;
    }

    private async Task WriteAfterAsync(Task before, byte[] frame, TaskCompletionSource done)
    {
        // Always yields first, so nothing is written on the caller's thread (a caller may hold its own lock while it queues).
        await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        try
        {
            Exception? broken;
            lock (gate) broken = fault;
            if (broken is not null)
                throw new IOException("An earlier renderer message couldn't be written whole, so this pipe can't be used.", broken);
            try
            {
                await pipe.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
                await pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (gate) fault ??= error;
                throw;
            }
            lock (gate) pending--;
            done.TrySetResult();
        }
        catch (Exception error)
        {
            lock (gate) pending--;
            done.TrySetException(error);
            // A caller that stopped waiting never sees this failure; it must not surface later as an unobserved task exception.
            _ = done.Task.Exception;
        }
    }
}

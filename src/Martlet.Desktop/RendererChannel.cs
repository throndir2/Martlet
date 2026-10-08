using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>
/// Martlet's side of the character renderer's command pipe and reply pipe. The renderer runs one command at a time and
/// answers each with exactly one reply (an "error" reply when it fails, and then it closes), so replies are matched to commands
/// in order. Messages always go whole: a caller's timeout or cancellation stops only its wait, never a read or a write part
/// way. (A cancelled read or write on an anonymous pipe can stop in the middle of a message, and every later exchange is then
/// out of step: "Renderer message length is invalid.") A reply that nobody waits for any more is dropped when it arrives,
/// and the next command goes out only after an earlier one is written whole.
/// <para>Anything that breaks the protocol (an unreadable message, a reply when nothing was asked, a reply for another
/// activation, a closed pipe, a failed write), an "error" reply or a stuck renderer (see the stall watchdog) breaks the
/// channel for good: requests that wait fail with an <see cref="IOException"/>, later ones fail at once, and
/// <see cref="Broken"/> completes with the reason, so the renderer can be ended. A <see cref="RendererStoppedException"/>
/// reason means the renderer stopped by itself.</para>
/// </summary>
internal sealed class RendererChannel
{
    // No healthy renderer takes this long to answer: its own longest wait (loading a model) gives up well before it.
    private static readonly TimeSpan DefaultStallCheck = TimeSpan.FromSeconds(5);
    private const int DefaultStallChecks = 18;
    private readonly RendererPipeWriter commands;
    private readonly Stream replies;
    private readonly Guid activation;
    private readonly SemaphoreSlim exchange = new(1, 1);
    private readonly object gate = new();
    private readonly Queue<TaskCompletionSource<RendererMessage>> unanswered = new();
    private readonly TaskCompletionSource<Exception> broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan stallCheck;
    private readonly int stallChecks;
    private Task lastWrite = Task.CompletedTask;
    private Task? reading;
    private Timer? watchdog;
    private int quietChecks;
    private int answered;

    /// <summary>Opens the channel. The stall watchdog checks every <paramref name="stallCheck"/> (5 seconds by default); after
    /// <paramref name="stallChecks"/> checks in a row (90 seconds) with a command unanswered and no reply at all, the renderer
    /// is stuck (alive, but no longer reading or answering) and the channel breaks. Checks don't run while the PC sleeps, so
    /// a sleep never counts.</summary>
    internal RendererChannel(Stream commands, Stream replies, Guid activation, TimeSpan? stallCheck = null,
        int stallChecks = DefaultStallChecks)
    {
        // One command waits at most: a command goes out only after the one before it is written.
        this.commands = new RendererPipeWriter(commands, capacity: 2);
        this.replies = replies;
        this.activation = activation;
        this.stallCheck = stallCheck ?? DefaultStallCheck;
        this.stallChecks = stallChecks;
    }

    /// <summary>Completes with the reason once the channel is broken.</summary>
    internal Task<Exception> Broken => broken.Task;
    internal bool IsBroken => broken.Task.IsCompleted;
    /// <summary>Commands sent whose reply hasn't arrived, including those whose caller stopped waiting.</summary>
    internal int Unanswered { get { lock (gate) return unanswered.Count; } }
    /// <summary>The reply reader: it ends once the channel is broken or the reply pipe closes.</summary>
    internal Task Reading => reading ?? Task.CompletedTask;

    /// <summary>Starts reading replies and the stall watchdog. Reads are never cancelled; closing the renderer's end of the
    /// pipe ends them.</summary>
    internal void Start()
    {
        lock (gate)
        {
            if (reading is not null || broken.Task.IsCompleted) return;
            reading = Task.Run(ReadRepliesAsync);
            watchdog = new Timer(static state => ((RendererChannel)state!).CheckStall(), this, stallCheck, stallCheck);
        }
    }

    /// <summary>Sends one command and waits for its reply. <paramref name="token"/> (a timeout or the caller giving up) stops
    /// only the wait.</summary>
    /// <exception cref="IOException">The channel is broken, or broke while this request waited.</exception>
    /// <exception cref="OperationCanceledException">The wait was stopped.</exception>
    /// <exception cref="InvalidDataException">The command exceeds the message limit; nothing was sent.</exception>
    internal async Task<RendererMessage> SendAsync(RendererMessage command, CancellationToken token)
    {
        var frame = RendererProtocol.Frame(command);
        ThrowIfBroken();
        await exchange.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A command whose caller stopped waiting can still be going out: never start a message before it is whole.
            if (!lastWrite.IsCompleted)
                await lastWrite.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            token.ThrowIfCancellationRequested();
            if (lastWrite.IsFaulted) Break(WriteFailed(lastWrite.Exception!.InnerException!));
            var reply = new TaskCompletionSource<RendererMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? write = null;
            IOException? refused = null;
            lock (gate)
            {
                // One step: nothing goes out once the channel is broken, and the reply has its place before the command can be
                // answered (the writer starts writing only after this lock is released).
                ThrowIfBroken();
                try
                {
                    write = commands.WriteFrameAsync(frame);
                    unanswered.Enqueue(reply);
                    lastWrite = write;
                }
                catch (IOException error) { refused = error; }
            }
            if (refused is not null)
            {
                Break(WriteFailed(refused));
                throw BrokenError(broken.Task.Result);
            }
            _ = write!.ContinueWith(static (failed, state) => ((RendererChannel)state!).Break(WriteFailed(failed.Exception!.InnerException!)),
                this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return await reply.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally { exchange.Release(); }
    }

    /// <summary>Breaks the channel (if it isn't already) with <paramref name="reason"/>: requests that wait fail and later ones
    /// fail at once. Returns whether this call broke it.</summary>
    internal bool Break(Exception reason)
    {
        TaskCompletionSource<RendererMessage>[] waiting;
        lock (gate)
        {
            if (!broken.TrySetResult(reason)) return false;
            waiting = unanswered.ToArray();
            unanswered.Clear();
            watchdog?.Dispose();
        }
        foreach (var request in waiting)
        {
            request.TrySetException(BrokenError(reason));
            // A request whose caller stopped waiting never sees this; it must not surface as an unobserved task exception.
            _ = request.Task.Exception;
        }
        return true;
    }

    private void CheckStall()
    {
        lock (gate)
        {
            if (broken.Task.IsCompleted) return;
            if (unanswered.Count == 0 || Interlocked.Exchange(ref answered, 0) == 1)
            {
                quietChecks = 0;
                return;
            }
            if (++quietChecks < stallChecks) return;
        }
        Break(new TimeoutException($"The character renderer didn't answer for {(stallCheck * stallChecks).TotalSeconds:0} seconds."));
    }

    private async Task ReadRepliesAsync()
    {
        try
        {
            while (!IsBroken)
            {
                var reply = await RendererProtocol.ReadAsync(replies, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Exchange(ref answered, 1);
                if (reply.Activation != activation)
                {
                    Break(new InvalidDataException("The character renderer replied for another activation."));
                    return;
                }
                TaskCompletionSource<RendererMessage>? request;
                lock (gate) unanswered.TryDequeue(out request);
                if (reply.Kind == "error")
                {
                    // The renderer failed and closes: whoever asked gets its error, everyone else the reason.
                    request?.TrySetResult(reply);
                    Break(new RendererStoppedException($"The character renderer stopped after an error ({ErrorCode(reply)}).",
                        errorCode: ErrorCode(reply)));
                    return;
                }
                if (request is null)
                {
                    Break(new InvalidDataException($"The character renderer sent a '{reply.Kind}' reply when nothing was asked."));
                    return;
                }
                // Dropped when its caller stopped waiting.
                request.TrySetResult(reply);
            }
        }
        catch (EndOfStreamException error)
        {
            Break(new RendererStoppedException("The character renderer closed its reply pipe.", error));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ObjectDisposedException or
            OperationCanceledException or NotSupportedException)
        {
            Break(error);
        }
    }

    private void ThrowIfBroken()
    {
        if (broken.Task.IsCompleted) throw BrokenError(broken.Task.Result);
    }

    private static IOException BrokenError(Exception reason) => new(reason is RendererStoppedException
        ? reason.Message : $"The character renderer can't be reached: {reason.Message}", reason);

    private static IOException WriteFailed(Exception error) =>
        new("A command to the character renderer couldn't be written whole.", error);

    private static string ErrorCode(RendererMessage reply) =>
        reply.Data.ValueKind == JsonValueKind.Object && reply.Data.TryGetProperty("code", out var code) &&
        code.ValueKind == JsonValueKind.String && code.GetString() is { Length: > 0 and <= 64 } value &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? value : "no code";
}

/// <summary>The character renderer stopped by itself: it closed its reply pipe, or it reported an error (with
/// <see cref="ErrorCode"/>, such as <c>avatar.renderer_unavailable</c>) and closes.</summary>
internal sealed class RendererStoppedException(string message, Exception? inner = null, string? errorCode = null)
    : IOException(message, inner)
{
    internal string? ErrorCode { get; } = errorCode;
}

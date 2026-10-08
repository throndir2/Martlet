using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>
/// The renderer's command and reply pipes, over real anonymous pipes: on Windows a cancelled read or write on them stops the
/// I/O itself (CancelSynchronousIo), which is how a timeout used to cut a message and put every later exchange out of step
/// ("Renderer message length is invalid.").
/// </summary>
public sealed class RendererChannelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Martlet's ends (<see cref="Commands"/>, <see cref="Replies"/>) and the renderer's ends of the two pipes.</summary>
    private sealed class Pipes : IDisposable
    {
        internal AnonymousPipeServerStream Commands { get; } = new(PipeDirection.Out);
        internal AnonymousPipeServerStream Replies { get; } = new(PipeDirection.In);
        internal AnonymousPipeClientStream RendererInput { get; }
        internal AnonymousPipeClientStream RendererOutput { get; }
        internal Guid Activation { get; } = Guid.NewGuid();

        internal Pipes()
        {
            RendererInput = new(PipeDirection.In, Commands.ClientSafePipeHandle);
            RendererOutput = new(PipeDirection.Out, Replies.ClientSafePipeHandle);
        }

        internal RendererChannel Start()
        {
            var channel = new RendererChannel(Commands, Replies, Activation);
            channel.Start();
            return channel;
        }

        internal RendererMessage Message(string kind, object data) => RendererProtocol.Message(kind, Activation, data);
        internal Task<RendererMessage> ReadCommandAsync() => RendererProtocol.ReadAsync(RendererInput, default).WaitAsync(Patience);
        internal Task ReplyAsync(string kind, object data) =>
            RendererProtocol.WriteAsync(RendererOutput, Message(kind, data), default).WaitAsync(Patience);

        public void Dispose()
        {
            RendererOutput.Dispose();
            RendererInput.Dispose();
            Commands.Dispose();
            Replies.Dispose();
        }
    }

    private static CancellationToken After(TimeSpan delay) => new CancellationTokenSource(delay).Token;

    [Fact]
    public async Task Channel_drops_a_reply_whose_caller_gave_up_and_the_next_exchange_gets_its_own()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        var first = channel.SendAsync(pipes.Message("where", new { }), After(TimeSpan.FromMilliseconds(150)));
        Assert.Equal("where", (await pipes.ReadCommandAsync()).Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The renderer answers late, after its caller gave up.
        await pipes.ReplyAsync("placement", new { n = 1 });
        var second = channel.SendAsync(pipes.Message("zoom", new { action = "status" }), After(Patience));
        Assert.Equal("zoom", (await pipes.ReadCommandAsync()).Kind);
        await pipes.ReplyAsync("view", new { n = 2 });

        var reply = await second;
        Assert.Equal("view", reply.Kind);
        Assert.Equal(2, reply.Data.GetProperty("n").GetInt32());
        Assert.False(channel.IsBroken);
        Assert.Equal(0, channel.Unanswered);
    }

    [Fact]
    public async Task Channel_timeout_part_way_through_a_large_reply_keeps_the_next_exchange_in_step()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        var snapshot = channel.SendAsync(pipes.Message("snapshot", new { }), After(TimeSpan.FromMilliseconds(300)));
        Assert.Equal("snapshot", (await pipes.ReadCommandAsync()).Kind);

        // A picture of about 2 MB: its caller gives up while it is half on the pipe.
        var picture = RendererProtocol.Frame(pipes.Message("picture", new { png = new string('A', 2 * 1024 * 1024) }));
        await pipes.RendererOutput.WriteAsync(picture.AsMemory(0, picture.Length / 2)).AsTask().WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot);
        await pipes.RendererOutput.WriteAsync(picture.AsMemory(picture.Length / 2)).AsTask().WaitAsync(Patience);

        var where = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
        Assert.Equal("where", (await pipes.ReadCommandAsync()).Kind);
        await pipes.ReplyAsync("placement", new { left = 12.5 });
        var reply = await where;
        Assert.Equal("placement", reply.Kind);
        Assert.Equal(12.5, reply.Data.GetProperty("left").GetDouble());
        Assert.False(channel.IsBroken);
    }

    [Fact]
    public async Task Channel_timeout_while_a_command_goes_out_never_cuts_it_and_the_next_command_waits_for_it()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        // Far larger than the pipe's buffer, and the renderer isn't reading yet: the write can't finish before the timeout.
        var payload = new string('p', 1024 * 1024);
        var apply = channel.SendAsync(pipes.Message("apply", new { payload }), After(TimeSpan.FromMilliseconds(200)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.False(channel.IsBroken);

        var where = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
        await Task.Delay(100);
        Assert.False(where.IsCompleted);

        // The renderer reads on: the first command arrives whole, then the next one.
        var first = await pipes.ReadCommandAsync();
        Assert.Equal("apply", first.Kind);
        Assert.Equal(payload, first.Data.GetProperty("payload").GetString());
        await pipes.ReplyAsync("ok", new { });
        Assert.Equal("where", (await pipes.ReadCommandAsync()).Kind);
        await pipes.ReplyAsync("placement", new { left = 3.0 });

        Assert.Equal("placement", (await where).Kind);
        Assert.False(channel.IsBroken);
        Assert.Equal(0, channel.Unanswered);
    }

    [Fact]
    public async Task Channel_corrupt_reply_breaks_it_for_good()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        var where = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
        await pipes.ReadCommandAsync();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, RendererProtocol.MaximumMessageBytes + 1);
        await pipes.RendererOutput.WriteAsync(header).AsTask().WaitAsync(Patience);

        var failed = await Assert.ThrowsAsync<IOException>(() => where);
        Assert.IsType<InvalidDataException>(failed.InnerException);
        Assert.True(channel.IsBroken);
        Assert.IsType<InvalidDataException>(await channel.Broken.WaitAsync(Patience));
        // Later commands fail at once, without waiting for their timeout.
        var later = await Assert.ThrowsAsync<IOException>(() => channel.SendAsync(pipes.Message("where", new { }), After(Patience))
            .WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Contains("Renderer message length is invalid", later.Message);
    }

    [Fact]
    public async Task Channel_reply_when_nothing_was_asked_breaks_it()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        await pipes.ReplyAsync("placement", new { });
        var reason = await channel.Broken.WaitAsync(Patience);
        Assert.IsType<InvalidDataException>(reason);
        Assert.Contains("nothing was asked", reason.Message);
        await Assert.ThrowsAsync<IOException>(() => channel.SendAsync(pipes.Message("where", new { }), After(Patience)));
    }

    [Fact]
    public async Task Channel_reply_for_another_activation_breaks_it()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        var where = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
        await pipes.ReadCommandAsync();
        await RendererProtocol.WriteAsync(pipes.RendererOutput, RendererProtocol.Message("placement", Guid.NewGuid(), new { }), default);
        await Assert.ThrowsAsync<IOException>(() => where);
        Assert.Contains("another activation", (await channel.Broken.WaitAsync(Patience)).Message);
    }

    [Fact]
    public async Task Channel_error_reply_reaches_its_caller_and_then_the_renderer_counts_as_stopped()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        var apply = channel.SendAsync(pipes.Message("apply", new { }), After(Patience));
        await pipes.ReadCommandAsync();
        await pipes.ReplyAsync("error", new { code = "avatar.renderer_unavailable", message = "failed" });

        Assert.Equal("error", (await apply).Kind);
        var stopped = Assert.IsType<RendererStoppedException>(await channel.Broken.WaitAsync(Patience));
        Assert.Equal("avatar.renderer_unavailable", stopped.ErrorCode);
        await Assert.ThrowsAsync<IOException>(() => channel.SendAsync(pipes.Message("where", new { }), After(Patience)));
    }

    [Fact]
    public async Task Channel_renderer_closing_its_pipe_fails_the_waiting_request_with_an_IOException()
    {
        using var pipes = new Pipes();
        var channel = pipes.Start();
        var where = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
        await pipes.ReadCommandAsync();
        pipes.RendererOutput.Dispose();

        await Assert.ThrowsAsync<IOException>(() => where);
        Assert.IsType<RendererStoppedException>(await channel.Broken.WaitAsync(Patience));
        await channel.Reading.WaitAsync(Patience);
    }

    [Fact]
    public async Task Channel_requests_that_gave_up_and_then_fail_raise_no_unobserved_task_exception()
    {
        var marker = "marker-" + Guid.NewGuid().ToString("N");
        var unobserved = new List<Exception>();
        void Record(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains(marker, StringComparison.Ordinal)) lock (unobserved) unobserved.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += Record;
        try
        {
            using (var pipes = new Pipes())
            {
                var channel = pipes.Start();
                await Abandon(channel, pipes);
                Assert.True(channel.Break(new InvalidDataException(marker)));
                Assert.False(channel.Break(new InvalidDataException("again")));
            }
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            lock (unobserved) Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= Record; }

        static async Task Abandon(RendererChannel channel, Pipes pipes)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                channel.SendAsync(pipes.Message("where", new { }), After(TimeSpan.FromMilliseconds(100))));
            Assert.Equal(1, channel.Unanswered);
        }
    }

    [Fact]
    public async Task Pipe_writer_keeps_messages_whole_and_in_order_when_callers_stop_waiting()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var writer = new RendererPipeWriter(server);
        var id = Guid.NewGuid();
        for (var i = 0; i < 20; i++)
        {
            // Every caller gives up almost at once; nobody reads yet, so the messages wait.
            var write = writer.WriteAsync(RendererProtocol.Message("stroke", id, new { i, fill = new string('x', 16 * 1024 + i) }));
            try { await write.WaitAsync(TimeSpan.FromMilliseconds(1)); }
            catch (TimeoutException) { }
        }
        for (var i = 0; i < 20; i++)
        {
            var message = await RendererProtocol.ReadAsync(reader, default).WaitAsync(Patience);
            Assert.Equal(i, message.Data.GetProperty("i").GetInt32());
            Assert.Equal(16 * 1024 + i, message.Data.GetProperty("fill").GetString()!.Length);
        }
        Assert.Null(writer.Fault);
    }

    [Fact]
    public async Task Pipe_writer_refuses_a_message_whole_when_the_other_side_stops_reading()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var writer = new RendererPipeWriter(server, capacity: 2);
        var id = Guid.NewGuid();
        var big = new { fill = new string('x', 256 * 1024) };
        var first = writer.WriteAsync(RendererProtocol.Message("stroke", id, big));
        var second = writer.WriteAsync(RendererProtocol.Message("stroke", id, big));
        var refused = Assert.Throws<IOException>(() => { _ = writer.WriteAsync(RendererProtocol.Message("stroke", id, big)); });
        Assert.Contains("isn't reading", refused.Message);
        Assert.Equal(2, writer.Pending);

        // Nothing of the refused message went out: exactly the two others arrive, whole.
        for (var i = 0; i < 2; i++) Assert.Equal("stroke", (await RendererProtocol.ReadAsync(reader, default).WaitAsync(Patience)).Kind);
        await Task.WhenAll(first, second).WaitAsync(Patience);
        Assert.Equal(0, writer.Pending);
    }

    [Fact]
    public async Task Pipe_writer_breaks_after_a_failed_write()
    {
        var writer = new RendererPipeWriter(new FailingStream());
        var message = RendererProtocol.Message("touch", Guid.NewGuid(), new { });
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(message));
        Assert.IsType<IOException>(writer.Fault);
        var refused = Assert.Throws<IOException>(() => { _ = writer.WriteAsync(message); });
        Assert.Contains("couldn't be written whole", refused.Message);
    }

    [Fact]
    public async Task Protocol_writes_a_message_in_one_write()
    {
        using var stream = new CountingStream();
        var id = Guid.NewGuid();
        await RendererProtocol.WriteAsync(stream, RendererProtocol.Message("where", id, new { }), default);
        Assert.Equal(1, stream.Writes);
        stream.Position = 0;
        Assert.Equal(id, (await RendererProtocol.ReadAsync(stream, default)).Activation);
    }

    [Theory]
    [InlineData(true, false, "disposed", 0, "INFO", "Avatar renderer stopped by Martlet.")]
    [InlineData(false, true, null, 1, "INFO", "Avatar renderer ended by Martlet (code 0x00000001) because it stopped answering properly.")]
    [InlineData(false, false, "avatar.renderer_unavailable", 0, "WARN", "Avatar renderer closed itself after an error (avatar.renderer_unavailable).")]
    [InlineData(true, false, "avatar.renderer_unavailable", 0, "WARN", "Avatar renderer closed itself after an error (avatar.renderer_unavailable).")]
    [InlineData(true, false, "pipe", 0, "WARN", "Avatar renderer closed itself (code 0x00000000): its window was closed")]
    [InlineData(false, false, null, 0, "WARN", "Avatar renderer closed itself (code 0x00000000): its window was closed")]
    [InlineData(false, false, "pipe", -1073741819, "ERROR", "Avatar renderer exited unexpectedly with code 0xC0000005.")]
    [InlineData(false, false, null, -1, "ERROR", "Avatar renderer exited unexpectedly with code 0xFFFFFFFF.")]
    public void Renderer_exit_is_logged_for_what_ended_it(bool stoppedByMartlet, bool ended, string? broke, int exitCode,
        string level, string message)
    {
        Exception? broken = broke switch
        {
            null => null,
            "disposed" => new ObjectDisposedException("renderer"),
            "pipe" => new RendererStoppedException("The character renderer closed its reply pipe."),
            _ => new RendererStoppedException("stopped", errorCode: broke)
        };
        var note = AvatarRendererProcess.ExitNote(stoppedByMartlet, ended ? new InvalidDataException("bad") : null, broken, exitCode);
        Assert.Equal(level, note.Level);
        Assert.StartsWith(message, note.Message);
    }

    [Fact]
    public async Task Channel_breaks_when_the_renderer_stops_answering_but_not_while_it_is_idle_or_slow()
    {
        using var pipes = new Pipes();
        // Checks every 50 ms; 20 quiet checks in a row (about 1 second) count as stuck.
        var channel = new RendererChannel(pipes.Commands, pipes.Replies, pipes.Activation, TimeSpan.FromMilliseconds(50), 20);
        channel.Start();
        await Task.Delay(1500);
        Assert.False(channel.IsBroken);

        // Slow but steady: each reply takes 150 ms, well within the stall, so it never breaks.
        for (var i = 0; i < 4; i++)
        {
            var where = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
            await pipes.ReadCommandAsync();
            await Task.Delay(150);
            await pipes.ReplyAsync("placement", new { });
            await where;
        }
        Assert.False(channel.IsBroken);

        // Stuck: the renderer took the command and never answers.
        var stuck = channel.SendAsync(pipes.Message("where", new { }), After(Patience));
        await pipes.ReadCommandAsync();
        var failed = await Assert.ThrowsAsync<IOException>(() => stuck);
        Assert.IsType<TimeoutException>(failed.InnerException);
        Assert.Contains("didn't answer", (await channel.Broken.WaitAsync(Patience)).Message);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The pipe has been ended."));
    }

    private sealed class CountingStream : MemoryStream
    {
        internal int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}

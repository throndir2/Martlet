using System.Collections.Concurrent;
using Martlet.Audio.Tests;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Contracts;
using Martlet.Desktop;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>A character renderer that fails (a broken pipe, an unreadable reply, its own time limit) during speech, lip-sync and
/// stopping: the failure ends only that work, stopping always finishes, and no task is left unobserved.</summary>
public sealed class RendererFailureTests
{
    private sealed class Renderer : IAvatarRenderer
    {
        internal ConcurrentQueue<RendererMessage> Messages { get; } = new();
        internal ConcurrentQueue<string> Attempts { get; } = new();
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        internal RendererParameter[] Parameters { get; init; } = [new("aa", 0, 1, 0, ["Mouth"])];
        /// <summary>The failure a command meets now, or null when it succeeds.</summary>
        internal Func<string, Exception?>? Fail { get; set; }
        /// <summary>Commands that wait until they are canceled and then fail with an unreadable reply, as a pipe whose read was
        /// canceled in the middle of a message did.</summary>
        internal Func<string, bool>? FailOnCancel { get; set; }
        internal string Marker { get; init; } = "";
        private readonly Guid activation = Guid.NewGuid();

        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), Parameters);
            return Task.CompletedTask;
        }

        public async Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            Attempts.Enqueue(kind);
            await Task.Yield();
            if (FailOnCancel?.Invoke(kind) == true)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { throw new InvalidDataException($"Renderer message length is invalid. {Marker}"); }
            }
            token.ThrowIfCancellationRequested();
            if (Fail?.Invoke(kind) is { } failure) throw failure;
            Messages.Enqueue(RendererProtocol.Message(kind, activation, data));
            return RendererProtocol.Message("ok", activation, new { });
        }

        public ValueTask DisposeAsync()
        {
            HasExited = true;
            exited.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostLink : IAvatarHostLink
    {
        public string Authority => "192.168.1.20:9443";
        public Task<bool> ReadyAsync(CancellationToken token) => Task.FromResult(true);
        public async IAsyncEnumerable<Martlet.Avatar.Audio2Face.Remote.RemoteFaceFrame> AnimateAsync(CorrelationIds ids, long epoch,
            int sampleRate, ReadOnlyMemory<byte> pcm, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            yield return new(0, new Dictionary<string, double> { ["jawOpen"] = 0.6 });
            yield return new(480, new Dictionary<string, double> { ["jawOpen"] = 0.3 });
        }
        public void Invalidate() { }
        public void Dispose() { }
    }

    /// <summary>Records the unobserved task exceptions that mention <paramref name="marker"/> (other tests run at the same time).</summary>
    private sealed class Unobserved : IDisposable
    {
        private readonly EventHandler<UnobservedTaskExceptionEventArgs> handler;
        internal ConcurrentQueue<Exception> Seen { get; } = new();
        internal Unobserved(string marker)
        {
            handler = (_, e) => { if (e.Exception.ToString().Contains(marker, StringComparison.Ordinal)) Seen.Enqueue(e.Exception); };
            TaskScheduler.UnobservedTaskException += handler;
        }
        internal static void Collect()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        public void Dispose() => TaskScheduler.UnobservedTaskException -= handler;
    }

    private sealed record MouthLevel(double Level);

    private static bool Moves(Renderer renderer) =>
        renderer.Messages.Any(m => m.Kind == "mouth" && RendererProtocol.Data<MouthLevel>(m).Level > 0);

    private static AvatarRemoteHost Remote() => new()
    {
        Origin = "https://192.168.1.20:9443", HostId = "gpu-host", SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    private static string ClosedEndpoint()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        return $"http://127.0.0.1:{port}/";
    }

    private static Exception Failure(string kind, string marker) => kind switch
    {
        "invalid-data" => new InvalidDataException($"Renderer message length is invalid. {marker}"),
        "io" => new IOException($"Pipe is broken. {marker}"),
        "json" => new System.Text.Json.JsonException($"Unreadable reply. {marker}"),
        "disposed" => new ObjectDisposedException($"Renderer {marker}"),
        // The renderer's own time limit, not the caller's token.
        "timeout" => new OperationCanceledException($"Renderer time limit. {marker}", new CancellationToken(true)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task SpeakAsync(Harness harness, ControlledDevice device, Func<bool> until)
    {
        device.AutoConsume = false;
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(until);
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
    }

    [Theory]
    [InlineData("invalid-data")]
    [InlineData("io")]
    [InlineData("json")]
    [InlineData("disposed")]
    [InlineData("timeout")]
    public async Task A_renderer_failing_the_loudness_mouth_ends_only_that_sentence_and_the_character_still_stops(string failure)
    {
        var marker = Guid.NewGuid().ToString("N");
        using var unobserved = new Unobserved(marker);
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Marker = marker, Fail = kind => kind == "mouth" ? Failure(failure, marker) : null };
        await using (var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true))
        {
            await controller.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
            var device = new ControlledDevice { AutoConsume = false };
            await using var harness = new Harness(device, generatedSpeech: controller.Observer);
            await SpeakAsync(harness, device, () => renderer.Attempts.Contains("mouth"));
            Assert.False(Moves(renderer));

            // The character's lip-sync goes on with the next sentence once the renderer answers again.
            renderer.Fail = null;
            await SpeakAsync(harness, device, () => Moves(renderer));

            await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(controller.IsShowing);
            Assert.Equal("Character hidden. Voice continues.", controller.Status);
        }
        Unobserved.Collect();
        Assert.Empty(unobserved.Seen);
    }

    [Fact]
    public async Task Stopping_while_a_renderer_turns_canceled_audio2face_frames_into_unreadable_replies_finishes_without_an_error()
    {
        // The desktop log's case: hiding the character canceled the frames' renderer commands mid-message, and the renderer then
        // answered "Renderer message length is invalid" for the face and for the loudness mouth.
        var marker = Guid.NewGuid().ToString("N");
        using var unobserved = new Unobserved(marker);
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Marker = marker, FailOnCancel = kind => kind is "apply" or "mouth" };
        var device = new ControlledDevice { AutoConsume = false };
        await using (var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true,
            openHost: _ => new HostLink()))
        {
            await controller.ShowAsync(scope.Profile(ClosedEndpoint()) with { RemoteHost = Remote() }, default);
            await using var harness = new Harness(device, generatedSpeech: controller.Observer);
            harness.Answer("Actual generated PCM test.");
            var turn = harness.Start();
            await Harness.Until(() => renderer.Attempts.Contains("apply"));

            await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(controller.IsShowing);
            Assert.Equal("Character hidden. Voice continues.", controller.Status);

            // The voice is unaffected.
            device.AutoConsume = true;
            Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
            Assert.Equal(SpeechFixtures.Audio(), device.Bytes);
        }
        Unobserved.Collect();
        Assert.Empty(unobserved.Seen);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("apply")]
    public async Task A_renderer_failing_audio2face_frames_isnt_blamed_on_audio2face_and_the_loudness_mouth_goes_on(string command)
    {
        var marker = Guid.NewGuid().ToString("N");
        using var unobserved = new Unobserved(marker);
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Marker = marker, Fail = kind => kind == command ? Failure("invalid-data", marker) : null };
        await using (var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true,
            openHost: _ => new HostLink()))
        {
            await controller.ShowAsync(scope.Profile(ClosedEndpoint()) with { RemoteHost = Remote() }, default);
            var device = new ControlledDevice { AutoConsume = false };
            await using var harness = new Harness(device, generatedSpeech: controller.Observer);
            await SpeakAsync(harness, device, () => renderer.Attempts.Contains(command) && Moves(renderer));
            Assert.DoesNotContain("wasn't available", controller.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(renderer.Messages, m => m.Kind == "stop");

            await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(controller.IsShowing);
        }
        Unobserved.Collect();
        Assert.Empty(unobserved.Seen);
    }

    [Fact]
    public void A_renderer_failure_is_the_renderers_own_never_the_callers_cancellation()
    {
        using var caller = new CancellationTokenSource();
        var timeLimit = new OperationCanceledException(new CancellationToken(true));
        Assert.True(RendererFailures.Is(timeLimit, caller.Token));
        Assert.True(RendererFailures.Is(new InvalidDataException("Renderer message length is invalid."), caller.Token));
        Assert.True(RendererFailures.Is(new System.Text.Json.JsonException(), caller.Token));
        Assert.True(RendererFailures.Is(new IOException(), caller.Token));
        Assert.True(RendererFailures.Is(new EndOfStreamException(), caller.Token));
        Assert.True(RendererFailures.Is(new ObjectDisposedException("renderer"), caller.Token));
        Assert.True(RendererFailures.Is(new TimeoutException(), caller.Token));
        Assert.True(RendererFailures.Is(new CharacterRendererException(new InvalidDataException()), caller.Token));
        Assert.False(RendererFailures.Is(new NullReferenceException(), caller.Token));
        Assert.False(RendererFailures.Is(new ArgumentException(), caller.Token));
        caller.Cancel();
        Assert.False(RendererFailures.Is(new OperationCanceledException(caller.Token), caller.Token));
        Assert.False(RendererFailures.Is(timeLimit, caller.Token));
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Renderer_failures_get_one_short_line_for_each_kind_in_a_minute()
    {
        var clock = new Clock();
        var log = new RendererFailureLog(clock);
        const string placement = "The character's new position couldn't be read to save it";
        var unreadable = new InvalidDataException("Renderer message length is invalid.");
        Assert.Equal($"{placement}: Renderer message length is invalid (InvalidDataException).", log.Line(placement, unreadable));
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.Null(log.Line(placement, unreadable));
        Assert.Null(log.Line(placement, new InvalidDataException("Renderer message length is invalid.")));
        // Another reason, or another piece of work, is another kind of failure.
        var late = log.Line(placement, new OperationCanceledException());
        Assert.Equal($"{placement}: the character renderer didn't answer in time.", late);
        Assert.NotNull(log.Line("The character couldn't be told where to look", unreadable));
        Assert.Equal("The camera view couldn't be refreshed: the character renderer had already closed.",
            log.Line("The camera view couldn't be refreshed", new ObjectDisposedException("x")));
        clock.Now += TimeSpan.FromSeconds(55);
        Assert.Equal($"{placement}: Renderer message length is invalid (InvalidDataException). (2 more like it in the minute before weren't logged.)",
            log.Line(placement, unreadable));
        Assert.Null(log.Line(placement, unreadable));
        Assert.DoesNotContain("   at ", log.Line("Other work", unreadable), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_simulated_renderer_failure_fixture_fails_only_the_named_commands()
    {
        var renderer = new Renderer();
        Assert.Same(renderer, SimulatedRendererFailure.Wrap(() => renderer, null)());
        Assert.Same(renderer, SimulatedRendererFailure.Wrap(() => renderer, " , ")());
        var failing = SimulatedRendererFailure.Wrap(() => renderer, "where, mouth")();
        Assert.NotSame(renderer, failing);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => failing.SendAsync("where", new { }, default));
        Assert.Contains(SimulatedRendererFailure.Variable, error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => failing.SendAsync("mouth", new { level = 0.5 }, default));
        Assert.Equal("ok", (await failing.SendAsync("gaze", new { }, default)).Kind);
        Assert.Equal("gaze", Assert.Single(renderer.Attempts));
        await failing.DisposeAsync();
        Assert.True(renderer.HasExited);
    }
}

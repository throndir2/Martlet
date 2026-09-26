using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Speech.Windows;

namespace Martlet.Speech.Windows.Tests;

public sealed class WindowsSpeechTests
{
    [Fact]
    public async Task Construction_is_passive_and_discovery_is_explicit_owned_fixture()
    {
        var count = 0;
        var engine = new FakeEngine();
        await using var adapter = new WindowsSpeechSynthesisAdapter(() => { count++; return engine; }, TimeProvider.System);
        Assert.Equal(0, count);
        var voices = await adapter.GetInstalledVoicesAsync();
        Assert.Equal("fixture-voice", Assert.Single(voices).Id);
        Assert.True(engine.Disposed);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Bounded_pcm_has_exact_format_offsets_and_fixture_provenance()
    {
        var engine = new FakeEngine { Bytes = 1922 };
        await using var adapter = Adapter(engine);
        var action = Action();
        var result = await Synthesize(adapter, action);
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Outcome);
        Assert.Equal(EvidenceProvenance.Fixture, result.Provenance);
        Assert.True(result.OwnershipReleased);
        Assert.True(engine.Disposed);
        var audio = Assert.IsType<WindowsSpeechAudio>(result.Audio);
        Assert.Equal(961, audio.SampleCount);
        var frames = await Read(audio);
        Assert.Equal(new[] { 960, 960, 2 }, frames.Select(f => f.Data.Length));
        Assert.Equal(new long[] { 0, 1, 2 }, frames.Select(f => f.Sequence));
        Assert.Equal(new long[] { 0, 480, 960 }, frames.Select(f => f.SampleOffset));
        Assert.All(frames, f =>
        {
            Assert.Equal(action.Context.Ids, f.Ids);
            Assert.Equal(action.Context.Epoch, f.Epoch);
            Assert.Equal(WindowsSpeechSynthesisAdapter.Format, f.Format);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(audio));
    }

    [Theory]
    [InlineData("missing", WindowsSpeechFailure.ConsentMissing)]
    [InlineData("denied", WindowsSpeechFailure.ConsentMissing)]
    [InlineData("text", WindowsSpeechFailure.ConsentMismatch)]
    [InlineData("voice", WindowsSpeechFailure.ConsentMismatch)]
    [InlineData("context", WindowsSpeechFailure.ConsentMismatch)]
    [InlineData("limits", WindowsSpeechFailure.ConsentMismatch)]
    [InlineData("expired", WindowsSpeechFailure.ConsentExpired)]
    public async Task Consent_is_exact_without_cloud_fields(string change, WindowsSpeechFailure expected)
    {
        var opened = false;
        await using var adapter = new WindowsSpeechSynthesisAdapter(() => { opened = true; return new FakeEngine(); }, TimeProvider.System);
        var action = Action();
        var consent = change == "missing" ? null : new WindowsSpeechAuthorization(
            change == "context" ? action.Context with { Epoch = 2 } : action.Context,
            change == "voice" ? "another-voice" : Voice,
            change == "text" ? new(action.Input.Text) : action.Input,
            change == "limits" ? action.Limits with { MaxAudioBytes = 2 } : action.Limits,
            change == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : action.Context.Deadline,
            change != "denied");
        var result = await adapter.SynthesizeAsync(action.Context, Voice, action.Input, action.Limits, consent);
        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Audio);
        Assert.False(opened);
    }

    [Fact]
    public async Task Consent_cannot_be_replayed_and_canceled_buffer_cannot_be_delivered()
    {
        await using var adapter = Adapter(new());
        var action = Action();
        using var cancel = new CancellationTokenSource();
        var result = await Synthesize(adapter, action, cancel.Token);
        Assert.Equal(WindowsSpeechFailure.ConsentConsumed, (await Synthesize(adapter, action)).Failure);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(result.Audio!));
    }

    [Theory]
    [InlineData(0, WindowsSpeechFailure.InvalidAudio)]
    [InlineData(3, WindowsSpeechFailure.InvalidAudio)]
    [InlineData(1924, WindowsSpeechFailure.AudioLimit)]
    public async Task Empty_unaligned_and_oversized_audio_fail_closed(int bytes, WindowsSpeechFailure expected)
    {
        await using var adapter = Adapter(new() { Bytes = bytes });
        var action = Action(new() { MaxAudioBytes = 1922 });
        var result = await Synthesize(adapter, action);
        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task Duration_cap_is_enforced_even_when_byte_cap_is_larger()
    {
        await using var adapter = Adapter(new() { Bytes = 962 });
        var result = await Synthesize(adapter, Action(new() { MaxAudioDuration = TimeSpan.FromMilliseconds(20) }));
        Assert.Equal(WindowsSpeechFailure.AudioLimit, result.Failure);
    }

    [Fact]
    public async Task Cancellation_retains_native_and_buffer_ownership_until_worker_and_dispose_finish()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var engine = new FakeEngine
        {
            Work = (_, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        var adapter = Adapter(engine);
        var operation = Synthesize(adapter, Action(), cancel.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        cancel.Cancel();
        Assert.False(operation.IsCompleted);
        Assert.Equal(WindowsSpeechFailure.Busy,
            Assert.Throws<WindowsSpeechException>(() => { _ = Synthesize(adapter, Action()); }).Failure);
        var dispose = adapter.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.False(engine.Disposed);
        release.Set();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose;
        Assert.Equal(SpeechSynthesisOutcome.Canceled, result.Outcome);
        Assert.Null(result.Audio);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task Cleanup_failure_quarantines_and_never_publishes_audio()
    {
        await using var adapter = Adapter(new() { FailDispose = true });
        var result = await Synthesize(adapter, Action());
        Assert.Equal(WindowsSpeechFailure.CleanupFailed, result.Failure);
        Assert.False(result.OwnershipReleased);
        Assert.Null(result.Audio);
        Assert.Equal(WindowsSpeechFailure.Quarantined,
            Assert.Throws<WindowsSpeechException>(() => { _ = adapter.GetInstalledVoicesAsync(); }).Failure);
    }

    [Theory]
    [InlineData(false, WindowsSpeechFailure.FirstAudioTimeout)]
    [InlineData(true, WindowsSpeechFailure.IdleTimeout)]
    public async Task Progress_deadlines_are_checked_against_original_clock(bool write, WindowsSpeechFailure expected)
    {
        var clock = new TestClock();
        var engine = new FakeEngine
        {
            Work = (stream, _) =>
            {
                if (write) stream.Write(new byte[2]);
                clock.Advance(TimeSpan.FromSeconds(3));
            }
        };
        await using var adapter = new WindowsSpeechSynthesisAdapter(() => engine, clock);
        var result = await Synthesize(adapter, Action(new()
        {
            FirstAudioTimeout = TimeSpan.FromSeconds(2), IdleTimeout = TimeSpan.FromSeconds(2)
        }));
        Assert.Equal(expected, result.Failure);
        Assert.Equal(SpeechSynthesisOutcome.DeadlineExceeded, result.Outcome);
    }

    [Fact]
    public async Task Deadline_expiry_during_native_disposal_discards_completed_buffer()
    {
        var clock = new TestClock();
        var engine = new FakeEngine { OnDispose = () => clock.Advance(TimeSpan.FromSeconds(31)) };
        await using var adapter = new WindowsSpeechSynthesisAdapter(() => engine, clock);
        var result = await Synthesize(adapter, Action());
        Assert.Equal(WindowsSpeechFailure.DeadlineExceeded, result.Failure);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task Original_deadline_is_rechecked_before_buffered_frames()
    {
        var clock = new TestClock();
        await using var adapter = new WindowsSpeechSynthesisAdapter(() => new FakeEngine(), clock);
        var result = await Synthesize(adapter, Action());
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(WindowsSpeechFailure.DeadlineExceeded,
            (await Assert.ThrowsAsync<WindowsSpeechException>(() => Read(result.Audio!))).Failure);
    }

    [Fact]
    public async Task Unexpected_worker_failure_still_disposes_native_owner()
    {
        var engine = new FakeEngine { Work = (_, _) => throw new NotSupportedException("fixture failure") };
        var adapter = Adapter(engine);
        await Assert.ThrowsAsync<NotSupportedException>(() => Synthesize(adapter, Action()));
        Assert.True(engine.Disposed);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.DisposeAsync().AsTask());
    }

    internal const string Voice = "fixture-voice";
    internal static WindowsSpeechSynthesisAdapter Adapter(FakeEngine engine) => new(() => engine, TimeProvider.System);
    internal static SpeechAction Action(SpeechSynthesisLimits? limits = null)
    {
        var context = new ProviderRequestContext
        {
            Ids = new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
            Epoch = 1, Deadline = DateTimeOffset.UtcNow.AddSeconds(30)
        };
        var input = new BoundedSpeechInput("Hello. This is a local speech test.");
        limits ??= new();
        return new(context, input, limits, new(context, Voice, input, limits, context.Deadline, true));
    }

    internal static Task<WindowsSpeechResult> Synthesize(WindowsSpeechSynthesisAdapter adapter, SpeechAction action,
        CancellationToken token = default) =>
        adapter.SynthesizeAsync(action.Context, Voice, action.Input, action.Limits, action.Consent, token);

    internal static async Task<List<PcmFrame>> Read(WindowsSpeechAudio audio)
    {
        var frames = new List<PcmFrame>();
        await foreach (var frame in audio.Frames) frames.Add(frame);
        return frames;
    }

    internal sealed record SpeechAction(ProviderRequestContext Context, BoundedSpeechInput Input,
        SpeechSynthesisLimits Limits, WindowsSpeechAuthorization Consent);

    internal sealed class FakeEngine : IWindowsSpeechEngine
    {
        internal int Bytes { get; init; } = 960;
        internal Action<Stream, Action>? Work { get; init; }
        internal Action? OnDispose { get; init; }
        internal bool FailDispose { get; init; }
        internal bool Disposed { get; private set; }
        public IReadOnlyList<WindowsSpeechVoice> GetVoices() => [new(Voice, "Fixture only", "en-US")];
        public void Synthesize(string voiceId, string text, Stream output, Action ensureActive)
        {
            Assert.Equal(Voice, voiceId);
            if (Work is not null) Work(output, ensureActive);
            else output.Write(new byte[Bytes]);
        }
        public void Dispose()
        {
            Disposed = true;
            OnDispose?.Invoke();
            if (FailDispose) throw new InvalidOperationException("fixture cleanup failure");
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset initial = DateTimeOffset.UtcNow;
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(ticks);
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}

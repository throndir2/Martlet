using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Martlet.LocalStt;

namespace Martlet.Stt.Windows.Tests;

public sealed class AdapterTests
{
    [Fact]
    public async Task Constructor_and_invalid_or_precanceled_requests_never_touch_native_engine()
    {
        var factory = new FakeFactory();
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        Assert.Equal(0, factory.Calls);
        Assert.Equal(WindowsOfflineSttFailureCode.AuthorizationMissing,
            (await adapter.TranscribeAsync(request, audio, null)).Failure!.Code);
        Assert.Equal(WindowsOfflineSttOutcome.Canceled,
            (await adapter.TranscribeAsync(request, audio, Permit(request, audio), new(true))).Outcome);
        Assert.Equal(WindowsOfflineSttFailureCode.InvalidRequest,
            (await adapter.TranscribeAsync(request with { RecognizerId = "\0" }, audio, null)).Failure!.Code);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("deadline")]
    [InlineData("recognizer")]
    [InlineData("audio")]
    [InlineData("size")]
    public async Task Exact_selection_audio_and_deadline_bind_permission_before_consumption(string mismatch)
    {
        var factory = new FakeFactory();
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var permit = new WindowsOfflineSttAuthorization(
            mismatch == "operation" ? Guid.NewGuid() : request.OperationId,
            mismatch == "deadline" ? request.Deadline.AddSeconds(-1) : request.Deadline,
            mismatch == "recognizer" ? "other" : request.RecognizerId,
            mismatch == "audio" ? new string('0', 64) : audio.Sha256,
            mismatch == "size" ? 46 : audio.ByteLength, true);
        var result = await adapter.TranscribeAsync(request, audio, permit);
        Assert.Equal(WindowsOfflineSttFailureCode.AuthorizationMismatch, result.Failure!.Code);
        Assert.True(permit.TryConsume());
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData(" hello world ", WindowsOfflineSttOutcome.Completed, "hello world")]
    [InlineData(" \r\n\t", WindowsOfflineSttOutcome.NoSpeech, null)]
    public async Task Successful_result_has_fixture_provenance_and_no_default_input(
        string text, WindowsOfflineSttOutcome outcome, string? expected)
    {
        var factory = new FakeFactory { Text = text };
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var permit = Permit(request, audio);
        var result = await adapter.TranscribeAsync(request, audio, permit);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(expected, result.Text);
        Assert.Equal(WindowsOfflineSttProvenance.Fixture, result.Provenance);
        Assert.Equal(request.RecognizerId, factory.SelectedId);
        Assert.Equal(audio.CopyWave(), factory.Input);
        Assert.Equal(1, factory.Disposals);
        Assert.Equal(WindowsOfflineSttFailureCode.AuthorizationConsumed,
            (await adapter.TranscribeAsync(request, audio, permit)).Failure!.Code);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task Cancellation_retains_owner_and_audio_until_native_cleanup_then_discards_late_text()
    {
        var factory = new FakeFactory { Block = true };
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        using var cancel = new CancellationTokenSource();
        var request = Request();
        var pending = adapter.TranscribeAsync(request, audio, Permit(request, audio), cancel.Token);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        audio.Dispose();
        cancel.Cancel();
        Assert.False(pending.IsCompleted);
        using var otherAudio = Audio();
        var other = Request();
        Assert.Equal(WindowsOfflineSttFailureCode.Busy,
            (await adapter.TranscribeAsync(other, otherAudio, Permit(other, otherAudio))).Failure!.Code);
        Assert.Equal(WindowsOfflineSttFailureCode.Busy, (await adapter.GetInstalledRecognizersAsync()).Failure!.Code);
        factory.Release.Set();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsOfflineSttOutcome.Canceled, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public async Task Dispose_cancels_and_joins_native_owner_not_just_the_public_task()
    {
        var factory = new FakeFactory { Block = true };
        var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var pending = adapter.TranscribeAsync(request, audio, Permit(request, audio));
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = adapter.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.True(factory.Token.IsCancellationRequested);
        factory.Release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsOfflineSttOutcome.Canceled, (await pending).Outcome);
        Assert.Equal(1, factory.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetInstalledRecognizersAsync());
    }

    [Fact]
    public async Task Deadline_includes_native_work_and_discards_output_without_abandoning_engine()
    {
        var factory = new FakeFactory { Block = true };
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request() with { Deadline = DateTimeOffset.UtcNow.AddSeconds(1) };
        var pending = adapter.TranscribeAsync(request, audio, Permit(request, audio));
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await Task.Run(() => factory.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))));
        Assert.False(pending.IsCompleted);
        factory.Release.Set();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsOfflineSttFailureCode.DeadlineExceeded, result.Failure!.Code);
        Assert.Null(result.Text);
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public async Task Cleanup_failure_overrides_success_and_cancellation_and_quarantines_owner()
    {
        var factory = new FakeFactory { FailDispose = true };
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var result = await adapter.TranscribeAsync(request, audio, Permit(request, audio));
        Assert.Equal(WindowsOfflineSttFailureCode.CleanupFailed, result.Failure!.Code);
        Assert.Null(result.Text);
        Assert.Equal(WindowsOfflineSttFailureCode.Quarantined,
            (await adapter.TranscribeAsync(request, audio, Permit(request, audio))).Failure!.Code);
        Assert.Equal(WindowsOfflineSttFailureCode.Quarantined, (await adapter.GetInstalledRecognizersAsync()).Failure!.Code);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_during_cleanup_rejects_text_even_without_timer_delivery(bool utcRollback)
    {
        var clock = new MutableClock();
        var factory = new FakeFactory
        {
            BeforeDispose = () =>
            {
                clock.Elapsed = TimeSpan.FromSeconds(31);
                clock.Utc = clock.Utc.AddSeconds(utcRollback ? -60 : 31);
            }
        };
        await using var adapter = new WindowsOfflineSttAdapter(factory, clock);
        using var audio = Audio();
        var request = Request() with { Deadline = clock.Utc.AddSeconds(25) };
        var result = await adapter.TranscribeAsync(request, audio, Permit(request, audio));
        Assert.Equal(WindowsOfflineSttFailureCode.DeadlineExceeded, result.Failure!.Code);
        Assert.Null(result.Text);
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public async Task Cancellation_during_create_still_reports_failed_disposal()
    {
        using var cancel = new CancellationTokenSource();
        var factory = new FakeFactory { FailDispose = true, BeforeCreateReturn = cancel.Cancel };
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var result = await adapter.TranscribeAsync(request, audio, Permit(request, audio), cancel.Token);
        Assert.Equal(WindowsOfflineSttFailureCode.CleanupFailed, result.Failure!.Code);
        Assert.Empty(factory.Input);
    }

    [Theory]
    [InlineData("unavailable", WindowsOfflineSttFailureCode.RecognizerUnavailable)]
    [InlineData("platform", WindowsOfflineSttFailureCode.UnsupportedHost)]
    [InlineData("com", WindowsOfflineSttFailureCode.RecognitionFailed)]
    public async Task Native_errors_are_typed_and_redacted(string error, WindowsOfflineSttFailureCode expected)
    {
        var factory = new FakeFactory { Error = error };
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var result = await adapter.TranscribeAsync(request, audio, Permit(request, audio));
        Assert.Equal(expected, result.Failure!.Code);
        Assert.DoesNotContain("private-canary", result.Failure.ToString());
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("surrogate")]
    [InlineData("limit")]
    public async Task Malformed_or_excessive_text_is_never_accepted(string kind)
    {
        var factory = new FakeFactory { Text = kind switch
        {
            "invalid" => "bad\0text", "surrogate" => "bad\ud800", _ => new string('a', 4097)
        }};
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        using var audio = Audio();
        var request = Request();
        var result = await adapter.TranscribeAsync(request, audio, Permit(request, audio));
        Assert.Equal(kind == "limit" ? WindowsOfflineSttFailureCode.TranscriptLimit :
            WindowsOfflineSttFailureCode.TranscriptInvalid, result.Failure!.Code);
        Assert.Null(result.Text);
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public async Task Explicit_discovery_distinguishes_installed_missing_and_canceled_without_creating_engine()
    {
        var factory = new FakeFactory();
        await using var adapter = new WindowsOfflineSttAdapter(factory, TimeProvider.System);
        var found = await adapter.GetInstalledRecognizersAsync();
        Assert.Single(found.Recognizers);
        Assert.Null(found.Failure);
        factory.Recognizers = [];
        var missing = await adapter.GetInstalledRecognizersAsync();
        Assert.Empty(missing.Recognizers);
        Assert.Equal(WindowsOfflineSttFailureCode.RecognizerUnavailable, missing.Failure!.Code);
        Assert.True((await adapter.GetInstalledRecognizersAsync(new(true))).Canceled);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Canonical_wave_export_is_an_owned_copy_and_rejects_disposal()
    {
        using var audio = Audio();
        var expected = audio.CopyWave();
        var copy = audio.CopyWave();
        copy[0] = 0;
        Assert.Equal(expected, audio.CopyWave());
        audio.Dispose();
        Assert.Equal((byte)'R', expected[0]);
        Assert.Throws<ObjectDisposedException>(() => audio.CopyWave());
    }

    internal static CanonicalWaveAudio Audio()
    {
        var bytes = new byte[44 + 32_000];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        return CanonicalWaveAudio.FromWave(bytes);
    }

    internal static WindowsOfflineSttRequest Request() =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(25), "fixture-id");
    internal static WindowsOfflineSttAuthorization Permit(WindowsOfflineSttRequest request, CanonicalWaveAudio audio) =>
        new(request.OperationId, request.Deadline, request.RecognizerId, audio.Sha256, audio.ByteLength, true);

    private sealed class FakeFactory : IOfflineRecognizerFactory
    {
        internal int Calls, Disposals;
        internal bool Block, FailDispose;
        internal string Text = "fixture transcript";
        internal string? Error, SelectedId;
        internal byte[] Input = [];
        internal CancellationToken Token;
        internal Action? BeforeCreateReturn, BeforeDispose;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal ImmutableArray<WindowsOfflineRecognizer> Recognizers = [new("fixture-id", "Fixture", "en-US")];
        public WindowsOfflineSttProvenance Provenance => WindowsOfflineSttProvenance.Fixture;
        public ImmutableArray<WindowsOfflineRecognizer> Discover() => Recognizers;
        public IOfflineRecognizerSession Create(string recognizerId)
        {
            Calls++;
            SelectedId = recognizerId;
            if (Error == "unavailable") throw new RecognizerUnavailableException();
            if (Error == "platform") throw new PlatformNotSupportedException("private-canary");
            if (Error == "com") throw new COMException("private-canary");
            BeforeCreateReturn?.Invoke();
            return new Session(this);
        }
        private sealed class Session(FakeFactory owner) : IOfflineRecognizerSession
        {
            public string Recognize(Stream wave, CancellationToken cancellationToken)
            {
                using var copy = new MemoryStream();
                wave.CopyTo(copy);
                owner.Input = copy.ToArray();
                owner.Token = cancellationToken;
                owner.Started.SetResult();
                if (owner.Block && !owner.Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new InvalidOperationException("Test release timed out.");
                return owner.Text;
            }
            public void Dispose()
            {
                owner.Disposals++;
                owner.BeforeDispose?.Invoke();
                if (owner.FailDispose) throw new COMException("private-canary");
            }
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Utc = DateTimeOffset.UtcNow;
        internal TimeSpan Elapsed;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}

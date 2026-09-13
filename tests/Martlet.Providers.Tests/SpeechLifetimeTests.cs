using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class SpeechLifetimeTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("credential")]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task Cancellation_at_every_await_suppresses_late_data_and_releases_resources(string phase)
    {
        using var stop = new CancellationTokenSource();
        BoundProviderCredential? credential = null;
        var source = new FixtureCredentials
        {
            Resolve = async (binding, _) =>
            {
                await Task.Yield();
                if (phase == "credential") stop.Cancel();
                return credential = new(binding, ProviderFixtures.Secret);
            }
        };
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 960)
        {
            IgnoreCancellation = true,
            OnRead = () => { if (phase == "body") stop.Cancel(); }
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = async (_, _) =>
        {
            await Task.Yield();
            if (phase == "headers") stop.Cancel();
            return SpeechFixtures.Pcm(body);
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        if (phase == "before") stop.Cancel();
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits), stop.Token));
        Assert.Equal(SpeechSynthesisOutcome.Canceled, result.Result.Outcome);
        Assert.Empty(result.Frames);
        Assert.Equal(phase == "before" ? 0 : 1, source.Calls);
        Assert.Equal(phase is "before" or "credential" ? 0 : 1, handler.Calls);
        if (credential is not null) Assert.Throws<CredentialUnavailableException>(() => credential.CreateAuthorization());
        if (phase is "headers" or "body") Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("enumerator")]
    [InlineData("adapter")]
    public async Task Stop_after_pcm_discards_the_rest_preserving_partial_state_and_epoch(string mode)
    {
        using var stop = new CancellationTokenSource();
        var body = new FragmentedTextBody(SpeechFixtures.Audio(2000), 960);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits), mode == "caller" ? stop.Token : default);
        await using var enumerator = stream.GetAsyncEnumerator(mode == "enumerator" ? stop.Token : default);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(480, enumerator.Current.SamplesPerChannel);
        if (mode == "adapter") adapter.Dispose(); else stop.Cancel();
        Assert.True(body.Disposed);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(960, body.BytesRead);
        Assert.Equal(SpeechSynthesisOutcome.Canceled, stream.Result!.Outcome);
        Assert.Equal(context, stream.Result.Context);
        Assert.Equal(480, stream.Result.DeliveredSampleCount);
        Assert.True(stream.Result.IsPartial);
        Assert.Null(stream.Result.FinalSampleCount);
        Assert.Null(stream.Result.ProviderTerminal);
        stream.Result.ToTerminalEvent().Validate();
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_enumerator_with_or_without_starting_is_local_cancellation(bool start)
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio(2000), 4096);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var source = new FixtureCredentials();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits, SpeechFixtures.Authorize(context, input, limits));
        var enumerator = stream.GetAsyncEnumerator();
        if (start) Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(SpeechSynthesisOutcome.Canceled, stream.Result!.Outcome);
        Assert.Equal(start ? 480 : 0, stream.Result.DeliveredSampleCount);
        Assert.Equal(start ? 1 : 0, source.Calls);
        Assert.Equal(start ? 1 : 0, handler.Calls);
        if (start) Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task Pull_backpressure_retains_only_one_frame_and_never_starts_background_reads()
    {
        var bytes = SpeechFixtures.Audio(2_160_000);
        var body = new FragmentedTextBody(bytes, int.MaxValue);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits, SpeechFixtures.Authorize(context, input, limits));
        await using var enumerator = stream.GetAsyncEnumerator();
        Assert.Equal(0, handler.Calls);
        Assert.True(await enumerator.MoveNextAsync());
        var first = enumerator.Current;
        Assert.Equal(960, body.BytesRead);
        await Task.Yield();
        Assert.Equal(960, body.BytesRead);
        Assert.False(body.Disposed);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1920, body.BytesRead);
        Assert.Equal(bytes[..960], first.Data.ToArray());
        Assert.Equal(bytes[960..1920], enumerator.Current.Data.ToArray());
        await enumerator.DisposeAsync();
        Assert.True(body.Disposed);
        Assert.Equal(1920, body.BytesRead);
    }

    [Theory]
    [InlineData("expiry", ProviderFailureCode.ConsentExpired)]
    [InlineData("rollback", ProviderFailureCode.ConsentExpired)]
    [InlineData("forward", ProviderFailureCode.ConsentExpired)]
    [InlineData("deadline", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("maximum", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("first", ProviderFailureCode.FirstAudioTimeout)]
    public async Task Delayed_timers_after_credentials_do_not_extend_original_windows(string cause, ProviderFailureCode code)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        BoundProviderCredential? credential = null;
        var source = new FixtureCredentials
        {
            Resolve = async (binding, _) =>
            {
                await Task.Yield();
                if (cause == "forward") clock.ShiftUtc(TimeSpan.FromHours(1));
                else clock.Advance(TimeSpan.FromSeconds(5));
                if (cause == "rollback") clock.ShiftUtc(TimeSpan.FromHours(-1));
                return credential = new(binding, ProviderFixtures.Secret);
            }
        };
        var context = ProviderFixtures.Context();
        if (cause == "deadline") context = context with { Deadline = ProviderFixtures.Now.AddSeconds(5) };
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits
        {
            MaxRequestTime = TimeSpan.FromSeconds(cause == "maximum" ? 5 : 90),
            FirstAudioTimeout = TimeSpan.FromSeconds(cause == "first" ? 5 : 20)
        };
        var handler = SpeechFixtures.Handler();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, clock);
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cause is "expiry" or "rollback" or "forward"
                ? ProviderFixtures.Now.AddSeconds(5) : null)));
        Assert.Equal(code, result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Throws<CredentialUnavailableException>(() => credential!.CreateAuthorization());
    }

    [Theory]
    [InlineData("consent", ProviderFailureCode.ConsentExpired)]
    [InlineData("total", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("first", ProviderFailureCode.FirstAudioTimeout)]
    public async Task Serializer_entry_checks_windows_before_writing_any_request_bytes(string cause, ProviderFailureCode code)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var handler = SpeechFixtures.Handler();
        handler.BeforeSerialization = () => clock.Advance(TimeSpan.FromSeconds(5));
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits
        {
            MaxRequestTime = TimeSpan.FromSeconds(cause == "total" ? 5 : 90),
            FirstAudioTimeout = TimeSpan.FromSeconds(cause == "first" ? 5 : 20)
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cause == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null)));
        Assert.Equal(code, result.Result.Failure!.Code);
        Assert.Empty(handler.Body);
        Assert.Empty(result.Frames);
    }

    [Theory]
    [InlineData("consent", ProviderFailureCode.ConsentExpired)]
    [InlineData("total", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("idle", ProviderFailureCode.IdleTimeout)]
    public async Task Deferred_cutoffs_after_pcm_prevent_any_more_buffered_output(string cause, ProviderFailureCode code)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 960);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits
        {
            MaxRequestTime = TimeSpan.FromSeconds(cause == "total" ? 5 : 90),
            IdleTimeout = TimeSpan.FromSeconds(cause == "idle" ? 5 : 10)
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cause == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null));
        await using var enumerator = stream.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        clock.Advance(TimeSpan.FromSeconds(5));
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(code, stream.Result!.Failure!.Code);
        Assert.Equal(960, body.BytesRead);
        Assert.True(stream.Result.IsPartial);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("consent")]
    [InlineData("forward")]
    public async Task Noncooperative_late_bytes_are_rechecked_before_acceptance(string cause)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 960)
        {
            IgnoreCancellation = true,
            BeforeRead = async _ =>
            {
                await Task.Yield();
                if (cause == "forward") clock.ShiftUtc(TimeSpan.FromHours(1));
                else clock.Advance(TimeSpan.FromSeconds(2));
            }
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits { FirstAudioTimeout = TimeSpan.FromSeconds(cause == "first" ? 2 : 20) };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cause == "consent" ? ProviderFixtures.Now.AddSeconds(2) : null)));
        Assert.Empty(result.Frames);
        Assert.Equal(cause == "first" ? ProviderFailureCode.FirstAudioTimeout : ProviderFailureCode.ConsentExpired, result.Result.Failure!.Code);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("first", "credential")]
    [InlineData("first", "headers")]
    [InlineData("first", "body")]
    [InlineData("total", "body")]
    [InlineData("consent", "body")]
    [InlineData("idle", "body")]
    public async Task Real_timer_callbacks_cancel_waits_and_dispose_acquired_streams(string cutoff, string phase)
    {
        var clock = new FixtureClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Wait(CancellationToken token)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        var source = new FixtureCredentials
        {
            Resolve = async (binding, token) =>
            {
                if (phase == "credential") await Wait(token);
                return new(binding, ProviderFixtures.Secret);
            }
        };
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 960);
        body.BeforeRead = async token =>
        {
            if (phase == "body" && (cutoff != "idle" || body.BytesRead > 0)) await Wait(token);
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = async (_, token) =>
        {
            if (phase == "headers") await Wait(token);
            return SpeechFixtures.Pcm(body);
        };
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits
        {
            FirstAudioTimeout = TimeSpan.FromSeconds(cutoff == "first" ? 2 : 20),
            MaxRequestTime = TimeSpan.FromSeconds(cutoff == "total" ? 2 : 90),
            IdleTimeout = TimeSpan.FromSeconds(cutoff == "idle" ? 2 : 10)
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, clock);
        var collect = SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cutoff == "consent" ? ProviderFixtures.Now.AddSeconds(2) : null)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await collect.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(cutoff switch
        {
            "first" => ProviderFailureCode.FirstAudioTimeout, "idle" => ProviderFailureCode.IdleTimeout,
            _ => ProviderFailureCode.DeadlineExceeded
        }, result.Result.Failure!.Code);
        Assert.Equal(SpeechSynthesisOutcome.DeadlineExceeded, result.Result.Outcome);
        Assert.Equal(cutoff == "idle", result.Result.IsPartial);
        if (phase == "body") Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Consumer_pause_consumes_idle_budget_and_timer_aborts_without_read_ahead()
    {
        var clock = new FixtureClock();
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 960);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits, SpeechFixtures.Authorize(context, input, limits));
        await using var enumerator = stream.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(body.Disposed);
        Assert.Equal(960, body.BytesRead);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderFailureCode.IdleTimeout, stream.Result!.Failure!.Code);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("deadline")]
    [InlineData("consent")]
    public async Task Cancellation_or_synchronous_cutoff_precedes_known_status_even_if_error_body_throws(string cause)
    {
        using var stop = new CancellationTokenSource();
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new FragmentedTextBody([])
        {
            OnRead = () =>
            {
                if (cause == "cancel") stop.Cancel();
                else clock.Advance(TimeSpan.FromSeconds(5));
                throw new IOException(ProviderFixtures.Secret);
            }
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body, 401));
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits { MaxRequestTime = TimeSpan.FromSeconds(cause == "deadline" ? 5 : 90) };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cause == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null), stop.Token));
        Assert.Equal(401, result.Result.HttpStatusCode);
        if (cause == "cancel") Assert.Equal(SpeechSynthesisOutcome.Canceled, result.Result.Outcome);
        else Assert.Equal(cause == "deadline" ? ProviderFailureCode.DeadlineExceeded : ProviderFailureCode.ConsentExpired, result.Result.Failure!.Code);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("consent")]
    [InlineData("total")]
    public async Task Lazy_enumeration_cannot_start_a_new_window(string cause)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var source = new FixtureCredentials();
        var handler = SpeechFixtures.Handler();
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits
        {
            FirstAudioTimeout = TimeSpan.FromSeconds(cause == "first" ? 5 : 20),
            MaxRequestTime = TimeSpan.FromSeconds(cause == "total" ? 5 : 90)
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, clock);
        var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits, expiresAt: cause == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null));
        clock.Advance(TimeSpan.FromSeconds(5));
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        var result = await SpeechFixtures.Collect(stream);
        Assert.Equal(cause switch
        {
            "first" => ProviderFailureCode.FirstAudioTimeout, "consent" => ProviderFailureCode.ConsentExpired,
            _ => ProviderFailureCode.DeadlineExceeded
        }, result.Result.Failure!.Code);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task No_reserialization_retry_or_fallback_even_after_http_send()
    {
        var handler = SpeechFixtures.Handler();
        handler.Respond = async (request, token) =>
        {
            using var replay = new MemoryStream();
            await request.Content!.CopyToAsync(replay, token);
            return SpeechFixtures.Pcm(new FragmentedTextBody(SpeechFixtures.Audio()));
        };
        var result = await SpeechFixtures.Run(handler);
        Assert.Equal(ProviderFailureCode.Network, result.Result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public async Task Concurrent_independent_requests_share_client_but_not_correlation_or_cancellation()
    {
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var handler = SpeechFixtures.Handler();
        handler.Respond = async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstArrived.SetResult();
                await secondArrived.Task.WaitAsync(token);
            }
            else secondArrived.SetResult();
            return SpeechFixtures.Pcm(new FragmentedTextBody(SpeechFixtures.Audio()));
        };
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var other = context with { Ids = context.Ids with { RequestId = Guid.NewGuid() }, Epoch = context.Epoch + 1 };
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        using var stop = new CancellationTokenSource();
        var first = SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits), stop.Token));
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        var second = await SpeechFixtures.Collect(adapter.Stream(other, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(other, input, limits)));
        Assert.Equal(SpeechSynthesisOutcome.Canceled, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Result.Outcome);
        Assert.Equal(SpeechSynthesisOutcome.Completed, second.Result.Outcome);
        Assert.All(second.Frames, f => Assert.Equal(other.Ids, f.Ids));
        Assert.All(second.Frames, f => Assert.Equal(other.Epoch, f.Epoch));
        Assert.False(handler.Disposed);
        Assert.Equal(2, handler.Calls);
        adapter.Dispose();
        Assert.True(handler.Disposed);
        Assert.Throws<ObjectDisposedException>(() => adapter.Stream(context, SpeechFixtures.Selection, input, limits, null));
    }

    [Fact]
    public async Task Small_network_fragments_do_not_extend_first_audio_timeout()
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new FragmentedTextBody(SpeechFixtures.Audio(), 1)
        {
            OnRead = () => clock.Advance(TimeSpan.FromSeconds(1))
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits { FirstAudioTimeout = TimeSpan.FromSeconds(2) };
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits)));
        Assert.Equal(ProviderFailureCode.FirstAudioTimeout, result.Result.Failure!.Code);
        Assert.Empty(result.Frames);
        Assert.Equal(2, body.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_eof_or_final_short_frame_is_not_accepted_after_expiry(bool atEof)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new FragmentedTextBody(SpeechFixtures.Audio(atEof ? 480 : 490), 960);
        body.OnRead = () =>
        {
            if (body.BytesRead >= 960) clock.ShiftUtc(TimeSpan.FromHours(1));
        };
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits)));
        Assert.Equal(480, result.Result.DeliveredSampleCount);
        Assert.True(result.Result.IsPartial);
        Assert.Equal(ProviderFailureCode.ConsentExpired, result.Result.Failure!.Code);
        Assert.Null(result.Result.FinalSampleCount);
    }

    [Fact]
    public async Task Expired_request_deadline_does_not_consume_credentials_or_send()
    {
        var clock = new FixtureClock();
        var context = ProviderFixtures.Context() with { Deadline = ProviderFixtures.Now };
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var source = new FixtureCredentials();
        var handler = SpeechFixtures.Handler();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, clock);
        var result = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits,
            SpeechFixtures.Authorize(context, input, limits)));
        Assert.Equal(ProviderFailureCode.DeadlineExceeded, result.Result.Failure!.Code);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Failed_partial_attempt_consumes_authorization_and_new_call_does_not_repeat_it()
    {
        var body = new FragmentedTextBody(SpeechFixtures.Audio(481).Take(961).ToArray(), 960);
        var handler = SpeechFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var source = new FixtureCredentials();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, source, new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var limits = new SpeechSynthesisLimits();
        var authorization = SpeechFixtures.Authorize(context, input, limits);
        var first = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits, authorization));
        Assert.True(first.Result.IsPartial);
        var second = await SpeechFixtures.Collect(adapter.Stream(context, SpeechFixtures.Selection, input, limits, authorization));
        Assert.Equal(ProviderFailureCode.ConsentConsumed, second.Result.Failure!.Code);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Immutable_record_copies_cannot_change_the_authorized_request_after_stream_creation()
    {
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("Fixture.");
        var selection = SpeechFixtures.Selection;
        var limits = new SpeechSynthesisLimits();
        var handler = SpeechFixtures.Handler();
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var stream = adapter.Stream(context, selection, input, limits, SpeechFixtures.Authorize(context, input, limits));
        context = context with { Epoch = 99 };
        limits = limits with { MaxAudioBytes = 2 };
        selection = selection with { Voice = "coral" };
        var result = await SpeechFixtures.Collect(stream);
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Result.Outcome);
        Assert.Equal(ProviderFixtures.Context(), result.Result.Context);
        Assert.NotEqual(context, result.Result.Context);
        Assert.True(result.Result.DeliveredSampleCount * 2 > limits.MaxAudioBytes);
        using var request = System.Text.Json.JsonDocument.Parse(handler.Body);
        Assert.NotEqual(selection.Voice, request.RootElement.GetProperty("voice").GetString());
    }
}

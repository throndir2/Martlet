using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class TextLifetimeTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("credential")]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task Cancellation_at_each_boundary_suppresses_late_data_and_cleans_up(string phase)
    {
        using var stop = new CancellationTokenSource();
        var clock = new FixtureClock();
        var source = new FixtureCredentials
        {
            Resolve = async (binding, _) =>
            {
                await Task.Yield();
                if (phase == "credential") stop.Cancel();
                return new(binding, ProviderFixtures.Secret);
            }
        };
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(string.Concat(TextFixtures.Trace())), 4096)
        {
            IgnoreCancellation = true,
            OnRead = () => { if (phase == "body") stop.Cancel(); }
        };
        var handler = new TextRecordingHandler
        {
            Respond = async (_, _) =>
            {
                await Task.Yield();
                if (phase == "headers") stop.Cancel();
                return TextRecordingHandler.Sse(body);
            }
        };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, clock);
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        if (phase == "before") stop.Cancel();
        var stream = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits), stop.Token);
        var result = await TextFixtures.Collect(stream);
        Assert.Equal(TextGenerationOutcome.Canceled, result.Result.Outcome);
        Assert.DoesNotContain(result.Events, e => e.Kind == ProviderEventKind.TextDelta);
        Assert.Equal(phase == "before" ? 0 : 1, source.Calls);
        Assert.Equal(phase is "before" or "credential" ? 0 : 1, handler.Calls);
        if (phase is "headers" or "body") Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("enumerator")]
    [InlineData("adapter")]
    public async Task Stop_after_delta_drops_buffered_terminal_and_retains_original_correlation(string mode)
    {
        using var stop = new CancellationTokenSource();
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(string.Concat(TextFixtures.Trace())), 4096);
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var stream = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits),
            mode == "caller" ? stop.Token : default);
        await using var enumerator = stream.GetAsyncEnumerator(mode == "enumerator" ? stop.Token : default);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.Started, enumerator.Current.Kind);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.TextDelta, enumerator.Current.Kind);
        if (mode == "adapter") adapter.Dispose(); else stop.Cancel();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.Canceled, enumerator.Current.Kind);
        Assert.Equal(context.Ids, enumerator.Current.Ids);
        Assert.Equal(context.Epoch, enumerator.Current.Epoch);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Pull_backpressure_does_not_read_past_first_delta_and_abandonment_releases_body()
    {
        var trace = TextFixtures.Trace();
        var bytes = Encoding.UTF8.GetBytes(string.Concat(trace));
        var body = new FragmentedTextBody(bytes, 1);
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var stream = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits));
        var enumerator = stream.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(0, handler.Calls);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.TextDelta, enumerator.Current.Kind);
        int read = body.BytesRead;
        Assert.Equal(Encoding.UTF8.GetByteCount(string.Concat(trace.Take(5))), read);
        await Task.Yield();
        Assert.Equal(read, body.BytesRead);
        Assert.True(read < bytes.Length);
        await enumerator.DisposeAsync();
        Assert.True(body.Disposed);
        Assert.Equal(TextGenerationOutcome.Canceled, stream.Result!.Outcome);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("consent", ProviderFailureCode.ConsentExpired)]
    [InlineData("total", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("idle", ProviderFailureCode.IdleTimeout)]
    public async Task Deferred_timers_cannot_accept_buffered_data_after_a_cutoff(string phase, ProviderFailureCode expected)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var handler = new TextRecordingHandler();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits
        {
            IdleTimeout = TimeSpan.FromSeconds(phase == "idle" ? 5 : 10),
            MaxRequestTime = TimeSpan.FromSeconds(phase == "total" ? 5 : 60)
        };
        var stream = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits,
            TextFixtures.Authorize(context, limits, phase == "consent" ? ProviderFixtures.Now.AddSeconds(5) : null));
        await using var enumerator = stream.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.TextDelta, enumerator.Current.Kind);
        clock.Advance(TimeSpan.FromSeconds(5));
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.Failed, enumerator.Current.Kind);
        Assert.Equal(expected, stream.Result!.Failure!.Code);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Theory]
    [InlineData("first", ProviderFailureCode.FirstDeltaTimeout)]
    [InlineData("idle", ProviderFailureCode.IdleTimeout)]
    [InlineData("total", ProviderFailureCode.DeadlineExceeded)]
    public async Task Actual_timers_cancel_pending_reads(string timeout, ProviderFailureCode expected)
    {
        var clock = new FixtureClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new FragmentedTextBody([])
        {
            BeforeRead = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits
        {
            FirstDeltaTimeout = TimeSpan.FromSeconds(timeout == "first" ? 2 : 15),
            IdleTimeout = TimeSpan.FromSeconds(timeout == "idle" ? 2 : 10),
            MaxRequestTime = TimeSpan.FromSeconds(timeout == "total" ? 2 : 60)
        };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var collect = TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await collect.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(401, "io", ProviderFailureCode.Authentication)]
    [InlineData(403, "io", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "io", ProviderFailureCode.RateLimited)]
    [InlineData(401, "http", ProviderFailureCode.Authentication)]
    [InlineData(403, "http", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, "http", ProviderFailureCode.RateLimited)]
    [InlineData(200, "http", ProviderFailureCode.ResponseTruncated)]
    public async Task Optional_unreadable_error_body_cannot_replace_known_status(int status, string exception, ProviderFailureCode expected)
    {
        var body = new FragmentedTextBody([])
        {
            OnRead = () => throw (exception == "http"
                ? new HttpIOException(HttpRequestError.ResponseEnded, ProviderFixtures.Secret)
                : new IOException(ProviderFixtures.ContentCanary))
        };
        var handler = new TextRecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = TextRecordingHandler.Sse(body, status);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(400));
                return Task.FromResult(response);
            }
        };
        var result = await TextProtocolTests.Run(handler);
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Equal(status == 200 ? null : TimeSpan.FromSeconds(300), result.Result.Failure.RetryAfter);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(ProviderFixtures.Secret, JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData(401, "{}", ProviderFailureCode.Authentication)]
    [InlineData(403, "{}", ProviderFailureCode.PermissionDenied)]
    [InlineData(429, """{"error":{"code":"insufficient_quota","message":"private-echo"}}""", ProviderFailureCode.QuotaExceeded)]
    [InlineData(429, """{"error":{"code":"unknown"}}""", ProviderFailureCode.RateLimited)]
    [InlineData(404, """{"error":{"code":"model_not_found"}}""", ProviderFailureCode.ModelNotFound)]
    [InlineData(400, "{}", ProviderFailureCode.RequestRejected)]
    [InlineData(415, "{}", ProviderFailureCode.RequestRejected)]
    [InlineData(500, "{}", ProviderFailureCode.Server)]
    [InlineData(307, "{}", ProviderFailureCode.RedirectRejected)]
    public async Task Error_statuses_are_sanitized_without_retry_or_fallback(int status, string payload, ProviderFailureCode expected)
    {
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(ProviderFixtures.Json(payload, status)) };
        var result = await TextProtocolTests.Run(handler);
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.False(result.Result.Failure.Error.Retryable);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("private-echo", JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task Stop_precedes_known_error_even_when_body_throws(string cause)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        using var stop = new CancellationTokenSource();
        var body = new FragmentedTextBody([])
        {
            OnRead = () =>
            {
                if (cause == "cancel") stop.Cancel(); else clock.Advance(TimeSpan.FromSeconds(30));
                throw new IOException(ProviderFixtures.Secret);
            }
        };
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body, 401)) };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits), stop.Token));
        Assert.Equal(cause == "cancel" ? TextGenerationOutcome.Canceled : TextGenerationOutcome.DeadlineExceeded, result.Result.Outcome);
    }

    [Fact]
    public async Task Shared_client_keeps_concurrent_requests_independent_until_adapter_disposal()
    {
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrived = 0;
        var handler = new TextRecordingHandler
        {
            Respond = async (_, token) =>
            {
                if (Interlocked.Increment(ref arrived) == 1)
                {
                    firstArrived.SetResult();
                    await secondArrived.Task.WaitAsync(token);
                }
                else secondArrived.SetResult();
                return TextRecordingHandler.Sse(string.Concat(TextFixtures.Trace()));
            }
        };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var firstStream = adapter.Stream(context, TextFixtures.Selection, new("First"), limits, TextFixtures.Authorize(context, limits));
        var first = TextFixtures.Collect(firstStream);
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var otherContext = context with { Ids = context.Ids with { RequestId = Guid.NewGuid() }, Epoch = context.Epoch + 1 };
        var second = adapter.Stream(otherContext, TextFixtures.Selection, new("Second"), limits, TextFixtures.Authorize(otherContext, limits));
        var secondEvents = new List<ProviderEvent>();
        await foreach (var e in second) secondEvents.Add(e);
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(firstResult.Events, e => Assert.Equal(context.Ids, e.Ids));
        Assert.All(secondEvents, e => Assert.Equal(otherContext.Ids, e.Ids));
        Assert.All(secondEvents, e => Assert.Equal(otherContext.Epoch, e.Epoch));
        Assert.Equal(TextGenerationOutcome.Completed, second.Result!.Outcome);
        Assert.False(handler.Disposed);
        Assert.Equal(2, handler.Calls);
        adapter.Dispose();
        Assert.True(handler.Disposed);
        Assert.Throws<ObjectDisposedException>(() => adapter.Stream(context, TextFixtures.Selection, new("Third"), limits, null));
    }

    [Fact]
    public async Task Handler_reserialization_is_rejected_not_retried()
    {
        var handler = new TextRecordingHandler
        {
            Respond = async (request, token) =>
            {
                using var second = new MemoryStream();
                await request.Content!.CopyToAsync(second, token);
                return TextRecordingHandler.Sse("");
            }
        };
        var result = await TextProtocolTests.Run(handler);
        Assert.Equal(TextGenerationOutcome.Failed, result.Result.Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("consent")]
    [InlineData("forward")]
    public async Task Noncooperative_late_body_is_rechecked_synchronously(string cause)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(string.Concat(TextFixtures.Trace())), 4096)
        {
            IgnoreCancellation = true,
            BeforeRead = async _ =>
            {
                await Task.Yield();
                if (cause == "forward") clock.ShiftUtc(TimeSpan.FromHours(1));
                else clock.Advance(TimeSpan.FromSeconds(2));
            }
        };
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits { FirstDeltaTimeout = TimeSpan.FromSeconds(cause == "first" ? 2 : 15) };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), clock);
        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits,
            TextFixtures.Authorize(context, limits, cause == "consent" ? ProviderFixtures.Now.AddSeconds(2) : null)));
        Assert.Equal(cause == "first" ? ProviderFailureCode.FirstDeltaTimeout : ProviderFailureCode.ConsentExpired, result.Result.Failure!.Code);
        Assert.DoesNotContain(result.Events, e => e.Kind == ProviderEventKind.TextDelta);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Request_window_starts_at_creation_not_at_delayed_enumeration()
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits { MaxRequestTime = TimeSpan.FromSeconds(5) };
        var source = new FixtureCredentials();
        var handler = new TextRecordingHandler();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, clock);
        var stream = adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits, TextFixtures.Authorize(context, limits));
        clock.Advance(TimeSpan.FromSeconds(5));
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        var result = await TextFixtures.Collect(stream);
        Assert.Equal(ProviderFailureCode.DeadlineExceeded, result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("credentials", false)]
    [InlineData("credentials", true)]
    [InlineData("headers", false)]
    [InlineData("headers", true)]
    public async Task Pending_credentials_and_header_waits_receive_stop_or_deadline(string phase, bool timer)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var clock = new FixtureClock();
        async Task Wait(CancellationToken token)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        var source = new FixtureCredentials
        {
            Resolve = async (binding, token) =>
            {
                if (phase == "credentials") await Wait(token);
                return new(binding, ProviderFixtures.Secret);
            }
        };
        var handler = new TextRecordingHandler
        {
            Respond = async (_, token) =>
            {
                await Wait(token);
                return TextRecordingHandler.Sse("");
            }
        };
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits { MaxRequestTime = TimeSpan.FromSeconds(2) };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, source, clock);
        var pending = TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture"), limits,
            TextFixtures.Authorize(context, limits), stop.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (timer) clock.Advance(TimeSpan.FromSeconds(2)); else stop.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(timer ? TextGenerationOutcome.DeadlineExceeded : TextGenerationOutcome.Canceled, result.Result.Outcome);
        Assert.Equal(phase == "credentials" ? 0 : 1, handler.Calls);
    }
}

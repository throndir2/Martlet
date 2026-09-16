using System.Text;
using Martlet.Core.Contracts;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests.Ollama;

public sealed class OllamaChatLifetimeTests
{
    public static IEnumerable<object[]> BlockedCases()
    {
        foreach (var source in new[] { "caller", "operation", "enumerator" })
            foreach (var boundary in new[] { "authority", "serialization", "headers", "open", "read" })
                yield return [source, boundary];
    }

    [Theory]
    [MemberData(nameof(BlockedCases))]
    public async Task Original_cancellation_flags_win_while_newer_LIFO_callback_blocks(string tokenSource, string boundary)
    {
        using var original = new CancellationTokenSource();
        using var callbackRelease = new ManualResetEventSlim();
        var entered = Signal();
        var resume = Signal();
        var callbackEntered = Signal();
        var authority = new OllamaAuthority();
        var handler = OllamaFixtures.Handler();
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace), 1) { IgnoreCancellation = true };
        if (boundary == "authority")
            authority.Authorize = async (a, _) =>
            {
                entered.TrySetResult();
                await resume.Task;
                return new(a, a.EffectiveDeadline, authority.Lease);
            };
        if (boundary == "serialization")
            handler.BeforeSerialization = () => { entered.TrySetResult(); resume.Task.GetAwaiter().GetResult(); };
        if (boundary == "read")
            body.BeforeRead = async _ => { entered.TrySetResult(); await resume.Task; };
        handler.Respond = async (_, _) =>
        {
            if (boundary == "headers") { entered.TrySetResult(); await resume.Task; }
            var response = OllamaFixtures.Response(body);
            if (boundary == "open")
            {
                response.Content = new DeferredContent(async _ => { entered.TrySetResult(); await resume.Task; return body; });
                response.Content.Headers.ContentType = new("application/x-ndjson");
            }
            return response;
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, authority, new FixtureClock());
        var stream = OllamaFixtures.Stream(adapter,
            caller: tokenSource == "caller" ? original.Token : default,
            operation: tokenSource == "operation" ? original.Token : default);
        var work = Task.Run(() => OllamaFixtures.Collect(stream, tokenSource == "enumerator" ? original.Token : default));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var registration = original.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
        var cancellation = original.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            resume.TrySetResult();
            var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TextGenerationOutcome.Canceled, result.Result.Outcome);
            Assert.Equal(0, result.Result.EmittedTextCharacters);
            Assert.Equal(boundary == "authority" ? 0 : 1, handler.Calls);
            if (boundary is "authority" or "serialization") Assert.Empty(handler.Body);
            Assert.Equal(1, authority.Lease.Calls);
            Assert.False(cancellation.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            resume.TrySetResult();
            await cancellation;
            await work.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("operation")]
    [InlineData("enumerator")]
    [InlineData("stream")]
    public async Task Cancel_after_delta_discards_buffered_terminal_with_original_context(string source)
    {
        using var cancel = new CancellationTokenSource();
        await using var adapter = OllamaChatAdapter.CreateForFixture(OllamaFixtures.Handler(), new OllamaAuthority(), new FixtureClock());
        var stream = OllamaFixtures.Stream(adapter, caller: source == "caller" ? cancel.Token : default,
            operation: source == "operation" ? cancel.Token : default);
        await using var iterator = stream.GetAsyncEnumerator(source == "enumerator" ? cancel.Token : default);
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(await iterator.MoveNextAsync());
        if (source == "stream") stream.RequestCancellation(); else await cancel.CancelAsync();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(ProviderEventKind.Canceled, iterator.Current.Kind);
        Assert.Equal(ProviderFixtures.Context().Ids, iterator.Current.Ids);
        Assert.Equal(7, iterator.Current.Epoch);
        Assert.False(await iterator.MoveNextAsync());
        Assert.True(stream.Result!.HasPartialOutput);
    }

    [Theory]
    [InlineData("first", ProviderFailureCode.FirstDeltaTimeout)]
    [InlineData("idle", ProviderFailureCode.IdleTimeout)]
    [InlineData("total", ProviderFailureCode.DeadlineExceeded)]
    [InlineData("permit", ProviderFailureCode.ConsentExpired)]
    public async Task Actual_deadline_timers_cancel_cooperative_pending_reads(string kind, ProviderFailureCode expected)
    {
        var clock = new FixtureClock();
        var entered = Signal();
        var body = new FragmentedTextBody([])
        {
            BeforeRead = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        var source = new OllamaAuthority();
        if (kind == "permit")
            source.Authorize = (a, _) => ValueTask.FromResult<OllamaChatAuthorization?>(new(a, ProviderFixtures.Now.AddSeconds(2), source.Lease));
        var limits = new TextGenerationLimits
        {
            FirstDeltaTimeout = TimeSpan.FromSeconds(kind == "first" ? 2 : 15),
            IdleTimeout = TimeSpan.FromSeconds(kind == "idle" ? 2 : 10),
            MaxRequestTime = TimeSpan.FromSeconds(kind == "total" ? 2 : 60)
        };
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(body));
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, clock);
        var work = OllamaFixtures.Collect(OllamaFixtures.Stream(adapter, limits));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, result.Result.FailureCode);
        Assert.True(body.Disposed);
        Assert.True(source.Lease.Released);
    }

    [Theory]
    [InlineData("enumeration", false)]
    [InlineData("authority", false)]
    [InlineData("serialization", false)]
    [InlineData("headers", false)]
    [InlineData("read", false)]
    [InlineData("authority", true)]
    [InlineData("read", true)]
    public async Task Deferred_timers_and_UTC_changes_cannot_extend_original_window(string boundary, bool forward)
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        void Expire()
        {
            if (forward) clock.ShiftUtc(TimeSpan.FromMinutes(5));
            else { clock.Advance(TimeSpan.FromSeconds(30)); clock.ShiftUtc(TimeSpan.FromHours(-1)); }
        }
        var source = new OllamaAuthority();
        if (boundary == "authority")
            source.Authorize = (a, _) =>
            {
                Expire();
                return ValueTask.FromResult<OllamaChatAuthorization?>(new(a, a.EffectiveDeadline, source.Lease));
            };
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace)) { IgnoreCancellation = true };
        if (boundary == "read") body.OnRead = Expire;
        var handler = OllamaFixtures.Handler();
        if (boundary == "serialization") handler.BeforeSerialization = Expire;
        handler.Respond = (_, _) =>
        {
            if (boundary == "headers") Expire();
            return Task.FromResult(OllamaFixtures.Response(body));
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, clock);
        var stream = OllamaFixtures.Stream(adapter);
        if (boundary == "enumeration") Expire();
        var result = await OllamaFixtures.Collect(stream);
        Assert.Equal(ProviderFailureCode.DeadlineExceeded, result.Result.FailureCode);
        Assert.Equal(0, result.Result.EmittedTextCharacters);
        Assert.Equal(boundary is "enumeration" or "authority" ? 0 : 1, handler.Calls);
        if (boundary == "serialization") Assert.Empty(handler.Body);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("read")]
    [InlineData("body disposal")]
    [InlineData("lease disposal")]
    public async Task Noncooperative_work_keeps_response_lease_and_handler_owned_until_actual_exit(string phase)
    {
        var entered = Signal();
        var resume = Signal();
        var source = new OllamaAuthority();
        var handler = OllamaFixtures.Handler();
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var body = new CleanupBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace));
        if (phase == "authority")
            source.Authorize = async (a, _) => { entered.TrySetResult(); await resume.Task; return new(a, a.EffectiveDeadline, source.Lease); };
        if (phase == "body disposal") body.BeforeDispose = async () => { entered.TrySetResult(); await resume.Task; };
        if (phase == "lease disposal") source.Lease.OnDispose = async () => { entered.TrySetResult(); await resume.Task; };
        var readBody = new FragmentedTextBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace)) { IgnoreCancellation = true };
        if (phase == "read") readBody.BeforeRead = async _ => { entered.TrySetResult(); await resume.Task; };
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(phase == "read" ? readBody : body));
        var adapter = OllamaChatAdapter.CreateForFixture(handler, source, clock);
        var stream = OllamaFixtures.Stream(adapter);
        await using var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var move = iterator.MoveNextAsync().AsTask();
        if (phase is "body disposal" or "lease disposal")
        {
            Assert.True(await move);
            move = iterator.MoveNextAsync().AsTask();
        }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(40));
        var disposing = adapter.DisposeAsync().AsTask();
        try
        {
            Assert.False(stream.OwnershipRelease.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.False(handler.Disposed);
            Assert.False(source.Lease.Released);
            if (phase == "read") Assert.False(readBody.Disposed);
            Assert.Throws<ObjectDisposedException>(() => OllamaFixtures.Stream(adapter));
        }
        finally { resume.TrySetResult(); }
        Assert.False(await move.WaitAsync(TimeSpan.FromSeconds(10)));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stream.OwnershipRelease.IsCompletedSuccessfully);
        Assert.Equal(1, source.Lease.Calls);
        Assert.True(handler.Disposed);
        Assert.Equal(TextGenerationOutcome.Canceled, stream.Result!.Outcome);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("lease")]
    [InlineData("callback")]
    public async Task Cleanup_failure_quarantines_owner_without_successful_release_or_retry(string phase)
    {
        var source = new OllamaAuthority();
        if (phase == "lease") source.Lease.OnDispose = () => throw new IOException(ProviderFixtures.ContentCanary);
        CancellationTokenRegistration callback = default;
        if (phase == "callback")
            source.Authorize = (a, token) =>
            {
                callback = token.Register(() => throw new IOException(ProviderFixtures.ContentCanary));
                return ValueTask.FromResult<OllamaChatAuthorization?>(new(a, a.EffectiveDeadline, source.Lease));
            };
        var body = new CleanupBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace)) { Fail = phase == "body" };
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(body));
        var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        var stream = OllamaFixtures.Stream(adapter);
        var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(await iterator.MoveNextAsync());
        var exception = await Assert.ThrowsAsync<OllamaChatCleanupException>(() => iterator.MoveNextAsync().AsTask());
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, exception.ToString());
        Assert.True(adapter.IsQuarantined);
        Assert.True(stream.OwnershipRelease.IsFaulted);
        Assert.Equal(phase == "lease" ? 1 : 0, source.Lease.Calls);
        Assert.Throws<OllamaChatCleanupException>(() => OllamaFixtures.Stream(adapter));
        await Assert.ThrowsAsync<OllamaChatCleanupException>(() => iterator.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<OllamaChatCleanupException>(() => adapter.DisposeAsync().AsTask());
        Assert.False(handler.Disposed);
        callback.Dispose();
    }

    [Fact]
    public async Task Owned_blocked_cancellation_callback_retains_lease_and_does_not_publish_success()
    {
        var callbackEntered = Signal();
        using var callbackRelease = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        var source = new OllamaAuthority();
        source.Authorize = (a, token) =>
        {
            registration = token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
            return ValueTask.FromResult<OllamaChatAuthorization?>(new(a, a.EffectiveDeadline, source.Lease));
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(OllamaFixtures.Handler(), source, new FixtureClock());
        var stream = OllamaFixtures.Stream(adapter);
        await using var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(await iterator.MoveNextAsync());
        stream.RequestCancellation();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var terminal = iterator.MoveNextAsync().AsTask();
        try
        {
            Assert.False(terminal.IsCompleted);
            Assert.False(stream.OwnershipRelease.IsCompleted);
            Assert.False(source.Lease.Released);
        }
        finally { callbackRelease.Set(); }
        Assert.True(await terminal.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ProviderEventKind.Canceled, iterator.Current.Kind);
        registration.Dispose();
    }

    [Fact]
    public async Task A_semantic_done_still_waits_for_actual_EOF_with_original_deadline()
    {
        var clock = new FixtureClock();
        var eof = Signal();
        var bytes = Encoding.UTF8.GetBytes(OllamaFixtures.Trace);
        var body = new FragmentedTextBody(bytes, bytes.Length);
        body.BeforeRead = async token =>
        {
            if (body.BytesRead == bytes.Length) { eof.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(body));
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), clock);
        var work = OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        await eof.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(work.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ProviderFailureCode.IdleTimeout, (await work.WaitAsync(TimeSpan.FromSeconds(10))).Result.FailureCode);
    }

    [Fact]
    public async Task A_precreated_stream_cannot_dispatch_after_another_stream_quarantines_the_owner()
    {
        var source = new OllamaAuthority();
        source.Lease.OnDispose = () => throw new IOException("fixture cleanup failure");
        var handler = OllamaFixtures.Handler();
        var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        var failed = OllamaFixtures.Stream(adapter);
        var waiting = OllamaFixtures.Stream(adapter);
        var first = failed.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.True(await first.MoveNextAsync());
        await Assert.ThrowsAsync<OllamaChatCleanupException>(() => first.MoveNextAsync().AsTask());
        var second = waiting.GetAsyncEnumerator();
        Assert.True(await second.MoveNextAsync());
        await Assert.ThrowsAsync<OllamaChatCleanupException>(() => second.MoveNextAsync().AsTask());
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, handler.Calls);
        await second.DisposeAsync();
        await Assert.ThrowsAsync<OllamaChatCleanupException>(() => first.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<OllamaChatCleanupException>(() => adapter.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Concurrent_pulls_are_rejected_and_disposal_waits_for_the_actual_pending_pull()
    {
        var entered = Signal();
        var resume = Signal();
        var source = new OllamaAuthority
        {
            Authorize = async (a, _) =>
            {
                entered.TrySetResult();
                await resume.Task;
                return new(a, a.EffectiveDeadline, new CountingLease());
            }
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(OllamaFixtures.Handler(), source, new FixtureClock());
        var stream = OllamaFixtures.Stream(adapter);
        var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var pending = iterator.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => iterator.MoveNextAsync().AsTask());
        var disposing = iterator.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.False(stream.OwnershipRelease.IsCompleted);
        resume.TrySetResult();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TextGenerationOutcome.Canceled, stream.Result!.Outcome);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("headers")]
    [InlineData("read")]
    public async Task Original_cancellation_precedes_late_expected_dependency_errors(string phase)
    {
        using var stop = new CancellationTokenSource();
        var source = new OllamaAuthority();
        if (phase == "authority")
            source.Authorize = (_, _) => { stop.Cancel(); throw new OllamaChatAuthorizationUnavailableException(); };
        var body = new FragmentedTextBody([])
        {
            OnRead = () => { stop.Cancel(); throw new HttpIOException(System.Net.Http.HttpRequestError.ResponseEnded, ProviderFixtures.ContentCanary); }
        };
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            if (phase == "headers") { stop.Cancel(); throw new HttpRequestException(ProviderFixtures.ContentCanary); }
            return Task.FromResult(OllamaFixtures.Response(body));
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter, operation: stop.Token));
        Assert.Equal(TextGenerationOutcome.Canceled, result.Result.Outcome);
        Assert.Null(result.Result.Error);
    }

    [Fact]
    public async Task Empty_records_and_lines_cannot_renew_the_first_text_or_idle_deadline()
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        string frame = OllamaFixtures.Frame("");
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(frame, 20))), Encoding.UTF8.GetByteCount(frame));
        body.OnRead = () => clock.Advance(TimeSpan.FromSeconds(1));
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(body));
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), clock);
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter,
            new() { FirstDeltaTimeout = TimeSpan.FromSeconds(3) }));
        Assert.Equal(ProviderFailureCode.FirstDeltaTimeout, result.Result.FailureCode);
        Assert.Equal(0, result.Result.EmittedTextCharacters);
        Assert.Equal(3 * Encoding.UTF8.GetByteCount(frame), body.BytesRead);
    }

    [Theory]
    [InlineData("caller", false)]
    [InlineData("operation", false)]
    [InlineData("enumerator", false)]
    [InlineData("deadline", false)]
    [InlineData("progress", false)]
    [InlineData("permit", false)]
    [InlineData("caller", true)]
    [InlineData("operation", true)]
    [InlineData("enumerator", true)]
    [InlineData("deadline", true)]
    [InlineData("progress", true)]
    [InlineData("permit", true)]
    public async Task Every_cancellation_origin_joins_owned_callbacks_before_lease_and_handler_retirement(string origin, bool throws)
    {
        using var original = new CancellationTokenSource();
        using var callbackRelease = new ManualResetEventSlim();
        var entered = Signal();
        CancellationTokenRegistration registration = default;
        var source = new OllamaAuthority();
        source.Authorize = (a, token) =>
        {
            registration = token.Register(() =>
            {
                entered.TrySetResult();
                callbackRelease.Wait();
                if (throws) throw new IOException(ProviderFixtures.ContentCanary);
            });
            return ValueTask.FromResult<OllamaChatAuthorization?>(new(a,
                origin == "permit" ? ProviderFixtures.Now.AddSeconds(2) : a.EffectiveDeadline, source.Lease));
        };
        var clock = new FixtureClock();
        var handler = OllamaFixtures.Handler();
        var adapter = OllamaChatAdapter.CreateForFixture(handler, source, clock);
        var stream = OllamaFixtures.Stream(adapter, new()
        {
            MaxRequestTime = TimeSpan.FromSeconds(origin == "deadline" ? 2 : 60),
            IdleTimeout = TimeSpan.FromSeconds(origin == "progress" ? 2 : 10)
        },
            caller: origin == "caller" ? original.Token : default,
            operation: origin == "operation" ? original.Token : default);
        var iterator = stream.GetAsyncEnumerator(origin == "enumerator" ? original.Token : default);
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(await iterator.MoveNextAsync());
        var trigger = origin is "deadline" or "progress" or "permit"
            ? Task.Run(() => clock.Advance(TimeSpan.FromSeconds(2))) : original.CancelAsync();
        Task<bool>? terminal = null;
        Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            terminal = iterator.MoveNextAsync().AsTask();
            await Task.WhenAny(terminal, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.False(terminal.IsCompleted);
            Assert.False(stream.OwnershipRelease.IsCompleted);
            Assert.Equal(0, source.Lease.Calls);
            disposing = adapter.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.False(handler.Disposed);
            callbackRelease.Set();
            if (throws)
            {
                await Assert.ThrowsAsync<OllamaChatCleanupException>(() => terminal.WaitAsync(TimeSpan.FromSeconds(10)));
                await Assert.ThrowsAsync<OllamaChatCleanupException>(() => disposing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(stream.OwnershipRelease.IsFaulted);
                Assert.Equal(0, source.Lease.Calls);
                Assert.True(adapter.IsQuarantined);
                Assert.False(handler.Disposed);
            }
            else
            {
                Assert.False(await terminal.WaitAsync(TimeSpan.FromSeconds(10)));
                await disposing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(stream.OwnershipRelease.IsCompletedSuccessfully);
                Assert.Equal(1, source.Lease.Calls);
                Assert.True(handler.Disposed);
            }
        }
        finally
        {
            callbackRelease.Set();
            _ = await Record.ExceptionAsync(() => trigger.WaitAsync(TimeSpan.FromSeconds(10)));
            if (terminal is not null) _ = await Record.ExceptionAsync(() => terminal.WaitAsync(TimeSpan.FromSeconds(10)));
            if (disposing is not null) _ = await Record.ExceptionAsync(() => disposing.WaitAsync(TimeSpan.FromSeconds(10)));
            _ = await Record.ExceptionAsync(() => iterator.DisposeAsync().AsTask());
            _ = await Record.ExceptionAsync(() => adapter.DisposeAsync().AsTask());
            registration.Dispose();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests.Ollama;

public sealed class OllamaChatCoreIntegrationTests
{
    [Fact]
    public async Task Pull_backpressure_and_real_Core_queue_preserve_contiguous_text_without_replay()
    {
        var trace = OllamaFixtures.Frame("same") + OllamaFixtures.Frame("same") + OllamaFixtures.Frame("", true, "stop");
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(trace), 1);
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(body));
        var clock = new FixtureClock();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), clock);
        var stream = OllamaFixtures.Stream(adapter);
        await using var iterator = stream.GetAsyncEnumerator();
        using var validator = new ProviderSequenceValidator(new()
        {
            Ids = ProviderFixtures.Context().Ids, Epoch = 7, Capabilities = stream.Capabilities
        }, new() { MaxQueuedChunks = 1 }, clock);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(SequenceDecision.Accepted, validator.Accept(iterator.Current).Decision);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(SequenceDecision.Accepted, validator.Accept(iterator.Current).Decision);
        int read = body.BytesRead;
        await Task.Yield();
        Assert.Equal(read, body.BytesRead);
        Assert.Equal(Encoding.UTF8.GetByteCount(OllamaFixtures.Frame("same")), read);
        Assert.True(await iterator.MoveNextAsync());
        var next = iterator.Current;
        Assert.Equal(SequenceDecision.Backpressured, validator.Accept(next).Decision);
        Assert.True(validator.TryReadText(out var first));
        Assert.Equal("same", first!.Text);
        Assert.Equal(SequenceDecision.Accepted, validator.Accept(next).Decision);
        Assert.True(validator.TryReadText(out var second));
        Assert.Equal("same", second!.Text);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(SequenceDecision.Terminal, validator.Accept(iterator.Current).Decision);
        Assert.False(await iterator.MoveNextAsync());
        Assert.Equal(TurnOutcome.Completed, validator.EndOfInput().Snapshot.Result!.Outcome);
        Assert.Equal(8, stream.Result!.EmittedTextCharacters);
        Assert.Equal(1, validator.Snapshot.PeakQueuedChunks);
    }

    [Fact]
    public async Task Core_Stop_and_transition_discard_original_epoch_events()
    {
        var context = ProviderFixtures.Context();
        await using var adapter = OllamaChatAdapter.CreateForFixture(OllamaFixtures.Handler(), new OllamaAuthority(), new FixtureClock());
        var old = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        using var validator = new ProviderSequenceValidator(new() { Ids = context.Ids, Epoch = context.Epoch,
            Capabilities = OllamaChatAdapter.Capabilities(OllamaFixtures.Model, EvidenceProvenance.Fixture) });
        validator.Accept(old.Events[0]);
        validator.Accept(old.Events[1]);
        validator.Stop();
        Assert.False(validator.TryReadText(out _));
        Assert.Equal(SequenceDecision.ClosedDiscarded, validator.Accept(old.Events[^1]).Decision);
        validator.Transition(new()
        {
            Ids = context.Ids with { TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
            Epoch = validator.Snapshot.CurrentEpoch + 1,
            Capabilities = OllamaChatAdapter.Capabilities(OllamaFixtures.Model, EvidenceProvenance.Fixture)
        }, AttemptTransition.Replace);
        Assert.Equal(SequenceDecision.StaleDiscarded, validator.Accept(old.Events[^1]).Decision);
    }

    [Fact]
    public async Task Independent_streams_and_old_stop_handles_do_not_cancel_new_work()
    {
        var source = new OllamaAuthority
        {
            Authorize = (a, _) => ValueTask.FromResult<OllamaChatAuthorization?>(new(a, a.EffectiveDeadline, new CountingLease()))
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(OllamaFixtures.Handler(), source, new FixtureClock());
        var old = OllamaFixtures.Stream(adapter);
        await old.DisposeAsync();
        var context = ProviderFixtures.Context();
        var first = OllamaFixtures.Stream(adapter);
        var second = OllamaFixtures.Stream(adapter, context: context with
        { Ids = context.Ids with { RequestId = Guid.NewGuid() }, Epoch = 8 });
        old.RequestCancellation();
        var results = await Task.WhenAll(OllamaFixtures.Collect(first), OllamaFixtures.Collect(second));
        Assert.All(results, result => Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome));
        Assert.All(results[0].Events, e => Assert.Equal(7, e.Epoch));
        Assert.All(results[1].Events, e => Assert.Equal(8, e.Epoch));
        Assert.NotEqual(results[0].Result.Context.Ids, results[1].Result.Context.Ids);
    }

    [Fact]
    public async Task Consumer_pause_consumes_idle_budget_and_early_dispose_owns_no_fake_terminal()
    {
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var handler = OllamaFixtures.Handler();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), clock);
        var stream = OllamaFixtures.Stream(adapter);
        await using var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(await iterator.MoveNextAsync());
        clock.Advance(TimeSpan.FromSeconds(10));
        clock.ShiftUtc(TimeSpan.FromHours(-1));
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(ProviderFailureCode.IdleTimeout, stream.Result!.FailureCode);
        Assert.Equal(ProviderEventKind.Failed, iterator.Current.Kind);
        var unstarted = OllamaFixtures.Stream(adapter);
        await unstarted.DisposeAsync();
        Assert.True(unstarted.OwnershipRelease.IsCompletedSuccessfully);
        Assert.Equal(TextGenerationOutcome.Canceled, unstarted.Result!.Outcome);
        Assert.Throws<ObjectDisposedException>(() => unstarted.GetAsyncEnumerator());
    }

    [Fact]
    public async Task Concurrent_requests_keep_their_tokens_and_owned_leases_independent()
    {
        using var firstStop = new CancellationTokenSource();
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = new List<CountingLease>();
        var source = new OllamaAuthority
        {
            Authorize = (a, _) =>
            {
                var lease = new CountingLease();
                leases.Add(lease);
                return ValueTask.FromResult<OllamaChatAuthorization?>(new(a, a.EffectiveDeadline, lease));
            }
        };
        int arrived = 0;
        var handler = OllamaFixtures.Handler();
        handler.Respond = async (_, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 1)
            {
                firstArrived.TrySetResult();
                await releaseFirst.Task;
            }
            else secondArrived.TrySetResult();
            return OllamaFixtures.Response(new FragmentedTextBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace)));
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        var first = OllamaFixtures.Collect(OllamaFixtures.Stream(adapter, caller: firstStop.Token));
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var context = ProviderFixtures.Context();
        var second = OllamaFixtures.Collect(OllamaFixtures.Stream(adapter,
            context: context with { Ids = context.Ids with { RequestId = Guid.NewGuid() }, Epoch = 9 }));
        await secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await firstStop.CancelAsync();
        Assert.False(leases[0].Released);
        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TextGenerationOutcome.Canceled, results[0].Result.Outcome);
        Assert.Equal(TextGenerationOutcome.Completed, results[1].Result.Outcome);
        Assert.All(leases, lease => Assert.Equal(1, lease.Calls));
        Assert.All(results[1].Events, e => Assert.Equal(9, e.Epoch));
    }
}

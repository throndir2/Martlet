using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Fixtures;

namespace Martlet.Sessions;

// One active run, one serialized cursor owner, and one optional real sink. No settings or provider access.
public sealed class FixtureSession : IAsyncDisposable
{
    public static IReadOnlyList<string> Scenarios { get; } = Array.AsReadOnly(new[]
    {
        "complete", "streaming", "refused", "refused-after-partial", "no-speech",
        "not-addressed", "canceled", "truncated", "slow", "failed"
    });
    private readonly object gate = new();
    private readonly PcmPlaybackSink? sink;
    private readonly TimeProvider time;
    private readonly TimeSpan pacing;
    private readonly Func<Task>? beforePlaybackStop;
    private readonly Guid sessionId = Guid.NewGuid();
    private long epoch = -2;
    private CancellationTokenSource? cancellation;
    private FixtureCursor? cursor;
    private PlaybackRun? playback;
    private Task<FixtureSessionSnapshot>? task;
    private FixtureSessionSnapshot? snapshot;
    private bool disposed;

    public FixtureSession(PcmPlaybackSink? sink = null, TimeProvider? timeProvider = null, TimeSpan pacing = default)
    {
        ContractRules.Require(pacing >= TimeSpan.Zero && pacing <= TimeSpan.FromMilliseconds(250),
            "Fixture presentation pacing must be between zero and 250 ms.");
        this.sink = sink;
        time = timeProvider ?? TimeProvider.System;
        this.pacing = pacing;
    }

    // Deterministic scheduling seam for lifetime races; no callback runs under the session monitor.
    internal FixtureSession(PcmPlaybackSink sink, Func<Task> beforePlaybackStop) : this(sink) =>
        this.beforePlaybackStop = beforePlaybackStop;

    public bool IsRunning { get { lock (gate) return task is { IsCompleted: false }; } }
    public FixtureSessionSnapshot? Snapshot
    {
        get
        {
            lock (gate)
                return snapshot is not null && snapshot.Stage == FixtureSessionStage.Playback && playback is not null
                    ? snapshot with { Playback = playback.Snapshot } : snapshot;
        }
    }

    public Task<FixtureSessionSnapshot> RunAsync(string scenario, OutputSelection? approvedToneOutput = null,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (task is { IsCompleted: false })
                throw new InvalidOperationException("Stop and await the active fixture before starting another.");
            ContractRules.Require(Scenarios.Contains(scenario, StringComparer.Ordinal), "Unknown fixture session scenario.");
            ContractRules.Require(approvedToneOutput is null || sink is not null,
                "Tone playback is unavailable in this host.", ErrorCode.NotImplemented);
            approvedToneOutput?.Validate();
            ContractRules.Require(epoch < int.MaxValue - 3, "End this fixture session before its epoch budget is exhausted.");
            epoch += 2; // Stop reserves the intervening epoch in the temporal validator.
            var script = FixtureCatalog.Create(scenario switch
            {
                "slow" => "first-deadline", "not-addressed" => "suppressed", _ => scenario
            });
            var ids = new CorrelationIds { SessionId = sessionId, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
            script = script with
            {
                Request = script.Request with { Ids = ids, Epoch = epoch },
                Steps = script.Steps.Select(step => step.Event is null ? step :
                    step with { Event = step.Event with { Ids = ids, Epoch = epoch } }).ToArray()
            };
            cancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cursor = new(script, cancellation.Token);
            playback = null;
            snapshot = new()
            {
                Scenario = scenario, Sequence = cursor.Snapshot, Stage = FixtureSessionStage.Script,
                ToneRequested = approvedToneOutput is not null
            };
            // Even zero-pacing self-tests never execute a synchronous script on the WPF thread.
            task = Task.Run(() => ExecuteAsync(approvedToneOutput, cancellation.Token), CancellationToken.None);
            return task;
        }
    }

    private async Task<FixtureSessionSnapshot> ExecuteAsync(OutputSelection? output, CancellationToken token)
    {
        try
        {
            while (true)
            {
                int? next;
                lock (gate) next = cursor!.NextMilliseconds;
                if (next is null)
                    break;
                var delay = next > 0 && pacing > TimeSpan.Zero ? TimeSpan.FromSeconds(2) : pacing;
                await Task.Delay(delay, time, token).ConfigureAwait(false);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    var step = cursor!.Advance();
                    var text = snapshot!.Text;
                    foreach (var chunk in step.Text)
                    {
                        ContractRules.Require(chunk.Ids == snapshot.Sequence.Ids && chunk.Epoch == epoch,
                            "A retired fixture cannot publish text.");
                        text += chunk.Text;
                    }
                    ContractRules.Require(text.Length <= 4096, "Fixture presentation text exceeded its bound.", ErrorCode.PayloadTooLarge);
                    snapshot = snapshot with { Sequence = cursor.Snapshot, Text = text, RefusalText = cursor.RefusalText };
                }
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                snapshot = snapshot! with { Trace = cursor!.Finish(), Sequence = cursor.Snapshot };
                // A tone is never generated from text or refusal, and only follows an ordinary completed fixture.
                if (output is not null && snapshot.Sequence.Result?.Outcome == TurnOutcome.Completed)
                {
                    snapshot = snapshot with { Stage = FixtureSessionStage.Playback };
                    try
                    {
                        playback = sink!.Start(new(snapshot.Sequence.Ids, epoch, SyntheticTone.Format,
                            output, time.GetUtcNow().AddSeconds(5)), token);
                    }
                    catch (InvalidOperationException)
                    {
                        snapshot = snapshot with { PlaybackError = PlaybackErrors.Create(ErrorCode.AudioPlaybackFailed) };
                    }
                }
            }
            if (playback is not null)
            {
                if (await playback.Ready.ConfigureAwait(false) is not null)
                {
                    foreach (var frame in SyntheticTone.Frames(playback.Snapshot.Ids, playback.Snapshot.Epoch))
                    {
                        lock (gate)
                        {
                            token.ThrowIfCancellationRequested();
                            if (playback.Submit(frame) != FrameAcceptance.Accepted)
                                break;
                        }
                    }
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        playback.CompleteInput(SyntheticTone.SampleCount);
                    }
                }
                var terminal = await playback.Completion.ConfigureAwait(false);
                lock (gate) snapshot = snapshot! with { Playback = terminal, PlaybackError = terminal.Error };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (gate) StopCore();
            if (playback is not null)
            {
                var terminal = await playback.StopAsync().ConfigureAwait(false);
                lock (gate) snapshot = snapshot! with { Playback = terminal, PlaybackError = terminal.Error };
            }
        }
        catch (ContractException) when (playback is not null)
        {
            // Submit rejects and terminates invalid/over-budget PCM itself; retain its normalized terminal.
            var terminal = await playback.Completion.ConfigureAwait(false);
            lock (gate) snapshot = snapshot! with { Playback = terminal, PlaybackError = terminal.Error };
        }
        finally
        {
            lock (gate)
            {
                if (token.IsCancellationRequested)
                    StopCore();
                snapshot = snapshot! with { Trace = cursor!.Finish(), Sequence = cursor.Snapshot, Stage = FixtureSessionStage.Finished };
                cursor.Dispose();
                cursor = null;
            }
        }
        lock (gate)
        {
            snapshot!.Validate();
            return snapshot;
        }
    }

    private void StopCore()
    {
        cursor?.Stop();
        if (snapshot is not null)
            snapshot = snapshot with
            {
                Sequence = cursor?.Snapshot ?? snapshot.Sequence, Stopped = true,
                Text = "", RefusalText = null,
                Trace = snapshot.Trace is not null && cursor is not null ? cursor.Finish() : snapshot.Trace
            };
    }

    public async Task StopAsync()
    {
        Task<FixtureSessionSnapshot>? active;
        PlaybackRun? capturedPlayback;
        lock (gate)
        {
            active = task;
            if (active is not { IsCompleted: false })
                return;
            capturedPlayback = playback;
            StopCore();
            cancellation!.Cancel();
        }
        if (beforePlaybackStop is not null)
            await beforePlaybackStop().ConfigureAwait(false);
        if (capturedPlayback is not null)
            await capturedPlayback.StopAsync().ConfigureAwait(false);
        await active.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await StopAsync().ConfigureAwait(false);
        if (sink is not null)
            await sink.DisposeAsync().ConfigureAwait(false);
        lock (gate) cancellation?.Dispose();
    }
}

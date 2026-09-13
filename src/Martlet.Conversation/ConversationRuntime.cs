using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Conversation;

public sealed class ConversationRuntime : IAsyncDisposable
{
    internal object Sync { get; } = new();
    internal OpenAiTextGenerationAdapter Text { get; }
    internal OpenAiSpeechSynthesisAdapter? Speech { get; }
    internal PcmPlaybackSink? Sink { get; }
    internal PlaybackOptions PlaybackOptions { get; }
    internal TimeProvider Clock { get; }
    private ConversationTurn? active;
    private long epoch, playbackEpoch;
    private bool disposed;
    private Task? disposal;

    public Guid SessionId { get; } = Guid.NewGuid();
    public long CurrentEpoch { get { lock (Sync) return epoch; } }
    public ConversationState State { get { lock (Sync) return active?.Snapshot.State ?? ConversationState.Idle; } }

    private ConversationRuntime(OpenAiTextGenerationAdapter text, OpenAiSpeechSynthesisAdapter? speech,
        PcmPlaybackSink? sink, PlaybackOptions options, TimeProvider clock)
    {
        Text = text;
        Speech = speech;
        PlaybackOptions = options;
        Clock = clock;
        Sink = sink;
    }

    // Merely constructing adapters/sink is passive. No credential resolution, HTTP or device enumeration.
    public static ConversationRuntime Create(IProviderCredentialSource credentials,
        IPlaybackDeviceFactory? devices = null, PlaybackOptions? playbackOptions = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var time = clock ?? TimeProvider.System;
        var options = playbackOptions ?? new();
        var sink = devices is null ? null : new PcmPlaybackSink(devices, options, time);
        return new(OpenAiTextGenerationAdapter.Create(credentials, time),
            devices is null ? null : OpenAiSpeechSynthesisAdapter.Create(credentials, time), sink, options, time);
    }

    internal static ConversationRuntime ForFixture(OpenAiTextGenerationAdapter text,
        OpenAiSpeechSynthesisAdapter? speech, IPlaybackDeviceFactory? devices, PlaybackOptions options, TimeProvider clock)
    {
        return new(text, speech, devices is null ? null : new(devices, options, clock), options, clock);
    }

    // This is the explicit new-turn operation. It neither interrupts nor queues behind existing ownership.
    public ConversationTurn Start(ConversationRequest request, IConversationAuthorizationSource authorization,
        CancellationToken cancellationToken = default) => StartCore(request, authorization, null, false, cancellationToken);

    public ConversationTurn Retry(ConversationTurn previous, ConversationRequest request,
        IConversationAuthorizationSource authorization, bool acknowledgeEarlierSpeech,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        lock (Sync)
        {
            ContractRules.Require(previous.Owner == this && previous.Completion.IsCompleted &&
                previous.OwnershipRelease.IsCompleted, "Retry requires a finished turn from this runtime.");
            ContractRules.Require(!previous.Snapshot.MayHavePlayed || acknowledgeEarlierSpeech,
                "A retry may repeat earlier speech. Explicit acknowledgment is required.");
            return StartCore(request, authorization, previous.TurnId, previous.Snapshot.MayHavePlayed, cancellationToken);
        }
    }

    private ConversationTurn StartCore(ConversationRequest request, IConversationAuthorizationSource authorization,
        Guid? retryOf, bool earlierSpeech, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        request.Validate();
        if (request.Speech is { } speech)
        {
            ContractRules.Require(Sink is not null && Speech is not null,
                "Voice output needs an explicitly composed playback device factory.");
            ContractRules.Require(speech.Limits.MaxInputBytes >= 4,
                "Speech segmentation requires room for one Unicode scalar.");
            ValidateSpeechPrebuffer();
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is not null && (!active.Completion.IsCompleted || !active.OwnershipRelease.IsCompleted || active.Snapshot.Quarantined))
                throw new InvalidOperationException("The previous turn still owns work or playback is quarantined.");
            ContractRules.Require(epoch < int.MaxValue - 1, "The conversation epoch range is exhausted.");
            active = new(this, request, authorization, ++epoch, retryOf, earlierSpeech);
            active.Begin(cancellationToken);
            return active;
        }
    }

    public Task<ConversationSnapshot?> StopAsync()
    {
        ConversationTurn? captured;
        lock (Sync) captured = active;
        return StopCapturedAsync(captured);
    }

    private static async Task<ConversationSnapshot?> StopCapturedAsync(ConversationTurn? captured) =>
        captured is null ? null : await captured.StopAsync().ConfigureAwait(false);

    internal void InvalidateEpoch(ConversationTurn turn)
    {
        // All callers hold Sync. A delayed Stop on an old handle cannot invalidate a newer turn.
        if (active == turn && epoch == turn.Epoch) epoch++;
    }

    internal PlaybackRun StartPlayback(ConversationTurn turn, CorrelationIds ids, OutputSelection output,
        DateTimeOffset deadline, CancellationToken token)
    {
        lock (Sync)
        {
            turn.CheckActive();
            ContractRules.Require(playbackEpoch < int.MaxValue, "The playback epoch range is exhausted.");
            return Sink!.Start(new(ids, ++playbackEpoch, OpenAiSpeechSynthesisCatalog.PcmFormat, output, deadline), token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ConversationTurn? captured;
        lock (Sync)
        {
            disposed = true;
            captured = active;
        }
        if (captured is not null) await captured.StopAsync().ConfigureAwait(false);
        lock (Sync) disposal ??= DisposeOwnedAsync(captured);
        // Stop is bounded; a noncooperative dependency retains ownership until it actually exits.
        if (disposal.IsCompleted) await disposal.ConfigureAwait(false);
    }

    private async Task DisposeOwnedAsync(ConversationTurn? captured)
    {
        if (captured is not null) await captured.OwnershipRelease.ConfigureAwait(false);
        Text.Dispose();
        Speech?.Dispose();
        if (Sink is not null) await Sink.DisposeAsync().ConfigureAwait(false);
    }

    private void ValidateSpeechPrebuffer()
    {
        // The selected raw-PCM adapter emits full 20 ms frames (with a possible shorter final frame).
        var rate = OpenAiSpeechSynthesisCatalog.PcmFormat.SampleRate;
        var frameSamples = rate / 50;
        var prebufferSamples = (long)Math.Ceiling(rate * PlaybackOptions.Prebuffer.TotalSeconds);
        var frames = (prebufferSamples + frameSamples - 1) / frameSamples;
        var capacitySamples = (long)(rate * PlaybackOptions.Capacity.TotalSeconds);
        ContractRules.Require(frames <= PlaybackOptions.MaximumQueuedFrames && frames * frameSamples <= capacitySamples,
            "The playback queue and sample capacity must fit the prebuffer rounded up to full 20 ms speech frames.");
    }

}

internal sealed class ConversationException(ConversationFailure failure) : Exception("The conversation operation could not continue.")
{
    internal ConversationFailure Failure { get; } = failure;
}

internal sealed class MonotonicWindow
{
    private readonly TimeProvider clock;
    private readonly long start;
    private readonly DateTimeOffset startUtc, absolute;
    private readonly TimeSpan duration;
    internal ConversationFailure ExpiryFailure { get; }

    internal MonotonicWindow(TimeProvider clock, TimeSpan duration)
        : this(clock, clock.GetTimestamp(), clock.GetUtcNow(), duration, ConversationFailure.DeadlineExceeded) { }

    private MonotonicWindow(TimeProvider clock, long start, DateTimeOffset startUtc, TimeSpan duration, ConversationFailure failure)
    {
        this.clock = clock;
        this.start = start;
        this.startUtc = startUtc;
        this.duration = duration;
        absolute = startUtc + duration;
        ExpiryFailure = failure;
    }

    internal MonotonicWindow Restrict(DateTimeOffset expiry, ConversationFailure failure)
    {
        var permitted = expiry - startUtc;
        return permitted < duration ? new(clock, start, startUtc, permitted, failure) : this;
    }

    internal TimeSpan Remaining => duration - clock.GetElapsedTime(start);
    internal bool Expired => Remaining <= TimeSpan.Zero || clock.GetUtcNow() >= absolute;
    internal DateTimeOffset Deadline
    {
        get
        {
            var adjusted = clock.GetUtcNow() + Remaining;
            return adjusted < absolute ? adjusted : absolute;
        }
    }
}

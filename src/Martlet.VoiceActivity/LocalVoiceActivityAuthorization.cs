using Martlet.Audio;

namespace Martlet.VoiceActivity;

public sealed class LocalVoiceActivityAuthorization
{
    private readonly CapturedUtterance input;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan lifetime;
    private int consumed, revoked;
    public VoiceActivityBinding Binding { get; }
    public VoiceActivityOptions Options { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool LocalAnalysisRequested { get; }
    public bool IsConsumed => Volatile.Read(ref consumed) != 0;
    public bool IsRevoked => Volatile.Read(ref revoked) != 0;

    public LocalVoiceActivityAuthorization(CapturedUtterance input, VoiceActivityBinding binding,
        VoiceActivityOptions options, DateTimeOffset expiresAt, bool localAnalysisRequested = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(options);
        binding.Validate();
        options.Validate();
        clock = timeProvider ?? TimeProvider.System;
        started = clock.GetTimestamp();
        lifetime = expiresAt - clock.GetUtcNow();
        VadCheck.Require(lifetime > TimeSpan.Zero && lifetime <= TimeSpan.FromSeconds(30) &&
            expiresAt.Offset == TimeSpan.Zero, VoiceActivityFailureCode.DeadlineExceeded);
        this.input = input;
        Binding = binding;
        Options = options;
        ExpiresAt = expiresAt;
        LocalAnalysisRequested = localAnalysisRequested;
    }

    public void Revoke() => Interlocked.Exchange(ref revoked, 1);

    internal void ValidateFor(CapturedUtterance candidate, Guid sessionId, Guid profileId, Guid revision,
        VoiceActivityOptions options, TimeProvider time)
    {
        VadCheck.Require(LocalAnalysisRequested, VoiceActivityFailureCode.AuthorizationRequired);
        VadCheck.Require(ReferenceEquals(input, candidate) && ReferenceEquals(clock, time) &&
            Binding.Ids == candidate.Ids && Binding.Epoch == candidate.Epoch &&
            Binding.SampleCount == candidate.SampleCount && Binding.Ids.SessionId == sessionId &&
            Binding.ProfileId == profileId && Binding.ConfigurationRevision == revision &&
            Options == options, VoiceActivityFailureCode.InvalidBinding);
        VadCheck.Require(!IsConsumed, VoiceActivityFailureCode.AuthorizationConsumed);
        Check();
    }

    internal void Consume()
    {
        VadCheck.Require(Interlocked.CompareExchange(ref consumed, 1, 0) == 0, VoiceActivityFailureCode.AuthorizationConsumed);
    }

    internal void Check()
    {
        VadCheck.Require(!IsRevoked, VoiceActivityFailureCode.Canceled);
        VadCheck.Require(clock.GetUtcNow() < ExpiresAt && clock.GetElapsedTime(started) < lifetime,
            VoiceActivityFailureCode.DeadlineExceeded);
    }

    public override string ToString() => nameof(LocalVoiceActivityAuthorization);
}

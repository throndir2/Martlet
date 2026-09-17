using Martlet.Audio;

namespace Martlet.VoiceActivity;

public sealed class CpuVoiceActivityAnalyzer : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Guid sessionId, profileId, revision;
    private readonly string modelPath;
    private readonly VoiceActivityOptions options;
    private readonly TimeProvider clock;
    private readonly IVoiceActivityInferenceFactory? factory;
    private VoiceActivityOperation? active;
    private bool disposed;
    private long highestEpoch = -1;

    public NativeAvailability Availability => NativeEligibility.Production;

    public CpuVoiceActivityAnalyzer(Guid sessionId, Guid profileId, Guid configurationRevision,
        string modelPath, VoiceActivityOptions? options = null, TimeProvider? timeProvider = null)
    {
        VadCheck.Require(sessionId != Guid.Empty && profileId != Guid.Empty && configurationRevision != Guid.Empty &&
            modelPath is { Length: > 0 and <= 1024 } && !modelPath.Any(char.IsControl));
        this.sessionId = sessionId;
        this.profileId = profileId;
        revision = configurationRevision;
        this.modelPath = modelPath;
        this.options = options ?? new();
        this.options.Validate();
        clock = timeProvider ?? TimeProvider.System;
    }

    internal CpuVoiceActivityAnalyzer(Guid sessionId, Guid profileId, Guid configurationRevision,
        string modelPath, IVoiceActivityInferenceFactory factory, VoiceActivityOptions? options = null,
        TimeProvider? timeProvider = null) : this(sessionId, profileId, configurationRevision, modelPath, options, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
    }

    public VoiceActivityOperation Start(CapturedUtterance input, LocalVoiceActivityAuthorization authorization,
        CancellationToken originalCallerToken = default)
    {
        // This gate precedes even data admission. A local-data assertion cannot grant native eligibility.
        if (factory is null) throw new VoiceActivityException(VoiceActivityFailureCode.NativePrivacyUnqualified);
        lock (gate)
        {
            VadCheck.Require(!disposed, VoiceActivityFailureCode.Disposed);
            VadCheck.Require(active is null || active.OwnershipRelease.IsCompletedSuccessfully &&
                active.OwnershipRelease.Result.Released, VoiceActivityFailureCode.Busy);
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(authorization);
            VadCheck.Require(input.SampleCount is > 0 && input.SampleCount <= options.MaximumInputSamples,
                VoiceActivityFailureCode.PayloadTooLarge);
            authorization.ValidateFor(input, sessionId, profileId, revision, options, clock);
            VadCheck.Require(input.Epoch > highestEpoch, VoiceActivityFailureCode.InvalidBinding);
            VadCheck.Require(!originalCallerToken.IsCancellationRequested, VoiceActivityFailureCode.Canceled);
            authorization.Consume();
            active?.Cancel();
            highestEpoch = input.Epoch;
            active = new(input, authorization, modelPath, factory, options, clock, originalCallerToken);
            active.Begin();
            return active;
        }
    }

    public async ValueTask DisposeAsync()
    {
        VoiceActivityOperation? owned;
        lock (gate)
        {
            disposed = true;
            owned = active;
            owned?.Cancel();
        }
        if (owned is null) return;
        try { await owned.OwnershipRelease.WaitAsync(options.ObservationWait, clock).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}

namespace Martlet.Avatar.Audio2Face;

// The owner constructs this only after permission for this exact clip and destination.
public sealed class Audio2FaceAuthorization : IDisposable
{
    private readonly object input;
    private readonly Audio2FaceOptions options;
    private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly TimeSpan lifetime;
    private readonly CancellationTokenSource revoked = new();
    private readonly CancellationToken revocation;
    private readonly object gate = new();
    private readonly bool allowGeneratedSpeechAnalysis;
    private int consumed;
    private bool disposed;

    public DateTimeOffset ExpiresAt { get; }
    public bool IsConsumed => Volatile.Read(ref consumed) != 0;

    public Audio2FaceAuthorization(GeneratedSpeechClip input, Audio2FaceOptions options,
        DateTimeOffset expiresAt, bool allowGeneratedSpeechAnalysis = false)
        : this((object)input, options, expiresAt, allowGeneratedSpeechAnalysis) { }

    public Audio2FaceAuthorization(GeneratedSpeechStream input, Audio2FaceOptions options,
        DateTimeOffset expiresAt, bool allowGeneratedSpeechAnalysis = false)
        : this((object)input, options, expiresAt, allowGeneratedSpeechAnalysis) { }

    private Audio2FaceAuthorization(object input, Audio2FaceOptions options,
        DateTimeOffset expiresAt, bool allowGeneratedSpeechAnalysis)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lifetime = expiresAt - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(120) ||
            expiresAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Authorization must expire within 120 seconds in UTC.");
        this.input = input;
        this.options = options;
        this.allowGeneratedSpeechAnalysis = allowGeneratedSpeechAnalysis;
        revocation = revoked.Token;
        ExpiresAt = expiresAt;
    }

    public void Revoke()
    {
        lock (gate)
        {
            if (!disposed) revoked.Cancel();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            revoked.Cancel();
            revoked.Dispose();
            disposed = true;
        }
    }

    internal CancellationToken Revocation => revocation;

    internal TimeSpan Remaining
    {
        get
        {
            var monotonic = lifetime - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var wall = ExpiresAt - DateTimeOffset.UtcNow;
            return monotonic < wall ? monotonic : wall;
        }
    }

    internal void Consume(object candidate, Audio2FaceOptions selected)
    {
        if (!allowGeneratedSpeechAnalysis)
            throw new Audio2FaceException(Audio2FaceFailure.AuthorizationRequired);
        if (!ReferenceEquals(input, candidate) || options != selected)
            throw new Audio2FaceException(Audio2FaceFailure.InvalidBinding);
        revocation.ThrowIfCancellationRequested();
        if (Remaining <= TimeSpan.Zero)
            throw new Audio2FaceException(Audio2FaceFailure.AuthorizationExpired);
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
            throw new Audio2FaceException(Audio2FaceFailure.AuthorizationConsumed);
    }

    public override string ToString() => nameof(Audio2FaceAuthorization);
}

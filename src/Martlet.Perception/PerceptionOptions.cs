namespace Martlet.Perception;

public sealed record PerceptionOptions
{
    public const int HardMaximumLongestEdge = 1280;
    public const int HardMaximumFrameBytes = 1024 * 1024;
    public const int HardMaximumSources = 128;
    public static readonly TimeSpan HardMinimumFrameInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan HardMaximumSessionLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HardMaximumFrameAge = TimeSpan.FromSeconds(5);

    public int MaximumLongestEdge { get; init; } = HardMaximumLongestEdge;
    public int MaximumFrameBytes { get; init; } = HardMaximumFrameBytes;
    public int MaximumSources { get; init; } = 64;
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumFrameAge { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan ShutdownObservationWait { get; init; } = TimeSpan.FromSeconds(2);

    public double MaximumFramesPerSecond => 1d / FrameInterval.TotalSeconds;

    public void Validate()
    {
        PerceptionGuard.Require(MaximumLongestEdge is >= 1 and <= HardMaximumLongestEdge &&
            MaximumFrameBytes is >= 4 and <= HardMaximumFrameBytes &&
            MaximumFrameBytes % 4 == 0 &&
            MaximumSources is >= 1 and <= HardMaximumSources &&
            FrameInterval >= HardMinimumFrameInterval && FrameInterval <= TimeSpan.FromSeconds(30) &&
            SessionLifetime >= TimeSpan.FromSeconds(1) &&
            SessionLifetime <= HardMaximumSessionLifetime &&
            MaximumFrameAge > TimeSpan.Zero && MaximumFrameAge <= HardMaximumFrameAge &&
            MaximumFrameAge <= SessionLifetime &&
            ShutdownObservationWait > TimeSpan.Zero &&
            ShutdownObservationWait <= TimeSpan.FromSeconds(2));
    }
}

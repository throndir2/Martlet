namespace Martlet.Core.Reading;

/// <summary>
/// When Martlet reads the text on the screen while it watches (docs/READING.md): it takes a full-size picture at most once per
/// <see cref="Every"/> (Companion › Reading › Read the screen, talk-preferences.json's ReadEveryMs, shared with every computer
/// as part of how you talk) and reads it when the picture changed since the last read, or <see cref="Refresh"/> passed (small
/// text such as a score can change without changing the coarse picture much). The reader itself reads one picture at a time,
/// so a read slower than <see cref="Every"/> sets the pace. Reading more often finds new text sooner and uses more of the
/// processor or graphics card that reads.
/// </summary>
public sealed class ScreenReadPace(TimeProvider? clock = null)
{
    /// <summary>The default: every 3 seconds, the pace of a look's screenshot.</summary>
    public const int DefaultMs = 3000;
    /// <summary>The picture changed at least this much (0 to 1) since the last read: read it now.</summary>
    public const double PictureChange = 0.002;
    /// <summary>The choices on Companion › Reading, fastest first.</summary>
    public static IReadOnlyList<int> ChoicesMs { get; } = [500, 1000, 2000, 3000, 5000, 10000];
    /// <summary>A screen that looks the same is read again after this long.</summary>
    public static TimeSpan Refresh => TimeSpan.FromSeconds(9);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private long? lastCapture, lastRead;
    private byte[]? lastSignature;

    /// <summary>At most one full-size picture this often.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromMilliseconds(DefaultMs);

    /// <summary>The nearest choice to <paramref name="milliseconds"/> (a newer Martlet may offer others); the default when it
    /// isn't positive.</summary>
    public static int Normalize(int milliseconds) =>
        milliseconds <= 0 ? DefaultMs : ChoicesMs.MinBy(choice => Math.Abs(choice - milliseconds));

    /// <summary>A choice in words: "Twice a second", "Every second", "Every 3 seconds".</summary>
    public static string Describe(int milliseconds) => Normalize(milliseconds) switch
    {
        500 => "Twice a second",
        1000 => "Every second",
        var ms => $"Every {ms / 1000} seconds"
    };

    /// <summary>Whether a new full-size picture is due now. True starts the wait for the next one.</summary>
    public bool Capture()
    {
        if (lastCapture is { } at && clock.GetElapsedTime(at) < Every) return false;
        lastCapture = clock.GetTimestamp();
        return true;
    }

    /// <summary>Whether to read the picture with this grey thumbnail: one that changed at least <see cref="PictureChange"/>
    /// since the last read, or any picture once <see cref="Refresh"/> passed or when <paramref name="always"/> (no read worked
    /// yet). True counts it as read.</summary>
    public bool Read(byte[]? signature, bool always = false)
    {
        if (!always && lastRead is { } at && clock.GetElapsedTime(at) < Refresh && Change(lastSignature, signature) < PictureChange)
            return false;
        lastRead = clock.GetTimestamp();
        lastSignature = signature;
        return true;
    }

    /// <summary>How much a grey thumbnail changed, 0 (identical) to 1; 1 without one of them to compare.</summary>
    public static double Change(byte[]? before, byte[]? after) =>
        before is null || after is null || before.Length != after.Length || before.Length == 0
            ? 1
            : before.Zip(after, (a, b) => Math.Abs(a - b)).Average() / 255.0;
}

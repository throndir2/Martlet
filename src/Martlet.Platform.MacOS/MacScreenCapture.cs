using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Martlet.Companion.Platform;

namespace Martlet.Platform.MacOS;

/// <summary>"Watch my screen" on a Mac: one downscaled JPEG of the main display per look, taken by macOS's own
/// <c>screencapture</c> tool and resized with <c>sips</c> (both ship with macOS, so Martlet needs no helper of its own).
/// macOS attributes both to Martlet, which needs the Screen &amp; System Audio Recording permission. Nothing prompts by
/// itself: only <see cref="RequestConsentAsync"/> (the user's Start watching) asks macOS.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacScreenCapture : IScreenCapture
{
    public const string ScreenCaptureTool = "/usr/sbin/screencapture";
    public const string ImageTool = "/usr/bin/sips";

    /// <summary>What the app shows when the permission is missing.</summary>
    public const string PermissionExplanation =
        "To watch your screen, allow Martlet in System Settings > Privacy & Security > Screen & System Audio Recording, " +
        "then quit and reopen Martlet (macOS applies it only after a restart). From macOS 15 on, macOS asks again from time " +
        "to time whether Martlet may keep recording; that is normal. Martlet takes one picture per look, only while Watch my " +
        "screen is on.";

    public FeatureStatus Status => Native.Preflight()
        ? FeatureStatus.Yes("Screen looks are allowed (Screen & System Audio Recording).")
        : FeatureStatus.No(PermissionExplanation);

    public Task<FeatureStatus> RequestConsentAsync(CancellationToken cancellationToken)
    {
        if (Native.Preflight()) return Task.FromResult(Status);
        // Shows macOS's prompt the first time and adds Martlet to the Settings list; granted takes effect after a restart.
        Native.Request();
        return Task.FromResult(Native.Preflight() ? Status : FeatureStatus.No(PermissionExplanation));
    }

    public async Task<ScreenShot?> CaptureAsync(ScreenCaptureRequest request, CancellationToken cancellationToken)
    {
        if (!Native.Preflight()) return null;
        var folder = Directory.CreateTempSubdirectory("martlet-look-");
        var raw = Path.Combine(folder.FullName, "screen.jpg");
        var small = Path.Combine(folder.FullName, "look.jpg");
        try
        {
            if (await RunAsync(ScreenCaptureTool, CaptureArguments(raw, 1), cancellationToken).ConfigureAwait(false) != 0 || !File.Exists(raw))
                return null;
            var resized = await RunAsync(ImageTool, ResizeArguments(raw, small, request.MaxLongEdge, request.JpegQuality), cancellationToken)
                .ConfigureAwait(false) == 0 && File.Exists(small);
            var jpeg = await File.ReadAllBytesAsync(resized ? small : raw, cancellationToken).ConfigureAwait(false);
            var (width, height) = JpegSize(jpeg) ?? (0, 0);
            return new ScreenShot(jpeg, width, height, DateTimeOffset.UtcNow);
        }
        finally
        {
            try { folder.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Nothing stays open between looks.</summary>
    public void Release() { }

    /// <summary>screencapture: -x silent, -t jpg, -D display (1 = main); no cursor and no window shadow by default.</summary>
    internal static IReadOnlyList<string> CaptureArguments(string file, int display)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(display, 1);
        return ["-x", "-t", "jpg", "-D", display.ToString(CultureInfo.InvariantCulture), file];
    }

    /// <summary>sips: long edge at most <paramref name="maxEdge"/> pixels (-Z never upscales), JPEG at
    /// <paramref name="quality"/> percent.</summary>
    internal static IReadOnlyList<string> ResizeArguments(string from, string to, int maxEdge, int quality) =>
    [
        "-Z", Math.Max(64, maxEdge).ToString(CultureInfo.InvariantCulture), "-s", "format", "jpeg", "-s", "formatOptions",
        Math.Clamp(quality, 1, 100).ToString(CultureInfo.InvariantCulture), from, "--out", to
    ];

    /// <summary>Width and height from the JPEG's start-of-frame marker, or null when it has none.</summary>
    internal static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var i = 2;
        while (i + 8 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xFF) { i += marker == 0xFF ? 1 : 2; continue; }
            var length = jpeg[i + 2] << 8 | jpeg[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (jpeg[i + 7] << 8 | jpeg[i + 8], jpeg[i + 5] << 8 | jpeg[i + 6]);
            if (length < 2) return null;
            i += 2 + length;
        }
        return null;
    }

    private static async Task<int> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} didn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellation.IsCancellationRequested) throw;
            return -1;
        }
        return process.ExitCode;
    }

    private static class Native
    {
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGPreflightScreenCaptureAccess();
        [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGRequestScreenCaptureAccess();
        public static bool Preflight() => CGPreflightScreenCaptureAccess();
        public static bool Request() => CGRequestScreenCaptureAccess();
    }
}

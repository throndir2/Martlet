using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Desktop;

/// <summary>Local transcript of every Martlet host run (<see cref="HostRunWindow"/>): the output exactly as shown, so a
/// failed setup, pairing or role change can be investigated after its window is closed. Written next to desktop.log as
/// host-runs.log (rotates at 2 MiB, keeps one older copy); never uploaded. Hosts never print secrets, Martlet hides
/// pairing codes before showing them, and any pairing code that still reaches this log is masked.</summary>
internal static partial class HostRunLog
{
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly object gate = new();

    [GeneratedRegex(@"martlet-pair-v1\.[A-Za-z0-9_-]+")]
    private static partial Regex PairingCodePattern();

    internal static string? Path => ErrorLog.Directory is { } directory ? System.IO.Path.Combine(directory, "host-runs.log") : null;

    internal static void Write(string run, string line)
    {
        if (Path is not { } path) return;
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{run}] " +
            PairingCodePattern().Replace(line, "martlet-pair-v1.(hidden)") + Environment.NewLine;
        lock (gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaximumFileBytes)
                    File.Move(path, System.IO.Path.ChangeExtension(path, ".1.log"), overwrite: true);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }
}

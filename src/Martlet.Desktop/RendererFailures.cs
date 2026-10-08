using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>How Martlet treats a character renderer that fails a command. The renderer (its process or its pipe) can close,
/// time out, send something unreadable or refuse a command; work that only draws the character (lip-sync, a gaze, an emote,
/// reading where it is) then ends quietly with one short log line, and stopping the character always finishes.</summary>
internal static class RendererFailures
{
    private static readonly RendererFailureLog log = new(TimeProvider.System);

    /// <summary>The character renderer, not the caller, failed a command: it closed, broke or refused the exchange
    /// (<see cref="IOException"/>, <see cref="InvalidDataException"/>, <see cref="InvalidOperationException"/>,
    /// <see cref="ObjectDisposedException"/>, <see cref="JsonException"/>, <see cref="TimeoutException"/>), or its time limit
    /// ran out (<see cref="OperationCanceledException"/> while <paramref name="caller"/> is not canceled). A cancellation by
    /// the caller's own token is never a renderer failure.</summary>
    internal static bool Is(Exception error, CancellationToken caller) => error switch
    {
        OperationCanceledException => !caller.IsCancellationRequested,
        IOException or InvalidDataException or InvalidOperationException or JsonException or TimeoutException => true,
        _ => false
    };

    /// <summary>Writes one short WARN line (no stack trace) that <paramref name="what"/> failed because of the character renderer.
    /// The same failure again within a minute is only counted; the next line for it says how many were not written.</summary>
    internal static void Log(string what, Exception error)
    {
        if (log.Line(what, error) is { } line) ErrorLog.Warn(line);
    }
}

/// <summary>The short log lines for character renderer failures: one line for each kind of failure (what failed and why) in a
/// minute, so a renderer that fails many commands cannot fill the log.</summary>
internal sealed class RendererFailureLog(TimeProvider clock)
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);
    private const int Remembered = 64;
    private readonly object gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, int Skipped)> seen = new(StringComparer.Ordinal);

    /// <summary>The line to write for <paramref name="what"/> failing with <paramref name="error"/>, or null when the same
    /// failure already has a line from less than <see cref="Quiet"/> ago.</summary>
    internal string? Line(string what, Exception error)
    {
        var reason = Reason(error);
        var key = what + "\n" + reason;
        var now = clock.GetUtcNow();
        int skipped;
        lock (gate)
        {
            if (seen.TryGetValue(key, out var last) && now - last.At < Quiet)
            {
                seen[key] = (last.At, last.Skipped + 1);
                return null;
            }
            skipped = last.Skipped;
            seen[key] = (now, 0);
            if (seen.Count > Remembered)
                foreach (var old in seen.Where(entry => now - entry.Value.At >= Quiet).Select(entry => entry.Key).ToList())
                    seen.Remove(old);
        }
        return $"{what}: {reason}" + (skipped > 0 ? $" ({skipped} more like it in the minute before weren't logged.)" : "");
    }

    /// <summary>Why, in a few words: a time limit, a closed renderer, or the error's own message and type.</summary>
    internal static string Reason(Exception error) => error switch
    {
        OperationCanceledException => "the character renderer didn't answer in time.",
        ObjectDisposedException => "the character renderer had already closed.",
        _ => $"{error.Message.TrimEnd('.')} ({error.GetType().Name})."
    };
}

/// <summary>A character renderer command failed (<see cref="Exception.InnerException"/> says how), told apart from a failure of
/// Audio2Face or of the paired host so that only the work that draws the character ends.</summary>
internal sealed class CharacterRendererException(Exception inner) : IOException(inner.Message, inner);

/// <summary>FIXTURE for MCP verification, never a real failure: <c>MARTLET_SIMULATE_RENDERER_FAILURE</c> names character renderer
/// commands (comma-separated, such as <c>where,mouth</c>) that the shown character's renderer fails the way a broken pipe does
/// (<see cref="InvalidDataException"/>). The renderer itself still starts, draws and closes normally.</summary>
internal sealed class SimulatedRendererFailure(IAvatarRenderer inner, IReadOnlySet<string> kinds) : IAvatarRenderer
{
    internal const string Variable = "MARTLET_SIMULATE_RENDERER_FAILURE";

    /// <summary><paramref name="create"/>, or with <c>MARTLET_SIMULATE_RENDERER_FAILURE</c> set, renderers from it that fail
    /// the commands it names.</summary>
    internal static Func<IAvatarRenderer> Wrap(Func<IAvatarRenderer> create, string? setting)
    {
        var kinds = (setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(kind => kind.Length is > 0 and <= 32).ToFrozenSet(StringComparer.Ordinal);
        if (kinds.Count == 0) return create;
        return () =>
        {
            ErrorLog.Warn($"FIXTURE: the character renderer fails its {string.Join(", ", kinds.Order(StringComparer.Ordinal))} commands ({Variable}).");
            return new SimulatedRendererFailure(create(), kinds);
        };
    }

    public RendererCapabilities? Capabilities => inner.Capabilities;
    public bool HasExited => inner.HasExited;
    public Task Exited => inner.Exited;
    public int? ProcessId => inner.ProcessId;
    public event Action<string>? Requested { add => inner.Requested += value; remove => inner.Requested -= value; }
    public event Action<CharacterTouch>? Touched { add => inner.Touched += value; remove => inner.Touched -= value; }
    public event Action<CharacterStroke>? Stroked { add => inner.Stroked += value; remove => inner.Stroked -= value; }
    public event Action<RendererPhysical>? Physical { add => inner.Physical += value; remove => inner.Physical -= value; }

    public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token) =>
        inner.StartAsync(profile, revision, placement, voiceMuted, token);

    public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null) =>
        kinds.Contains(kind)
            ? Task.FromException<RendererMessage>(new InvalidDataException($"Renderer message length is invalid (simulated by {Variable})."))
            : inner.SendAsync(kind, data, token, timeout);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

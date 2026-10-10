using System.Globalization;

namespace Martlet.Conversation;

/// <summary>The Thinking trace (docs/VOICE_LATENCY.md, Thinking trace): short log lines that follow every Thinking request step
/// by step, so a log shows the flow and where the time went. Each conversation turn (a reply, a glance, a background think, a
/// Thinking pool job...) says when it started and with what (route, model, sizes), each of its requests (authorized, sent, the
/// response, hidden reasoning, first words, the end and its tokens), each tool round, Backup Thinking's race and how the turn
/// ended; while it waits for anything for 2 s or more it says what it waits for, again at 5, 10, 20 and 30 s and then every
/// 30 s. Every request for the Thinking pool's slots (<see cref="ThinkingRequests"/>) says when it waits in line, starts on a
/// member, lets it go and ends. Never what was said: only counts, names of routes, models, members and tools, and times.
/// Lines go to every listener (the desktop's log writes them on a background thread); a listener must never block. With none,
/// nothing is formatted, so the trace costs nothing.</summary>
public static class ThinkingTrace
{
    private static Action<string>[] listeners = [];
    private static long turns;

    /// <summary>Sends every trace line to <paramref name="listener"/> until the result is disposed. A listener that throws is
    /// ignored; it must never block (the lines are written on the reply's path).</summary>
    public static IDisposable Listen(Action<string> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        Change(all => [.. all, listener]);
        return new Listening(listener);
    }

    /// <summary>Whether anything listens.</summary>
    public static bool Enabled => Volatile.Read(ref listeners).Length > 0;

    /// <summary>Gives <paramref name="line"/> to every listener.</summary>
    public static void Write(string line)
    {
        foreach (var listener in Volatile.Read(ref listeners))
        {
            // The trace must never change what a request does.
            try { listener(line); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    /// <summary>The next conversation turn's number in the trace (1, 2...), the same across every runtime of the process.</summary>
    internal static long NextTurn() => Interlocked.Increment(ref turns);

    /// <summary>Milliseconds, whole, without separators (as the reply latency line writes them).</summary>
    internal static string Ms(TimeSpan span) => Math.Max(0, span.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>When the time a wait has lasted says so again: 2, 5, 10, 20 and 30 s, then every 30 s.</summary>
    internal static TimeSpan Notice(int index) => index switch
    {
        0 => TimeSpan.FromSeconds(2),
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(10),
        3 => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(30 * (index - 3))
    };

    /// <summary>An endpoint as the log may show it: scheme, host, port and path (never a query, which may hold a key).</summary>
    internal static string Endpoint(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/') : "(an endpoint)";

    private static void Change(Func<Action<string>[], Action<string>[]> change)
    {
        while (true)
        {
            var before = Volatile.Read(ref listeners);
            if (Interlocked.CompareExchange(ref listeners, change(before), before) == before) return;
        }
    }

    private sealed class Listening(Action<string> listener) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Change(all =>
            {
                var index = Array.IndexOf(all, listener);
                return index < 0 ? all : [.. all[..index], .. all[(index + 1)..]];
            });
        }
    }
}

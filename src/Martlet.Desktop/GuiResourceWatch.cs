using System.Diagnostics;

namespace Martlet.Desktop;

/// <summary>Writes how many Window Manager and GDI objects Martlet holds to the desktop log, so a slow leak shows long before
/// Windows' limit stops Martlet (timers, windows and menus then fail with "... all of its system allowance of handles for
/// Window Manager objects"). It reads every five minutes and writes a line once an hour, when either count grew by 1,000
/// since the last line (with Martlet's windows by class), and a warning at half, three quarters and nine tenths of the limit.
/// It runs on a thread-pool timer, so it keeps working when the UI thread can no longer make timers.</summary>
internal sealed class GuiResourceWatch
{
    internal enum Note { None, Hourly, Grew, Warning }

    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Hourly = TimeSpan.FromHours(1);
    internal const int Growth = 1_000;
    internal static readonly double[] Levels = [0.5, 0.75, 0.9];
    private static Timer? timer;
    private static readonly GuiResourceWatch Running = new();
    private static readonly object gate = new();
    private static TimeSpan? loggedNow;

    private GuiCount? logged;
    private TimeSpan loggedAt;
    private int warned = -1;

    /// <summary>Starts the readings (the first one a minute after start). Starting again does nothing.</summary>
    internal static void Start()
    {
        lock (gate)
        {
            timer ??= new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), Every);
        }
    }

    /// <summary>Writes the counts and Martlet's windows by class now, after <paramref name="reason"/> (an error at the limit),
    /// at most once a minute.</summary>
    internal static void LogNow(string reason)
    {
        lock (gate)
        {
            var now = Stopwatch.GetElapsedTime(0);
            if (loggedNow is { } last && now - last < TimeSpan.FromMinutes(1)) return;
            loggedNow = now;
        }
        try
        {
            if (GuiResources.Read(Environment.ProcessId) is not { } count) return;
            ErrorLog.Warn($"Window resources {reason}: {count.Describe()}; {GuiResources.Census(Environment.ProcessId).Describe(12)}.");
        }
        catch (Exception error) when (!ErrorLog.IsFatal(error)) { ErrorLog.Warn("Couldn't read Martlet's window resources", error); }
    }

    private static void Tick()
    {
        try
        {
            if (GuiResources.Read(Environment.ProcessId) is not { } count) return;
            Note note;
            lock (gate) note = Running.Next(count, Stopwatch.GetElapsedTime(0));
            if (note == Note.None) return;
            var census = GuiResources.Census(Environment.ProcessId);
            using var self = Process.GetCurrentProcess();
            var threads = self.Threads.Count;
            switch (note)
            {
                case Note.Warning:
                    ErrorLog.Warn($"Window resources: Martlet uses {count.Share:P0} of what Windows allows it: {count.Describe()}; " +
                        $"{census.Describe(12)}; {threads} threads. At the limit, Martlet stops working until it restarts.");
                    break;
                case Note.Grew:
                    ErrorLog.Info($"Window resources grew: {count.Describe()}; {census.Describe()}; {threads} threads.");
                    break;
                default:
                    ErrorLog.Info($"Window resources: {count.Describe()}; {census.Describe(5)}; {threads} threads.");
                    break;
            }
        }
        catch (Exception error) when (!ErrorLog.IsFatal(error)) { ErrorLog.Warn("Couldn't read Martlet's window resources", error); }
    }

    /// <summary>Which line, if any, a reading <paramref name="count"/> taken at <paramref name="now"/> writes: the first
    /// reading and then one an hour, one when a count grew by <see cref="Growth"/> since the last line, and a warning the
    /// first time the larger share of a limit reaches each of <see cref="Levels"/> (again after it fell 10 points below).</summary>
    internal Note Next(GuiCount count, TimeSpan now)
    {
        var level = -1;
        for (var i = 0; i < Levels.Length; i++)
            if (count.Share >= Levels[i]) level = i;
        if (warned >= 0 && count.Share < Levels[warned] - 0.1) warned = level;
        Note note;
        if (level > warned)
        {
            warned = level;
            note = Note.Warning;
        }
        else if (logged is null) note = Note.Hourly;
        else if (count.User - logged.User >= Growth || count.Gdi - logged.Gdi >= Growth) note = Note.Grew;
        else if (now - loggedAt >= Hourly) note = Note.Hourly;
        else return Note.None;
        logged = count;
        loggedAt = now;
        return note;
    }
}

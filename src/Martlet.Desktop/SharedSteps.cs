namespace Martlet.Desktop;

/// <summary>Setup steps several runs can need at the same time on this PC: installing Docker Desktop, getting Windows ready
/// for it, starting it, building the host image, opening Windows Firewall and setting up and pairing this PC's own host
/// service. The first run does the step; every other run that needs it meanwhile waits for that one in its own window,
/// saying which run it waits for, instead of repeating it (a second administrator prompt, a second Docker Desktop restart, a
/// second pairing) or being refused. A waiting run continues when the step is done, stops with the same reason when it
/// failed, and does the step itself when the run doing it was canceled. Everything else runs side by side: changes to a host
/// take turns in the host's own engine lock (deploy/host/README.md, One change at a time).</summary>
internal static class SharedSteps
{
    internal const string DockerInstall = "docker-install";
    internal const string WindowsReady = "windows";
    internal const string DockerStart = "docker";
    internal const string Firewall = "firewall";
    internal const string ThisPcHost = "this-pc-host";
    internal static string Image(string image) => "image:" + image;

    private sealed record Step(Task Task, string By, string Doing);

    private static readonly object gate = new();
    private static readonly Dictionary<string, Step> running = new(StringComparer.Ordinal);

    /// <summary>A step started or ended (raised on the thread that started or ended it).</summary>
    internal static event Action? Changed;

    /// <summary>Whether a run does <paramref name="key"/> now.</summary>
    internal static bool IsRunning(string key)
    {
        lock (gate) return running.ContainsKey(key);
    }

    /// <summary>The steps running now, as "<c>by</c>: <c>doing</c>" (run titles and fixed words; never output).</summary>
    internal static IReadOnlyList<(string Key, string By, string Doing)> Now()
    {
        lock (gate) return running.Select(pair => (pair.Key, pair.Value.By, pair.Value.Doing)).ToArray();
    }

    /// <summary>Does <paramref name="step"/> as <paramref name="by"/> (a run's title), unless another run does
    /// <paramref name="key"/> already: then this one waits for it (its status and output say so) and returns its result.
    /// <paramref name="doing"/> says what the step does ("starting Docker Desktop"), for the runs that wait.</summary>
    internal static async Task<T> RunAsync<T>(string key, string by, string doing, Func<Task<T>> step, Action<string> status,
        IProgress<string>? output, CancellationToken token)
    {
        while (true)
        {
            Step? other;
            TaskCompletionSource<T>? lead = null;
            lock (gate)
            {
                if (!running.TryGetValue(key, out other))
                {
                    lead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    running[key] = new(lead.Task, by, doing);
                }
            }
            if (lead is not null) return await LeadAsync(key, lead, step);
            if (await FollowAsync(other!, status, output, token)) return await (Task<T>)other!.Task;
        }
    }

    /// <summary>Waits for <paramref name="key"/> when another run does it now; returns false at once when none does, or
    /// when the run doing it was canceled (the caller then decides what to do). Throws that run's reason when it failed.</summary>
    internal static async Task<bool> WaitAsync(string key, Action<string> status, IProgress<string>? output, CancellationToken token)
    {
        Step? other;
        lock (gate) running.TryGetValue(key, out other);
        return other is not null && await FollowAsync(other, status, output, token);
    }

    private static async Task<T> LeadAsync<T>(string key, TaskCompletionSource<T> lead, Func<Task<T>> step)
    {
        // The step leaves the list before its outcome is published, so a run that waited for it never finds it again (a
        // canceled one would otherwise be waited for over and over).
        void Done()
        {
            lock (gate)
                if (running.TryGetValue(key, out var mine) && ReferenceEquals(mine.Task, lead.Task)) running.Remove(key);
        }

        Raise();
        T result;
        try { result = await step(); }
        catch (OperationCanceledException error)
        {
            Done();
            lead.TrySetCanceled(error.CancellationToken);
            Raise();
            throw;
        }
        catch (Exception error)
        {
            Done();
            lead.TrySetException(error);
            Raise();
            throw;
        }
        Done();
        lead.TrySetResult(result);
        Raise();
        return result;
    }

    /// <summary>Waits for <paramref name="other"/>: true once it is done, false when the run doing it was canceled; throws
    /// its reason when it failed, or when this run is canceled.</summary>
    private static async Task<bool> FollowAsync(Step other, Action<string> status, IProgress<string>? output, CancellationToken token)
    {
        status($"Waiting: \"{other.By}\" is {other.Doing}. This continues once that's done...");
        output?.Report($"\"{other.By}\" is {other.Doing} already, so this run waits for it instead of doing it again.");
        try
        {
            await other.Task.WaitAsync(token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            output?.Report($"\"{other.By}\" was canceled before it finished {other.Doing}, so this run carries on by itself.");
            return false;
        }
    }

    private static void Raise() => Changed?.Invoke();
}

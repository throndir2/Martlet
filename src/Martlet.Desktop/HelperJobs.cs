using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Providers;

// Also built into Martlet's MCP server (helper_jobs_check), in its own namespace.
#if MARTLET_MCP
using Martlet.Mcp.Logging;

namespace Martlet.Mcp.Shared;
#else
using Martlet.Logging;

namespace Martlet.Desktop;
#endif

/// <summary>Background model work Martlet does on its own, besides replies and think_longer.</summary>
internal enum HelperJobKind
{
    /// <summary>Remembering and learning names after a reply.</summary>
    Memory,
    /// <summary>Naming a character's emotes and motions.</summary>
    ActionNaming,
    /// <summary>Finding a character's touch zones in one picture of it.</summary>
    TouchZones,
    /// <summary>Deciding a character's touch temperament from its personality.</summary>
    Temperament
}

/// <summary>What a pool member must do to take a helper job: read text, or also see one picture.</summary>
internal enum HelperCapability { Text, Vision }

/// <summary>How soon the pool takes a helper job. Memory and naming wait behind other work; the owner waits for touch zones.</summary>
internal enum HelperJobPriority { Low, Normal }

/// <summary>One helper job posted to the Thinking pool: its kind, the log's name for it and the one request it makes.</summary>
internal sealed record HelperJob(HelperJobKind Kind, string Purpose, BoundedTextInput Input)
{
    internal HelperCapability Capability => Input.Image is null ? HelperCapability.Text : HelperCapability.Vision;
    internal HelperJobPriority Priority => HelperJobs.PriorityOf(Kind);
    public override string ToString() => $"{nameof(HelperJob)} {{ Kind = {Kind}, Capability = {Capability} }}";
}

/// <summary>What a pool member answered: its name (for the log and MCP), the answer, or null with why it failed.</summary>
internal sealed record HelperPoolAnswer(string Member, string? Answer, string? Failure);

/// <summary>The Thinking pool as helper jobs see it.</summary>
internal interface IHelperJobPool
{
    /// <summary>Whether a pool member can take a job that needs <paramref name="capability"/> (cheap, no request).</summary>
    bool Has(HelperCapability capability);

    /// <summary>Posts <paramref name="job"/>; a free member that can take it runs it. Null at once when no member can take it,
    /// so the caller uses the conversation's own Thinking model.</summary>
    Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token);
}

/// <summary>Where the last helper job of <paramref name="Kind"/> ran: on a pool member (<paramref name="Member"/>) or on the
/// conversation's own Thinking model after the reply (fallback), how it ended and how long it waited for the reply first.</summary>
internal sealed record HelperRoute(HelperJobKind Kind, bool Pooled, string? Member, string Outcome, DateTimeOffset At, long WaitedMs)
{
    internal string Route => Pooled ? "pool" : "fallback";
}

/// <summary>The answer of a helper job, and whether a pool member gave it (the caller reads it with that route's prompt).</summary>
internal readonly record struct HelperResult(string? Answer, string? Failure, bool Pooled);

/// <summary>Runs helper jobs: on a free Thinking pool member when the pool has one that can take the job, so they never compete
/// with the reply for the conversation's model and its prompt cache. Without one, on the conversation's own Thinking route as
/// before, but only once no reply is running or speaking and the live floor (<see cref="Floor"/>) is Idle; when the floor goes
/// Live while it runs, it stops and runs again once the conversation is quiet. Keeps the last route of each kind
/// (helper-jobs.json, for MCP).</summary>
internal sealed class HelperJobs(Func<IHelperJobPool?> pool, Func<bool> replyBusy, string? dataDirectory)
{
    /// <summary>The helper-jobs.json status file in the data directory (kinds, routes, outcomes and times; never a prompt or
    /// answer), which MCP's helper_jobs_status reads.</summary>
    internal const string StatusFile = "helper-jobs.json";
    internal static TimeSpan IdleCheck { get; } = TimeSpan.FromMilliseconds(100);

    private readonly object gate = new();
    private readonly object fileGate = new();
    private readonly Dictionary<HelperJobKind, HelperRoute> last = [];

    /// <summary>The live floor the fallback on the conversation's own Thinking route waits for (it starts only while Idle and
    /// stops when the floor goes Live); null: only whether a reply runs decides.</summary>
    internal LiveFloor? Floor { get; init; }

    internal static HelperJobPriority PriorityOf(HelperJobKind kind) =>
        kind == HelperJobKind.TouchZones ? HelperJobPriority.Normal : HelperJobPriority.Low;

    internal static string Name(HelperJobKind kind) => kind switch
    {
        HelperJobKind.Memory => "memory",
        HelperJobKind.ActionNaming => "action_naming",
        HelperJobKind.Temperament => "temperament",
        _ => "touch_zones"
    };

    /// <summary>The last route of each kind that ran since Martlet started.</summary>
    internal IReadOnlyList<HelperRoute> Last
    {
        get { lock (gate) return [.. last.Values.OrderBy(route => route.Kind)]; }
    }

    /// <summary>Whether the pool has a member for <paramref name="capability"/> now.</summary>
    internal bool PoolHas(HelperCapability capability) => pool() is { } current && current.Has(capability);

    /// <summary>Runs one helper job. <paramref name="poolInput"/> is the request a pool member gets (made only when one can
    /// take it; a request too large for it falls back); <paramref name="fallback"/> asks the conversation's own Thinking
    /// model, after the reply finished speaking and while no other reply runs.</summary>
    internal async Task<HelperResult> RunAsync(HelperJobKind kind, string purpose, HelperCapability capability,
        Func<BoundedTextInput> poolInput, Func<CancellationToken, Task<(string? Answer, string? Failure)>> fallback,
        CancellationToken token)
    {
        var why = "the Thinking pool has no member";
        if (pool() is { } current && current.Has(capability))
        {
            HelperJob? job = null;
            try { job = new(kind, purpose, poolInput()); }
            catch (Exception error) when (error is not OperationCanceledException) { why = "the request can't be made for the pool"; }
            if (job is not null)
            {
                var answer = await current.TryRunAsync(job, token).ConfigureAwait(false);
                if (answer is not null)
                {
                    Record(new(kind, true, answer.Member, Outcome(answer.Answer, answer.Failure), DateTimeOffset.UtcNow, 0));
                    ErrorLog.Info($"{purpose}: ran on Thinking pool member {answer.Member} ({Outcome(answer.Answer, answer.Failure)}).");
                    return new(answer.Answer, answer.Failure, true);
                }
                why = "no Thinking pool member was free to take it";
            }
        }
        else if (pool() is not null) why = $"no Thinking pool member can take {(capability == HelperCapability.Vision ? "a picture" : "it")}";
        var waited = Stopwatch.StartNew();
        var again = 0;
        while (true)
        {
            // Never beside a reply or while you talk with Martlet: the conversation's own model is busy with you then.
            while (replyBusy() || Floor is { Level: not LiveFloorLevel.Idle }) await Task.Delay(IdleCheck, token).ConfigureAwait(false);
            var waitedMs = waited.ElapsedMilliseconds;
            if (again == 0) ErrorLog.Info($"{purpose}: runs on the conversation's Thinking model ({why}) after waiting {waitedMs} ms for the reply to finish.");
            using var live = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stopped = 0;
            // The live turn comes first: the floor going Live stops it, and it runs again later. The request closes off the
            // floor's thread.
            void Follow(LiveFloorChange change)
            {
                if (change.To == LiveFloorLevel.Live && Interlocked.Exchange(ref stopped, 1) == 0) _ = live.CancelAsync();
            }
            var floor = Floor;
            if (floor is not null) floor.Changed += Follow;
            (string? Answer, string? Failure) answer;
            try { answer = await fallback(live.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref stopped) != 0) { answer = (null, null); }
            finally
            {
                if (floor is not null) floor.Changed -= Follow;
            }
            if (Volatile.Read(ref stopped) != 0 && !token.IsCancellationRequested)
            {
                again++;
                ErrorLog.Info($"{purpose}: stopped on the conversation's Thinking model for the conversation; it runs again once the conversation is quiet.");
                continue;
            }
            var (text, failure) = answer;
            Record(new(kind, false, null, Outcome(text, failure), DateTimeOffset.UtcNow, waitedMs));
            return new(text, failure, false);
        }
    }

    private static string Outcome(string? answer, string? failure) =>
        answer is not null ? "answered" : failure is null ? "no answer" : "failed: " + failure;

    private void Record(HelperRoute route)
    {
        string status;
        lock (gate)
        {
            last[route.Kind] = route;
            status = Status(last.Values);
        }
        if (dataDirectory is null) return;
        lock (fileGate)
        {
            try
            {
                var path = Path.Combine(dataDirectory, StatusFile);
                File.WriteAllText(path + ".tmp", status);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string Status(IEnumerable<HelperRoute> routes) => JsonSerializer.Serialize(new
    {
        updatedAt = DateTimeOffset.UtcNow,
        kinds = routes.OrderBy(route => route.Kind).Select(route => new
        {
            kind = Name(route.Kind), route = route.Route, member = route.Member, outcome = route.Outcome,
            priority = PriorityOf(route.Kind).ToString().ToLowerInvariant(), at = route.At, waitedMs = route.WaitedMs
        }).ToArray()
    });
}

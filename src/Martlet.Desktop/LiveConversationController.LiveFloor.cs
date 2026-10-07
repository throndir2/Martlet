using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Participation;
using Martlet.Providers;

namespace Martlet.Desktop;

// The live floor (docs/CONVERSATION.md, Live floor): the live turn comes first. What the microphone hears and the replies feed
// it; the Thinking pool's broker follows it (LiveFloorRules), the paired hosts that serve the live routes keep their graphics
// cards for it (ILiveGpuHold), and live-floor.json shows it to MCP's live_floor_status.
internal sealed partial class LiveConversationController
{
    /// <summary>live-floor.json in the data directory: the floor's level, what the conversation runs on, which pool members
    /// share it, what the floor held and stopped by kind and its last changes (never what was said), which MCP's
    /// live_floor_status reads.</summary>
    internal const string LiveFloorFile = "live-floor.json";
    /// <summary>How long one hold on a paired host's graphics cards lasts, and how often it is renewed while the floor is Live.</summary>
    internal static TimeSpan HoldTime => TimeSpan.FromSeconds(10);
    internal static TimeSpan HoldRenewal => TimeSpan.FromSeconds(5);
    private LiveFloor floor = null!;
    private LiveFloorRules floorRules = null!;
    private ILiveGpuHold gpuHold = NoLiveGpuHold.Instance;
    private CancellationTokenSource? holding;
    private readonly object floorFile = new();
    private string? holdProblem;
    private string[] heldHosts = [];

    /// <summary>The live floor: whether the conversation needs its hardware now (Idle, Listening or Live).</summary>
    internal LiveFloor LiveFloor => floor;

    /// <summary>The live floor's rules on the Thinking pool's broker: what they held and stopped.</summary>
    internal LiveFloorRules LiveFloorRules => floorRules;

    /// <summary>Asks the paired hosts that serve the live routes to keep those graphics cards free of pool work while the floor
    /// is Live (the gateway's signed client); <see cref="NoLiveGpuHold"/> until a host supports it.</summary>
    internal ILiveGpuHold GpuHold
    {
        get => Volatile.Read(ref gpuHold);
        set => Volatile.Write(ref gpuHold, value ?? NoLiveGpuHold.Instance);
    }

    /// <summary>The reply latency line's part about the live floor this turn ("held 2 pool jobs, stopped 1 (think longer)"), or
    /// null when it held and stopped nothing.</summary>
    internal string? LiveFloorNote => floorRules.Period.Describe();

    // Made in the constructor, before anything can post background work.
    private void StartLiveFloor()
    {
        floor = new LiveFloor(clock);
        floorRules = new LiveFloorRules(floor).Attach(jobs.Places);
        // The paired hosts' signed hold client; a controller without a data directory (tests) asks no host.
        gpuHold = dataDirectory is null ? NoLiveGpuHold.Instance : LiveGpuHolds.Shared;
        floor.Changed += FloorChanged;
        floorRules.StoppedWork += lease => Task.Run(() =>
        {
            ErrorLog.Info($"Live floor: stopped {lease.Holder} on {lease.Place.Name} for the conversation" +
                (lease.Kind == ThinkingJobKind.Digest ? "; its summary is dropped." : "; it goes on later."));
            WriteFloorStatus();
        });
    }

    // What the conversation runs on now (its Thinking, voice and listening routes), for the floor's rules.
    private void UseLiveResources(LiveConversationConfiguration? configured) =>
        floorRules.Resources = configured is null ? LiveResources.None : LiveResources.For(configured.Routes, HostRouteGpus.For);

    private void FloorChanged(LiveFloorChange change)
    {
        // The rules already acted (LiveFloorRules); what follows never runs on the caller's thread, which may be the
        // microphone loop or the reply's own path.
        var did = change.To == LiveFloorLevel.Idle ? floorRules.Period.Describe() : null;
        if (change.To == LiveFloorLevel.Live) HoldGpus();
        else if (change.To == LiveFloorLevel.Idle) ReleaseGpus();
        Task.Run(() =>
        {
            // A new turn: what the conversation runs on, with the graphics cards the hosts said serve it since (when they say).
            if (change.From == LiveFloorLevel.Idle) UseLiveResources(Configuration);
            ErrorLog.Info($"Live floor: {change.To} ({change.Why})" + (did is null ? "." : $"; this turn it {did}."));
            WriteFloorStatus();
        });
    }

    /// <summary>A reply to the user (what they said or typed, a touch, a message from a paired chat, people in your Discord call)
    /// holds the floor until its voice is made or it stops; Martlet's own reports and replies to what this PC played don't. A
    /// reply started early holds it from its start, before the turn ends (let go, it ends its hold at once).</summary>
    private void BeginFloorReply(LiveConversationOperation operation)
    {
        if (operation.Report || operation.PcAudio && operation.UserWords is null && !operation.DiscordCall || operation.FloorReply is not null ||
            operation.Early is { LetGo: true })
            return;
        operation.FloorReply = floor.BeginReply(operation.Early is not null ? "a reply started early"
            : operation.Touch ? "a reply to a touch started" : operation.Remote ? "a reply to a message started"
            : operation.Spoken || operation.Authorization.Microphone ? "a reply to what you said started"
            : operation.PcAudio ? "a reply in your Discord call started" : "a reply to what you typed started");
    }

    // The reply's voice is made (only playback is left): the floor may drop after its grace.
    private static void EndFloorReplyWhenMade(LiveConversationOperation operation, ConversationTurn turn)
    {
        if (operation.FloorReply is not { } held) return;
        turn.Synthesized.ContinueWith(_ => held.End("the reply's voice was made"), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // A request on that place's paired computer ended because the computer kept its graphics card for a live turn (its own
    // companion PC's or another's) since it began: the think goes on later, never a failure.
    private static bool HeldOnHost(DeepThinkingPool pool, BackgroundPlace at, long began) =>
        pool.Find(at.Id)?.Settings is { Place: DeepThinkingPlace.Host, HostId: { } host } && HostLiveHolds.Since(host, began);

    // Real words in what the user said (or Martlet's name) make the floor Live, when Martlet answers them (Martlet.Participation:
    // always with the default policy; in a group mode only when it is addressed, else once its reply starts).
    private void FloorWords(LiveConversationOperation operation, string? text, UtteranceContext context, ListeningSensitivity sensitivity,
        string why)
    {
        if (!LiveFloor.RealWords(text, context, sensitivity)) return;
        var mode = policy.Configuration.Mode;
        if (mode == ParticipationMode.Disabled) return;
        if (mode is ParticipationMode.NameAddressed or ParticipationMode.Conversational &&
            !UtteranceFilter.Addressed(UtteranceFilter.Words(UtteranceFilter.WithoutSounds(text!, out _)), context.Names)) return;
        floor.Words(why);
    }

    // A quick transcript of what is being said (the end-of-turn judge's) with real words makes the floor Live before the turn ends.
    private void FloorWords(LiveConversationOperation operation, Task<LocalTranscript> transcript)
    {
        var sensitivity = operation.Listening?.WordCheck ?? ListeningSensitivity.Normal;
        transcript.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully)
                FloorWords(operation, done.Result.Text, WordsContext(operation, null, done.Result.Evidence), sensitivity,
                    "a quick transcript had real words");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Whether the participation policy turning down what was heard means Martlet won't answer it at all (rather than
    /// answering once it is free), so the user's words no longer hold the live floor.</summary>
    private static bool Dismisses(PolicyReason reason) =>
        reason is not (PolicyReason.Busy or PolicyReason.InsufficientGap or PolicyReason.FinalTranscriptRequired or PolicyReason.FreshIntentRequired);
    // ---------- holds on the paired hosts' graphics cards ----------

    // Live: every paired host that serves a live route keeps those graphics cards free of pool work, renewed while Live.
    private void HoldGpus()
    {
        if (floorRules.Resources.Hosts.Count == 0 || Volatile.Read(ref holding) is not null) return;
        var stop = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref holding, stop, null) is not null)
        {
            stop.Dispose();
            return;
        }
        Task.Run(() => HoldAsync(stop.Token)).Forget();
    }

    private void ReleaseGpus() => _ = Interlocked.Exchange(ref holding, null)?.CancelAsync();

    private async Task HoldAsync(CancellationToken token)
    {
        var hold = GpuHold;
        HashSet<string> asked = new(StringComparer.Ordinal);
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Renewed only while Live; Listening lets the hold run out, Idle ends it. A host problem never throws here: the
                // live turn goes on without the hold.
                if (floor.Level == LiveFloorLevel.Live)
                    foreach (var (host, routes) in floorRules.Resources.Hosts)
                    {
                        try
                        {
                            await hold.HoldAsync(host, routes, HoldTime, token).ConfigureAwait(false);
                            if (asked.Add(host)) Volatile.Write(ref heldHosts, [.. asked]);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            var problem = $"{host} didn't take the hold ({error.GetType().Name})";
                            if (Interlocked.Exchange(ref holdProblem, problem) != problem)
                                ErrorLog.Info($"Live floor: {problem}; its pool work isn't held for the conversation.");
                        }
                    }
                await Task.Delay(HoldRenewal, clock, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Volatile.Write(ref heldHosts, []);
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            foreach (var host in asked)
            {
                try { await hold.ReleaseAsync(host, release.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
    }

    // ---------- live-floor.json ----------

    private void WriteFloorStatus()
    {
        if (dataDirectory is null || disposed) return;
        try
        {
            var resources = floorRules.Resources;
            var members = ThinkingPool.Board.Members;
            var turn = floorRules.Period;
            var total = floorRules.Total;
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, updated = clock.GetUtcNow(), level = floor.Level.ToString(), replies = floor.Replies, periods = floor.Periods,
                timing = new
                {
                    listeningHoldSeconds = floor.Timing.ListeningHold.TotalSeconds, wordsHoldSeconds = floor.Timing.WordsHold.TotalSeconds,
                    graceSeconds = floor.Timing.Grace.TotalSeconds
                },
                resources = resources.Items.Select(r => new { job = r.Job, machine = r.Machine ?? "cloud", gpus = r.Gpus, hostId = r.HostId, routeId = r.RouteId }),
                members = members.Select(m => new { id = m.Id, name = m.Name, machine = m.Machine ?? "cloud", gpus = m.Gpus, shares = resources.Shares(m) }),
                turn = new { held = turn.Held, stopped = turn.Stopped, line = turn.Describe() },
                total = new { held = total.Held, stopped = total.Stopped },
                holds = new
                {
                    client = GpuHold.GetType().Name, live = Volatile.Read(ref holding) is not null, hosts = resources.Hosts.Select(h => h.HostId),
                    asked = Volatile.Read(ref heldHosts),
                    // What the hosts granted (the signed client knows; older hosts grant nothing).
                    granted = GpuHold is HostLiveGpuHold signed
                        ? resources.Hosts.Select(h => new { hostId = h.HostId, until = signed.HeldUntil(h.HostId) }).Where(h => h.until is not null).ToArray()
                        : null,
                    problem = Volatile.Read(ref holdProblem)
                },
                workQueue = new { stoppedBackground = WorkQueue.Shared.Stopped },
                changes = floor.Recent.Select(c => new { at = c.At, from = c.From.ToString(), to = c.To.ToString(), why = c.Why })
            }, new JsonSerializerOptions { WriteIndented = true });
            lock (floorFile)
            {
                var path = Path.Combine(dataDirectory, LiveFloorFile);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}

/// <summary>The graphics cards each paired host said serve its routes (its route list's "gpus"), as this PC last read them, so
/// the live floor knows when a pool member shares a live route's graphics card on the same computer. Empty until the host says.</summary>
internal static class HostRouteGpus
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Known = new(StringComparer.Ordinal);

    internal static void Note(string hostId, IReadOnlyList<HostRoute> routes)
    {
        foreach (var route in routes) Known[hostId + "|" + route.RouteId] = route.Gpus;
    }

    internal static IReadOnlyList<string> For(string hostId, string routeId) =>
        Known.TryGetValue(hostId + "|" + routeId, out var gpus) ? gpus : [];

    /// <summary>A Thinking pool member's graphics cards, when its paired computer said.</summary>
    internal static IReadOnlyList<string> For(DeepThinkingSettings member) =>
        member.Place == DeepThinkingPlace.Host && member.HostId is { } host ? For(host, member.HostRoute) : [];
}

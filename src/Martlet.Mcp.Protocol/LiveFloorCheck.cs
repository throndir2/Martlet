using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>live_floor_status and live_floor_check: the live floor (docs/CONVERSATION.md, Live floor), which puts the live
/// conversation turn before all background work. The status reads a data directory's routes and Thinking pool (what the
/// conversation runs on, which pool members share it and what the floor does to each kind there) and the desktop's
/// live-floor.json (its level, what it held and stopped by kind, its last changes, the hosts it holds; never what was said).
/// The check drives the production floor, rules, job board, background jobs and work queue with fixture inputs and simulated
/// members (NOT models) and reports each decision. Nothing leaves the process.</summary>
internal static class LiveFloorCheck
{
    internal const string StatusFile = "live-floor.json";

    // ---------- live_floor_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var routes = loaded.Settings?.Setup?.Routes ?? [];
        var resources = LiveResources.For(routes);
        var (pool, state) = ThinkingPoolSettings.Read(dataDirectory, save: false);
        var plan = pool.Plan(routes);
        var places = ThinkLonger.Places(plan);
        return new
        {
            pool = state,
            resources = resources.Items.Select(r => new { job = r.Job, machine = r.Machine ?? "cloud", gpus = r.Gpus, hostId = r.HostId, routeId = r.RouteId }),
            holds = resources.Hosts.Select(h => new { hostId = h.HostId, routes = h.Routes }),
            members = places.Select(p => new
            {
                id = p.Id, name = p.Name, machine = p.Machine ?? "cloud", shares = resources.Shares(p),
                conversationModel = p.Id == LiveResources.ConversationModel
            }),
            rules = Rules(),
            desktop = Desktop(dataDirectory)
        };
    }

    /// <summary>What the floor does to each kind on a member that shares the conversation's hardware, at each level.</summary>
    internal static object Rules() => ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind => new
    {
        listening = LiveFloorRules.ServesTheTurn(kind) ? "starts (prefers a member that shares nothing)" : "waits for the conversation",
        live = LiveFloorRules.ServesTheTurn(kind) ? "starts (prefers a member that shares nothing)"
            : !LiveFloorRules.Stops(kind) ? "waits; running work goes on"
            : kind == ThinkingJobKind.Digest ? "waits; running work stops and its result is dropped"
            : kind is ThinkingJobKind.ThinkLonger or ThinkingJobKind.Research ? "waits; running work stops and goes on later from what it wrote"
            : "waits; running work stops and waits in line again"
    });

    private static object Desktop(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, StatusFile);
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't run a conversation with this data directory." };
            if (new FileInfo(path).Length > 262_144) return new { state = "unreadable", why = "live-floor.json is too large." };
            return new { state = "loaded", file = JsonNode.Parse(File.ReadAllText(path)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
    }

    // ---------- live_floor_check ----------

    private sealed record Step(string Name, bool Passed, string Detail);

    /// <summary>A clock that moves only when told, with timers, like the desktop's own on a long wait.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch.AddTicks(ticks); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (gate)
            {
                ticks += by.Ticks;
                due = [.. timers.Where(t => t.Due <= ticks)];
                foreach (var timer in due) timer.Due = long.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                    if (!owner.timers.Contains(this)) owner.timers.Add(this);
                }
                return true;
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (owner.gate) owner.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static readonly string[] Fixtures = ["Mmm.", "Yeah, right.", "Haha.", "What time is it in Tokyo?", "Okay Martlet.", "Hey, stop for a second."];

    internal static async Task<object> RunAsync(IReadOnlyList<string>? said, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        List<Step> steps = [];
        void Check(string name, bool passed, string detail) => steps.Add(new(name, passed, detail));
        static ThinkingJob Job(ThinkingJobKind kind, TimeSpan? timeout = null, bool stale = false) =>
            new() { Kind = kind, Instructions = "Answer in one word.", Text = "fixture", Timeout = timeout ?? TimeSpan.FromSeconds(30), DropWhenStale = stale };
        BackgroundPlace shared = new("endpoint:local", "this PC") { Machine = LiveResources.ThisPc, Slots = 6 },
            beside = new("host:other", "other") { Machine = "lan:192.168.1.50", Slots = 2 };
        var thinkingHere = new LiveResources([new("thinking", LiveResources.ThisPc, [])]);

        // 1. What the user says: real words make the floor Live; sounds, filler and backchannel words don't.
        var context = new UtteranceContext { Voiced = TimeSpan.FromSeconds(1), Speech = TimeSpan.FromSeconds(2) };
        var words = (said is { Count: > 0 } ? said : Fixtures).Select(text => new
        {
            said = text, realWords = LiveFloor.RealWords(text, context, ListeningSensitivity.Normal)
        }).ToArray();
        if (said is not { Count: > 0 })
            Check("words: only real words or Martlet's name go Live",
                words.Select(w => w.realWords).SequenceEqual([false, false, false, true, true, true]),
                string.Join("; ", words.Select(w => $"\"{w.said}\" {(w.realWords ? "Live" : "not words")}")));

        // 2. The levels on a clock of their own: Listening for the voice, Idle again after quiet or a sound, Live for words and
        //    for the reply until its voice is made, then a short grace.
        {
            var clock = new ManualClock();
            using var floor = new LiveFloor(clock);
            List<string> seen = [];
            floor.Changed += change => seen.Add($"{change.To} ({change.Why})");
            floor.Heard();
            var listening = floor.Level;
            clock.Advance(TimeSpan.FromSeconds(6.1));
            var quiet = floor.Level;
            floor.Heard();
            floor.NotWords("a sound, not words");
            var sound = floor.Level;
            floor.Heard();
            floor.Words();
            var live = floor.Level;
            var reply = floor.BeginReply("a reply to what you said started");
            clock.Advance(TimeSpan.FromSeconds(20));
            var during = floor.Level;
            reply.End();
            clock.Advance(TimeSpan.FromSeconds(1.9));
            var grace = floor.Level;
            clock.Advance(TimeSpan.FromSeconds(0.2));
            Check("levels: voice, quiet, a sound, words, a reply and its grace",
                (listening, quiet, sound, live, during, grace, floor.Level) ==
                (LiveFloorLevel.Listening, LiveFloorLevel.Idle, LiveFloorLevel.Idle, LiveFloorLevel.Live, LiveFloorLevel.Live, LiveFloorLevel.Live, LiveFloorLevel.Idle),
                string.Join(" > ", seen));
        }

        // 3. The board while Listening: no new work on a member that shares the conversation; running work goes on; judges run.
        {
            using var floor = new LiveFloor();
            var places = new BackgroundPlaces();
            var rules = new LiveFloorRules(floor, thinkingHere).Attach(places);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var board = new ThinkingJobBoard(places, () => [shared], async (m, job, token) =>
            {
                if (job.Kind == ThinkingJobKind.Research) await release.Task.WaitAsync(token);
                return ThinkingAnswer.Done(m.Name);
            });
            var research = board.RunAsync(Job(ThinkingJobKind.Research), cancellation);
            await WaitAsync(() => places.Leases.Count == 1, cancellation);
            floor.Heard();
            var memory = board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            await WaitAsync(() => places.HeldKinds.Count == 1, cancellation);
            var judge = await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge), cancellation);
            var status = board.Status();
            Check("listening: new work waits for the conversation, running work and judges go on",
                !memory.IsCompleted && !research.IsCompleted && judge.Succeeded && status.Held.GetValueOrDefault("memory") == 1 &&
                places.Leases.All(l => !l.StopRequested),
                $"floor {status.Floor}; waiting for the conversation {Json(status.Held)}; research running: {!research.IsCompleted}; judge {judge.Outcome}");
            floor.NotWords();
            release.TrySetResult();
            var done = await Task.WhenAll(memory, research);
            Check("listening: the held work runs once the floor is Idle", done.All(r => r.Succeeded),
                $"memory {done[0].Outcome}, research {done[1].Outcome}; held in all {Json(rules.Total.Held)}");
        }

        // 4. The board while Live: a summary stops and is dropped, remembering and naming stop and wait in line again, finding touch
        //    zones goes on; new touch zones work waits; the judges run.
        {
            using var floor = new LiveFloor();
            var places = new BackgroundPlaces();
            var rules = new LiveFloorRules(floor, thinkingHere).Attach(places);
            var zonesGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new Dictionary<ThinkingJobKind, int>();
            var board = new ThinkingJobBoard(places, () => [shared], async (m, job, token) =>
            {
                int call;
                lock (calls) call = calls[job.Kind] = calls.GetValueOrDefault(job.Kind) + 1;
                if (call == 1 && job.Kind is ThinkingJobKind.Digest or ThinkingJobKind.Memory or ThinkingJobKind.Naming)
                    await Task.Delay(Timeout.Infinite, token);
                if (call == 1 && job.Kind == ThinkingJobKind.TouchZones) await zonesGate.Task.WaitAsync(token);
                return ThinkingAnswer.Done(m.Name);
            });
            var digest = board.RunAsync(Job(ThinkingJobKind.Digest, stale: true), cancellation);
            var memory = board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var naming = board.RunAsync(Job(ThinkingJobKind.Naming), cancellation);
            var zones = board.RunAsync(Job(ThinkingJobKind.TouchZones), cancellation);
            await WaitAsync(() => places.Leases.Count == 4, cancellation);
            floor.Words();
            var dropped = await digest;
            await WaitAsync(() => places.HeldKinds.Count == 2, cancellation);
            var later = board.RunAsync(Job(ThinkingJobKind.TouchZones), cancellation);
            var judge = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge), cancellation);
            await WaitAsync(() => places.HeldKinds.Count == 3, cancellation);
            var status = board.Status();
            Check("live: a summary is dropped, remembering and naming stop, touch zones go on, judges run",
                dropped.Outcome == ThinkingJobOutcome.Preempted && !zones.IsCompleted && judge.Succeeded &&
                status.StoppedNow.Keys.Order().SequenceEqual(["digest", "memory", "naming"]) && status.Held.GetValueOrDefault("touch-zones") == 1,
                $"digest {dropped.Outcome}; stopped {Json(status.StoppedNow)}; waiting for the conversation {Json(status.Held)}; " +
                $"judge {judge.Outcome}; reply latency part \"{rules.Period.Describe()}\"");
            floor.Clear();
            zonesGate.TrySetResult();
            var done = await Task.WhenAll(memory, naming, later, zones);
            Check("live: stopped and held work goes on once the floor is Idle", done.All(r => r.Succeeded) && done[0].Preemptions == 1,
                $"memory {done[0].Outcome} after {done[0].Preemptions} stop; naming {done[1].Outcome}; touch zones {done[2].Outcome} and {done[3].Outcome}");
        }

        // 5. A member that shares nothing is never held and goes first while you talk.
        {
            using var floor = new LiveFloor();
            var places = new BackgroundPlaces();
            new LiveFloorRules(floor, thinkingHere).Attach(places);
            var board = new ThinkingJobBoard(places, () => [shared, beside], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            floor.Words();
            var memory = await board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var digestBeside = board.CanRunBeside(ThinkingJobKind.Digest);
            Check("live: a member on another computer takes the work at once (the screen summary when you start to speak too)",
                memory.Member == "other" && board.Find(ThinkingJobKind.Digest)?.Id == "host:other" && digestBeside,
                $"memory on {memory.Member}; a summary would go to {board.Find(ThinkingJobKind.Digest)?.Name}");
        }

        // 6. think_longer and research (background jobs): stopped by Live, kept, waiting for the conversation, then going on.
        {
            using var floor = new LiveFloor();
            using var jobs = new BackgroundJobs();
            new LiveFloorRules(floor, thinkingHere).Attach(jobs.Places);
            var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = jobs.Start(ThinkLonger.Kind(new()), "FIXTURE - NOT AI task", async (job, token) =>
            {
                var partial = "";
                while (true)
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, job.Stopping);
                    try
                    {
                        if (partial.Length == 0)
                        {
                            partial = "First half. ";
                            wrote.TrySetResult();
                            await Task.Delay(Timeout.Infinite, attempt.Token);
                        }
                        return new ThinkResume(partial, true).Combine(BackgroundJobOutcome.Done("Second half."));
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && job.Stopping.IsCancellationRequested)
                    {
                        await jobs.ReseatAsync(job, token);
                    }
                }
            }, [shared], wait: true);
            var job = start.Job!;
            await wrote.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            var reply = floor.BeginReply();
            await WaitAsync(() => job.State == BackgroundJobState.Paused, cancellation);
            var paused = $"{job.State}: {job.Progress}";
            var research = jobs.Start(WebResearch.Kind, "FIXTURE - NOT AI topic", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("report")), [shared]);
            await WaitAsync(() => research.Job?.Progress == BackgroundJob.WaitingForConversation, cancellation);
            reply.End();
            floor.Clear();
            await WaitAsync(() => job.Finished && research.Job!.Finished, cancellation);
            var input = ThinkLonger.Input(null, null, "FIXTURE task", null, null, "Persona", new ThinkResume("First half. ", true));
            var again = ThinkLonger.Input(null, null, "FIXTURE task", null, null, "Persona", new ThinkResume("First half. ", false));
            Check("think longer and research: stopped, waiting for the conversation, then on from what it wrote",
                paused == $"Paused: {BackgroundJob.WaitingForConversation}" && job.Result == "First half. Second half." && job.Preemptions == 1 &&
                research.ForConversation && research.Job!.Result == "report" && input.Continuation == "First half. " &&
                again.Continuation is null && again.UserText.Contains("You started on this before", StringComparison.Ordinal),
                $"{paused}; result \"{job.Result}\" after {job.Preemptions} stop; research started {(research.ForConversation ? "waiting for the conversation" : "at once")}; " +
                $"in place the request ends with the unfinished assistant message \"{input.Continuation}\"; elsewhere it starts again with it as context");
        }

        // 7. The fallbacks on the live route: the conversation model's own place never starts work while the floor is above Idle.
        {
            using var floor = new LiveFloor();
            var rules = new LiveFloorRules(floor, LiveResources.None);
            var model = new BackgroundPlace(LiveResources.ConversationModel, "the conversation model") { Slots = 4 };
            var idle = rules.MayStart(model, ThinkingJobKind.ThinkLonger);
            floor.Heard();
            var listening = rules.MayStart(model, ThinkingJobKind.ThinkLonger);
            floor.Words();
            var stops = rules.MustStop(model, ThinkingJobKind.ThinkLonger) && rules.MustStop(model, ThinkingJobKind.Research);
            Check("fallbacks: thinking longer and research on the conversation model wait above Idle and stop when Live",
                idle && !listening && stops, $"Idle may start {idle}; Listening may start {listening}; Live stops {stops}");
        }

        // 8. The work queue: a live request stops this PC's own background request on the same computer instead of waiting.
        {
            var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
            var host = new SimulatedOllama("diva");
            var background = Collect(queue, "remembering", WorkPriority.Background, host);
            await WaitAsync(() => queue.Background("thinking", "diva") == 1, cancellation);
            var timer = Stopwatch.StartNew();
            var live = await Collect(queue, "reply", WorkPriority.Live, host);
            var took = timer.ElapsedMilliseconds;
            var preempted = false;
            try { await background; }
            catch (WorkPreemptedException) { preempted = true; }
            host.HoldForLive = true;
            var heldOff = false;
            try { await Collect(queue, "naming", WorkPriority.Background, host); }
            catch (WorkPreemptedException) { heldOff = true; }
            Check("work queue: the live reply never waits behind this PC's own background request",
                live == "reply by diva" && preempted && heldOff && queue.Stopped == 1,
                $"the reply took the computer after {took} ms (the background request was stopped: {preempted}); " +
                $"background work a host holds off for a live turn goes on later: {heldOff}");
        }

        return new
        {
            passed = steps.All(s => s.Passed), elapsedMs = watch.ElapsedMilliseconds,
            words,
            steps = steps.Select(s => new { name = s.Name, passed = s.Passed, detail = s.Detail }),
            rules = Rules(),
            note = "In-process rehearsal of the production live floor, rules, job board, background jobs and work queue with fixture inputs and simulated members (NOT models)."
        };
    }

    private sealed class Busy : Exception;
    private sealed class Held : Exception;

    /// <summary>A computer's Ollama: one request at a time; a background request works until it is stopped.</summary>
    private sealed class SimulatedOllama(string id)
    {
        private int running;
        public string Id { get; } = id;
        public bool HoldForLive { get; set; }

        public async IAsyncEnumerable<string> Run(string request, bool background,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            if (background && HoldForLive) throw new Held();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                yield return $"{request} started";
                await Task.Delay(background ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(20), token);
                yield return $"{request} by {Id}";
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    private static async Task<string> Collect(WorkQueue queue, string request, WorkPriority priority, SimulatedOllama host)
    {
        var last = "";
        await foreach (var answer in queue.StreamAsync("thinking", [host], h => h.Id, (h, t) => h.Run(request, priority == WorkPriority.Background, t),
            error => error switch { Busy => WorkRefusal.Busy, Held => WorkRefusal.Preempted, _ => WorkRefusal.None },
            DateTimeOffset.UtcNow.AddSeconds(10), null, CancellationToken.None, priority))
            last = answer;
        return last;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task WaitAsync(Func<bool> done, CancellationToken token)
    {
        for (var i = 0; i < 500 && !done(); i++) await Task.Delay(10, token);
    }
}

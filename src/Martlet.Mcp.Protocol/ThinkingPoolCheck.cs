using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>thinking_pool_status and thinking_pool_check: Companion › Thinking pool. The status reads a data directory's pool
/// (thinking-pool.json, or what Martlet would make from deep-thinking.json, without writing it), each member's slots and what it
/// can do, the plan (whether each member can run and why), the likely-slowdown warnings and guidance, and the desktop's
/// thinking-pool-status.json (running and waiting jobs by kind, never a job's text). The check rehearses the production job board
/// (<see cref="ThinkingJobBoard"/> over <see cref="BackgroundPlaces"/>) with simulated members, NOT models: no member, capability
/// matching, the slot kept free for fast jobs, priorities, retry on another member, a stale job dropped, and the migration from
/// deep-thinking.json. Nothing leaves the process.</summary>
internal static class ThinkingPoolCheck
{
    // ---------- thinking_pool_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var routes = loaded.Settings?.Setup?.Routes ?? [];
        var (pool, state) = ThinkingPoolSettings.Read(dataDirectory, save: false);
        var abilities = ModelAbilities.Load(dataDirectory);
        var plan = pool.Plan(routes);
        var places = ThinkLonger.Places(plan, null, member => ThinkingPoolCapabilities.For(member, abilities)).Where(p => p.Id != "thinking").ToArray();
        var members = places.Select(p => new ThinkingPoolMemberStatus(p.Id, p.Name, p.Slots, 0, p.Can, p.Rank)).ToArray();
        var slots = members.Sum(m => m.Slots);
        return new
        {
            file = state,
            useConversationModelWhenEmpty = pool.UseConversationModelWhenEmpty,
            // The paired computers the owner keeps out (host IDs only): they never join the pool by themselves.
            leftByOwner = pool.LeftByOwner,
            migratedAt = pool.MigratedAt,
            members = pool.Members.Select(m =>
            {
                var spot = plan.Find(m.Key);
                var can = ThinkingPoolCapabilities.For(m, abilities);
                return new
                {
                    key = m.Key, where = m.Describe(), place = m.Place.ToString(), model = m.ModelId, hostId = m.HostId,
                    hostRole = m.OnHostRole, slots = m.ThinksAtOnce, ownKey = m.CredentialId is not null,
                    text = true, vision = can.HasFlag(ThinkingCapability.Vision), audio = can.HasFlag(ThinkingCapability.Audio),
                    available = spot?.Plan.Available ?? false, rank = spot?.Plan.Rank, why = spot?.Plan.Why,
                    // May answer for the conversation (Backup Thinking), off by default; a paid cloud member only when ticked.
                    answersForConversation = pool.Answers(m.Key), paid = ThinkingBackupMembers.Paid(m)
                };
            }),
            // Backup Thinking: its choices and who it would ask now for a plain reply that is taken (the production choice, on
            // what the conversation's routes run on).
            backup = new
            {
                on = pool.BackupThinking, delayMs = pool.BackupDelayMs, automaticDelay = pool.BackupDelayMs is null,
                minimumAutomaticDelayMs = FirstWordTimes.Minimum.TotalMilliseconds, startingDelayMs = FirstWordTimes.Starting.TotalMilliseconds,
                wouldAsk = ThinkingBackupMembers.Choose(pool, plan, places, LiveResources.For(routes), new BoundedTextInput("status"), held: false) is var choice
                    ? new { member = choice.Spot?.Settings.Describe(), why = choice.Why } : null
            },
            usable = places.Length, slots, keepsFastSlot = slots >= 2,
            conversationModel = pool.Members.Count == 0 && pool.UseConversationModelWhenEmpty
                ? new { used = true, available = plan.Plan.Available, why = plan.Plan.Why } : null,
            canRun = ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind => new
            {
                text = members.Length > 0, vision = members.Any(m => m.Can.HasFlag(ThinkingCapability.Vision)),
                audio = members.Any(m => m.Can.HasFlag(ThinkingCapability.Audio)),
                priority = (int)ThinkingJobKinds.Priority(kind), fast = ThinkingJobKinds.IsFast(kind)
            }),
            guidance = ThinkingJobBoard.Guidance(members),
            warnings = ThinkingPoolWarnings.For(plan, routes),
            desktop = DesktopStatus(dataDirectory)
        };
    }

    private static object DesktopStatus(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "thinking-pool-status.json");
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't run a conversation with this data directory." };
            if (new FileInfo(path).Length > 262_144) return new { state = "unreadable", why = "thinking-pool-status.json is too large." };
            return new { state = "loaded", file = JsonNode.Parse(File.ReadAllText(path)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
    }

    // ---------- thinking_pool_check ----------

    private sealed record Step(string Name, bool Passed, string Detail);

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        List<Step> steps = [];
        void Check(string name, bool passed, string detail) => steps.Add(new(name, passed, detail));
        static ThinkingJob Job(ThinkingJobKind kind, ThinkingCapability? needs = null, TimeSpan? timeout = null, bool stale = false) =>
            new() { Kind = kind, Instructions = "Answer in one word.", Text = "fixture", Needs = needs, Timeout = timeout ?? TimeSpan.FromSeconds(10), DropWhenStale = stale };

        // 1. An empty pool answers "no member" at once.
        {
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [], (_, _, _) => Task.FromResult(ThinkingAnswer.Done("x")));
            await board.RunAsync(Job(ThinkingJobKind.Digest), cancellation);
            // Timed on the second call, so loading the code the first time doesn't count.
            var timer = Stopwatch.StartNew();
            var result = await board.RunAsync(Job(ThinkingJobKind.Digest), cancellation);
            Check("empty pool: no member at once", !board.CanRun(ThinkingJobKind.Digest) && result.Outcome == ThinkingJobOutcome.NoMember &&
                timer.ElapsedMilliseconds < 1000, $"{result.Outcome} after {timer.ElapsedMilliseconds} ms: {result.Problem}");
        }

        // 2. Capabilities: a picture goes to the member that sees; a recording finds no member.
        {
            BackgroundPlace text = new("host:text-box", "text-box") { Slots = 1 }, eyes = new("endpoint:eyes", "eyes") { Slots = 1, Can = ThinkingCapability.Text | ThinkingCapability.Vision };
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [text, eyes], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var vision = await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision), cancellation);
            var audio = await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Audio), cancellation);
            Check("capabilities: vision to the member that sees, audio no member",
                vision.Member == "eyes" && audio.Outcome == ThinkingJobOutcome.NoMember && board.CanRun(ThinkingJobKind.Digest, ThinkingCapability.Vision) &&
                !board.CanRun(ThinkingJobKind.Digest, ThinkingCapability.Audio), $"vision on {vision.Member}; audio {audio.Outcome}");
        }

        // 3. Two slots: a long job may not take the last free slot; a fast job takes it at once.
        {
            var places = new BackgroundPlaces();
            BackgroundPlace member = new("host:gpu", "gpu") { Slots = 2 };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var board = new ThinkingJobBoard(places, () => [member], async (m, job, token) =>
            {
                if (job.Kind == ThinkingJobKind.ThinkLonger) await release.Task.WaitAsync(token);
                return ThinkingAnswer.Done(m.Name);
            });
            var first = board.RunAsync(Job(ThinkingJobKind.ThinkLonger), cancellation);
            await WaitAsync(() => places.Leases.Count == 1, cancellation);
            var second = board.RunAsync(Job(ThinkingJobKind.ThinkLonger), cancellation);
            await WaitAsync(() => places.WaitingKinds.Count == 1, cancellation);
            var status = board.Status();
            var fast = await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge), cancellation);
            Check("fast slot: the second long job waits, a judge takes the last slot",
                fast.Succeeded && !second.IsCompleted && status.KeepsFastSlot && status.Running.GetValueOrDefault("think-longer") == 1 &&
                status.Waiting.GetValueOrDefault("think-longer") == 1,
                $"judge {fast.Outcome}; second think waiting: {!second.IsCompleted}; running {Json(status.Running)}, waiting {Json(status.Waiting)}");
            release.TrySetResult();
            var done = await Task.WhenAll(first, second);
            Check("fast slot: both long jobs finish once a slot frees", done.All(r => r.Succeeded), string.Join(", ", done.Select(r => r.Outcome)));
        }

        // 4. Priority: with one slot busy, the barge-in judge goes before the digest and research waiting before it.
        {
            var places = new BackgroundPlaces();
            BackgroundPlace member = new("host:one", "one") { Slots = 1 };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            List<ThinkingJobKind> order = [];
            var board = new ThinkingJobBoard(places, () => [member], async (m, job, token) =>
            {
                lock (order) order.Add(job.Kind);
                if (order.Count == 1) await release.Task.WaitAsync(token);
                return ThinkingAnswer.Done(m.Name);
            });
            var holding = board.RunAsync(Job(ThinkingJobKind.ThinkLonger), cancellation);
            await WaitAsync(() => places.Leases.Count == 1, cancellation);
            var research = board.RunAsync(Job(ThinkingJobKind.Research), cancellation);
            await WaitAsync(() => places.WaitingKinds.Count == 1, cancellation);
            var digest = board.RunAsync(Job(ThinkingJobKind.Digest), cancellation);
            await WaitAsync(() => places.WaitingKinds.Count == 2, cancellation);
            var judge = board.RunAsync(Job(ThinkingJobKind.BargeInJudge), cancellation);
            await WaitAsync(() => places.WaitingKinds.Count == 3, cancellation);
            var line = places.WaitingKinds.Select(ThinkingJobKinds.Name).ToArray();
            release.TrySetResult();
            await Task.WhenAll(holding, research, digest, judge);
            var ran = order.Select(ThinkingJobKinds.Name).ToArray();
            Check("priority: one slot, highest priority first", ran.SequenceEqual(["think-longer", "barge-in-judge", "digest", "research"]),
                $"line {string.Join(" > ", line)}; ran {string.Join(", ", ran)}; 1 slot: long jobs may take it");
            Check("guidance: 1 slot", ThinkingJobBoard.Guidance([new("host:one", "one", 1, 0, ThinkingCapability.Text, 0)])[0].StartsWith("1 slot:", StringComparison.Ordinal),
                ThinkingJobBoard.Guidance([new("host:one", "one", 1, 0, ThinkingCapability.Text, 0)])[0]);
        }

        // 5. A member that fails or is busy is passed over for the next.
        {
            BackgroundPlace busy = new("host:busy", "busy") { Slots = 1 }, ok = new("host:ok", "ok") { Slots = 1, Rank = 1 };
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [busy, ok], (m, _, _) =>
                Task.FromResult(m.Id == "host:busy" ? ThinkingAnswer.Failed("busy: job.busy") : ThinkingAnswer.Done(m.Name)));
            var result = await board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            Check("retry: a busy member is passed over", result.Member == "ok" && result.Attempts == 2, $"{result.Outcome} on {result.Member} after {result.Attempts} attempts");
        }

        // 6. A stale job is dropped when no member frees up in time.
        {
            var places = new BackgroundPlaces();
            BackgroundPlace member = new("host:one", "one") { Slots = 1 };
            var held = places.TryAcquire([member], "other-job")!;
            var board = new ThinkingJobBoard(places, () => [member], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var result = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge, timeout: TimeSpan.FromMilliseconds(300), stale: true), cancellation);
            held.Dispose();
            Check("stale: a judge nobody took in time is dropped", result.Outcome == ThinkingJobOutcome.Stale, $"{result.Outcome}: {result.Problem}");
        }

        // 7. deep-thinking.json becomes thinking-pool.json once (members kept; the conversation model is never a member).
        {
            var directory = Path.Combine(Path.GetTempPath(), "martlet-pool-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var host = new DeepThinkingSettings
                {
                    Place = DeepThinkingPlace.Host, ModelId = "qwen3:8b", HostId = "diva", HostOrigin = "https://192.168.1.2:9443",
                    HostSpkiFingerprint = "sha256/fixture", HostDeviceId = "desk-pc", HostCredentialId = Guid.NewGuid(),
                    HostRouteId = SelfHostSetup.DeepThinkingRouteId, Slots = 2
                };
                new DeepThinkingSettings().WithPool([host]).Save(directory);
                var (migrated, state) = ThinkingPoolSettings.Read(directory);
                var (again, againState) = ThinkingPoolSettings.Read(directory);
                Check("migration: deep-thinking.json read once into thinking-pool.json",
                    state == "migrated" && againState == "loaded" && again.Members.Count == 1 && again.Members[0].Key == "host:diva" &&
                    again.Members[0].Slots == 2 && again.UseConversationModelWhenEmpty,
                    $"{state} then {againState}; members {string.Join(", ", again.Members.Select(m => m.Describe()))}");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        // 8. Auto-join (the production rule, ThinkingPoolAutoJoin): a host with the Thinking pool role joins with its slots, an
        // Ollama-only host joins unless it does this PC's conversation Thinking, a member on Ollama moves to the role, and a
        // computer the owner took out, one Sharing work never uses and a full pool are skipped. Sample hosts only.
        {
            static ThinkingPoolHost Seen(string id, params ThinkingPoolOffer[] offers) =>
                new(id, $"https://{id}.local:9443", "sha256/fixture", "desk-pc", Guid.NewGuid(), offers);
            ThinkingPoolOffer role = new(SelfHostSetup.DeepThinkingRouteId, "qwen3:8b", 2), ollama = new(SelfHostSetup.OllamaRouteId, "gemma4:e4b");
            var now = DateTimeOffset.UtcNow;
            var joined = ThinkingPoolAutoJoin.For(new(), Seen("diva", role), null, now: now);
            var thinker = ThinkingPoolAutoJoin.For(new(), Seen("ripley", ollama), "ripley", now: now);
            var other = ThinkingPoolAutoJoin.For(new(), Seen("quiet", ollama), "ripley", now: now);
            var moved = ThinkingPoolAutoJoin.For(other.Pool, Seen("quiet", ollama, role), "ripley", now: now);
            var left = ThinkingPoolAutoJoin.For(joined.Pool.TakeOut("diva"), Seen("diva", role), null, now: now);
            var never = ThinkingPoolAutoJoin.For(new(), Seen("diva", role), null,
                new Martlet.Core.Cluster.WorkSharingSettings().With(new Martlet.Core.Cluster.WorkSharingJob { Job = Martlet.Core.Cluster.WorkSharingJobs.DeepThinking, Never = ["diva"] }), now: now);
            var full = Enumerable.Range(0, DeepThinkingSettings.MaxPlaces).Aggregate(new ThinkingPoolSettings(),
                (pool, i) => ThinkingPoolAutoJoin.For(pool, Seen("gpu-" + i, role), null, now: now).Pool);
            var ninth = ThinkingPoolAutoJoin.For(full, Seen("gpu-8", role), null, now: now);
            var hostPc = ThinkingPoolAutoJoin.For(new(), Seen("diva", role), null, hostPc: true, now: now);
            Check("auto-join: a host with the Thinking pool role joins with its slots",
                joined is { Change: ThinkingPoolHostChange.Joined, Member: { OnHostRole: true, Slots: 2 } } && joined.Pool.Members.Count == 1, joined.Why);
            Check("auto-join: an Ollama-only host joins unless it does this PC's Thinking",
                thinker.Change == ThinkingPoolHostChange.None && other is { Change: ThinkingPoolHostChange.Joined, Member.OnHostRole: false },
                $"ripley: {thinker.Why} quiet: {other.Why}");
            Check("auto-join: a member on Ollama moves to the Thinking pool role",
                moved is { Change: ThinkingPoolHostChange.MovedToRole, Member.OnHostRole: true } && moved.Pool.Members.Count == 1, moved.Why);
            Check("auto-join: kept out, never used, a full pool and a host PC are skipped",
                left.Change == ThinkingPoolHostChange.None && left.Pool.Left("diva") && left.Pool.Members.Count == 0 &&
                never.Change == ThinkingPoolHostChange.None && full.Members.Count == DeepThinkingSettings.MaxPlaces &&
                ninth.Change == ThinkingPoolHostChange.None && hostPc.Change == ThinkingPoolHostChange.None,
                $"{left.Why} {never.Why} {ninth.Why} {hostPc.Why}");
        }

        // 9. A member whose computer refused a request as invalid (a paired computer's gateway: request.invalid) rests: the board
        // gives it no job that needs as much for ThinkingJobBoard.RefusedRest, so it never gets request after request it refuses.
        {
            const ThinkingCapability Sees = ThinkingCapability.Text | ThinkingCapability.Vision;
            BackgroundPlace old = new("host:old", "old-host") { Slots = 1, Can = Sees };
            var asked = 0;
            ThinkingPoolRest? rested = null;
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [old], (_, job, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult(job.Required.HasFlag(ThinkingCapability.Vision)
                    ? ThinkingAnswer.Rejected("old-host refused the request as invalid (request.invalid)") : ThinkingAnswer.Done("noted"));
            });
            board.Rested += rest => rested = rest;
            var started = DateTimeOffset.UtcNow;
            var first = await board.RunAsync(Job(ThinkingJobKind.Digest, Sees), cancellation);
            var again = await board.RunAsync(Job(ThinkingJobKind.Digest, Sees), cancellation);
            var text = await board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var resting = board.Status().Resting;
            Check("refused as invalid: the member rests for such jobs and still takes the others",
                first.Outcome == ThinkingJobOutcome.Failed && again.Outcome == ThinkingJobOutcome.NoMember && text.Succeeded && asked == 2 &&
                !board.CanRun(ThinkingJobKind.Digest, Sees) && board.CanRun(ThinkingJobKind.Memory) && resting.Count == 1 &&
                rested is { Needs: Sees } rest && rest.Until - started >= ThinkingJobBoard.RefusedRest - TimeSpan.FromMinutes(1),
                $"first {first.Outcome} ({first.Problem}); next picture job {again.Outcome} ({again.Problem}) without a request; text job " +
                $"{text.Outcome}; {asked} requests in all; resting {string.Join(", ", resting.Select(r => $"{r.Name} for {ThinkingJobResult.Describe(r.Needs)} until {r.Until:HH:mm:ss}"))}");
        }

        return new
        {
            passed = steps.All(s => s.Passed), elapsedMs = watch.ElapsedMilliseconds,
            steps = steps.Select(s => new { name = s.Name, passed = s.Passed, detail = s.Detail }),
            note = "In-process rehearsal of the production job board with simulated members (NOT models)."
        };
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task WaitAsync(Func<bool> done, CancellationToken token)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10, token);
    }
}

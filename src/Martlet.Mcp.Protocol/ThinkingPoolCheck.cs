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
/// matching, the slot kept free for fast jobs, priorities, retry on another member, a stale job dropped, the migration from
/// deep-thinking.json, and on a one-slot member the stops for priority, the raise after stops and the retries after a failure.
/// Nothing leaves the process.</summary>
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
        var places = ThinkLonger.Places(plan, null, member => ThinkingPoolCapabilities.For(member, abilities), choices: pool)
            .Where(p => p.Id != "thinking").ToArray();
        var members = places.Select(p => new ThinkingPoolMemberStatus(p.Id, p.Name, p.Slots, 0, p.Can, p.Rank) { Media = p.Media }).ToArray();
        var slots = members.Sum(m => m.Slots);
        var (desktop, file) = DesktopStatus(dataDirectory);
        // Which members' computers answer now: only the desktop knows (its host checks), through thinking-pool-status.json.
        JsonNode? Seen(string key) => (file?["members"] as JsonArray)?.FirstOrDefault(m => m?["id"]?.GetValue<string>() == key);
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
                var seen = Seen(m.Key);
                return new
                {
                    key = m.Key, where = m.Describe(), place = m.Place.ToString(), model = m.ModelId, hostId = m.HostId,
                    hostRole = m.OnHostRole, slots = m.ThinksAtOnce, ownKey = m.CredentialId is not null,
                    // A paired computer's graphics card (1, or 2-4 for a Thinking pool model on an extra card, a member of its own)
                    // and the route it thinks on.
                    card = m.Place == DeepThinkingPlace.Host ? m.Card : (int?)null, route = m.Place == DeepThinkingPlace.Host ? m.HostRoute : null,
                    text = true, vision = can.HasFlag(ThinkingCapability.Vision), audio = can.HasFlag(ThinkingCapability.Audio),
                    tools = can.HasFlag(ThinkingCapability.Tools),
                    available = spot?.Plan.Available ?? false, rank = spot?.Plan.Rank, why = spot?.Plan.Why,
                    // Whether its computer answers now (null: the desktop hasn't said), and since when it doesn't.
                    online = seen?["online"]?.GetValue<bool>(), offlineSince = seen?["offlineSince"]?.GetValue<DateTimeOffset>(),
                    // Backup for slow replies (Backup Thinking), off by default; a paid cloud member only when ticked.
                    answersForConversation = pool.Answers(m.Key), paid = ThinkingBackupMembers.Paid(m),
                    // The Quick jobs and Long jobs boxes: the judges and summaries, and every other kind.
                    quickJobs = pool.TakesQuickJobs(m.Key), longJobs = pool.TakesLongJobs(m.Key),
                    // May receive pictures and recordings: an external member (an endpoint not on this PC) only when ticked.
                    external = ThinkingPoolSettings.IsExternal(m), mayReceiveMedia = pool.MayReceiveMedia(m),
                    // How smart its model is (Runs on uses it): the owner's choice, else Martlet's guess from the model name.
                    smarts = pool.SmartsOf(m).ToString(), smartsGuess = ThinkingSmartsGuess.From(m.ModelId).ToString(),
                    smartsChosen = pool.Smarts.ContainsKey(m.Key)
                };
            }),
            // Runs on for each kind of job, and the members each kind may use now (by name).
            runsOn = ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind =>
            {
                var where = pool.RunsOn(ThinkingJobKinds.Name(kind));
                return new
                {
                    mode = where.Name, members = where.Members,
                    may = places.Where(p => p.Takes(kind) && ThinkingRunsOnRules.Allows(p, where)).Select(p => p.Name).ToArray()
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
            // The line's rules from thinking-pool.json: stops for priority, the raise after stops and the retries after a failure.
            line = ThinkingPoolPolicy.From(pool) is var policy
                ? new { preemptLowerPriority = policy.PreemptLowerPriority, raiseAfterStops = policy.RaiseAfterStops, retries = policy.Retries } : null,
            usable = places.Length, slots, keepsFastSlot = slots >= 2,
            conversationModel = pool.Members.Count == 0 && pool.UseConversationModelWhenEmpty
                ? new { used = true, available = plan.Plan.Available, why = plan.Plan.Why } : null,
            // Per kind, only the members whose Quick jobs or Long jobs box lets them take it.
            canRun = ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind => new
            {
                text = places.Any(p => p.Takes(kind)), vision = places.Any(p => p.Takes(kind) && p.Media && p.Can.HasFlag(ThinkingCapability.Vision)),
                audio = places.Any(p => p.Takes(kind) && p.Media && p.Can.HasFlag(ThinkingCapability.Audio)),
                tools = places.Any(p => p.Takes(kind) && p.Can.HasFlag(ThinkingCapability.Tools)),
                priority = (int)ThinkingJobKinds.Priority(kind), fast = ThinkingJobKinds.IsFast(kind)
            }),
            guidance = ThinkingJobBoard.Guidance(members),
            warnings = ThinkingPoolWarnings.For(plan, routes),
            // The pool now, as the desktop last wrote it: slots of the members that answer, of every member, and who is offline.
            presence = file is null ? null : new
            {
                slots = file["slots"]?.GetValue<int>(), free = file["free"]?.GetValue<int>(), configuredSlots = file["configuredSlots"]?.GetValue<int>(),
                offline = (file["members"] as JsonArray)?.Where(m => m?["online"]?.GetValue<bool>() == false).Select(m => m!["name"]?.GetValue<string>()).ToArray() ?? [],
                conversationModelStandsIn = file["conversationModelStandsIn"]?.GetValue<bool>(), updated = file["updated"]?.GetValue<DateTimeOffset>(),
                // Members whose providers limit requests: in plain words, when each tries again and how many jobs it runs at once.
                limiting = (file["cooling"] as JsonArray)?.Select(c => c?["says"]?.GetValue<string>()).ToArray() ?? []
            },
            desktop
        };
    }

    private static (object Status, JsonNode? File) DesktopStatus(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "thinking-pool-status.json");
        try
        {
            if (!File.Exists(path)) return (new { state = "none", why = "The desktop hasn't run a conversation with this data directory." }, null);
            if (new FileInfo(path).Length > 262_144) return (new { state = "unreadable", why = "thinking-pool-status.json is too large." }, null);
            var file = JsonNode.Parse(File.ReadAllText(path));
            return (new { state = "loaded", file }, file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new { state = "unreadable", why = error.GetType().Name }, null);
        }
    }

    // ---------- thinking_requests ----------

    /// <summary>The states thinking_requests filters on: active (waiting, running or paused), done, problems (ended other than
    /// succeeded or canceled, or with a retry or a stop for the conversation) or one exact state.</summary>
    internal static readonly string[] RequestStates =
        ["active", "done", "problems", .. Enum.GetNames<ThinkingRequestState>().Select(name => name.ToLowerInvariant())];

    /// <summary>The job kinds thinking_requests filters on (their names in thinking-requests.json).</summary>
    internal static readonly string[] RequestKinds = [.. ThinkingJobKinds.All.Select(ThinkingJobKinds.Name)];

    internal const string RequestsFile = "thinking-requests.json";
    private const long RequestsFileLimit = 4 * 1024 * 1024;

    /// <summary>thinking_requests: the desktop's thinking-requests.json (every Thinking request kept, with its timings, and the
    /// totals by kind; never a request's text, answer or topic), filtered by state and kind, newest last-ended first.</summary>
    internal static object Requests(string dataDirectory, string? state, string? kind, int? limit)
    {
        state = state?.Trim().ToLowerInvariant();
        kind = kind?.Trim().ToLowerInvariant();
        if (state is not null && !RequestStates.Contains(state))
            throw new ArgumentException($"state must be one of {string.Join(", ", RequestStates)}.");
        if (kind is not null && !RequestKinds.Contains(kind))
            throw new ArgumentException($"kind must be one of {string.Join(", ", RequestKinds)}.");
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var path = Path.Combine(dataDirectory, RequestsFile);
        JsonNode? file;
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't written thinking-requests.json in this data directory (no request yet)." };
            if (new FileInfo(path).Length > RequestsFileLimit) return new { state = "unreadable", why = $"{RequestsFile} is larger than 4 MiB." };
            file = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
        if (file is not JsonObject root || root["requests"] is not JsonArray all)
            return new { state = "unreadable", why = $"{RequestsFile} has no requests list." };

        static string? Text(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        static int Number(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<int>(out var n) ? n : 0;
        bool Matches(JsonNode? request)
        {
            if (kind is not null && Text(request, "kind") != kind) return false;
            if (state is null) return true;
            var now = Text(request, "state")?.ToLowerInvariant();
            var active = now is "waiting" or "running" or "paused";
            return state switch
            {
                "active" => active,
                "done" => !active,
                "problems" => !active && now is not ("succeeded" or "canceled") || Number(request, "retries") > 0 || Number(request, "preemptions") > 0,
                _ => now == state
            };
        }
        var matched = all.Where(Matches).ToArray();
        return new
        {
            state = "loaded", file = path, schemaVersion = root["schemaVersion"]?.DeepClone(), updated = root["updated"]?.DeepClone(),
            active = root["active"]?.DeepClone(), kept = root["kept"]?.DeepClone(), totals = root["totals"]?.DeepClone(),
            filter = new { state, kind, limit = take }, matched = matched.Length, shown = Math.Min(take, matched.Length),
            requests = new JsonArray([.. matched.Take(take).Select(request => request?.DeepClone())]),
            note = "Each request's type, task, companion, member, tries and timings; never its text, answer or topic."
        };
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
            // Without stops for priority: the line alone decides (steps 12 to 15 stop the job that holds the slot).
            var board = new ThinkingJobBoard(places, () => [member], async (m, job, token) =>
            {
                lock (order) order.Add(job.Kind);
                if (order.Count == 1) await release.Task.WaitAsync(token);
                return ThinkingAnswer.Done(m.Name);
            }) { Policy = () => new(PreemptLowerPriority: false) };
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

        // 8b. One Thinking pool member per graphics card (the production rule): a host with a Thinking pool model on each of two
        // cards joins with two members (host:diva and host:diva#gpu2), each with its own slots; the second card's member leaves
        // when the host no longer runs a model there; taking the computer out removes both; the member on a card of its own
        // isn't warned about sharing Thinking's card, and the board places work there first. Sample hosts only, NOT models.
        {
            static ThinkingPoolHost Seen(string id, params ThinkingPoolOffer[] offers) =>
                new(id, $"https://{id}.local:9443", "sha256/fixture", "desk-pc", Guid.NewGuid(), offers);
            ThinkingPoolOffer card1 = new(SelfHostSetup.DeepThinkingRouteId, "qwen3:8b", 2), card2 = new(SelfHostSetup.DeepThinkingRouteIdFor(2), "qwen3:8b", 1);
            var now = DateTimeOffset.UtcNow;
            var both = ThinkingPoolAutoJoin.For(new(), Seen("diva", card1, card2), null, now: now);
            var keys = both.Pool.Members.Select(m => m.Key).ToArray();
            var again = ThinkingPoolAutoJoin.For(both.Pool, Seen("diva", card1, card2), null, now: now);
            var gone = ThinkingPoolAutoJoin.For(both.Pool, Seen("diva", card1), null, now: now);
            var later = ThinkingPoolAutoJoin.For(gone.Pool, Seen("diva", card1, card2), null, now: now);
            var takenOut = both.Pool.TakeOut("diva");
            Check("per GPU: a host with a model on each of two cards joins as two members with their own slots",
                both.Change == ThinkingPoolHostChange.Joined && keys.SequenceEqual(["host:diva", "host:diva#gpu2"]) &&
                both.Pool.Members.Select(m => m.ThinksAtOnce).SequenceEqual([2, 1]) && both.Pool.Members.All(m => m.OnHostRole) &&
                DeepThinkingSettings.HostOfKey("host:diva#gpu2") == "diva" && !again.Changed, both.Why);
            Check("per GPU: a card that stops running a model leaves, comes back by itself, and taking the computer out removes every card",
                gone is { Change: ThinkingPoolHostChange.CardRemoved } && gone.Pool.Members.Count == 1 &&
                later is { Change: ThinkingPoolHostChange.Joined, Member.Card: 2 } && takenOut.Members.Count == 0 && takenOut.Left("diva"),
                $"{gone.Why} {later.Why}");

            var thinking = new SetupRoute
            {
                RouteType = SetupRouteType.GatewayOllama, Role = SetupRole.Llm, ProviderAlias = SelfHostSetup.GatewayOllamaAlias,
                Origin = "https://diva.local:9443", ModelId = "gemma4-e4b", ConfigurationRevision = Guid.NewGuid(), Enabled = true,
                Gateway = new() { SchemaVersion = 1, HostId = "diva", Origin = "https://192.168.1.2:9443", SpkiFingerprint = "sha256:" + new string('0', 64), DeviceRole = SelfHostSetup.GatewayRole }
            };
            IReadOnlyList<string> Cards(string host, string route) => route == SelfHostSetup.DeepThinkingRouteIdFor(2) ? ["GPU-b"] : ["GPU-a"];
            var plan = both.Pool.Plan([thinking]);
            var warnings = ThinkingPoolWarnings.For(plan, [thinking], Cards);
            var ownCard = ThinkingPoolWarnings.OwnCard(both.Pool.Members[1], thinking, Cards);
            var sharesCard = ThinkingPoolWarnings.OwnCard(both.Pool.Members[0], thinking, Cards);
            Check("per GPU: only the member on Thinking's own card is warned about sharing it",
                ownCard && !sharesCard && warnings.Count(w => w.Contains("Thinking model", StringComparison.Ordinal)) == 1 &&
                warnings.Any(w => w.StartsWith("diva's Thinking pool (", StringComparison.Ordinal)),
                string.Join(" ", warnings));
        }
        // 9. Presence: a member's computer goes offline and answers again (the desktop's HostPresence feeds the broker's Reachable).
        {
            var thinkingRoute = new SetupRoute
            {
                RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias,
                Origin = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, ModelId = "x-ai/grok-4.3", ConfigurationRevision = Guid.NewGuid(), Enabled = true
            };
            SetupRoute[] routes = [thinkingRoute];
            static DeepThinkingSettings Role(string host, int slots) => new()
            {
                Place = DeepThinkingPlace.Host, ModelId = "qwen3:8b", HostId = host, HostOrigin = $"https://{host}.local:9443",
                HostSpkiFingerprint = "sha256/fixture", HostDeviceId = "desk-pc", HostCredentialId = Guid.NewGuid(),
                HostRouteId = SelfHostSetup.DeepThinkingRouteId, Slots = slots
            };
            var settings = new ThinkingPoolSettings().Add(Role("diva", 2)).Add(Role("ripley", 1));
            var offline = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            var places = new BackgroundPlaces { Reachable = place => DeepThinkingSettings.HostOfKey(place.Id) is not { } host || !offline.ContainsKey(host) };
            // The members the board reads: every configured member, whether its computer answers or not (as the desktop's PoolMembers).
            var configured = ThinkLonger.Places(settings.Plan(routes));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var board = new ThinkingJobBoard(places, () => configured, async (m, job, token) =>
            {
                if (job.Kind == ThinkingJobKind.Memory) await release.Task.WaitAsync(token);
                return ThinkingAnswer.Done(m.Name);
            });
            // The tool text a reply carries comes from the configured members only (the desktop's ReplyThinkTools).
            string Tools() => JsonSerializer.Serialize(ThinkLonger.Definitions(new ThinkLongerSettings(), ThinkLonger.Slots(ThinkLonger.Places(settings.Plan(routes)))));
            var tools = Tools();
            var all = board.Status();

            offline["diva"] = true;
            places.Reconsider();
            var away = board.Status();
            var live = settings.Plan(routes, offline: [.. offline.Keys]);
            var ran = await board.RunAsync(Job(ThinkingJobKind.Digest), cancellation);
            Check("presence: an offline member's slots leave the pool and jobs go to the others",
                all is { Slots: 3, ConfiguredSlots: 3 } && away is { Slots: 1, ConfiguredSlots: 3 } &&
                away.Members.Single(m => m.Id == "host:diva").Online == false && ran.Member == "ripley" &&
                live.Usable.Select(s => s.Computer).SequenceEqual(["ripley"]) && live.Find("host:diva")!.Plan.Offline,
                $"slots {all.Slots} -> {away.Slots} of {away.ConfiguredSlots}; digest on {ran.Member}; diva: {live.Find("host:diva")!.Plan.Why}");

            // ripley busy and diva offline: a memory job waits in line, and starts on diva once it answers again.
            var holding = places.TryAcquire([configured.Single(p => p.Name == "ripley")], "other-job")!;
            var waiting = board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            await WaitAsync(() => places.WaitingKinds.Count == 1, cancellation);
            var waited = !waiting.IsCompleted;
            offline["ripley"] = true;
            var none = settings.Plan(routes, offline: [.. offline.Keys]);
            var noneStatus = board.Status();
            var toolsAllOffline = Tools();
            offline.Clear();
            places.Reconsider();
            await WaitAsync(() => places.Leases.Any(l => l.Holder.StartsWith("memory", StringComparison.Ordinal)), cancellation);
            var placedOn = places.Leases.FirstOrDefault(l => l.Holder.StartsWith("memory", StringComparison.Ordinal))?.Place.Name;
            release.TrySetResult();
            var back = await waiting;
            holding.Dispose();
            var again = board.Status();
            Check("presence: a job waiting in line starts on a member that answers again, and its slots come back",
                waited && placedOn == "diva" && back.Member == "diva" && again is { Slots: 3, ConfiguredSlots: 3 },
                $"waited {waited}; placed on {placedOn}; slots back to {again.Slots}");
            Check("presence: every computer offline lets the conversation model stand in",
                none.Plan.Available && none.Usable.Single().Settings.Separate == false && noneStatus.Slots == 0 &&
                noneStatus.Guidance[0].StartsWith("Every Thinking pool computer is offline", StringComparison.Ordinal),
                none.Plan.Why);
            Check("presence: the think_longer tool text stays byte-identical", Tools() == tools && toolsAllOffline == tools,
                ThinkLonger.Description(new ThinkLongerSettings(), ThinkLonger.Slots(ThinkLonger.Places(settings.Plan(routes)))));
        }

        // 10. A member whose computer refused a request as invalid (a paired computer's gateway: request.invalid) rests: the board
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

        // 11. Quick jobs and Long jobs (the owner's boxes for each machine on Companion › Thinking pool): a judge passes over a
        // member kept from quick jobs, a long job passes over a member kept from long jobs, and a long job on a member that takes
        // no quick jobs keeps no slot free (that member's slots were never the quick jobs' slots).
        {
            BackgroundPlace big = new("host:big", "big") { Slots = 1, QuickJobs = false }, fast = new("endpoint:fast", "fast") { Slots = 1, LongJobs = false };
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [big, fast], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var judge = await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge), cancellation);
            var think = await board.RunAsync(Job(ThinkingJobKind.ThinkLonger), cancellation);
            var quickOnly = new ThinkingJobBoard(new BackgroundPlaces(), () => [fast], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var noLong = await quickOnly.RunAsync(Job(ThinkingJobKind.Research), cancellation);
            Check("quick and long jobs: each kind goes only to a member that takes it",
                judge.Member == "fast" && think.Member == "big" && noLong.Outcome == ThinkingJobOutcome.NoMember &&
                !quickOnly.CanRun(ThinkingJobKind.ThinkLonger) && quickOnly.CanRun(ThinkingJobKind.BargeInJudge),
                $"judge on {judge.Member}; think on {think.Member} ({think.Outcome}); research with a quick-only member: {noLong.Outcome}");

            // The only slot that takes quick jobs is busy: a long job still starts at once on the member that never takes them.
            var slotPlaces = new BackgroundPlaces();
            BackgroundPlace longOnly = new("host:big", "big") { Slots = 1, QuickJobs = false }, mixed = new("host:mixed", "mixed") { Slots = 1 };
            var held = slotPlaces.TryAcquire([mixed], "judge");
            var slotBoard = new ThinkingJobBoard(slotPlaces, () => [longOnly, mixed], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var run = slotBoard.RunAsync(Job(ThinkingJobKind.ThinkLonger), cancellation);
            var atOnce = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5), cancellation)) == run;
            held?.Dispose();
            var beside = await run;
            Check("quick and long jobs: a long job on a member that takes no quick jobs keeps no slot free",
                held is not null && atOnce && beside.Member == "big",
                $"the only quick-jobs slot busy; think {(atOnce ? "started at once" : "waited")} on {beside.Member} ({beside.Outcome})");

            var host = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Host, ModelId = "qwen3:8b", HostId = "diva", HostOrigin = "https://192.168.1.2:9443",
                HostSpkiFingerprint = "sha256/fixture", HostDeviceId = "desk-pc", HostCredentialId = Guid.NewGuid(),
                HostRouteId = SelfHostSetup.DeepThinkingRouteId, Slots = 2
            };
            var pool = new ThinkingPoolSettings().Add(host).WithJobs(host.Key, @long: false);
            var removed = pool.Remove(host.Key);
            Check("quick and long jobs: long jobs see only the members that take them, and leaving clears the boxes",
                pool.TakesQuickJobs(host.Key) && !pool.TakesLongJobs(host.Key) && pool.ForLongJobs().Members.Count == 0 &&
                pool.Members.Count == 1 && removed.NoLongJobs.Count == 0 && removed.TakesLongJobs(host.Key),
                $"long jobs see {pool.ForLongJobs().Members.Count} of {pool.Members.Count} members; after leaving: {removed.NoLongJobs.Count} kept from long jobs");
        }

        // 12. The request journal (ThinkingRequests: the Thinking requests page and thinking-requests.json): the board records a
        // success, a retry on another member and a stale drop, each with plausible timings, and the totals by kind count them.
        {
            var places = new BackgroundPlaces();
            BackgroundPlace bad = new("host:bad", "bad") { Slots = 1, Model = "fixture-a" }, ok = new("host:ok", "ok") { Slots = 1, Rank = 1, Model = "fixture-b" };
            var board = new ThinkingJobBoard(places, () => [bad, ok], async (m, job, token) =>
            {
                await Task.Delay(60, token);
                return job.Kind == ThinkingJobKind.Memory && m.Id == "host:bad" ? ThinkingAnswer.Failed("busy: job.busy") : ThinkingAnswer.Done("fixture answer");
            });
            var success = await board.RunAsync(Job(ThinkingJobKind.Digest), cancellation);
            var retry = await board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var heldBad = places.TryAcquire([bad], "other-job-1")!;
            var heldOk = places.TryAcquire([ok], "other-job-2")!;
            var stale = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge, timeout: TimeSpan.FromMilliseconds(300), stale: true), cancellation);
            heldBad.Dispose();
            heldOk.Dispose();
            var list = places.Requests.List(places);
            var ofDigest = list.Single(r => r.Kind == ThinkingJobKind.Digest);
            var ofMemory = list.Single(r => r.Kind == ThinkingJobKind.Memory);
            var ofJudge = list.Single(r => r.Kind == ThinkingJobKind.BargeInJudge);
            static string Timing(ThinkingRequestInfo r) =>
                $"{r.Id} {r.KindName} {r.State}: {r.Attempts.Count} tries ({string.Join(", ", r.Attempts.Select(a => $"{a.Member} {a.Duration(r.Finished ?? r.Now).TotalMilliseconds:0} ms: {a.Ending}"))}), " +
                $"waited {r.Waited.TotalMilliseconds:0} ms, ran {r.Ran.TotalMilliseconds:0} ms, total {r.Total.TotalMilliseconds:0} ms";
            var second = TimeSpan.FromSeconds(1);
            Check("requests: a success is recorded with one try and its timings",
                success.Succeeded && ofDigest is { State: ThinkingRequestState.Succeeded, Attempts.Count: 1, Retries: 0, Start.Source: ThinkingRequestSource.Pool } &&
                ofDigest.Attempts[0].Member == "bad" && ofDigest.Attempts[0].Model == "fixture-a" && ofDigest.Attempts[0].Ending == "answered" &&
                ofDigest.Ran >= TimeSpan.FromMilliseconds(50) && ofDigest.Ran < 5 * second && ofDigest.Waited < second &&
                ofDigest.Total >= ofDigest.Ran && ofDigest.AnswerLength == "fixture answer".Length && ofDigest.Finished is not null,
                Timing(ofDigest));
            Check("requests: a retry is recorded as two tries, the failed member then the one that answered",
                retry.Succeeded && retry.Attempts == 2 && ofMemory is { State: ThinkingRequestState.Succeeded, Attempts.Count: 2, Retries: 1 } &&
                ofMemory.Attempts[0].Member == "bad" && ofMemory.Attempts[0].Ending == "busy: job.busy" &&
                ofMemory.Attempts[1].Member == "ok" && ofMemory.Attempts[1].Ending == "answered" &&
                ofMemory.Attempts[1].Started >= ofMemory.Attempts[0].Ended && ofMemory.Ran >= TimeSpan.FromMilliseconds(100) &&
                ofMemory.Ran < 5 * second && ofMemory.Waited < second,
                Timing(ofMemory));
            Check("requests: a stale judge is recorded as dropped after its wait, with no try",
                stale.Outcome == ThinkingJobOutcome.Stale && ofJudge is { State: ThinkingRequestState.Stale, Attempts.Count: 0, Fast: true } &&
                ofJudge.Ran == TimeSpan.Zero && ofJudge.Waited >= TimeSpan.FromMilliseconds(250) && ofJudge.Waited < 5 * second &&
                ofJudge.Start.DropWhenStale && ofJudge.Note == stale.Problem,
                $"{Timing(ofJudge)}; note: {ofJudge.Note}");
            var totals = places.Requests.Totals;
            var digestTotals = totals.GetValueOrDefault(ThinkingJobKind.Digest, ThinkingRequestTotals.Empty);
            var memoryTotals = totals.GetValueOrDefault(ThinkingJobKind.Memory, ThinkingRequestTotals.Empty);
            var judgeTotals = totals.GetValueOrDefault(ThinkingJobKind.BargeInJudge, ThinkingRequestTotals.Empty);
            Check("requests: the totals by kind count the success, the retry and the drop",
                totals.Count == 3 && places.Requests.ActiveCount == 0 && list.Count == 3 &&
                digestTotals is { Count: 1, Succeeded: 1, Problems: 0, Retries: 0 } && memoryTotals is { Count: 1, Succeeded: 1, Problems: 0, Attempts: 2, Retries: 1 } &&
                judgeTotals is { Count: 1, Succeeded: 0, Problems: 1, Attempts: 0 } && judgeTotals.MaxWaited >= TimeSpan.FromMilliseconds(250) &&
                memoryTotals.MaxRan >= TimeSpan.FromMilliseconds(100),
                string.Join("; ", totals.OrderBy(t => t.Key).Select(t => $"{ThinkingJobKinds.Name(t.Key)}: {t.Value.Count} ended, {t.Value.Succeeded} succeeded, " +
                    $"{t.Value.Problems} problems, {t.Value.Retries} retries, average wait {t.Value.AverageWait.TotalMilliseconds:0} ms, average run {t.Value.AverageRun.TotalMilliseconds:0} ms")));
        }

        // 12 to 15. Priority on one member with one slot (a sequential local model): higher-priority work stops lower-priority work,
        // the stopped job waits at the front of its priority, its priority goes up after every RaiseAfterStops stops, and a failed
        // job is tried again at the priority it had. The outcomes go into the report's priority section.
        Dictionary<string, object?> priority = [];
        static ThinkingJob Labeled(ThinkingJobKind kind, string label, ThinkingPriority? priority = null, TimeSpan? timeout = null) =>
            Job(kind, timeout: timeout) with { Text = label, Priority = priority };
        static object Outcome(Task<ThinkingJobResult> task)
        {
            if (!task.IsCompletedSuccessfully) return "not finished";
            var r = task.Result;
            return new { outcome = r.Outcome.ToString(), r.Attempts, r.Preemptions, r.PriorityStops, r.Retries, r.Priority, r.Problem };
        }
        static object Counters(ThinkingPoolStatus status) => new
        {
            preemptLowerPriority = status.Policy.PreemptLowerPriority, raiseAfterStops = status.Policy.RaiseAfterStops, retries = status.Policy.Retries,
            status.StoppedForPriority, status.Raised, status.Retried
        };

        // 12. A barge-in judge finds the one slot busy with research: research stops, the judge runs, research runs again later.
        // With the stop turned off, the judge waits for research to end.
        {
            ThinkingPoolStatus? on = null, off = null;
            string[] ranOn = [], ranOff = [];
            Task<ThinkingJobResult> researchOn, judgeOn, researchOff, judgeOff;
            bool tookOver, waitedOff;
            {
                var lane = new Lane("research");
                var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], lane.RunAsync)
                {
                    Policy = () => new(PreemptLowerPriority: true, RaiseAfterStops: 3, Retries: 1)
                };
                researchOn = board.RunAsync(Labeled(ThinkingJobKind.Research, "research"), cancellation);
                await WaitAsync(() => lane.Count("research") == 1, cancellation);
                judgeOn = board.RunAsync(Labeled(ThinkingJobKind.BargeInJudge, "judge"), cancellation);
                tookOver = await WithinAsync(judgeOn, Settle, cancellation);
                await WaitAsync(() => lane.Count("research") == 2, cancellation);
                lane.ReleaseAll();
                await WithinAsync(Task.WhenAll(researchOn, judgeOn), Settle, cancellation);
                on = board.Status();
                ranOn = lane.Ran;
            }
            {
                var lane = new Lane("research");
                var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], lane.RunAsync)
                {
                    Policy = () => new(PreemptLowerPriority: false, RaiseAfterStops: 3, Retries: 1)
                };
                researchOff = board.RunAsync(Labeled(ThinkingJobKind.Research, "research"), cancellation);
                await WaitAsync(() => lane.Count("research") == 1, cancellation);
                judgeOff = board.RunAsync(Labeled(ThinkingJobKind.BargeInJudge, "judge"), cancellation);
                waitedOff = !await WithinAsync(judgeOff, Brief, cancellation) && lane.Count("research") == 1;
                lane.ReleaseAll();
                await WithinAsync(Task.WhenAll(researchOff, judgeOff), Settle, cancellation);
                off = board.Status();
                ranOff = lane.Ran;
            }
            Check("priority stop: on one slot, a barge-in judge stops research and research runs again later",
                tookOver && Done(judgeOn) && Done(researchOn) && researchOn.Result is { PriorityStops: 1, Preemptions: >= 1 } &&
                researchOn.Result.Priority == (int)ThinkingPriority.Research && ranOn.SequenceEqual(["research", "judge", "research"]) &&
                on is { StoppedForPriority: 1, Policy.PreemptLowerPriority: true },
                $"judge {(tookOver ? "took the slot" : "waited")}; ran {string.Join(", ", ranOn)}; research {Json(Outcome(researchOn))}; " +
                $"stopped for priority {on?.StoppedForPriority}");
            Check("priority stop off: the judge waits for research to end",
                waitedOff && Done(judgeOff) && Done(researchOff) && researchOff.Result.PriorityStops == 0 &&
                ranOff.SequenceEqual(["research", "judge"]) && off is { StoppedForPriority: 0, Policy.PreemptLowerPriority: false },
                $"judge {(waitedOff ? "waited" : "did not wait")}; ran {string.Join(", ", ranOff)}; stopped for priority {off?.StoppedForPriority}");
            priority["stop"] = new { ran = ranOn, research = Outcome(researchOn), judge = Outcome(judgeOn), status = on is null ? null : Counters(on) };
            priority["stopOff"] = new { ran = ranOff, research = Outcome(researchOff), judge = Outcome(judgeOff), status = off is null ? null : Counters(off) };
        }

        // 13. The stopped job keeps its priority and goes to the front of the line for it: research A, stopped by a judge, runs
        // again before research B, which waited before the judge came. A summary (digest) stopped for priority waits again too.
        {
            var places = new BackgroundPlaces();
            var lane = new Lane("a");
            var board = new ThinkingJobBoard(places, () => [Local()], lane.RunAsync) { Policy = () => new(true, 3, 1) };
            var a = board.RunAsync(Labeled(ThinkingJobKind.Research, "a"), cancellation);
            await WaitAsync(() => lane.Count("a") == 1, cancellation);
            var b = board.RunAsync(Labeled(ThinkingJobKind.Research, "b"), cancellation);
            await WaitAsync(() => places.WaitingKinds.Count == 1, cancellation);
            var judge = board.RunAsync(Labeled(ThinkingJobKind.BargeInJudge, "judge"), cancellation);
            await WaitAsync(() => lane.Count("a") == 2, cancellation);
            var line = places.WaitingKinds.Select(ThinkingJobKinds.Name).ToArray();
            lane.ReleaseAll();
            await WithinAsync(Task.WhenAll(a, b, judge), Settle, cancellation);
            var ran = lane.Ran;
            Check("priority stop: the stopped job keeps its priority and goes before same-priority jobs that waited",
                Done(a) && Done(b) && Done(judge) && a.Result is { PriorityStops: 1 } && a.Result.Priority == (int)ThinkingPriority.Research &&
                ran.SequenceEqual(["a", "judge", "a", "b"]),
                $"ran {string.Join(", ", ran)}; line while a ran again {string.Join(" > ", line)}; a {Json(Outcome(a))}");
            priority["front"] = new { ran, a = Outcome(a), b = Outcome(b), judge = Outcome(judge), status = Counters(board.Status()) };

            var digestLane = new Lane("digest");
            var digestBoard = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], digestLane.RunAsync) { Policy = () => new(true, 3, 1) };
            var digest = digestBoard.RunAsync(Labeled(ThinkingJobKind.Digest, "digest"), cancellation);
            await WaitAsync(() => digestLane.Count("digest") == 1, cancellation);
            var digestJudge = digestBoard.RunAsync(Labeled(ThinkingJobKind.BargeInJudge, "judge"), cancellation);
            await WaitAsync(() => digestLane.Count("digest") == 2, cancellation);
            digestLane.ReleaseAll();
            await WithinAsync(Task.WhenAll(digest, digestJudge), Settle, cancellation);
            Check("priority stop: a summary stopped for priority waits again and is not dropped",
                Done(digest) && Done(digestJudge) && digest.Result.PriorityStops == 1 && digestLane.Ran.SequenceEqual(["digest", "judge", "digest"]),
                $"ran {string.Join(", ", digestLane.Ran)}; digest {Json(Outcome(digest))}");
            priority["digest"] = new { ran = digestLane.Ran, digest = Outcome(digest), judge = Outcome(digestJudge) };
        }

        // 14. Raise after stops (RaiseAfterStops 2): research at priority 10 is stopped twice and goes up to 11, so a job of
        // priority 11 no longer stops it and waits.
        {
            var places = new BackgroundPlaces();
            var lane = new Lane("low");
            var board = new ThinkingJobBoard(places, () => [Local()], lane.RunAsync) { Policy = () => new(true, RaiseAfterStops: 2, Retries: 1) };
            var low = board.RunAsync(Labeled(ThinkingJobKind.Research, "low", ThinkingPriority.Research), cancellation);
            await WaitAsync(() => lane.Count("low") == 1, cancellation);
            var first = board.RunAsync(Labeled(ThinkingJobKind.EndOfTurnJudge, "judge-1"), cancellation);
            await WaitAsync(() => lane.Count("low") == 2, cancellation);
            var second = board.RunAsync(Labeled(ThinkingJobKind.EndOfTurnJudge, "judge-2"), cancellation);
            await WaitAsync(() => lane.Count("low") == 3, cancellation);
            var raisedStatus = board.Status();
            var even = board.RunAsync(Labeled(ThinkingJobKind.Research, "even", (ThinkingPriority)((int)ThinkingPriority.Research + 1)), cancellation);
            var waited = !await WithinAsync(even, Brief, cancellation) && lane.Count("low") == 3;
            lane.ReleaseAll();
            await WithinAsync(Task.WhenAll(low, first, second, even), Settle, cancellation);
            var ran = lane.Ran;
            var status = board.Status();
            Check("priority raise: after every 2 stops the stopped job's priority goes up by 1",
                Done(low) && Done(first) && Done(second) && Done(even) && low.Result is { PriorityStops: 2 } &&
                low.Result.Priority == (int)ThinkingPriority.Research + 1 && raisedStatus.Raised == 1 && status is { Raised: 1, StoppedForPriority: 2 },
                $"low {Json(Outcome(low))}; raised {status.Raised}; stopped for priority {status.StoppedForPriority}");
            Check("priority raise: a job of the raised priority no longer stops it",
                waited && ran.SequenceEqual(["low", "judge-1", "low", "judge-2", "low", "even"]),
                $"priority 11 job {(waited ? "waited" : "did not wait")}; ran {string.Join(", ", ran)}");
            priority["raise"] = new { ran, low = Outcome(low), even = Outcome(even), status = Counters(status) };
        }

        // 15. Retries: a job that failed (or timed out) on the only member is tried again up to Retries times at the priority it had.
        {
            static Func<BackgroundPlace, ThinkingJob, CancellationToken, Task<ThinkingAnswer>> FailsFirst(bool hang)
            {
                var tries = 0;
                return async (m, job, token) =>
                {
                    if (Interlocked.Increment(ref tries) > 1) return ThinkingAnswer.Done(m.Name);
                    if (hang) await Task.Delay(Timeout.Infinite, token);
                    return ThinkingAnswer.Failed($"{m.Name} failed (fixture)");
                };
            }
            var retryBoard = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], FailsFirst(hang: false)) { Policy = () => new(true, 3, Retries: 1) };
            var retried = await retryBoard.RunAsync(Labeled(ThinkingJobKind.Memory, "flaky"), cancellation);
            var retryStatus = retryBoard.Status();
            var noRetryBoard = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], FailsFirst(hang: false)) { Policy = () => new(true, 3, Retries: 0) };
            var failed = await noRetryBoard.RunAsync(Labeled(ThinkingJobKind.Memory, "flaky"), cancellation);
            var slowBoard = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], FailsFirst(hang: true)) { Policy = () => new(true, 3, Retries: 1) };
            var slow = await slowBoard.RunAsync(Labeled(ThinkingJobKind.Memory, "slow", timeout: TimeSpan.FromMilliseconds(300)), cancellation);
            Check("retries: a failed job is tried again at its priority; with 0 retries it fails",
                retried is { Succeeded: true, Retries: 1, Attempts: 2 } && retried.Priority == (int)ThinkingPriority.Helper && retryStatus.Retried == 1 &&
                failed is { Outcome: ThinkingJobOutcome.Failed, Retries: 0 } && noRetryBoard.Status().Retried == 0,
                $"1 retry: {Json(Outcome(Task.FromResult(retried)))}; 0 retries: {failed.Outcome} ({failed.Problem})");
            Check("retries: a timed-out job is tried again", slow is { Succeeded: true, Retries: 1 } && slowBoard.Status().Retried == 1,
                $"{Json(Outcome(Task.FromResult(slow)))}");

            // Stopped once for priority (RaiseAfterStops 1: up to 11), then failed: the retry runs at 11.
            var tries = 0;
            var lane = new Lane("r");
            var raisedBoard = new ThinkingJobBoard(new BackgroundPlaces(), () => [Local()], async (m, job, token) =>
            {
                var answer = await lane.RunAsync(m, job, token);
                return job.Text == "r" && Interlocked.Increment(ref tries) == 1 ? ThinkingAnswer.Failed("r failed (fixture)") : answer;
            }) { Policy = () => new(true, RaiseAfterStops: 1, Retries: 1) };
            var r = raisedBoard.RunAsync(Labeled(ThinkingJobKind.Research, "r", ThinkingPriority.Research), cancellation);
            await WaitAsync(() => lane.Count("r") == 1, cancellation);
            var judge = raisedBoard.RunAsync(Labeled(ThinkingJobKind.BargeInJudge, "judge"), cancellation);
            await WaitAsync(() => lane.Count("r") == 2, cancellation);
            lane.ReleaseAll();
            await WithinAsync(Task.WhenAll(r, judge), Settle, cancellation);
            Check("retries: a raised job is tried again at the priority it was left at",
                Done(r) && r.Result is { PriorityStops: 1, Retries: 1 } && r.Result.Priority == (int)ThinkingPriority.Research + 1,
                $"ran {string.Join(", ", lane.Ran)}; r {Json(Outcome(r))}");
            priority["retries"] = new
            {
                oneRetry = Outcome(Task.FromResult(retried)), noRetry = Outcome(Task.FromResult(failed)), timedOut = Outcome(Task.FromResult(slow)),
                raisedThenFailed = Outcome(r), status = Counters(retryStatus)
            };
        }

        // 16. May receive pictures and recordings: an external member (an endpoint not on this PC) gets no job with a picture or a
        // recording until the owner ticks it, but still takes text jobs; this PC and paired computers always may.
        {
            const ThinkingCapability Sees = ThinkingCapability.Text | ThinkingCapability.Vision;
            var cloud = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "https://api.example.com/v1", ModelId = "gpt-fixture" };
            var local = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "http://127.0.0.1:11434/v1", ModelId = "gemma3" };
            var pool = new ThinkingPoolSettings().Add(cloud).Add(local);
            var allowed = pool.WithMedia(cloud.Key, true);
            BackgroundPlace outside = new(cloud.Key, "api.example.com") { Slots = 1, Can = Sees, Media = pool.MayReceiveMedia(cloud) };
            var asked = 0;
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [outside], (m, _, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult(ThinkingAnswer.Done(m.Name));
            });
            var picture = await board.RunAsync(Job(ThinkingJobKind.CheckIn, Sees), cancellation);
            var text = await board.RunAsync(Job(ThinkingJobKind.CheckIn), cancellation);
            var guidance = board.Status().Guidance;
            var ticked = new ThinkingJobBoard(new BackgroundPlaces(), () => [outside with { Media = allowed.MayReceiveMedia(cloud) }],
                (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var after = await ticked.RunAsync(Job(ThinkingJobKind.CheckIn, Sees), cancellation);
            Check("pictures and recordings: an external member gets them only when ticked; text jobs still go to it",
                ThinkingPoolSettings.IsExternal(cloud) && !ThinkingPoolSettings.IsExternal(local) && pool.MayReceiveMedia(local) &&
                picture.Outcome == ThinkingJobOutcome.NoMember && picture.Problem?.Contains("may not receive pictures") == true &&
                text.Succeeded && asked == 1 && !board.CanRun(ThinkingJobKind.CheckIn, Sees) &&
                guidance.Any(g => g.Contains("may receive pictures", StringComparison.OrdinalIgnoreCase)) &&
                after.Succeeded && allowed.Remove(cloud.Key).MediaAllowed.Count == 0,
                $"picture job before ticking: {picture.Outcome} ({picture.Problem}); text job {text.Outcome}; {asked} request(s); after " +
                $"ticking: {after.Outcome} on {after.Member}");
        }

        // 17. Rate limits: an OpenAI-compatible cloud endpoint (NVIDIA Build's address, simulated: no request leaves this PC) answers
        // 429 with Retry-After 1 s. The member cools down and runs fewer jobs at once; the job waits for it instead of failing
        // (with no retries on failure allowed), and a job that another member can take goes there at once.
        {
            BackgroundPlace nvidia = new("endpoint:https://integrate.api.nvidia.com/v1", "NVIDIA") { Slots = 4 };
            var calls = 0;
            ThinkingPoolCooling? said = null;
            var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [nvidia], (m, _, _) =>
                Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ThinkingAnswer.Limited("NVIDIA is limiting requests", TimeSpan.FromSeconds(1))
                    : ThinkingAnswer.Done(m.Name)))
            {
                Policy = () => new ThinkingPoolPolicy(PreemptLowerPriority: false, Retries: 0)
            };
            board.Limited += cooling => said = cooling;
            var watchLimit = System.Diagnostics.Stopwatch.StartNew();
            var waited = await board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var waitedFor = watchLimit.Elapsed;
            var status = board.Status();
            var member = status.Members.Single();
            Check("rate limits: a limited endpoint member cools down, runs fewer jobs at once, and the job waits for it instead of failing",
                waited.Succeeded && waited.Attempts == 2 && waitedFor >= TimeSpan.FromSeconds(0.9) && said is { SlotsNow: 2, Slots: 4, Times: 1 } &&
                member.SlotsNow == 2 && status.Cooling.Count == 1 && status.Cooling[0].Until is null &&
                status.Guidance.Any(g => g.Contains("NVIDIA runs 2 of 4 jobs at once", StringComparison.Ordinal)),
                $"{waited.Outcome} on {waited.Member} after {waited.Attempts} attempts and {waitedFor.TotalSeconds:0.0} s; then {member.SlotsNow} of " +
                $"{member.Slots} slots; said \"{said?.Describe(DateTimeOffset.UtcNow)}\"");

            BackgroundPlace busy = new("endpoint:https://integrate.api.nvidia.com/v1", "NVIDIA") { Slots = 4 }, local = new("host:diva", "diva", Rank: 1);
            var other = new ThinkingJobBoard(new BackgroundPlaces(), () => [busy, local], (m, _, _) =>
                Task.FromResult(m.Id == busy.Id ? ThinkingAnswer.Limited("NVIDIA is limiting requests", TimeSpan.FromMinutes(1)) : ThinkingAnswer.Done(m.Name)));
            var moved = await other.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var cooling = other.Status().Cooling;
            Check("rate limits: while a member cools down, its jobs go to another member and it gets none",
                moved.Succeeded && moved.Member == "diva" && cooling is [{ Until: not null }] && other.Find(ThinkingJobKind.Memory)?.Name == "diva",
                $"{moved.Outcome} on {moved.Member}; {string.Join("; ", cooling.Select(c => c.Describe(DateTimeOffset.UtcNow)))}");
        }

        // 18. Smarts and Runs on (Companion › Thinking pool, and each check-in card): Martlet guesses each member's smarts from its
        // model name; Prefer smart takes the smartest free member, and a less smart one after its short wait; Smart only gets no
        // member without a Smart one; These members uses only the chosen ones; Any member keeps today's choice.
        {
            BackgroundPlace small = new("endpoint:small", "small") { Slots = 1, Smarts = ThinkingSmarts.Fast, Model = "gemma4:e2b" },
                big = new("endpoint:big", "big") { Slots = 1, Smarts = ThinkingSmarts.Smart, Model = "nvidia/llama-3.3-nemotron-super-49b-v1" };
            var places = new BackgroundPlaces();
            var board = new ThinkingJobBoard(places, () => [small, big], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
            var any = await board.RunAsync(Job(ThinkingJobKind.Memory), cancellation);
            var prefer = await board.RunAsync(Job(ThinkingJobKind.Memory) with { RunsOn = ThinkingRunsOn.PreferSmart }, cancellation);
            var chosen = await board.RunAsync(Job(ThinkingJobKind.Memory) with { RunsOn = ThinkingRunsOn.Only(["endpoint:small"]) }, cancellation);
            var smallOnly = new ThinkingJobBoard(new BackgroundPlaces(), () => [small], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)))
            {
                RunsOn = _ => ThinkingRunsOn.SmartOnly
            };
            var none = await smallOnly.RunAsync(Job(ThinkingJobKind.CheckIn), cancellation);
            Check("runs on: prefer smart, smart only and these members choose the members",
                any.Member == "small" && prefer.Member == "big" && chosen.Member == "small" && none.Outcome == ThinkingJobOutcome.NoMember &&
                !smallOnly.CanRun(ThinkingJobKind.CheckIn) && board.Find(ThinkingJobKind.Memory, where: ThinkingRunsOn.PreferSmart)?.Name == "big" &&
                prefer.Placed?.StartsWith("Prefer smart", StringComparison.Ordinal) == true,
                $"any on {any.Member}; prefer smart on {prefer.Member} ({prefer.Placed}); these members on {chosen.Member}; smart only " +
                $"without a Smart member: {none.Outcome} ({none.Problem})");

            // The Smart member is busy: Prefer smart waits a quarter of the job's time (here 100 ms), then takes the less smart one.
            var held = places.TryAcquire([big], "busy");
            var timer = Stopwatch.StartNew();
            var fallback = await board.RunAsync(Job(ThinkingJobKind.Digest, timeout: TimeSpan.FromMilliseconds(400)) with { RunsOn = ThinkingRunsOn.PreferSmart },
                cancellation);
            held?.Dispose();
            var placements = board.Status().Placements;
            Check("runs on: prefer smart takes a less smart member after its short wait",
                held is not null && fallback.Member == "small" && timer.ElapsedMilliseconds >= 90 &&
                fallback.Placed?.Contains("no Smart member came free", StringComparison.Ordinal) == true &&
                placements.Count >= 4 && placements[0].Member == "small" && placements[0].RunsOn == "prefer-smart",
                $"{fallback.Member} after {timer.ElapsedMilliseconds} ms: {fallback.Placed}; {placements.Count} placements kept");

            var guesses = new[] { "gemma4:e2b", "qwen3:8b", "gemma4:27b", "nvidia/llama-3.3-nemotron-super-49b-v1", "llama3.3:70b" }
                .Select(model => $"{model} {ThinkingSmartsGuess.From(model)}").ToArray();
            var endpoint = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "https://integrate.api.nvidia.com", ModelId = "qwen3:8b" };
            var settings = new ThinkingPoolSettings().Add(endpoint).WithSmarts(endpoint.Key, ThinkingSmarts.Smart)
                .WithRunsOn("check-in", ThinkingRunsOn.Only([endpoint.Key]));
            var left = settings.Remove(endpoint.Key);
            Check("runs on: smarts are guessed from the model name, and the owner's choice is kept until the member leaves",
                ThinkingSmartsGuess.From("gemma4:e2b") == ThinkingSmarts.Fast && ThinkingSmartsGuess.From("qwen3:8b") == ThinkingSmarts.Standard &&
                ThinkingSmartsGuess.From("nvidia/llama-3.3-nemotron-super-49b-v1") == ThinkingSmarts.Smart &&
                settings.SmartsOf(endpoint) == ThinkingSmarts.Smart && settings.RunsOn("check-in").Members.Count == 1 &&
                left.Smarts.Count == 0 && left.RunsOn("check-in").Members.Count == 0,
                $"{string.Join("; ", guesses)}; chosen {settings.SmartsOf(endpoint)}; after leaving: {left.Smarts.Count} chosen, " +
                $"check-ins on {left.RunsOn("check-in").Describe()}");
        }

        return new
        {
            passed = steps.All(s => s.Passed), elapsedMs = watch.ElapsedMilliseconds,
            steps = steps.Select(s => new { name = s.Name, passed = s.Passed, detail = s.Detail }),
            priority,
            note = "In-process rehearsal of the production job board with simulated members (NOT models)."
        };
    }

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5), Brief = TimeSpan.FromMilliseconds(300);

    // The one-slot member of the priority steps: a sequential local model.
    private static BackgroundPlace Local() => new("host:local", "local") { Slots = 1 };

    private static bool Done(Task<ThinkingJobResult> task) => task.IsCompletedSuccessfully && task.Result.Succeeded;

    private static async Task<bool> WithinAsync(Task task, TimeSpan time, CancellationToken token) =>
        await Task.WhenAny(task, Task.Delay(time, token)) == task;

    /// <summary>A simulated one-slot member that notes the job (its text) each time it starts one. A held job runs until it is
    /// released or its attempt is stopped.</summary>
    private sealed class Lane(params string[] held)
    {
        private readonly object gate = new();
        private readonly List<string> starts = [];
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string[] Ran { get { lock (gate) return [.. starts]; } }

        public int Count(string label) { lock (gate) return starts.Count(s => s == label); }

        public void ReleaseAll() => release.TrySetResult();

        public async Task<ThinkingAnswer> RunAsync(BackgroundPlace member, ThinkingJob job, CancellationToken token)
        {
            lock (gate) starts.Add(job.Text);
            if (held.Contains(job.Text)) await release.Task.WaitAsync(token);
            return ThinkingAnswer.Done(job.Text);
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task WaitAsync(Func<bool> done, CancellationToken token)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10, token);
    }
}

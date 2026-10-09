using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The Thinking pool in the desktop: the job board (<see cref="ThinkingJobBoard"/>) over the pool's members
/// (Companion › Thinking pool, thinking-pool.json), sharing its slots with the conversation's background jobs (think_longer and
/// research). Get it from <see cref="LiveConversationController.ThinkingPool"/>. Post a job with <see cref="RunAsync"/>; ask
/// <see cref="CanRun"/> first to choose a fallback without posting. Pool jobs never use the conversation's own Thinking route.</summary>
internal sealed class ThinkingPool
{
    private readonly ThinkingJobBoard board;

    internal ThinkingPool(ThinkingJobBoard board) => this.board = board;

    /// <summary>Whether a member can take a job of <paramref name="kind"/> needing <paramref name="needs"/> (no slot taken), on
    /// <paramref name="where"/> (null: the kind's Runs on).</summary>
    internal bool CanRun(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text, ThinkingRunsOn? where = null) =>
        board.CanRun(kind, needs, where);

    /// <summary>The member such a job would go to first (its <see cref="BackgroundPlace.Name"/> and <see cref="BackgroundPlace.Model"/>),
    /// or null when no member can run it. Never the conversation's own route.</summary>
    internal BackgroundPlace? Find(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text, ThinkingRunsOn? where = null) =>
        board.Find(kind, needs, where);

    /// <summary>Runs <paramref name="job"/> on a free capable member; <see cref="ThinkingJobOutcome.NoMember"/> at once when none.</summary>
    internal Task<ThinkingJobResult> RunAsync(ThinkingJob job, CancellationToken token) => board.RunAsync(job, token);

    /// <summary>What the pool does now: members, slots, the kept fast slot, running and waiting jobs by kind, guidance.</summary>
    internal ThinkingPoolStatus Status() => board.Status();

    internal ThinkingJobBoard Board => board;

    public override string ToString() => nameof(ThinkingPool);
}

internal sealed partial class LiveConversationController
{
    /// <summary>thinking-pool-status.json in the data directory: the pool's members, slots and jobs by kind (never a job's text),
    /// which MCP's thinking_pool_status reads.</summary>
    internal const string PoolStatusFile = "thinking-pool-status.json";

    private ThinkingPoolSettings thinkingPool = new();
    private ModelAbilities? poolAbilities;
    private ThinkingPool? pool;
    private readonly ConcurrentDictionary<string, ConcurrentBag<ThinkSlot>> poolSlots = new(StringComparer.Ordinal);

    /// <summary>The Thinking pool: post background model jobs here (<see cref="ThinkingPool.RunAsync"/>).</summary>
    internal ThinkingPool ThinkingPool => pool ??= NewThinkingPool();

    /// <summary>What the pool does now, or null before any job or status made it (Companion › Thinking pool's Busy pool line).</summary>
    internal ThinkingPoolStatus? PoolStatusNow() => Volatile.Read(ref pool)?.Status();

    private ThinkingPool NewThinkingPool()
    {
        var board = new ThinkingJobBoard(jobs.Places, PoolMembers, RunPoolJobAsync, clock)
        {
            // Companion › Thinking pool's priority and retry choices, read on each job and each try.
            Policy = () => ThinkingPoolPolicy.From(Volatile.Read(ref thinkingPool)),
            // Runs on, by kind (Companion › Thinking pool), read on each job.
            RunsOn = kind => Volatile.Read(ref thinkingPool).RunsOn(ThinkingJobKinds.Name(kind))
        };
        board.Rested += PoolMemberRested;
        board.Limited += PoolMemberLimited;
        return new(board);
    }

    // Said once when it starts, in plain words with the computer to update; thinking-pool-status.json shows it until it ends.
    private void PoolMemberRested(ThinkingPoolRest rest)
    {
        var minutes = ThinkingJobBoard.RefusedRest.TotalMinutes;
        ErrorLog.Warn($"Thinking pool: {rest.Name} refused this PC's request as invalid (request.invalid), so Martlet sends it no " +
            $"Thinking pool jobs with {ThinkingJobResult.Describe(rest.Needs)} for {minutes:0} minutes. Update {rest.Name} and this PC " +
            "to the same Martlet version.");
        Task.Run(WritePoolStatus).Forget();
    }

    // Said each time a member's provider limits a request, in plain words; thinking-pool-status.json shows it until it ends. Never
    // on the reply's path: pool jobs never use the conversation's own Thinking route.
    private void PoolMemberLimited(ThinkingPoolCooling cooling)
    {
        ErrorLog.Info($"Thinking pool: {cooling.Problem}. {cooling.Describe(clock.GetUtcNow())}; its jobs wait in line instead of failing.");
        Task.Run(WritePoolStatus).Forget();
    }

    /// <summary>The pool members whose providers limit requests now (none before the pool is made).</summary>
    internal IReadOnlyList<ThinkingPoolCooling> PoolCooling() => pool?.Status().Cooling ?? [];

    private int poolRead;

    /// <summary>Reads the Thinking pool unless it was read already: the talk window reads it when it opens, and a Companion page
    /// (Touch zones) can ask what the pool can do before that.</summary>
    internal void ReadThinkingPoolOnce()
    {
        if (Volatile.Read(ref poolRead) == 0) ReloadThinkingPool();
    }

    /// <summary>Reads the Thinking pool (thinking-pool.json, made from deep-thinking.json once) again, after Companion › Thinking
    /// pool saved it: the next reply offers think_longer only where the pool (or the conversation model) can run it.</summary>
    internal void ReloadThinkingPool()
    {
        Volatile.Write(ref poolRead, 1);
        Volatile.Write(ref thinkingPool, ThinkingPoolSettings.Load(dataDirectory));
        Volatile.Write(ref poolAbilities, dataDirectory is null ? null : ModelAbilities.Load(dataDirectory));
        WritePoolStatus();
    }

    /// <summary>What a think, a research job or a song's lyrics may run on, as Runs on for <paramref name="kind"/> says
    /// (Companion › Thinking pool): the places it may use, whether its work takes the smartest free one first (Prefer smart) and,
    /// when no member may take it, why.</summary>
    private (IReadOnlyList<BackgroundPlace> Places, bool SmartFirst, string? Why) LongRunsOn(IReadOnlyList<BackgroundPlace> places, ThinkingJobKind kind)
    {
        var where = Volatile.Read(ref thinkingPool).RunsOn(ThinkingJobKinds.Name(kind));
        var allowed = ThinkLonger.RunsOn(places, where);
        return (allowed, where.Mode == ThinkingRunsOnMode.PreferSmart,
            allowed.Count == 0 ? ThinkingRunsOnRules.NoMember(where, ThinkingCapability.Text) + "." : null);
    }

    /// <summary>What a member can do: pictures and recordings as its model is known to take them. A paired computer's Ollama
    /// takes no recordings.</summary>
    internal ThinkingCapability PoolCan(DeepThinkingSettings member) => ThinkingPoolCapabilities.For(member, Volatile.Read(ref poolAbilities));

    // The pool's members for jobs posted to the board: every usable member, never the conversation's own Thinking route. Members
    // whose computers are offline stay in the list: the broker passes over them (Answers), and their slots come back with them.
    private IReadOnlyList<BackgroundPlace> PoolMembers()
    {
        var routes = Configuration?.Routes ?? [];
        var plan = PoolPlan(routes);
        var thinking = routes.FirstOrDefault(r => r.Role == SetupRole.Llm && r.Enabled != false);
        // A member on a graphics card of its own beside the Thinking model's card (one Thinking pool model per card of a host)
        // shares only the computer, so it goes before a member on Thinking's own card.
        return [.. ThinkLonger.Places(plan, BackgroundDuties.Of(dataDirectory), PoolCan, HostRouteGpus.For, Volatile.Read(ref thinkingPool))
            .Where(place => place.Id != "thinking")
            .Select(place => place.Rank == 2 && thinking is not null && plan.Find(place.Id)?.Settings is { } member &&
                ThinkingPoolWarnings.OwnCard(member, thinking, HostRouteGpus.For) ? place with { Rank = 1 } : place)];
    }

    /// <summary>The pool's plan for <paramref name="routes"/>. Without <paramref name="live"/> every computer counts as online: the
    /// tools a reply offers (think_longer and its "Up to N at once", research) come from that plan, so the start of every Thinking
    /// request stays the same while computers come and go. With it, members on computers that don't answer now
    /// (<see cref="HostPresence"/>) can't run, and the conversation model stands in when none can and that is allowed: for
    /// placing work, status and the log. With <paramref name="longJobs"/> only the members whose Long jobs box is ticked count:
    /// thinking longer, research and lyrics run on those.</summary>
    private DeepThinkingPool PoolPlan(IReadOnlyList<SetupRoute> routes, bool live = false, bool longJobs = false)
    {
        var pool = Volatile.Read(ref thinkingPool);
        return (longJobs ? pool.ForLongJobs() : pool).Plan(routes, WorkSharingRoster.Settings(dataDirectory), WorkSharingRoster.Device,
            live ? HostPresence.Offline : null);
    }

    /// <summary>Where background thinking (a think, research, a song's lyrics) may be placed: what can run now
    /// (<paramref name="live"/>), and the members that would run but whose computers are offline. The broker passes over those,
    /// and work waiting in line goes to one as soon as it answers again. In the order the members were added.</summary>
    private static DeepThinkingPool Placing(DeepThinkingPool configured, DeepThinkingPool live) =>
        new([.. live.Usable.Where(spot => !spot.Settings.Separate),
            .. configured.Spots.Where(spot => spot.Settings.Separate && spot.Plan.Available && live.Find(spot.Key)?.Plan is { Available: true } or { Offline: true })]);

    /// <summary>The host a place runs on (<c>host:diva</c> and its card 2's <c>host:diva#gpu2</c> are diva), or null for an
    /// endpoint or the conversation model.</summary>
    internal static string? HostOf(string placeId) => DeepThinkingSettings.HostOfKey(placeId);

    // Whether a place's computer answers now, for the broker: a paired computer that a check found offline doesn't.
    private static bool Answers(BackgroundPlace place) => HostOf(place.Id) is not { } host || !HostPresence.IsOffline(host);

    // Made in the constructor, after the job list: the broker follows which computers answer, and a pool member's computer going
    // offline or answering again updates the pool at once.
    private void StartPresence()
    {
        jobs.Places.Reachable = Answers;
        HostPresence.Changed += PresenceChanged;
    }

    private void StopPresence()
    {
        HostPresence.Changed -= PresenceChanged;
        presence.Cancel();
        presence.Dispose();
    }

    private readonly CancellationTokenSource presence = new();
    private readonly ConcurrentDictionary<string, byte> watching = new(StringComparer.Ordinal);

    /// <summary>How often Martlet asks a Thinking pool computer that went offline whether it answers again (as device sync does).</summary>
    internal static TimeSpan PresenceRecheck => ClusterSync.Interval;

    // A computer went offline or answers again (device sync's check, or a pool job that couldn't reach it). Never on the reply's
    // path: the caller is the check's thread, and this only reads the pool and writes a small status file.
    private void PresenceChanged(string host, bool online)
    {
        if (disposed) return;
        var settings = Volatile.Read(ref thinkingPool);
        if (!settings.Members.Any(m => m.Place == DeepThinkingPlace.Host && m.HostId == host)) return;
        // Work waiting in line takes a slot on a member that answers again at once.
        if (online) jobs.Places.Reconsider();
        var status = ThinkingPool.Status();
        var answering = status.Members.Where(m => m.Online).ToArray();
        string now;
        if (answering.Length == status.Members.Count) now = $"{status.Slots} slot{(status.Slots == 1 ? "" : "s")}.";
        else if (answering.Length > 0)
            now = $"the pool has {status.Slots} slot{(status.Slots == 1 ? "" : "s")} on {answering.Length} member{(answering.Length == 1 ? "" : "s")} now " +
                $"({status.ConfiguredSlots} when all answer).";
        else
        {
            var plan = PoolPlan(Configuration?.Routes ?? [], live: true, longJobs: true).Plan;
            now = $"no pool computer answers now ({status.ConfiguredSlots} slot{(status.ConfiguredSlots == 1 ? "" : "s")} when all answer); " +
                (plan.Available ? "thinking longer and research use the conversation model meanwhile, and other pool jobs use their own fallbacks."
                    : "pool jobs use their own fallbacks until one answers again.");
        }
        ErrorLog.Info($"Thinking pool: {host} {(online ? "answers again" : "went offline")}; {now}");
        WritePoolStatus();
        if (!online) WatchAsync(host).Forget();
    }

    /// <summary>A pool request to <paramref name="member"/>'s computer failed because the computer didn't answer (a network
    /// error, never a model's): it counts as offline at once, so the next job goes elsewhere, until a check reaches it again.</summary>
    private void NoteUnreachable(DeepThinkingSettings member, string what)
    {
        if (member is not { Place: DeepThinkingPlace.Host, HostId: { } host } || HostPresence.IsOffline(host)) return;
        ErrorLog.Warn($"Thinking pool: {what} couldn't reach {host}.");
        HostPresence.Note(host, false);
    }

    /// <summary>For <see cref="YieldingThink.RunAsync"/>: whether a request of <paramref name="id"/> that failed on a member failed
    /// because the member's computer doesn't answer now; then the work goes on on another member.</summary>
    private static Func<BackgroundPlace, bool> GoesOnElsewhere(string area, string id) => at =>
    {
        if (Answers(at)) return false;
        ErrorLog.Info($"{area}: {id} lost {at.Name}, which stopped answering; it goes on on another member of the Thinking pool.");
        return true;
    };

    // While a pool member's computer is offline, asks it again every PresenceRecheck whether it answers: device sync does so
    // too while Keep in sync is on, and this covers it being off. One watch for each computer; it ends once the computer
    // answers, leaves the pool or Martlet closes.
    private async Task WatchAsync(string host)
    {
        if (!watching.TryAdd(host, 0)) return;
        try
        {
            var token = presence.Token;
            while (HostPresence.IsOffline(host))
            {
                await Task.Delay(PresenceRecheck, clock, token).ConfigureAwait(false);
                if (!HostPresence.IsOffline(host) || Member(host) is not { } member) break;
                if (await AnswersAsync(member, token).ConfigureAwait(false)) HostPresence.Note(host, true);
            }
        }
        catch (OperationCanceledException) when (presence.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        finally { watching.TryRemove(host, out _); }
    }

    private DeepThinkingSettings? Member(string host) =>
        Volatile.Read(ref thinkingPool).Members.FirstOrDefault(m => m.Place == DeepThinkingPlace.Host && m.HostId == host);

    // Whether a member's computer answers: its gateway lists its routes within a few seconds (what a pool job asks first).
    private static async Task<bool> AnswersAsync(DeepThinkingSettings member, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var target = DeepThinkTarget.For(member, ThinkEffort.Medium, null, null).Host!;
            using var connection = HostTextClient.Connect(target);
            await connection.ReadRoutesAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { return false; }
    }

    // One attempt of a pool job on a member: its own runtime and one-use authorization, the request fitted to the member.
    private async Task<ThinkingAnswer> RunPoolJobAsync(BackgroundPlace place, ThinkingJob job, CancellationToken token)
    {
        var configured = Configuration;
        var routes = configured?.Routes ?? [];
        if (PoolPlan(routes).Find(place.Id) is not { Settings.Separate: true } spot) return ThinkingAnswer.Failed($"{place.Name} left the pool");
        var member = spot.Settings;
        var thinking = routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        if (spot.Plan.ChecksFit && thinking?.ModelId is { } thinkingModel)
        {
            // A second model in Ollama on this PC runs only while it fits beside Thinking's, so the conversation keeps its cache.
            var fit = await LocalDeepThinking.CheckAsync(thinkingModel, member.ModelId!, loadThinking: false, token).ConfigureAwait(false);
            if (!fit.Fits) return ThinkingAnswer.Failed(fit.Why.TrimEnd('.'));
        }
        var input = new BoundedTextInput(job.Text, string.IsNullOrWhiteSpace(job.Instructions) ? null : job.Instructions,
            image: job.Image, tools: job.Tools, audio: job.Audio);
        var target = DeepThinkTarget.For(member, ThinkEffort.Medium, thinking, ModelLimits.Load(dataDirectory));
        if (input.Utf8Bytes > target.Input.MaxInputBytes || input.InputTokenReservation > target.Input.MaxInputTokens)
            return ThinkingAnswer.Failed($"the job is too long for {place.Name}");
        var toolRounds = job.Tools.Count > 0 ? job.MaxToolRounds : 0;
        var request = target.OneShot(input, job.MaxOutputTokens, job.Reasoning, job.Timeout, job.Tools.Count > 0 ? job.ToolHost : null, toolRounds);
        var slots = poolSlots.GetOrAdd(place.Id, _ => []);
        if (!slots.TryTake(out var slot))
        {
            slot = new ThinkSlot();
            var bound = slot;
            slot.Credentials = new(() => Volatile.Read(ref bound.Authorization));
        }
        try
        {
            var own = new DeepThinkAuthorization(target, request, configured?.Profile ?? Guid.Empty, thinking, vault, clock,
                clock.GetUtcNow() + job.Timeout + TimeSpan.FromSeconds(5), media: true, toolRounds: toolRounds);
            Volatile.Write(ref slot.Authorization, own);
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var started = ThinkRuntime(slot).Start(request, own, token);
            var terminal = await started.Completion.ConfigureAwait(false);
            await started.OwnershipRelease.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var outcome = ThinkLonger.Outcome(terminal, started.Content.Text);
            // The member's computer keeps its graphics card for a live turn: the job waits and goes on later, never a failure.
            if (outcome.Result is null && member.Place == DeepThinkingPlace.Host && HostLiveHolds.Since(member.HostId!, began))
                return ThinkingAnswer.Held($"{place.Name} keeps its graphics card for a live conversation");
            // The member's gateway refused the request itself (request.invalid): it refuses every such job until the two
            // computers' Martlet versions agree, so the pool rests it for such jobs instead of asking it again and again.
            if (outcome.Result is null && member.Place == DeepThinkingPlace.Host && terminal.ProviderFailure == ProviderFailureCode.RequestRejected)
                return ThinkingAnswer.Rejected($"{place.Name} refused the request as invalid (request.invalid)");
            // Its provider is busy (HTTP 429, a paired computer busy with its owner's work, or a cloud endpoint's 5xx such as 503
            // overloaded): the member cools down and the job waits in line instead of failing (ThinkingPoolLimits).
            if (outcome.Result is null && (terminal.ProviderFailure == ProviderFailureCode.RateLimited ||
                member.Place == DeepThinkingPlace.Endpoint && terminal.ProviderFailure == ProviderFailureCode.Server))
                return ThinkingAnswer.Limited(terminal.ProviderFailure == ProviderFailureCode.RateLimited
                    ? $"{place.Name} is limiting requests" : $"{place.Name} is busy (server error)", terminal.ProviderRetryAfter);
            // Its computer didn't answer: offline at once, so the board's next try (and the next job) goes to another member.
            if (outcome.Result is null && terminal.ProviderFailure == ProviderFailureCode.Network)
                NoteUnreachable(member, $"a {ThinkingJobKinds.Name(job.Kind)} job");
            return outcome.Result is { } text ? ThinkingAnswer.Done(text, outcome.Cut)
                : ThinkingAnswer.Failed($"{place.Name}: {outcome.Problem}");
        }
        finally
        {
            Volatile.Write(ref slot.Authorization, null);
            slots.Add(slot);
        }
    }

    /// <summary>Backup Thinking's part of thinking-pool-status.json: its choices, the automatic delay from recent replies and how
    /// it ended lately (never what was said).</summary>
    internal object BackupStatus(ThinkingPoolSettings settings)
    {
        var (recent, counts) = BackupHistory;
        return new
        {
            on = settings.BackupThinking, delayMs = settings.BackupDelayMs, usedDelayMs = BackupDelay.TotalMilliseconds,
            automaticDelayMs = firstWords.Delay.TotalMilliseconds, recentReplies = firstWords.Count,
            percentile95Ms = firstWords.Percentile95?.TotalMilliseconds, answering = settings.AnswersForConversation,
            recent = recent.Select(r => new
            {
                at = r.At, outcome = r.Result.Outcome.ToString(), member = r.Result.Member, delayMs = r.Result.Delay.TotalMilliseconds,
                askedAfterMs = r.Result.AskedAfter?.TotalMilliseconds, firstWordsAfterMs = r.Result.FirstWordsAfter?.TotalMilliseconds,
                why = r.Result.Why
            }),
            counts = counts.ToDictionary(c => c.Key.ToString(), c => c.Value)
        };
    }

    // thinking-pool-status.json: counts, names and kinds only (never a job's text). Written when the pool or its jobs change and
    // when a member's computer goes offline or answers again.
    private void WritePoolStatus()
    {
        if (dataDirectory is null || disposed) return;
        try
        {
            var json = PoolStatusJson();
            var path = Path.Combine(dataDirectory, PoolStatusFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    /// <summary>What thinking-pool-status.json says now.</summary>
    internal string PoolStatusJson()
    {
        var settings = Volatile.Read(ref thinkingPool);
        var status = ThinkingPool.Status();
        var plan = PoolPlan(Configuration?.Routes ?? [], live: true);
        var members = PoolMembers();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, updated = clock.GetUtcNow(),
            useConversationModelWhenEmpty = settings.UseConversationModelWhenEmpty,
            // The paired computers the owner keeps out of the pool (host IDs only); they never join by themselves.
            leftByOwner = settings.LeftByOwner,
            members = status.Members.Select(m => new
            {
                id = m.Id, name = m.Name, model = members.FirstOrDefault(p => p.Id == m.Id)?.Model,
                // A paired computer's member: the computer, its graphics card (1, or 2-4 for a Thinking pool model on an extra
                // card, a member of its own), its route and the cards the host said serve that route.
                host = HostOf(m.Id), card = settings.Members.FirstOrDefault(p => p.Key == m.Id) is { Place: DeepThinkingPlace.Host } own ? own.Card : (int?)null,
                route = settings.Members.FirstOrDefault(p => p.Key == m.Id) is { Place: DeepThinkingPlace.Host } routed ? routed.HostRoute : null,
                gpus = members.FirstOrDefault(p => p.Id == m.Id)?.Gpus ?? [],
                slots = m.Slots, used = m.Used, rank = m.Rank,
                // How many jobs it may start at once now: 0 while its provider asks Martlet to wait, fewer after it limited requests.
                slotsNow = m.SlotsNow, coolsUntil = m.CoolsUntil,
                vision = m.Can.HasFlag(ThinkingCapability.Vision), audio = m.Can.HasFlag(ThinkingCapability.Audio),
                // Whether it may take jobs that call tools (check-ins with tool sets): endpoint members only.
                tools = m.Can.HasFlag(ThinkingCapability.Tools),
                // Whether its computer answers now (HostPresence), and since when it doesn't.
                online = m.Online, offlineSince = HostOf(m.Id) is { } host ? HostPresence.OfflineSince(host) : null,
                // The owner's Quick jobs and Long jobs boxes on Companion › Thinking pool.
                quickJobs = settings.TakesQuickJobs(m.Id), longJobs = settings.TakesLongJobs(m.Id),
                // Whether it may receive pictures and recordings (an external member only when the owner ticked it).
                mayReceiveMedia = m.Media,
                // How smart its model is (the owner's choice, else Martlet's guess from the model name), and whether it was guessed.
                smarts = m.Smarts.ToString(), smartsGuessed = !settings.Smarts.ContainsKey(m.Id)
            }),
            // Runs on for each kind of job (any, prefer-smart, smart-only, members) and the chosen members of each that has them.
            runsOn = ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind => settings.RunsOn(ThinkingJobKinds.Name(kind)) is var where
                ? new { mode = where.Name, members = where.Members } : null),
            // The latest jobs: which member each went to and why (never a job's text).
            placements = status.Placements.Select(p => new
            {
                at = p.At, kind = p.Kind, runsOn = p.RunsOn, member = p.Member, memberId = p.MemberId, smarts = p.Smarts?.ToString(),
                outcome = p.Outcome, why = p.Why
            }),
            // The slots of the members that answer now, and of every member (when all answer).
            slots = status.Slots, free = status.Free, configuredSlots = status.ConfiguredSlots, keepsFastSlot = status.KeepsFastSlot,
            // Thinking longer and research use the conversation model because every member that would run is offline.
            conversationModelStandsIn = settings.Members.Count > 0 &&
                PoolPlan(Configuration?.Routes ?? [], live: true, longJobs: true).Spots is [{ Settings.Separate: false, Plan.Available: true }, ..],
            running = status.Running, waiting = status.Waiting, guidance = status.Guidance,
            // The live floor: its level, jobs waiting only for the conversation (held), and jobs it stopped this turn and in all.
            floor = status.Floor, waitingForConversation = status.Held, stoppedThisTurn = status.StoppedNow, stopped = status.Stopped,
            sharesLive = status.SharesLive,
            // The line's rules (Companion › Thinking pool) and, since the pool started, jobs stopped for higher-priority work,
            // priority raises after stops and retries after a failure.
            priority = new
            {
                preemptLowerPriority = status.Policy.PreemptLowerPriority, raiseAfterStops = status.Policy.RaiseAfterStops,
                retriesOnFailure = status.Policy.Retries, stoppedForPriority = status.StoppedForPriority, raised = status.Raised,
                retried = status.Retried
            },
            // Members whose computer refused a request as invalid: no such jobs go there until the time shown.
            resting = status.Resting.Select(r => new { id = r.Id, name = r.Name, needs = ThinkingJobResult.Describe(r.Needs), until = r.Until }),
            // Members whose providers limited requests: no new job until coolsUntil (null: it takes jobs again), and slotsNow of
            // slots at once until a run of successes raises it again.
            cooling = status.Cooling.Select(c => new
            {
                id = c.Id, name = c.Name, coolsUntil = c.Until, slotsNow = c.SlotsNow, slots = c.Slots, times = c.Times, problem = c.Problem,
                says = c.Describe(clock.GetUtcNow())
            }),
            warnings = ThinkingPoolWarnings.For(plan, Configuration?.Routes ?? [], HostRouteGpus.For),
            // Backup Thinking: its choices, the automatic delay from recent replies and how it ended lately (never what was said).
            backup = BackupStatus(settings)
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

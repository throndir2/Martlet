using System.Collections.Concurrent;
using System.Globalization;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Backup Thinking (Companion › Thinking pool › Backup Thinking; docs/CONVERSATION.md): a reply to the user whose Thinking request
// has no first words after a delay also asks a pool member the owner allowed to answer for the conversation, and the stream with
// words first gives the reply (ConversationTurn's race). The member is chosen when the delay passes
// (ThinkingBackupMembers.Choose). While its stream is read it is live work: its computer joins the live floor's resources (pool
// work there stops and waits), a paired computer keeps its graphics card for it, and its request goes ahead of background work.
internal sealed partial class LiveConversationController
{
    private const int KeptBackupResults = 10;
    private readonly FirstWordTimes firstWords = new();
    private readonly object backupGate = new();
    private string? firstWordsModel;
    private readonly ConcurrentDictionary<long, LiveResource> backupResources = new();
    private long backupLeases;
    private readonly Queue<(DateTimeOffset At, ThinkingBackupResult Result)> backupResults = new();
    private readonly Dictionary<ThinkingBackupOutcome, int> backupCounts = [];

    /// <summary>How fast the conversation's Thinking model began its recent replies (Backup Thinking's automatic delay).</summary>
    internal FirstWordTimes FirstWords => firstWords;

    /// <summary>Backup Thinking's delay now: the fixed one chosen on Companion › Thinking pool, else the automatic one.</summary>
    internal TimeSpan BackupDelay => firstWords.DelayFor(Volatile.Read(ref thinkingPool).BackupDelayMs);

    /// <summary>The Thinking pool settings the conversation uses now (thinking-pool.json); tests set them directly.</summary>
    internal ThinkingPoolSettings PoolSettings
    {
        get => Volatile.Read(ref thinkingPool);
        set => Volatile.Write(ref thinkingPool, value ?? new());
    }

    /// <summary>What Backup Thinking did in recent replies, newest last, and how often each way it ended.</summary>
    internal (IReadOnlyList<(DateTimeOffset At, ThinkingBackupResult Result)> Recent, IReadOnlyDictionary<ThinkingBackupOutcome, int> Counts) BackupHistory
    {
        get { lock (backupGate) return ([.. backupResults], new Dictionary<ThinkingBackupOutcome, int>(backupCounts)); }
    }

    // Backup Thinking for a reply about to start, or null when it is off or no member may answer for the conversation. Only
    // replies to the user, the ones that hold the live floor: never Martlet's own reports or replies to what this PC played.
    private IThinkingBackup? BackupFor(LiveConversationOperation operation)
    {
        var pool = Volatile.Read(ref thinkingPool);
        if (!pool.BackupThinking || pool.AnswersForConversation.Count == 0 || operation.Report ||
            operation.PcAudio && operation.UserWords is null && !operation.DiscordCall)
            return null;
        return new ConversationBackup(this, operation.Authorization.Configuration, BackupDelay);
    }

    private sealed class ConversationBackup(LiveConversationController owner, LiveConversationConfiguration configured, TimeSpan delay)
        : IThinkingBackup
    {
        private string? why;

        public TimeSpan Delay => delay;

        public async Task<ThinkingBackupStream?> OpenAsync(ConversationRequest reply, BoundedTextInput input, CorrelationIds ids, long epoch,
            bool held, CancellationToken token)
        {
            var (stream, reason) = await owner.OpenBackupAsync(configured, reply, input, ids, epoch, held, token).ConfigureAwait(false);
            why = reason;
            return stream;
        }

        public void Ended(ThinkingBackupResult result) =>
            owner.BackupEnded(result.Outcome == ThinkingBackupOutcome.NoMember && result.Why is null ? result with { Why = why } : result);

        public override string ToString() => nameof(ConversationBackup);
    }

    /// <summary>Opens Backup Thinking's stream for a reply's request on the member <see cref="ThinkingBackupMembers.Choose"/> picks
    /// now, with that member's own one-use authorization and slot, as live work (see the file's summary). Null, and why, when no
    /// member may take it or its stream couldn't be opened.</summary>
    internal async Task<(ThinkingBackupStream? Stream, string Why)> OpenBackupAsync(LiveConversationConfiguration configured,
        ConversationRequest reply, BoundedTextInput input, CorrelationIds ids, long epoch, bool held, CancellationToken token)
    {
        var routes = configured.Routes;
        var thinking = routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var limits = dataDirectory is null ? null : ModelLimits.Load(dataDirectory);
        var targets = new Dictionary<string, DeepThinkTarget>(StringComparer.Ordinal);
        // A member whose computer is offline now can't answer (HostPresence).
        var choice = ThinkingBackupMembers.Choose(Volatile.Read(ref thinkingPool), PoolPlan(routes, live: true), PoolMembers(), floorRules.Resources,
            input, held, member =>
            {
                DeepThinkTarget target;
                try { target = DeepThinkTarget.For(member, ThinkEffort.Medium, thinking, limits); }
                catch (ContractException) { return "isn't set up completely"; }
                targets[member.Key] = target;
                return input.Utf8Bytes > target.Input.MaxInputBytes || input.InputTokenReservation > target.Input.MaxInputTokens ||
                    input.History.Count > target.Input.MaxHistoryMessages ? "can't take a request this long" : null;
            });
        if (choice is not { Spot: { } spot, Place: { } place }) return (null, choice.Why);
        var member = spot.Settings;
        var chosen = targets[member.Key];
        var request = chosen.Backup(input, reply);
        var slots = poolSlots.GetOrAdd(place.Id, _ => []);
        if (!slots.TryTake(out var slot))
        {
            slot = new ThinkSlot();
            var bound = slot;
            slot.Credentials = new(() => Volatile.Read(ref bound.Authorization));
        }
        var own = new DeepThinkAuthorization(chosen, request, configured.Profile, thinking, vault, clock,
            clock.GetUtcNow() + reply.TextLimits.MaxRequestTime + TimeSpan.FromSeconds(5), media: true);
        Volatile.Write(ref slot.Authorization, own);
        var lease = new BackupLease(this, place, member, slot, slots);
        try
        {
            var stream = await ThinkRuntime(slot).OpenTextAsync(request, own, ids, epoch, token).ConfigureAwait(false);
            return (new(member.Describe(), stream, lease), choice.Why);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            return (null, $"{member.Describe()} couldn't be asked ({error.GetType().Name})");
        }
    }

    // A backup member's hold while its stream is read: its slot and one-use authorization, its computer among the live floor's
    // resources (pool work there stops and waits) and, on a paired computer, a hold on the graphics card that serves it.
    private sealed class BackupLease : IAsyncDisposable
    {
        private readonly LiveConversationController owner;
        private readonly long number;
        private readonly ThinkSlot slot;
        private readonly ConcurrentBag<ThinkSlot> slots;
        private int disposed;

        internal BackupLease(LiveConversationController owner, BackgroundPlace place, DeepThinkingSettings member, ThinkSlot slot,
            ConcurrentBag<ThinkSlot> slots)
        {
            this.owner = owner;
            this.slot = slot;
            this.slots = slots;
            number = Interlocked.Increment(ref owner.backupLeases);
            var host = member.Place == DeepThinkingPlace.Host ? member.HostId : null;
            owner.backupResources[number] = new("backup thinking", place.Machine, place.Gpus)
            {
                HostId = host, RouteId = host is null ? null : member.HostRoute
            };
            owner.UseLiveResources(owner.Configuration);
            if (host is not null) owner.HoldBackupGpu(host, member.HostRoute);
            Task.Run(owner.WriteFloorStatus).Forget();
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
            owner.backupResources.TryRemove(number, out _);
            owner.UseLiveResources(owner.Configuration);
            Volatile.Write(ref slot.Authorization, null);
            slots.Add(slot);
            Task.Run(owner.WriteFloorStatus).Forget();
            return ValueTask.CompletedTask;
        }
    }

    // A paired computer keeps the graphics card that serves the backup at once; while the floor is Live it renews the hold with
    // the conversation's own hosts.
    private void HoldBackupGpu(string host, string route)
    {
        var hold = GpuHold;
        Task.Run(async () =>
        {
            try { await hold.HoldAsync(host, [route], HoldTime, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ErrorLog.Info($"Backup Thinking: {host} didn't take the hold on its graphics card ({error.GetType().Name}).");
            }
        }).Forget();
        if (floor.Level == LiveFloorLevel.Live) HoldGpus();
    }

    // What Backup Thinking did in a reply: kept for thinking-pool-status.json and logged, off the reply's path.
    private void BackupEnded(ThinkingBackupResult result)
    {
        lock (backupGate)
        {
            backupResults.Enqueue((clock.GetUtcNow(), result));
            while (backupResults.Count > KeptBackupResults) backupResults.Dequeue();
            backupCounts[result.Outcome] = backupCounts.GetValueOrDefault(result.Outcome) + 1;
        }
        Task.Run(() =>
        {
            if (result.Describe() is { } line) ErrorLog.Info(line);
            WritePoolStatus();
        }).Forget();
    }

    // A finished reply's first words, from its Thinking request's start, for Backup Thinking's automatic delay. They are kept
    // per Thinking model: another model starts counting again.
    private void NoteFirstWords(LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        if (terminal.FirstTextAfter is not { } first || terminal.Timings?.TextRequestAfter is not { } asked) return;
        var model = configured.ToolModelKey();
        lock (backupGate)
        {
            if (firstWordsModel != model)
            {
                firstWords.Clear();
                firstWordsModel = model;
            }
        }
        firstWords.Add(first - asked);
    }

    /// <summary>Companion › Thinking pool's Backup Thinking line (MCP reads it as ThinkingPoolBackupStatus).</summary>
    internal static string BackupLine(ThinkingPoolSettings pool, IReadOnlyList<string> answering, TimeSpan? automatic, int replies)
    {
        if (!pool.BackupThinking) return "Off. Each reply waits for the conversation's own Thinking model.";
        if (answering.Count == 0)
            return "On, but no member may answer for the conversation yet. Tick May answer for the conversation on a member above.";
        var names = answering.Count == 1 ? answering[0] : string.Join(", ", answering.Take(answering.Count - 1)) + " and " + answering[^1];
        var wait = pool.BackupDelayMs is { } chosen
            ? string.Create(CultureInfo.InvariantCulture, $"{chosen} ms")
            : automatic is { } now
                ? string.Create(CultureInfo.InvariantCulture, $"{now.TotalMilliseconds:0} ms (automatic, from {replies} recent repl{(replies == 1 ? "y" : "ies")})")
                : "an automatic time (at least 900 ms)";
        return $"On: when the conversation's model has no words after {wait}, {names} may answer instead. " +
            "Whichever starts first gives the reply; the other stops at once.";
    }
}

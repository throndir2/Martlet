using System.IO;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Conversation;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Tidying memory in the background (docs/MEMORY.md, "Search words, history and tidying"): a while after facts change,
/// and again while work is left, each store this device may change gets one pass (<see cref="MemoryUpkeep"/>). Facts without
/// search words get them, duplicates are merged and facts a newer fact made outdated are kept as history. It runs only on a free
/// Thinking pool member that reads text, never on the conversation's own model, and touches a store only while no reply runs and
/// nobody else holds it, so a conversation never waits for it. How far each store is checked for duplicates and what the last
/// pass did are kept in memory-upkeep.json (counts and times only).</summary>
internal sealed partial class LiveConversationController
{
    internal const string MemoryUpkeepFile = "memory-upkeep.json";
    private static readonly TimeSpan UpkeepAfterChange = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpkeepBetweenPasses = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UpkeepWhenBusy = TimeSpan.FromMinutes(2);
    private readonly object upkeepGate = new();
    private readonly CancellationTokenSource upkeepStop = new();
    private bool upkeepQueued, upkeepRunning, upkeepWired, upkeepChanged, upkeepFailed;
    // Facts asked for words this session that the model gave none for (by ID and revision), so they aren't asked again.
    private readonly HashSet<(Guid, long)> upkeepAsked = [];

    /// <summary>The upkeep state file (memory-upkeep.json): per store, how far its facts are checked for duplicates.</summary>
    internal sealed record MemoryUpkeepState
    {
        public int SchemaVersion { get; init; } = 1;
        public Dictionary<Guid, Checked> Stores { get; init; } = [];
        public LastPass? Last { get; init; }
        public bool Stopped { get; init; }
        public string? Error { get; init; }

        public sealed record Checked(DateTimeOffset At, Guid Id);

        /// <summary>What the last pass did (counts only, never a fact).</summary>
        public sealed record LastPass(DateTimeOffset At, string Space, string Outcome, int Listed, int Groups, int NeedWords,
            int Retired, int Merged, int Worded, string? Member);
    }

    private static readonly JsonSerializerOptions UpkeepJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Starts following memory changes for upkeep (once; the main window calls it when Martlet starts).</summary>
    internal void StartMemoryUpkeep()
    {
        if (memory is null) return;
        lock (upkeepGate)
        {
            if (upkeepWired) return;
            upkeepWired = true;
        }
        memory.FactsChanged += () => QueueMemoryUpkeep(UpkeepAfterChange);
        SaveUpkeepState(LoadUpkeepState() with { Stopped = false, Error = null });
        QueueMemoryUpkeep(TimeSpan.FromMinutes(1));
    }

    /// <summary>One upkeep pass after <paramref name="delay"/> (one at a time; a pass already waiting stays).</summary>
    internal void QueueMemoryUpkeep(TimeSpan delay)
    {
        lock (upkeepGate)
        {
            if (upkeepFailed || upkeepStop.IsCancellationRequested) return;
            if (upkeepQueued)
            {
                if (upkeepRunning) upkeepChanged = true;
                return;
            }
            upkeepQueued = true;
        }
        RunUpkeepAsync(delay).Forget();

        async Task RunUpkeepAsync(TimeSpan wait)
        {
            var released = false;
            try
            {
                while (true)
                {
                    await Task.Delay(wait, clock, upkeepStop.Token).ConfigureAwait(false);
                    lock (upkeepGate)
                    {
                        if (upkeepFailed) return;
                        upkeepRunning = true;
                        upkeepChanged = false;
                    }
                    var next = await MemoryUpkeepPassAsync(upkeepStop.Token).ConfigureAwait(false);
                    lock (upkeepGate)
                    {
                        upkeepRunning = false;
                        if (upkeepFailed) return;
                        next ??= upkeepChanged ? UpkeepAfterChange : null;
                        if (next is null)
                        {
                            upkeepQueued = false;
                            released = true;
                            return;
                        }
                        wait = next.Value;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MemoryException or
                DesktopMemoryException or JsonException or InvalidOperationException)
            {
                FailMemoryUpkeep(LoadUpkeepState(), $"Tidying memory failed ({error.GetType().Name}).");
                ErrorLog.Warn("Tidying memory stopped until Martlet restarts.", error);
            }
            finally
            {
                if (!released)
                    lock (upkeepGate) { upkeepQueued = false; upkeepRunning = false; }
            }
        }
    }

    /// <summary>One pass over each store this device may change. Returns when to run again (work is left, or the pool or a
    /// store was busy), or null when nothing is left to do.</summary>
    internal async Task<TimeSpan?> MemoryUpkeepPassAsync(CancellationToken token)
    {
        lock (upkeepGate) if (upkeepFailed) return null;
        if (memory is null || disposed || !helpers.PoolHas(HelperCapability.Text)) return null;
        if (await memory.SpacesAsync(token).ConfigureAwait(false) is not { } set) return null;
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        var prompts = loaded.Settings?.Prompts;
        if (MemoryUpkeep.Instructions(prompts) is not { } instructions) return null;
        var state = LoadUpkeepState();
        TimeSpan? again = null;
        foreach (var space in set.Readable.Where(s => s.Writable))
        {
            token.ThrowIfCancellationRequested();
            if (Replying || ReplySpeaking()) return UpkeepWhenBusy;
            var roster = voices?.Roster;
            // The snapshot is taken while holding the store; the plan (which may compare vectors) is made after letting it go.
            var read = await memory.TryWithFreeStoreAsync(space, async (store, t) => await store.SnapshotAsync(t).ConfigureAwait(false),
                token, changes: false).ConfigureAwait(false);
            if (!read.Ran)
            {
                if (read.Why == "busy") again = UpkeepWhenBusy;
                continue;
            }
            var snapshot = read.Result!;
            var storeId = snapshot.StoreId;
            var mark = state.Stores.TryGetValue(storeId, out var seen) ? (seen.At, seen.Id) : ((DateTimeOffset, Guid)?)null;
            bool Asked(MemoryFact fact) { lock (upkeepGate) return upkeepAsked.Contains((fact.Id, fact.Revision)); }
            var plan = MemoryUpkeep.Plan(snapshot, memory.UtcNow, mark, Asked, id => MemoryPeople.Canonical(id, roster), token,
                MeaningNeighbors());
            if (!plan.HasWork)
            {
                if (plan.CheckedThrough is { } through) state = Checked(state, storeId, through);
                continue;
            }
            var people = MemoryPeople.Labels(plan.Facts, roster);
            var text = MemoryUpkeep.Text(plan, fact => MemoryPromptContext.Whose(fact, people));
            var result = await helpers.TryPoolAsync(HelperJobKind.MemoryUpkeep, "Tidying memory", HelperCapability.Text,
                () => new BoundedTextInput(text, instructions), token).ConfigureAwait(false);
            if (result is not { Answer: { } answer })
            {
                state = Record(state, space.Id, result is null ? "the pool was busy" : "failed", plan, null);
                if (result is not null)
                    FailMemoryUpkeep(state, "The Thinking pool could not tidy memory within its retry and time limits.");
                return result is null ? UpkeepWhenBusy : null;
            }
            var parsed = MemoryUpkeep.Parse(answer, plan);
            lock (upkeepGate)
                foreach (var number in plan.NeedWords.Where(n => !parsed.Words.ContainsKey(n)))
                    upkeepAsked.Add((plan[number].Id, plan[number].Revision));
            MemoryTidyReceipt? receipt = null;
            if (!parsed.IsEmpty)
            {
                if (Replying || ReplySpeaking()) return UpkeepWhenBusy;
                var applied = await memory.TryWithFreeStoreAsync(space, (store, t) => store.TidyAsync(MemoryUpkeep.Request(plan, parsed), t),
                    token).ConfigureAwait(false);
                if (!applied.Ran)
                {
                    Record(state, space.Id, "the store was busy", plan, null);
                    return UpkeepWhenBusy;
                }
                receipt = applied.Result;
                ErrorLog.Info($"Tidying memory: {receipt!.Retired} fact(s) kept as history, {receipt.Merged} duplicate(s) merged, " +
                    $"{receipt.Worded} fact(s) given search words.");
            }
            if (plan.CheckedThrough is { } checkedThrough) state = Checked(state, storeId, checkedThrough);
            state = Record(state, space.Id, "answered", plan, receipt);
            // Facts were left for the next pass.
            if (plan.Groups.Count == MemoryUpkeep.MaximumGroups || plan.NeedWords.Count == MemoryUpkeep.MaximumWordsFacts ||
                plan.Examined == MemoryUpkeep.MaximumExamined)
                again = UpkeepBetweenPasses;
        }
        SaveUpkeepState(state);
        return again;

        MemoryUpkeepState Record(MemoryUpkeepState current, string space, string outcome, MemoryUpkeepPlan plan, MemoryTidyReceipt? receipt)
        {
            var next = current with
            {
                Last = new(DateTimeOffset.UtcNow, space, outcome, plan.Facts.Count, plan.Groups.Count, plan.NeedWords.Count,
                    receipt?.Retired ?? 0, receipt?.Merged ?? 0, receipt?.Worded ?? 0,
                    helpers.Last.FirstOrDefault(r => r.Kind == HelperJobKind.MemoryUpkeep)?.Member)
            };
            SaveUpkeepState(next);
            return next;
        }
    }

    private void FailMemoryUpkeep(MemoryUpkeepState state, string error)
    {
        lock (upkeepGate) upkeepFailed = true;
        SaveUpkeepState(state with { Stopped = true, Error = error + " Tidying memory stopped until Martlet restarts." });
        ErrorLog.Warn(error + " Tidying memory stopped until Martlet restarts.");
    }

    private static MemoryUpkeepState Checked(MemoryUpkeepState state, Guid store, (DateTimeOffset At, Guid Id) through) =>
        state with { Stores = new(state.Stores) { [store] = new(through.At, through.Id) } };

    private MemoryUpkeepState LoadUpkeepState()
    {
        if (dataDirectory is null) return new();
        try
        {
            return JsonSerializer.Deserialize<MemoryUpkeepState>(File.ReadAllBytes(Path.Combine(dataDirectory, MemoryUpkeepFile)), UpkeepJson)
                is { SchemaVersion: 1, Stores: not null } state ? state : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new();
        }
    }

    private void SaveUpkeepState(MemoryUpkeepState state)
    {
        if (dataDirectory is null) return;
        var path = Path.Combine(dataDirectory, MemoryUpkeepFile);
        try
        {
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(state, UpkeepJson));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void StopMemoryUpkeep() => upkeepStop.Cancel();
}

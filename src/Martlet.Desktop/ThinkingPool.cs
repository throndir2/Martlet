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

    /// <summary>Whether a member can take a job of <paramref name="kind"/> needing <paramref name="needs"/> (no slot taken).</summary>
    internal bool CanRun(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text) => board.CanRun(kind, needs);

    /// <summary>The member such a job would go to first (its <see cref="BackgroundPlace.Name"/> and <see cref="BackgroundPlace.Model"/>),
    /// or null when no member can run it. Never the conversation's own route.</summary>
    internal BackgroundPlace? Find(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text) => board.Find(kind, needs);

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
    internal ThinkingPool ThinkingPool => pool ??= new(new ThinkingJobBoard(jobs.Places, PoolMembers, RunPoolJobAsync, clock));

    /// <summary>Reads the Thinking pool (thinking-pool.json, made from deep-thinking.json once) again, after Companion › Thinking
    /// pool saved it: the next reply offers think_longer only where the pool (or the conversation model) can run it.</summary>
    internal void ReloadThinkingPool()
    {
        Volatile.Write(ref thinkingPool, ThinkingPoolSettings.Load(dataDirectory));
        Volatile.Write(ref poolAbilities, dataDirectory is null ? null : ModelAbilities.Load(dataDirectory));
        WritePoolStatus();
    }

    /// <summary>What a member can do: pictures and recordings as its model is known to take them. A paired computer's Ollama
    /// takes no recordings.</summary>
    internal ThinkingCapability PoolCan(DeepThinkingSettings member) => ThinkingPoolCapabilities.For(member, Volatile.Read(ref poolAbilities));

    // The pool's members for jobs posted to the board: every usable member, never the conversation's own Thinking route.
    private IReadOnlyList<BackgroundPlace> PoolMembers()
    {
        var plan = PoolPlan(Configuration?.Routes ?? []);
        return [.. ThinkLonger.Places(plan, BackgroundDuties.Of(dataDirectory), PoolCan, HostRouteGpus.For).Where(place => place.Id != "thinking")];
    }

    private DeepThinkingPool PoolPlan(IReadOnlyList<SetupRoute> routes) =>
        Volatile.Read(ref thinkingPool).Plan(routes, WorkSharingRoster.Settings(dataDirectory), WorkSharingRoster.Device);

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
            image: job.Image, audio: job.Audio);
        var target = DeepThinkTarget.For(member, ThinkEffort.Medium, thinking, ModelLimits.Load(dataDirectory));
        if (input.Utf8Bytes > target.Input.MaxInputBytes || input.InputTokenReservation > target.Input.MaxInputTokens)
            return ThinkingAnswer.Failed($"the job is too long for {place.Name}");
        var request = target.OneShot(input, job.MaxOutputTokens, job.Reasoning, job.Timeout);
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
                clock.GetUtcNow() + job.Timeout + TimeSpan.FromSeconds(5), media: true);
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
            return outcome.Result is { } text ? ThinkingAnswer.Done(text, outcome.Cut)
                : ThinkingAnswer.Failed($"{place.Name}: {outcome.Problem}");
        }
        finally
        {
            Volatile.Write(ref slot.Authorization, null);
            slots.Add(slot);
        }
    }

    // thinking-pool-status.json: counts, names and kinds only (never a job's text).
    private void WritePoolStatus()
    {
        if (dataDirectory is null || disposed) return;
        try
        {
            var settings = Volatile.Read(ref thinkingPool);
            var status = ThinkingPool.Status();
            var plan = PoolPlan(Configuration?.Routes ?? []);
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, updated = clock.GetUtcNow(),
                useConversationModelWhenEmpty = settings.UseConversationModelWhenEmpty,
                members = status.Members.Select(m => new
                {
                    id = m.Id, name = m.Name, model = status.Members.Count > 0 ? PoolMembers().FirstOrDefault(p => p.Id == m.Id)?.Model : null,
                    slots = m.Slots, used = m.Used, rank = m.Rank,
                    vision = m.Can.HasFlag(ThinkingCapability.Vision), audio = m.Can.HasFlag(ThinkingCapability.Audio)
                }),
                slots = status.Slots, free = status.Free, keepsFastSlot = status.KeepsFastSlot,
                running = status.Running, waiting = status.Waiting, guidance = status.Guidance,
                // The live floor: its level, jobs waiting only for the conversation (held), and jobs it stopped this turn and in all.
                floor = status.Floor, waitingForConversation = status.Held, stoppedThisTurn = status.StoppedNow, stopped = status.Stopped,
                sharesLive = status.SharesLive,
                warnings = ThinkingPoolWarnings.For(plan, Configuration?.Routes ?? [])
            }, new JsonSerializerOptions { WriteIndented = true });
            var path = Path.Combine(dataDirectory, PoolStatusFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}

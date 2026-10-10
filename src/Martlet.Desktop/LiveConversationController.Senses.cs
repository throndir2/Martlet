using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

// Image and audio models (docs/SENSE_MODELS.md). Thinking, the text model, always writes the reply. A picture or recording whose
// kind has a model of its own (sense-models.json, Companion › Vision and Listening) goes to that model, which puts it into words
// for Thinking. Callers send such work here as sense jobs: one lane for each kind (SenseLanes), each job on its kind's pool
// (SensePool: the chosen model first, then other models that see or hear when it is busy or can't take it), each attempt on its
// own runtime slot with a one-use authorization bound to that model, as a Thinking pool job runs on its member. A reply never
// waits for a sense job.
internal sealed partial class LiveConversationController
{
    /// <summary>sense-models-status.json in the data directory: where pictures and recordings go now and how the image and audio
    /// models' recent jobs went (purposes, outcomes and times; never what was sent or said), and each kind's pool (the members the
    /// next job tries and which member took the last one), which MCP's sense_models_status reads.</summary>
    internal const string SenseStatusFile = "sense-models-status.json";

    private SenseModels senseModels = new();
    private SenseLanes? senseLanes;
    private readonly ConcurrentDictionary<string, ThinkSlot> senseSlots = new(StringComparer.Ordinal);
    private string? senseRoutesSaid;
    private int senseStatusPending;

    /// <summary>The image and audio models this PC uses (sense-models.json); tests set them directly.</summary>
    internal SenseModels SenseModels
    {
        get => Volatile.Read(ref senseModels);
        set
        {
            Volatile.Write(ref senseModels, value ?? new());
            SenseRoutesChanged();
        }
    }

    /// <summary>Reads sense-models.json again (Companion › Vision or Listening saved it): the next job, reply or look follows it.</summary>
    internal void ReloadSenseModels() => SenseModels = SenseModels.Load(dataDirectory);

    /// <summary>The image and audio models' lanes: one job at a time on each kind's model of its own.</summary>
    internal SenseLanes Senses => LazyInitializer.EnsureInitialized(ref senseLanes, NewSenseLanes);

    private SenseLanes NewSenseLanes()
    {
        var lanes = new SenseLanes(SenseRoute, RunSenseJobOrTestAsync, clock, SenseHeld);
        lanes.Changed += QueueSenseStatus;
        return lanes;
    }

    // The model's answer (or a test's, SenseRunner); a refused picture or recording is remembered either way.
    private async Task<SenseAnswer> RunSenseJobOrTestAsync(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token)
    {
        var answer = await (SenseRunner ?? RunSenseJobAsync)(kind, model, job, token).ConfigureAwait(false);
        if (answer.Refused) SenseRefused(model, sees: kind == SenseKind.Image ? false : null, hears: kind == SenseKind.Audio ? false : null);
        return answer;
    }

    /// <summary>Tests: answers a sense job in place of the model's request (<see cref="RunSenseJobAsync"/>); null (the default)
    /// sends it to the model.</summary>
    internal Func<SenseKind, DeepThinkingSettings, SenseJob, CancellationToken, Task<SenseAnswer>>? SenseRunner { get; set; }

    /// <summary>Tests: whether a kind's model shares the conversation's computer and graphics card, in place of reading the routes
    /// and their hardware; null (the default) reads them.</summary>
    internal Func<SenseKind, bool>? SenseSharing { get; set; }

    /// <summary>Tests: whether a second model fits beside Thinking's in Ollama on this PC (Thinking's model, then the sense model),
    /// in place of asking Ollama; null (the default) asks it.</summary>
    internal Func<string, string, CancellationToken, Task<SideBySideFit>>? SenseFit { get; set; }

    /// <summary>Where <paramref name="kind"/> goes now: in Thinking's own request, to the kind's model of its own to be put into
    /// words, or nowhere (docs/SENSE_MODELS.md). Cheap: no request and no file; a refusal recorded during use shows at once.</summary>
    internal SenseRoute SenseRoute(SenseKind kind) => SenseRoute(kind, Configuration);

    /// <summary>Where <paramref name="kind"/> goes for <paramref name="configured"/> (a turn's own configuration). Before a talk
    /// window loads the settings (null), a model of its own still goes by what Martlet found out about it (model-abilities.json),
    /// and a kind that goes to the text model says to set up Thinking.</summary>
    internal SenseRoute SenseRoute(SenseKind kind, LiveConversationConfiguration? configured) =>
        SenseRouting.For(kind, SenseModels, configured?.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm),
            configured?.Abilities ?? Volatile.Read(ref poolAbilities),
            configured?.Vision() ?? VisionSupport.Unknown, configured?.Hearing() ?? HearingSupport.Unknown);

    /// <summary>Runs <paramref name="job"/> on <paramref name="kind"/>'s model of its own (<see cref="SenseLanes.RunAsync"/>):
    /// NoModel at once when the kind goes to Thinking or nowhere. Canceling <paramref name="token"/> throws
    /// <see cref="OperationCanceledException"/>. While the conversation needs the model's hardware (<see cref="SenseHeld"/>) the
    /// job doesn't start, and a running one ends Preempted. A reply never waits for it.</summary>
    internal Task<SenseJobResult> RunSenseAsync(SenseKind kind, SenseJob job, CancellationToken token) => Senses.RunAsync(kind, job, token);

    /// <summary>Whether <paramref name="kind"/>'s model of its own shares the live conversation's computer and graphics card, as
    /// a Thinking pool member would (<see cref="LiveResources.Shares"/>: the conversation's Thinking, voice or listening runs
    /// there). False when the kind has no model of its own.</summary>
    internal bool SenseSharesConversation(SenseKind kind) =>
        SenseSharing is { } decide ? decide(kind)
            : SenseRoute(kind) is { Described: true, Model: { } model } && floorRules.Resources.Shares(SensePlace(kind, model));

    /// <summary>Whether <paramref name="kind"/>'s model must leave the hardware to the conversation now: it shares the
    /// conversation's computer and graphics card (<see cref="SenseSharesConversation"/>), and a reply's (or a look's) turn runs
    /// and its voice isn't all made yet (<see cref="ConversationTurn.Synthesized"/>: its Thinking request and its voice may need
    /// the same graphics card). Then a sense job of that kind doesn't start, and one that runs is stopped
    /// (<see cref="SenseLanes"/>), so the time to Martlet's first word never grows and the voice never falls behind.</summary>
    internal bool SenseHeld(SenseKind kind) => ReplyMakingItsVoice() && SenseSharesConversation(kind);

    // A reply's (or a look's) turn runs and its voice isn't all made yet (for a reply without a voice: its text isn't written).
    private bool ReplyMakingItsVoice()
    {
        lock (gate)
            return active is { Turn: { } turn, Status.Finished: false, OwnershipReleased: false } && !turn.Synthesized.IsCompleted;
    }

    private static BackgroundPlace SensePlace(SenseKind kind, DeepThinkingSettings model) =>
        new("sense:" + kind.ToString().ToLowerInvariant(), kind == SenseKind.Image ? "image model" : "audio model")
        {
            Model = model.ModelId, Machine = LiveResources.MachineOf(model),
            Gpus = HostRouteGpus.For(model) is { Count: > 0 } cards ? [.. cards.Take(16)] : []
        };

    // A sense job on the kind's pool (SensePool): the chosen model first, then the members whose model takes the kind. A free
    // chosen model gets the job at once, as before the pool: nothing is added on the way.
    private async Task<SenseAnswer> RunSenseJobAsync(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token)
    {
        IReadOnlyList<DeepThinkingSettings> members = job.OnlyChosen ? [model] : SenseMembers(kind, model);
        var (answer, route) = await SensePool.RunAsync(kind, members,
            (member, t) => (SenseAttemptRunner ?? AttemptSenseAsync)(kind, member, member.Key != model.Key, job, t),
            clock.GetUtcNow() + job.Timeout, clock, token).ConfigureAwait(false);
        NoteSensePool(kind, model, members.Count, route);
        return answer;
    }

    /// <summary>Tests: answers one attempt of a sense job on one pool member (kind, member, whether it comes after the chosen
    /// model, job) in place of <see cref="AttemptSenseAsync"/>; null (the default) sends it to the member.</summary>
    internal Func<SenseKind, DeepThinkingSettings, bool, SenseJob, CancellationToken, Task<SenseAttempt>>? SenseAttemptRunner { get; set; }

    /// <summary>The members a job of <paramref name="kind"/> tries now, first to last (<see cref="SensePool.Members"/>): the
    /// chosen model, then the Thinking pool's members whose model takes the kind. Left out: a computer a friend shares, a
    /// computer Devices › Sharing work keeps for other companion PCs, and a member on the conversation's own computer and graphics
    /// card (its Thinking, voice or listening), so the conversation keeps its model's cache. No request and no file read.</summary>
    internal IReadOnlyList<DeepThinkingSettings> SenseMembers(SenseKind kind, DeepThinkingSettings chosen)
    {
        var configured = Configuration;
        var thinking = configured?.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = configured?.Abilities ?? Volatile.Read(ref poolAbilities);
        return SensePool.Members(kind, chosen, Volatile.Read(ref thinkingPool), thinking, abilities, member =>
            member is { Place: DeepThinkingPlace.Host, HostId: { } host } &&
                (WorkSharingRoster.IsShared(host) || !WorkSharingRoster.Settings(dataDirectory).Allows(host, WorkSharingRoster.Device)) ||
            floorRules.Resources.Shares(SensePlace(kind, member)));
    }

    // One attempt of a sense job on one pool member: the request fitted to the member's model, on its own runtime slot and one-use
    // authorization (as RunPoolJobAsync runs a pool job). A member that can't take it now passes it on (SenseAttempt); a member after
    // the chosen model that refuses the picture or recording is remembered as not seeing or hearing and passes it on too.
    private async Task<SenseAttempt> AttemptSenseAsync(SenseKind kind, DeepThinkingSettings model, bool fallback, SenseJob job,
        CancellationToken token)
    {
        var configured = Configuration;
        var routes = configured?.Routes ?? [];
        var thinking = routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var name = model.Describe();
        if (model is { Place: DeepThinkingPlace.Host, HostId: { } host } && HostPresence.IsOffline(host))
            return SenseAttempt.Unavailable($"{host} is offline");
        // A second model in Ollama on this PC runs only while it fits beside Thinking's, so the conversation keeps its cache.
        if (DeepThinkingPlan.For(model, routes).ChecksFit && thinking?.ModelId is { } thinkingModel)
        {
            var fit = await (SenseFit is { } check ? check(thinkingModel, model.ModelId!, token)
                : LocalDeepThinking.CheckAsync(thinkingModel, model.ModelId!, loadThinking: false, token)).ConfigureAwait(false);
            if (!fit.Fits) return SenseAttempt.Unavailable(fit.Why.TrimEnd('.'));
        }
        BoundedTextInput input;
        try { input = new(job.Text, job.Instructions, image: job.Image, audio: job.Audio); }
        catch (ContractException) { return SenseAttempt.Done(SenseAnswer.Failed("the job is too large")); }
        var target = DeepThinkTarget.For(model, ThinkEffort.Medium, thinking, ModelLimits.Load(dataDirectory), pooled: true);
        if (input.Utf8Bytes > target.Input.MaxInputBytes || input.InputTokenReservation > target.Input.MaxInputTokens)
            return SenseAttempt.Unavailable($"the job is too long for {name}");
        var request = target.OneShot(input, job.MaxOutputTokens, job.Reasoning, job.Timeout);
        // One slot for each kind and member: a kind runs one job at a time (SenseLanes), so a slot never has two.
        var slot = senseSlots.GetOrAdd(Word(kind) + "|" + model.Key, _ => NewSenseSlot());
        try
        {
            var own = new DeepThinkAuthorization(target, request, configured?.Profile ?? Guid.Empty, thinking, vault, clock,
                clock.GetUtcNow() + job.Timeout + TimeSpan.FromSeconds(5), media: true);
            Volatile.Write(ref slot.Authorization, own);
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var started = ThinkRuntime(slot).Start(request, own, token, $"{kind} sense job on {name}");
            var terminal = await started.Completion.ConfigureAwait(false);
            await started.OwnershipRelease.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var text = started.Content.Text;
            // A model that refuses the recording is asked again without it, so that answer isn't about the recording.
            var refused = job.Audio is not null && terminal.AudioRejected ? SenseAnswer.Rejected($"{name} refused the recording")
                : job.Image is not null && string.IsNullOrWhiteSpace(text) && terminal.ProviderFailure is ProviderFailureCode.RequestRejected or
                    ProviderFailureCode.ModelUnsupported or ProviderFailureCode.FormatRejected ? SenseAnswer.Rejected($"{name} refused the picture")
                : null;
            if (refused is not null)
            {
                if (!fallback) return SenseAttempt.Done(refused);
                SenseRefused(model, sees: kind == SenseKind.Image ? false : null, hears: kind == SenseKind.Audio ? false : null);
                return SenseAttempt.Unavailable(refused.Problem!);
            }
            var outcome = ThinkLonger.Outcome(terminal, text);
            if (outcome.Result is { } words) return SenseAttempt.Done(SenseAnswer.Done(words));
            var problem = $"{name}: {outcome.Problem}";
            if (model is { Place: DeepThinkingPlace.Host, HostId: { } on })
            {
                // Its computer keeps its graphics card for a live turn, or does another job: another member, or a wait.
                if (HostLiveHolds.Since(on, began)) return SenseAttempt.Held($"{name} keeps its graphics card for a live conversation");
                if (HostBusy.Since(on, began)) return SenseAttempt.Busy($"{name} is busy with another job");
                // Its computer didn't answer: offline at once, so the next job finds out without a request.
                if (terminal.ProviderFailure == ProviderFailureCode.Network && !HostPresence.IsOffline(on))
                {
                    ErrorLog.Warn($"The {Word(kind)} model couldn't reach {on}.");
                    HostPresence.Note(on, false);
                }
            }
            // Its computer or provider can't take it now: the next member may.
            return terminal.ProviderFailure is ProviderFailureCode.Network or ProviderFailureCode.Server or ProviderFailureCode.RateLimited or
                ProviderFailureCode.QuotaExceeded or ProviderFailureCode.ModelNotFound or ProviderFailureCode.ModelRetired or
                ProviderFailureCode.CredentialUnavailable or ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied
                ? SenseAttempt.Unavailable(problem) : SenseAttempt.Done(SenseAnswer.Failed(problem));
        }
        finally { Volatile.Write(ref slot.Authorization, null); }
    }

    private readonly object sensePoolGate = new();
    private readonly (int Jobs, int Elsewhere, int Waited, SensePoolRoute? Last, DateTimeOffset? At, string? Chosen)[] sensePool = new
        (int, int, int, SensePoolRoute?, DateTimeOffset?, string?)[2];

    // A job went through its pool: the counts and the last route for the status file, and a log line when a member other than the
    // chosen model took it or it waited for one (never what was sent).
    private void NoteSensePool(SenseKind kind, DeepThinkingSettings chosen, int members, SensePoolRoute route)
    {
        var waited = route.Waited >= TimeSpan.FromMilliseconds(50) && route.Busy > 0;
        lock (sensePoolGate)
        {
            var now = sensePool[(int)kind];
            sensePool[(int)kind] = (now.Jobs + 1, now.Elsewhere + (route.Elsewhere ? 1 : 0), now.Waited + (waited ? 1 : 0), route,
                clock.GetUtcNow(), chosen.Describe());
        }
        if (route.Elsewhere || waited)
            ErrorLog.Info($"{(kind == SenseKind.Image ? "Image" : "Audio")} model pool: " +
                (route.Member is null ? $"no model of {members} took a job" : $"{route.Name} took a job" +
                    (route.Elsewhere ? $" in place of {chosen.Describe()}" : "")) +
                $" ({route.Busy} busy, {route.Unavailable} not able to take it{(waited ? $", waited {route.Waited.TotalMilliseconds:0} ms" : "")}).");
    }

    // The pool's part of sense-models-status.json for one kind: the members the next job tries, in order, and how jobs went.
    private object SensePoolStatus(SenseKind kind, SenseRoute route)
    {
        (int Jobs, int Elsewhere, int Waited, SensePoolRoute? Last, DateTimeOffset? At, string? Chosen) now;
        lock (sensePoolGate) now = sensePool[(int)kind];
        IReadOnlyList<DeepThinkingSettings> members = route is { Described: true, Model: { } model } ? SenseMembers(kind, model) : [];
        return new
        {
            lane = SensePool.Lane(kind),
            members = members.Select((m, i) => new { position = i, key = m.Key, name = m.Describe(), place = m.Place.ToString(), chosen = i == 0 }),
            jobs = now.Jobs, elsewhere = now.Elsewhere, waited = now.Waited,
            last = now.Last is not { } last ? null : new
            {
                member = last.Member, name = last.Name, position = last.Position, chosen = now.Chosen, busy = last.Busy,
                unavailable = last.Unavailable, waitedMs = Math.Round(last.Waited.TotalMilliseconds), at = now.At
            }
        };
    }

    private static ThinkSlot NewSenseSlot()
    {
        var slot = new ThinkSlot();
        slot.Credentials = new(() => Volatile.Read(ref slot.Authorization));
        return slot;
    }

    private static string Word(SenseKind kind) => kind == SenseKind.Image ? "image" : "audio";

    /// <summary>A helper job with a picture (finding touch zones, measuring the eyes) on the image model of its own, in place of the
    /// Thinking model: pictures go to the image model (Companion › Vision). It waits behind the image model's other jobs, at the
    /// lowest priority, and may write and run as much as on a Thinking pool member. The answer, or null with why not; when the
    /// image model no longer takes pictures, the Thinking model gets the job as before.</summary>
    private async Task<(string? Answer, string? Failure)> AskImageModelAsync(HelperJobKind kind, string purpose, string instructions,
        string text, BoundedImage image, CancellationToken token)
    {
        SenseJobResult result;
        try
        {
            result = await RunSenseAsync(SenseKind.Image, new SenseJob
            {
                Purpose = HelperJobs.Name(kind), Priority = HelperPriority, Instructions = instructions, Text = text, Image = image,
                Timeout = kind == HelperJobKind.TouchZones ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(5), DropWhenStale = false,
                MaxOutputTokens = SenseJob.MaximumOutputTokens, Reasoning = null
            }, token).ConfigureAwait(false);
        }
        catch (ContractException) { return (null, "the request is too large"); }
        if (result.Outcome == SenseJobOutcome.NoModel) return await AskThinkingAsync(purpose, instructions, text, image, token).ConfigureAwait(false);
        if (result.Succeeded)
        {
            ErrorLog.Info($"{purpose}: the image model ({result.Model}) did it in {result.Took.TotalMilliseconds:0} ms.");
            return (result.Text, null);
        }
        ErrorLog.Warn($"{purpose}: the image model ({result.Model}) didn't do it ({result.Outcome}: {result.Problem}).");
        return (null, result.Problem ?? result.Outcome.ToString());
    }

    /// <summary>The image model's priority for a helper job: after replies, looks and summaries.</summary>
    internal const int HelperPriority = -10;

    // A model of its own refused a picture or a recording: Martlet remembers that it can't see or hear (model-abilities.json; a
    // paired computer's model by its gateway's origin), so the kind's route says so and sends it nothing more until a check or a
    // test says otherwise.
    private void SenseRefused(DeepThinkingSettings model, bool? sees = null, bool? hears = null)
    {
        var origin = model.Place == DeepThinkingPlace.Host ? model.HostOrigin : model.Origin;
        if (origin is null || model.ModelId is not { } id) return;
        var ability = new ModelAbility
        {
            Origin = origin, ModelId = id, Sees = sees, Hears = hears, Source = hears is not null ? "a refused recording" : "a refused picture",
            CheckedAt = clock.GetUtcNow()
        };
        // Without a data folder (tests) nothing is saved, but the running conversation still follows it at once.
        if (dataDirectory is not null) RecordAbility(ability);
        else if (Configuration is { } configured) configured.UseAbilities(configured.Abilities.With(ability));
        ErrorLog.Info($"{model.Describe()} refused {(hears is not null ? "a recording; Martlet remembers that it can't hear" : "a picture; Martlet remembers that it can't see")}.");
        SenseRoutesChanged();
    }

    // Says where pictures and recordings go when that changes (and a kind has, or had, a model of its own), and writes the status.
    private void SenseRoutesChanged()
    {
        if (disposed) return;
        var image = SenseRoute(SenseKind.Image);
        var audio = SenseRoute(SenseKind.Audio);
        var said = $"{image.Why} {audio.Why}";
        var before = Interlocked.Exchange(ref senseRoutesSaid, said);
        if (before != said && (before is not null || image.Described || audio.Described))
            ErrorLog.Info($"Image and audio models: {said}");
        QueueSenseStatus();
    }

    private void QueueSenseStatus()
    {
        if (dataDirectory is null || Interlocked.Exchange(ref senseStatusPending, 1) == 1) return;
        Task.Run(() =>
        {
            Volatile.Write(ref senseStatusPending, 0);
            WriteSenseStatus();
        }).Forget();
    }

    private void WriteSenseStatus()
    {
        if (dataDirectory is null || disposed) return;
        try
        {
            var path = Path.Combine(dataDirectory, SenseStatusFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, SenseStatusJson());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    /// <summary>What sense-models-status.json says now.</summary>
    internal string SenseStatusJson()
    {
        var lanes = Senses.Status();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, updated = clock.GetUtcNow(),
            // Whether a conversation loaded the settings (a talk window opened); before that a kind that goes to the text model says
            // to set up Thinking, and the routes of models of their own come from model-abilities.json alone.
            conversation = Configuration is not null,
            senses = new[] { SenseKind.Image, SenseKind.Audio }.Select(kind =>
            {
                var route = SenseRoute(kind);
                var lane = lanes.First(l => l.Kind == kind);
                return new
                {
                    kind = kind.ToString(), path = route.Path.ToString(), model = route.Model?.Describe(), unknown = route.Unknown, why = route.Why,
                    sharesConversation = SenseSharesConversation(kind), busy = lane.Busy, waiting = lane.Waiting, held = lane.Held, runs = lane.Runs,
                    last = lane.LastOutcome is null ? null : new
                    {
                        purpose = lane.LastPurpose, outcome = lane.LastOutcome.ToString(), ms = lane.LastMilliseconds, at = lane.LastAt,
                        model = lane.LastModel, problem = lane.LastProblem
                    },
                    pool = SensePoolStatus(kind, route)
                };
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

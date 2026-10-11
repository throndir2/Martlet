using Martlet.Conversation;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;

namespace Martlet.Mcp;

/// <summary>recommended_setup_status: Home's Recommended setup without the desktop. It builds the network recommender's request
/// with the desktop's own builder (RecommendedSetupInputs) from a data directory (hosts.json, host-hardware.json, cluster.json,
/// settings.json, work-sharing.json, thinking-pool.json, speaking-engine.txt) or from the built-in fixture network, runs the
/// production recommender (<see cref="NetworkRecommender"/>) and lists the computers, today's jobs, the recommended changes
/// and whether a companion PC in use would ask (SetupAskRule, with this data directory's declined setups). Read-only: it
/// contacts nothing (lookOnThisPc only asks the model apps on this PC at 127.0.0.1), and it reads no keys. A data directory has no live host checks: a host the presence report
/// (node-presence.json, written by the desktop) last saw not answering counts as offline, as the desktop plans it; every
/// other host counts as online, and its roles are the shared plan's record. This PC's own hardware is its host service's
/// report (the desktop reads it live).</summary>
internal static class RecommendedSetupStatus
{
    internal const string FixtureName = "network";
    internal const string OfflineFixtureName = "offline";
    internal const string ServedFixtureName = "served";
    internal const string HostModelsFixtureName = "hostmodels";
    internal const string BetterFixtureName = "better";

    /// <summary>The made-up measurements of the "better" fixture: Gemma 4 E2B on gpu-box.</summary>
    private static (MeasuredModelMemory Memory, MeasuredFirstWords Speed) BetterMeasurements(DateTimeOffset now)
    {
        var speed = new MeasuredFirstWords();
        foreach (var ms in new[] { 170, 180, 195 }) speed = speed.With("https://gpu-box.lan:8443", "gemma4:e2b", ms, now);
        var memory = new MeasuredModelMemory().With(new MeasuredModelUse
        {
            Host = "https://gpu-box.lan:8443", Model = "gemma4:e2b", Bytes = 3_600_000_000, GraphicsBytes = 3_600_000_000, ContextTokens = 8192, MeasuredAt = now
        });
        return (memory, speed);
    }

    /// <summary><paramref name="lookOnThisPc"/>: with a data directory whose Use models your apps already run is on, Martlet
    /// asks the model apps on this PC (127.0.0.1 only) which models they serve, as the desktop does. <paramref name="preferences"/>:
    /// recommendation preferences to plan with instead of the saved ones (each field optional; nothing is saved).</summary>
    internal static async Task<object> RunAsync(string? dataDirectory, string? fixture, CancellationToken cancellation, bool lookOnThisPc = false,
        JsonElement? preferences = null, string? catalogChoice = null)
    {
        SetupSources sources;
        string source;
        var looked = false;
        if (fixture is not null)
        {
            if (fixture == FixtureName)
            {
                sources = RecommendedSetupInputs.Fixture(DateTimeOffset.UtcNow);
                source = "fixture network (NOT real computers): this PC (a companion PC with an RTX 4080 that runs Thinking, Speaking and " +
                    "Listening on its own host service), gpu-box (a Linux host PC with an RTX 4090 and nothing installed), DIVA (a companion " +
                    "PC whose host service runs Deep thinking) and old-box (a host that hasn't reported its hardware)";
            }
            else if (fixture == OfflineFixtureName)
            {
                sources = RecommendedSetupInputs.OfflineFixture(DateTimeOffset.UtcNow);
                source = "fixture offline (NOT real computers): this PC (a companion PC without a graphics card) and two hosts, MIKU and " +
                    "IMOUTO, that haven't answered for 155 minutes; MIKU ran Thinking, Speaking and Lip-sync, IMOUTO ran Listening; no API key is saved";
            }
            else if (fixture == ServedFixtureName)
            {
                sources = RecommendedSetupInputs.ServedFixture(DateTimeOffset.UtcNow);
                source = "fixture served (NOT real computers or apps): this PC alone (a companion PC with an RTX 5090, 32 GB) that thinks with " +
                    "Gemma 4 E2B in Martlet's Ollama; LM Studio on it serves qwen3-32b and an embedding model, and Ollama serves llama3.3:70b";
            }
            else if (fixture == HostModelsFixtureName)
            {
                sources = RecommendedSetupInputs.HostModelsFixture(DateTimeOffset.UtcNow);
                source = "fixture hostmodels (NOT real computers or models): this PC (a companion PC without a graphics card) and gpu-box " +
                    "(a Linux host with an RTX 4090, 24 GB) that thinks with Gemma 4 E4B and keeps qwen2.5:14b downloaded; Prefer models " +
                    "your hosts already have is on";
            }
            else if (fixture == BetterFixtureName)
            {
                sources = RecommendedSetupInputs.BetterFixture(DateTimeOffset.UtcNow);
                source = "fixture better (NOT real computers or measurements): this PC (a companion PC without a graphics card) and gpu-box " +
                    "(a Linux host with an RTX 4090, 24 GB) that thinks with Gemma 4 E2B; made-up measurements say its first word came in " +
                    "0.18 s over 3 replies and Ollama held it in 3.6 GB on the card. A smarter model fits there, so Thinking gets a suggestion " +
                    "(pass preferences.locks [{ job: \"thinking\", locked: false }] to let Martlet choose it)";
            }
            else throw new ArgumentException($"fixture must be \"{FixtureName}\", \"{OfflineFixtureName}\", \"{ServedFixtureName}\", \"{HostModelsFixtureName}\" or \"{BetterFixtureName}\".");
        }
        else
        {
            ArgumentNullException.ThrowIfNull(dataDirectory);
            sources = await FromDataDirectoryAsync(dataDirectory, cancellation);
            source = "data directory";
            sources = sources with { Preferences = RecommendationPreferences.Load(dataDirectory) };
        }
        var savedHere = fixture is null && RecommendationPreferences.Saved(dataDirectory);
        var chosen = Override(sources.Preferences, preferences);
        sources = RecommendedSetupInputs.WithPreferences(sources, chosen);
        if (fixture is null && lookOnThisPc && chosen.UseServedModels)
        {
            sources = sources with
            {
                ServedModels = RecommendedSetupInputs.Served(await Martlet.Providers.LocalModelServers.DetectAsync(cancellationToken: cancellation))
            };
            looked = true;
        }
        var build = RecommendedSetupInputs.Request(sources);
        // The planners' options (the model catalog with its local facts, then Martlet's own list) with the numbers measured on your
        // computers (model-memory.json and model-speed.json), as the desktop plans.
        var (measuredMemory, measuredSpeed) = fixture == BetterFixtureName ? BetterMeasurements(DateTimeOffset.UtcNow)
            : fixture is null ? (MeasuredModelMemory.Load(dataDirectory), MeasuredFirstWords.Load(dataDirectory)) : (new MeasuredModelMemory(), new MeasuredFirstWords());
        var catalog = PlanningOptions(fixture is null ? dataDirectory : null, catalogChoice, build.Request).WithMeasured(measuredMemory, measuredSpeed);
        var recommendation = NetworkRecommender.Recommend(build.Request, catalog);
        var planned = catalog.WithServed(build.Request.ServedModels);
        var memory = fixture is null ? RecommendedSetupMemory.Load(dataDirectory) : new RecommendedSetupMemory();
        var (step, why) = SetupAskRule.Decide(recommendation, memory, companion: true, idle: TimeSpan.Zero);
        string Name(string? id) => id is null ? "" : build.Names.GetValueOrDefault(id) ?? id;
        var retired = fixture is null ? await RetiredAsync(dataDirectory!, cancellation) : null;
        return new
        {
            source,
            measured = new
            {
                firstWords = measuredSpeed.Models.Select(m => new { host = m.Host, model = m.Model, ms = m.Ms, replies = m.Samples.Count, measuredAt = m.MeasuredAt }),
                memory = measuredMemory.Models.Select(m => new
                {
                    host = m.Host, model = m.Model, gb = Math.Round(m.Bytes / 1e9, 2), graphicsGb = Math.Round(m.GraphicsBytes / 1e9, 2),
                    onGraphicsCard = m.OnGraphicsCard, contextTokens = m.ContextTokens, measuredAt = m.MeasuredAt
                }),
                replaced = catalog.Measurements.Select(m => new
                {
                    option = m.OptionId, model = m.Model, host = m.Host, firstWordMs = m.FirstWordMs, estimatedFirstWordMs = m.EstimatedFirstWordMs,
                    replies = m.Replies, gpuGb = m.GpuGb, estimatedGpuGb = m.EstimatedGpuGb, describe = m.Describe()
                }),
                note = fixture == BetterFixtureName ? "FIXTURE: these measurements are made up."
                    : fixture is not null ? "A fixture has no measurements."
                    : "From model-speed.json (each reply's first word, after the reply) and model-memory.json (Ollama's /api/ps here and on paired hosts)."
            },
            locks = RecommendationPreferences.LockableJobs.Select(job => new
            {
                job, today = NetworkRecommender.TodayChoice(build.Request, job, catalog), state = sources.Preferences.LockState(job,
                    NetworkRecommender.TodayChoice(build.Request, job, catalog)).ToString(), unlocked = build.Request.Unlocked.Contains(job),
                chose = sources.Preferences.Locks.FirstOrDefault(l => l.Job == job)?.Chose
            }),
            suggestions = recommendation.Suggestions.Select(s => new
            {
                job = s.Job, option = s.OptionId, host = s.HostId is null ? null : Name(s.HostId), today = s.TodayOptionId, kind = s.Kind.ToString(),
                why = s.Why, locked = s.Locked, applied = s.Applied, text = s.Text
            }),
            betterSetup = new
            {
                available = recommendation.BetterSetupAvailable, fingerprint = recommendation.SuggestionsFingerprint,
                seenHere = memory.WasDeclined("better:" + recommendation.SuggestionsFingerprint),
                homeButton = recommendation.BetterSetupAvailable && !memory.WasDeclined("better:" + recommendation.SuggestionsFingerprint)
                    ? "A better setup is available" : null
            },
            retired,
            computers = sources.Computers.Select(c => new
            {
                id = c.Id, name = c.Name, kind = c.Kind.ToString(), thisPc = c.ThisPc, hasHostService = c.HasHostService, manageable = c.Manageable,
                online = c.Reachable != false, planned = build.Request.Machines.Any(m => m.Specs.Id == c.Id),
                hardware = build.Request.Machines.FirstOrDefault(m => m.Specs.Id == c.Id)?.Specs is { } specs
                    ? $"{string.Join(", ", specs.Gpus.Select(g => $"{g.Name} {g.VramGb:0.#} GB"))}{(specs.Gpus.Count == 0 ? "no graphics card" : "")}; " +
                      $"{specs.RamGb:0} GB memory; {specs.CpuThreads} threads"
                    : "not reported",
                roles = build.Request.Machines.FirstOrDefault(m => m.Specs.Id == c.Id)?.Roles.Select(r => r.Model is null ? r.Kind : $"{r.Kind}={r.Model}") ?? [],
                downloads = build.Request.Machines.FirstOrDefault(m => m.Specs.Id == c.Id)?.Downloaded.Select(r => $"{r.Kind}={r.Model}") ?? []
            }),
            notes = build.Notes,
            preferences = new
            {
                saved = savedHere,
                overridden = preferences is not null,
                quality = chosen.Quality.ToString(),
                firstWordTargetMs = chosen.FirstWordTargetMs,
                online = chosen.Online.ToString(),
                hosting = build.Request.Preference.ToString(),
                preferHearing = chosen.PreferHearing,
                hostGpuShare = chosen.HostGpuShare,
                useServedModels = chosen.UseServedModels,
                preferHostModels = chosen.PreferHostModels,
                games = build.Games.Select(g => new { device = g.Device, computer = g.Name, plays = g.Plays, answered = g.Answered }),
                keepGpuForGames = build.Request.Machines.Where(m => m.Specs.KeepGpuForGames).Select(m => Name(m.Specs.Id)),
                describe = chosen.Describe()
            },
            servedModels = new
            {
                use = chosen.UseServedModels,
                looked = looked || fixture == ServedFixtureName,
                found = build.Request.ServedModels.Select(m => new
                {
                    model = m.ModelId, app = m.AppName, address = m.BaseUrl, computer = Name(m.MachineId), chat = ServedModels.Chats(m.ModelId),
                    downloadGb = m.SizeGb, estimatedGb = ServedModels.Gb(m), option = ServedModels.OptionId(m.ModelId)
                }),
                note = fixture == ServedFixtureName ? "FIXTURE: these apps and models are made up."
                    : looked ? "Martlet asked the model apps on this PC (127.0.0.1 only)."
                    : !chosen.UseServedModels ? "Use models your apps already run is off."
                    : "Not looked: a data directory contacts nothing. Pass lookOnThisPc to ask the model apps on this PC."
            },
            hostModels = new
            {
                prefer = build.Request.PreferHostModels,
                found = build.Request.Machines.Where(m => m.Online && m.HasHostService)
                    .SelectMany(m => m.Roles.Select(r => (Role: r, Running: true)).Concat(m.Downloaded.Select(r => (Role: r, Running: false)))
                        .Where(r => r.Role.Kind == "ollama" && r.Role.Model is { Length: > 0 } model && ServedModels.Chats(model))
                        .DistinctBy(r => r.Role.Model!.Replace(':', '-'), StringComparer.OrdinalIgnoreCase)
                        .Select(r => new { model = r.Role.Model, computer = Name(m.Specs.Id), running = r.Running, tier = ServedModels.Tier(r.Role.Model!) })),
                note = build.Request.PreferHostModels
                    ? "Thinking uses the best chat model a host runs or keeps downloaded, before one it must download."
                    : "Prefer models your hosts already have is off."
            },
            today = new
            {
                jobs = build.Request.CurrentJobs.Select(j => new { job = j.Job, host = j.HostId, off = j.Off, option = j.OptionId, pool = j.Pool }),
                thinkingPool = build.Request.CurrentThinkingPool,
                thinkingPoolOptOut = build.Request.ThinkingPoolOptOut,
                voiceEngine = build.Request.VoiceEngine,
                preference = build.Request.Preference.ToString(),
                off = build.Request.Off.Select(p => p.ToString()),
                choices = build.Request.Choices.Select(c => new { part = ComponentRanking.Name(c.Component), on = c.On, option = c.OptionId, model = c.Model, where = c.Where, host = c.HostId })
            },
            recommendation = new
            {
                alreadyOptimal = recommendation.AlreadyOptimal, worthAsking = recommendation.WorthAsking, fingerprint = recommendation.Fingerprint,
                components = recommendation.Components.Select(c => new
                {
                    rank = c.Rank, part = c.Name, need = c.CanBeOff ? "optional" : "needed", on = c.On, where = c.Where, why = c.Why,
                    canBeOff = c.CanBeOff, ownerOff = c.OwnerOff, offInReview = c.OffInReview, page = c.Page
                }),
                changes = recommendation.Changes.Select(c => new
                {
                    kind = c.Kind.ToString(), computer = Name(c.MachineId), summary = c.Summary, why = c.Why, away = c.Away, benefit = c.Benefit.ToString(),
                    roleKind = c.RoleKind, model = c.Model, job = c.Job, needsSomeoneThere = c.NeedsSomeoneThere, downloadGb = c.DownloadGb,
                    onProcessor = c.OnProcessor
                }),
                machines = recommendation.Target.Machines.Select(m => new
                {
                    computer = Name(m.MachineId), kind = m.Kind.ToString(), roles = m.Roles.Select(r => r.Model is null ? r.Kind : $"{r.Kind}={r.Model}"),
                    why = m.Why,
                    load = m.Usage is { } usage ? new
                    {
                        graphicsMemoryPercent = usage.Gpus.Count == 0 ? (double?)null : Math.Round(usage.Gpus.Sum(g => g.Vram.Used) / Math.Max(0.1, usage.Gpus.Sum(g => g.Vram.Capacity)) * 100),
                        memoryPercent = usage.Ram.Percent, processorPercent = usage.Cpu.Percent
                    } : null
                }),
                jobs = recommendation.Target.Jobs.Select(j => new { job = j.Job, host = j.HostId is null ? null : Name(j.HostId), off = j.Off, option = j.OptionId, pool = j.Pool.Select(Name), why = j.Why }),
                thinkingPool = recommendation.Target.ThinkingPool.Select(Name),
                cannotReply = recommendation.CannotReply, cannotReplyNote = recommendation.CannotReplyNote,
                cannotSpeak = recommendation.CannotSpeak, cannotSpeakNote = recommendation.CannotSpeakNote,
                offline = recommendation.Offline.Select(o => new { computer = Name(o.Id), minutes = Math.Round(o.For.TotalMinutes), note = o.Note }),
                notes = recommendation.Notes
            },
            companionInUseAsks = new { step = step.ToString(), why, declinedHere = memory.WasDeclined(recommendation.Fingerprint), declined = memory.Declined.Count },
            // Where the planner's options came from (docs/RECOMMENDATION_DESIGN.md, "Planner on the catalog").
            planning = Planning(planned, recommendation, Name, fixture is null && dataDirectory is not null ? ModelAbilities.Load(dataDirectory).Models : null),
            // Recommended setup's three plans: where each live job goes Normally, While gaming and when a host is away.
            situations = SituationPlans.For(build.Request, recommendation, id => id is null ? "your companion PCs" : Name(id), planned)
                .Select(plan => new { situation = plan.Situation.ToString(), title = plan.Title, lines = plan.Lines })
        };
    }

    // ---------- the planner's options ----------

    /// <summary>The options to plan with: the model catalog (<paramref name="dataDirectory"/>'s daily copy, else the snapshot shipped
    /// with Martlet; a fixture always uses the snapshot) with its local facts and the data directory's route facts
    /// (model-abilities.json), plus Martlet's own list, as the desktop plans; or, with <paramref name="choice"/> "seed", Martlet's
    /// own list alone. Speed estimates are for this PC's best graphics card.</summary>
    internal static FootprintCatalog PlanningOptions(string? dataDirectory, string? choice, NetworkSetupRequest request)
    {
        if (choice == "seed") return FootprintCatalog.Default;
        if (choice is not (null or "models")) throw new ArgumentException("catalog must be \"models\" or \"seed\".");
        var machines = request.Machines.Select(m => m.Specs).ToList();
        var card = (machines.Where(m => m.IsPrimary).Concat(machines)).SelectMany(m => m.Gpus.OrderByDescending(g => g.VramGb)).FirstOrDefault()?.Name;
        var bandwidth = GraphicsCardBandwidth.Find(card)?.Gbps;
        var models = dataDirectory is null ? ModelCatalog.Build(ModelCatalogStore.Snapshot()) : ModelCatalogStore.For(dataDirectory).Load();
        return FootprintCatalog.FromModels(models, bandwidth, $"the model catalog ({PlanningCatalog.Copy(models)}) and Martlet's own list",
            dataDirectory is null ? null : ModelAbilities.Load(dataDirectory).Models);
    }

    /// <summary>Where each option the plan uses came from, and every option the model catalog gave (for Thinking, Deep thinking,
    /// Vision and Hearing). Smartness is in words or LMArena's rating with its credit, never a rank number.</summary>
    internal static object Planning(FootprintCatalog planned, NetworkRecommendation recommendation, Func<string?, string> name,
        IReadOnlyList<ModelAbility>? tested = null)
    {
        static object Describe(ComponentOption o) => new
        {
            id = o.Id, part = ComponentRanking.Name(o.Component), name = o.DisplayName, model = o.ModelId, from = o.Origin.ToString(),
            fromWords = OptionFacts.OriginWords(o), tier = o.QualityTier, smartness = o.Smartness, smartnessCredit = o.SmartnessCredit,
            firstWordMs = o.FirstWordMs, wordsPerSecond = o.WordsPerSecond, graphicsGb = o.UsesGpu ? o.GpuGb : (double?)null,
            takes = OptionFacts.Inputs(o), callsTools = o.CallsTools, online = !o.IsLocal, free = o.FreeTier, onlyThisPcsOllama = o.NativeOnly,
            evidence = o.Evidence.ToString(), source = o.Source
        };
        ComponentOption? Running(string? machine, string kind, PlanComponent part) =>
            machine is null ? null : recommendation.Target.Machine(machine)?.Roles.FirstOrDefault(r => r.Kind == kind)?.Model is { } model
                ? planned.FindModel(part, model) : null;
        ComponentOption? JobOption(JobPlan job) => (job.OptionId is { } id ? planned.Find(id) : null) ??
            (job.Job == ClusterJobs.Thinking ? Running(job.HostId, NetworkRecommender.ThinkingRole, PlanComponent.Thinking) : null);
        return new
        {
            catalog = planned.Models is null ? "seed" : "models",
            from = planned.From,
            counts = Enum.GetValues<OptionOrigin>().ToDictionary(o => o.ToString(), o => planned.Options.Count(x => x.Origin == o)),
            jobs = recommendation.Target.Jobs.Select(job => JobOption(job) is { } option
                ? new { job = job.Job, option = (string?)option.Id, model = option.ModelId, from = option.Origin.ToString(), fromWords = OptionFacts.OriginWords(option) }
                : new { job = job.Job, option = job.OptionId, model = (string?)null, from = "unknown", fromWords = "Martlet has no option for it" }),
            deepThinking = recommendation.Target.Machines.SelectMany(m => m.Roles.Where(r => r.Kind == NetworkRecommender.DeepThinkingRole)
                .Select(r => planned.FindModel(PlanComponent.DeepThinking, r.Model ?? "") is { } option
                    ? new { computer = name(m.MachineId), model = r.Model, from = option.Origin.ToString(), tier = option.QualityTier }
                    : new { computer = name(m.MachineId), model = r.Model, from = "unknown", tier = 0 })),
            onlineDeepThinking = recommendation.OnlineDeepThinking is { } deep ? Describe(deep) : null,
            catalogOptions = planned.Options.Where(o => CatalogOptions.TakesModels(o.Component) && (o.Origin != OptionOrigin.Seed || o.CatalogKey is not null))
                .Select(Describe),
            // Each named provider's suggested model for live Thinking (Companion › Thinking, If Thinking fails, the welcome
            // tour's key step) and for the Thinking pool, as the desktop's lists prefill them.
            providerModels = ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new
            {
                provider = e.Id, name = e.Name, thinking = CatalogOptions.SuggestedModel(planned.Models, e.Id, tested),
                thinkingPool = (planned.Models is { } models ? CatalogOptions.SmartestChat(models, e.Id, tested)?.ModelId : null) ?? e.DefaultModelId,
                presetDefault = e.DefaultModelId
            }),
            note = "Smartness shows as words or LMArena's rating with its credit (CC-BY-4.0); Martlet never shows a rank number."
        };
    }

    // ---------- preferences ----------

    /// <summary><paramref name="saved"/> with the fields <paramref name="overrides"/> gives: quality (balanced, quick, smarter),
    /// online (backup, never, yes), preferHearing, hostGpuShare (90, 75, 50), useServedModels, preferHostModels and games
    /// ([{ device, plays }]). Throws on a value Martlet doesn't know.</summary>
    internal static RecommendationPreferences Override(RecommendationPreferences saved, JsonElement? overrides)
    {
        if (overrides is not { ValueKind: JsonValueKind.Object } given) return saved;
        var next = saved;
        if (given.TryGetProperty("quality", out var quality))
            next = next with { Quality = Enum.TryParse<ReplyQuality>(quality.GetString(), ignoreCase: true, out var q) && Enum.IsDefined(q)
                ? q : throw new ArgumentException("preferences.quality must be balanced, quick or smarter.") };
        if (given.TryGetProperty("online", out var online))
            next = next with { Online = Enum.TryParse<OnlineServices>(online.GetString(), ignoreCase: true, out var o) && Enum.IsDefined(o)
                ? o : throw new ArgumentException("preferences.online must be backup, never or yes.") };
        if (given.TryGetProperty("preferHearing", out var hearing)) next = next with { PreferHearing = hearing.GetBoolean() };
        if (given.TryGetProperty("hostGpuShare", out var share))
            next = next with { HostGpuShare = share.TryGetInt32(out var percent) && RecommendationPreferences.HostGpuShares.Contains(percent)
                ? percent : throw new ArgumentException("preferences.hostGpuShare must be 90, 75 or 50.") };
        if (given.TryGetProperty("useServedModels", out var served)) next = next with { UseServedModels = served.GetBoolean() };
        if (given.TryGetProperty("preferHostModels", out var kept)) next = next with { PreferHostModels = kept.GetBoolean() };
        if (given.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
            foreach (var answer in games.EnumerateArray().Take(64))
                next = next.WithGames(answer.GetProperty("device").GetString() ?? throw new ArgumentException("preferences.games needs a device."),
                    answer.GetProperty("plays").GetBoolean());
        if (given.TryGetProperty("locks", out var locks) && locks.ValueKind == JsonValueKind.Array)
            foreach (var entry in locks.EnumerateArray().Take(8))
            {
                var job = entry.GetProperty("job").GetString();
                if (job is null || !RecommendationPreferences.LockableJobs.Contains(job))
                    throw new ArgumentException($"preferences.locks job must be {string.Join(", ", RecommendationPreferences.LockableJobs)}.");
                // Unlocking here lets Martlet choose whatever runs today (no remembered choice).
                next = next.WithLock(job, entry.GetProperty("locked").GetBoolean(), null);
            }
        return next;
    }

    /// <summary>Whether the data directory's Thinking model or If Thinking fails' model is retired on its server (a reply's or a
    /// test's HTTP 410, Martlet's list, or the model catalog's expiration date), and the replacement Martlet proposes.</summary>
    private static async Task<object?> RetiredAsync(string directory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var routes = new List<(string Kind, string Origin, string Model)>();
        if (loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is { RouteType: SetupRouteType.ChatCompletions } thinking)
            routes.Add(("thinking", thinking.Origin, thinking.ModelId));
        if (loaded.Settings?.ThinkingFallback is { } fallback) routes.Add(("fallback", fallback.Origin, fallback.ModelId));
        if (routes.Count == 0) return null;
        var abilities = ModelAbilities.Load(directory);
        ModelCatalog? catalog = null;
        try { catalog = ModelCatalogStore.For(directory).Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return routes.Select(r =>
        {
            var found = ChatCompletionsEndpointCatalog.RetiredOn(r.Origin, r.Model, abilities);
            var expired = RetiredModels.Expired(catalog, r.Origin, r.Model, today);
            return new
            {
                route = r.Kind, origin = r.Origin, model = r.Model, retired = found is not null || expired,
                source = found?.Source ?? (expired ? "the model catalog (its expiration date passed)" : null),
                since = found?.Since,
                replacement = found is not null || expired ? RetiredModels.Replacement(catalog, r.Origin, r.Model, abilities, today) : null
            };
        }).ToArray();
    }

    // ---------- a data directory ----------

    private static async Task<SetupSources> FromDataDirectoryAsync(string directory, CancellationToken cancellation)
    {
        var device = Martlet.Diagnostics.LocalLogs.ThisDeviceId(directory);
        var hardware = new HostHardwareStore(directory).Load();
        var plan = Plan(directory);
        var hosts = PairedHosts(directory);
        var own = hosts.FirstOrDefault(h => h.Method == "ThisPcDocker").HostId;
        var presence = NodePresenceReport.Load(directory);
        TimeSpan? Away(string hostId) => presence?.Hosts.FirstOrDefault(h => h.HostId == hostId) is
            { State: not (NodePresenceState.Answering or NodePresenceState.Returning or NodePresenceState.Back) } away
            ? away.AwayFor ?? (away.Since is { } since ? presence.UpdatedAt - since : TimeSpan.Zero)
            : null;
        var computers = new List<SetupComputer>
        {
            new(own ?? device, "This PC", NetworkMachineKind.Companion)
            {
                Hardware = own is null ? null : hardware.FirstOrDefault(h => h.HostId == own), HasHostService = own is not null, ThisPc = true,
                Device = device
            }
        };
        foreach (var host in hosts.Where(h => h.HostId != own))
        {
            var report = hardware.FirstOrDefault(h => h.HostId == host.HostId);
            var reachable = host.Method is "ThisPcDocker" or "Agent" || host.Method is "SshDocker" or "SshNative" && host.SshTarget is { Length: > 0 };
            var away = Away(host.HostId);
            computers.Add(new(host.HostId, presence?.Hosts.FirstOrDefault(h => h.HostId == host.HostId)?.Name ?? host.HostId, NetworkMachineKind.Host)
            {
                Hardware = report, HasHostService = true, Reachable = away is null ? null : false, OfflineFor = away,
                Manageable = reachable && PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost(host.HostId, report))
            });
        }
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var routes = loaded.Settings?.Setup?.Routes ?? [];
        var jobs = new List<JobPlan>();
        foreach (var (job, role) in new[] { (ClusterJobs.Thinking, SetupRole.Llm), (ClusterJobs.Listening, SetupRole.Stt), (ClusterJobs.Speaking, SetupRole.Tts) })
            if (routes.FirstOrDefault(r => r.Role == role) is { } route)
                jobs.Add(new JobPlan(job, route.Gateway is { } gateway && SelfHostSetup.IsGateway(route.RouteType) ? gateway.HostId : null));
        var thinking = routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var providers = thinking?.CredentialId is null ? []
            : thinking.RouteType == SetupRouteType.OpenAi ? ["openai"]
            : ChatCompletionsEndpointCatalog.Named(thinking.Origin)?.Id is { } id ? new[] { id } : [];
        var speaking = routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        string? chosen = null;
        try { chosen = File.ReadAllText(Path.Combine(directory, "speaking-engine.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var voice = SpeechEngines.ForRoute(speaking?.GatewaySnapshot?.RouteId) ?? SpeechEngines.ForKey(chosen) ?? SpeechEngines.Default;
        var poolSettings = ThinkingPoolSettings.Read(directory, save: false).Settings;
        var pool = poolSettings.Places.Places.Where(p => p.OnHostRole && p.HostId is not null).Select(p => p.HostId!).ToArray();
        return new SetupSources(computers)
        {
            Plan = plan, LocalJobs = jobs, Sharing = WorkSharingSettings.Load(directory), Pools = PoolSettings.Load(directory), Device = device, ThinkingPool = pool,
            PoolOptOut = poolSettings.LeftByOwner, VoiceEngine = voice.HostRoleKind, ConfiguredProviders = providers,
            Off = RecommendedSetupMemory.Load(directory).OffParts, Choices = Choices(directory, own),
            OnlinePool = RecommendedSetupInputs.OnlinePool(poolSettings)
        };
    }

    /// <summary>This PC's choices for the parts it sets on their Companion pages, as the desktop reads them: vision on and Let ...
    /// hear my voice (talk-preferences.json), the image and audio models (sense-models.json), Reading (its list in
    /// pools-local.json, else the older reading.json; <paramref name="own"/> is this PC's own host service) and the Home
    /// Assistant address (smart-home.json; never its token).</summary>
    private static IReadOnlyList<PartChoice> Choices(string directory, string? own)
    {
        var talk = Json(directory, "talk-preferences.json");
        var watch = talk?["Watch"] is JsonValue w && w.TryGetValue<bool>(out var watching) ? watching : true;
        bool? hear = talk?["HearVoice"] is JsonValue h && h.TryGetValue<bool>(out var hears) ? hears : null;
        // Version 4 made Let Thinking hear my voice three-way; an older "false" was only the old default (never chosen).
        if (hear == false && !(talk?["Version"] is JsonValue v && v.TryGetValue<int>(out var version) && version >= 4)) hear = null;
        var home = Json(directory, "smart-home.json")?["Address"] is JsonValue a && a.TryGetValue<string>(out var address) ? address : null;
        var reading = PoolSettings.LoadFor(directory, PoolAreas.Reading) is { } list
            ? Martlet.Core.Reading.ReadingPool.Choice(list, own) : Martlet.Core.Reading.ReadingSettings.Load(directory);
        return RecommendedSetupInputs.Choices(watch, hear, SenseSetup.Read(directory).Senses, reading, home);
    }

    private static JsonObject? Json(string directory, string file)
    {
        try
        {
            var path = Path.Combine(directory, file);
            return File.Exists(path) && new FileInfo(path).Length <= 262_144 ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static ClusterPlan? Plan(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "cluster.json");
            return File.Exists(path) ? ClusterPlan.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    // The paired hosts in hosts.json: host ID, how Martlet reaches it and the SSH target (never a secret). A host a friend shares
    // with this PC isn't one of your computers, so the recommended setup never plans on it (as the desktop's NetworkMap.Hosts).
    private static IReadOnlyList<(string HostId, string? Method, string? SshTarget)> PairedHosts(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return [];
            return [.. (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(h => h["access"]?.GetValue<string>() != "friend")
                .Select(h => (HostId: h["pairing"]?["hostId"]?.GetValue<string>(), Method: h["method"]?.GetValue<string>() ?? "Agent",
                    SshTarget: h["sshTarget"]?.GetValue<string>()))
                .Where(h => h.HostId is { Length: > 0 }).Select(h => (h.HostId!, (string?)h.Method, h.SshTarget))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}

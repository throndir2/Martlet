using System.Collections.Concurrent;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The context window a paired host's conversation model loads (its Ollama <c>num_ctx</c>): planned from the graphics card
/// it runs on (<see cref="ContextWindowPlan"/>), or the saved Context size when that is larger. Every request this PC sends to
/// that route gets the same window (<see cref="HostTextClient"/>), so Ollama never loads the model again because one request
/// asked for another window. Each reply still sends the same input (<see cref="ContextBudget.PlannedInputTokens"/>), so a
/// bigger window never makes a reply start later. The planned window is kept in model-limits.json for the conversation's
/// context line, the Thinking page and MCP.</summary>
internal static class HostContextWindow
{
    private static readonly ConcurrentDictionary<string, PlannedContext> Plans = new(StringComparer.Ordinal);
    // The saved Context size (Companion › Replies) of the newest conversation configuration; 0 when none is saved.
    private static int saved;

    /// <summary>The saved Context size every request to a host's conversation model loads at least (null: none saved).</summary>
    internal static void UseSaved(int? tokens) => Volatile.Write(ref saved, tokens is > 0 ? tokens.Value : 0);

    /// <summary>Plans <paramref name="hostId"/>'s windows again on its next request (its hardware or roles changed).</summary>
    internal static void Forget(string hostId)
    {
        foreach (var key in Plans.Keys.Where(k => k.StartsWith(hostId + "|", StringComparison.Ordinal))) Plans.TryRemove(key, out _);
    }

    /// <summary>The window to send to <paramref name="route"/> (a host's conversation model) on <paramref name="hostId"/>.</summary>
    internal static int For(string hostId, string origin, HostRoute route, IReadOnlyList<HostRoute> routes, string? dataDirectory)
    {
        var plan = Planned(hostId, origin, route, routes, dataDirectory);
        return Window(plan.Tokens, Volatile.Read(ref saved) is > 0 and var size ? size : null);
    }

    /// <summary>The window loaded: the larger of the planned one and the saved size, at most the host's bound.</summary>
    internal static int Window(int planned, int? savedSize) =>
        Math.Min(Math.Max(planned, savedSize ?? 0), GenerationSettings.MaximumHostContextTokens);

    /// <summary>The planned window for the route, worked out once per host, model and card (cached in memory, so a reply reads
    /// no file for it) and kept in model-limits.json in the background when it changes.</summary>
    internal static PlannedContext Planned(string hostId, string origin, HostRoute route, IReadOnlyList<HostRoute> routes, string? dataDirectory)
    {
        var key = $"{hostId}|{route.ModelId}|{string.Join(',', route.Gpus)}";
        if (Plans.TryGetValue(key, out var known)) return known;
        var hardware = dataDirectory is null ? null : new HostHardwareStore(dataDirectory).Find(hostId);
        var measured = dataDirectory is null || MeasuredFirstWords.HostKey(origin) is not { } host ? null
            : MeasuredModelMemory.Load(dataDirectory).Find(host, route.ModelId);
        var plan = Plan(hardware, route, routes, measured);
        Plans[key] = plan;
        if (dataDirectory is not null) _ = Task.Run(() => Keep(dataDirectory, origin, route.ModelId, plan));
        return plan;
    }

    private static void Keep(string dataDirectory, string origin, string model, PlannedContext plan)
    {
        var limits = ModelLimits.Load(dataDirectory);
        if (limits.Find(origin, model) is { ContextTokens: var tokens, Source: var source } && tokens == plan.Tokens && source == Source(plan)) return;
        if (limits.With(new() { Origin = origin, ModelId = model, ContextTokens = plan.Tokens, Source = Source(plan), CheckedAt = DateTimeOffset.UtcNow })
            .Save(dataDirectory))
            ErrorLog.Info($"Context window of {model} on the paired host: {plan.Why}. Each reply still sends at most about " +
                $"{ContextBudget.PlannedInputTokens:N0} tokens unless a context size is saved.");
    }

    /// <summary>What a planned window's model-limits.json source starts with; the reason in words follows.</summary>
    internal const string SourcePrefix = "planned from the graphics card's free memory: ";

    private static string Source(PlannedContext plan)
    {
        var text = SourcePrefix + plan.Why;
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>The planned window of <paramref name="route"/> from the host's <paramref name="hardware"/> report, the cards its
    /// routes name and what the model measured (<paramref name="measured"/>). The card is the one whose UUID the route names,
    /// else the host's only graphics card, else (a route on every card) the largest, else the smallest that holds the model.</summary>
    internal static PlannedContext Plan(HostHardware? hardware, HostRoute route, IReadOnlyList<HostRoute> routes, MeasuredModelUse? measured)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(routes);
        var model = route.ModelId;
        var guess = ListeningAdvisor.OllamaModelGb(model);
        var modelGb = measured is { GraphicsBytes: > 0 } m && m.OnGraphicsCard
            ? Math.Max(0.1, m.GraphicsBytes / 1e9 - ContextWindowPlan.ExtraGb(model, guess, m.ContextTokens ?? GenerationSettings.DefaultHostContextTokens))
            : DeepThinkingSlots.ThinkingGb(model, guess);
        var cards = hardware?.Gpus.Where(g => g.MemoryGb is > 0 && SetupAdvisor.Classify(g.Vendor, g.MemoryGb) != AdvisorGpu.None).ToList() ?? [];
        if (cards.Count == 0 || route.Gpus.Contains("cpu") || route.Gpus.Count > 1)
            return ContextWindowPlan.Fit(model, modelGb, null, []);
        var card = route.Gpus.Count == 1 ? cards.FirstOrDefault(g => g.Uuid == route.Gpus[0]) : null;
        card ??= cards.Count == 1 ? cards[0]
            : route.Gpus.Count == 0 ? cards.MaxBy(g => g.MemoryGb)
            : cards.Where(g => g.MemoryGb >= modelGb).MinBy(g => g.MemoryGb) ?? cards.MinBy(g => g.MemoryGb);
        var others = routes
            .Where(r => r.RouteId != route.RouteId && !r.Gpus.Contains("cpu") && Shares(route, r, card!))
            .Select(r => (Name(r), Gb(r)))
            .Concat(OtherApps(hardware, model))
            .ToArray();
        return ContextWindowPlan.Fit(model, modelGb, card!.MemoryGb, others, cardName: "the " + card.Name);
    }

    /// <summary>Graphics memory that model apps beside Martlet's own Ollama hold on the host (llama.cpp, LM Studio, vLLM or another
    /// Ollama, from the host's served models), so the window leaves room for them. Which card they use isn't reported, so each
    /// counts on this card; the route's own model (a Thinking pool on your own app) isn't counted twice.</summary>
    internal static IEnumerable<(string Name, double Gb)> OtherApps(HostHardware? hardware, string model) =>
        (hardware?.Served ?? [])
            .Where(s => s.LoadedBytes is not null && !Martlet.Core.Planning.ThinkingSpeed.SameModel(s.Model, model))
            .Select(s => ($"{s.AppName}'s {s.Model}", Math.Round((s.LoadedBytes is > 0 and var held ? held : s.Bytes ?? 0) / 1e9, 1)))
            .Where(o => o.Item2 > 0);

    // Another route is on the same card: it names one of the route's cards, or one of them runs on every card.
    private static bool Shares(HostRoute route, HostRoute other, HostGpu card) =>
        other.Gpus.Count == 0 || route.Gpus.Count == 0 ? other.Gpus.Count == 0 || card.Uuid is { } id && other.Gpus.Contains(id)
            : other.Gpus.Intersect(route.Gpus, StringComparer.Ordinal).Any();

    private static readonly (string Part, string? Kind, string Name, double Gb)[] Kinds =
    [
        ("f5-", HostRoles.F5, "the F5 voice", 4), ("xtts", HostRoles.Xtts, "the XTTS voice", 4),
        ("gpt-sovits", HostRoles.GptSovits, "the GPT-SoVITS voice", 4), ("dia-", HostRoles.Dia, "the Dia voice", 4),
        ("chatterbox", HostRoles.Chatterbox, "the Chatterbox voice", 4), ("audio2face", HostRoles.Audio2Face, "Audio2Face lip-sync", 4),
        ("transcription", HostRoles.Stt, "listening", 4), ("song", HostRoles.Singing, "singing", 4),
        ("picture", HostRoles.Pictures, "pictures", 4), ("ocr", null, "reading", 3.1), ("motion", null, "motion", 2),
        ("voice-recognition", null, "voice recognition", 1)
    ];

    private static string Name(HostRoute route) => SelfHostSetup.IsDeepThinkingRoute(route.RouteId)
        ? $"the Thinking pool's {route.ModelId}"
        : Kinds.FirstOrDefault(k => route.RouteId.Contains(k.Part, StringComparison.OrdinalIgnoreCase)).Name ?? route.RouteId;

    private static double Gb(HostRoute route)
    {
        if (SelfHostSetup.IsDeepThinkingRoute(route.RouteId))
        {
            var gb = DeepThinkingSlots.ModelGb(route.ModelId, ListeningAdvisor.OllamaModelGb(route.ModelId));
            return gb + DeepThinkingSlots.ContextGb(route.ModelId, gb) * Math.Max(1, route.MaximumConcurrency);
        }
        var known = Kinds.FirstOrDefault(k => route.RouteId.Contains(k.Part, StringComparison.OrdinalIgnoreCase));
        return known.Part is null ? 4 : known.Kind is { } kind ? DeepThinkingSlots.RoleGb(kind) ?? known.Gb : known.Gb;
    }
}

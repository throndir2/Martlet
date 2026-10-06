using Martlet.Companion.Platform;
using Martlet.Core.Cluster;
using Martlet.Core.Platforms;

namespace Martlet.Companion;

/// <summary>One engine as this computer sees it: the catalog's verdict and whether the companion offers it.</summary>
public sealed record EngineOption(string Id, string Job, string Name, PlatformVerdict Verdict, string Reason, bool Offered);

/// <summary>Keeps the companion from offering what this computer can't run. <see cref="PlatformCatalog"/> is the only
/// source: an engine is offered when the catalog allows it here and the companion has built it; everything else is listed
/// with the catalog's reason. Choices carried from another device are refused with that reason, never silently changed.</summary>
public sealed class CompanionGuardrails
{
    /// <summary>Engines the Linux/macOS companion has built. The catalog still has the final say per platform.</summary>
    public static IReadOnlySet<string> Built { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "openai-llm", "chat-completions", "openai-stt", "openai-tts", "loudness-lipsync", "character-overlay"
    };

    /// <summary>The jobs the companion's settings choose an engine for.</summary>
    public static IReadOnlyList<string> Jobs { get; } = [ClusterJobs.Thinking, ClusterJobs.Listening, ClusterJobs.Speaking, ClusterJobs.LipSync];

    public PlatformInfo Info { get; }
    public PlatformDevice Device { get; }

    public CompanionGuardrails(PlatformInfo info)
    {
        Info = info;
        Device = info.ToDevice();
    }

    public PlatformCheck Check(string engineId) =>
        PlatformCatalog.Engines.Any(e => e.Id == engineId)
            ? PlatformCatalog.Check(engineId, PlatformSide.Companion, Device)
            : new(PlatformVerdict.No, $"Martlet doesn't know the engine \"{engineId}\".");

    public EngineOption Option(PlatformEngine engine)
    {
        var check = PlatformCatalog.Check(engine.Id, PlatformSide.Companion, Device);
        var offered = check.Allowed && Built.Contains(engine.Id);
        var reason = check.Allowed && !offered
            ? $"{engine.Name} isn't built into Martlet for {PlatformCatalog.Name(Device.Platform)} yet."
            : check.Reason;
        return new(engine.Id, engine.Job, engine.Name, check.Verdict, reason, offered);
    }

    /// <summary>Every engine for <paramref name="job"/>, offered or not, with its reason.</summary>
    public IReadOnlyList<EngineOption> Options(string job) => [.. PlatformCatalog.ForJob(job).Select(Option)];

    /// <summary>Only what the settings may show for <paramref name="job"/>.</summary>
    public IReadOnlyList<EngineOption> Offered(string job) => [.. Options(job).Where(o => o.Offered)];

    public bool IsOffered(string engineId) =>
        PlatformCatalog.Engines.FirstOrDefault(e => e.Id == engineId) is { } engine && Option(engine).Offered;

    /// <summary>Plain-language warnings for a Thinking route: local models on an Intel Mac run on the CPU only.</summary>
    public IReadOnlyList<string> ThinkingWarnings(string engineId, string? baseUrl)
    {
        var warnings = new List<string>();
        if (engineId == "chat-completions" && IsLoopback(baseUrl))
        {
            if (Info.CpuOnlyLocalModels)
                warnings.Add("This Mac has an Intel processor: a model on this computer runs on the CPU only. Use a small 1-4B " +
                    "model, or a cloud route or a paired host for faster replies.");
            else if (Info.Platform == DevicePlatform.Linux && Info.Gpus is { } gpus && gpus.Count == 0)
                warnings.Add("No NVIDIA GPU was found: a model on this computer may run on the CPU only (AMD GPUs work through " +
                    "Ollama's ROCm or Vulkan builds). Use a small model, or a cloud route or a paired host.");
        }
        return warnings;
    }

    /// <summary>Settings from another device (or an older file) checked against this computer: every engine this one can't
    /// run is refused with the catalog's reason and this computer's current choice is kept.</summary>
    public (CompanionSettings Settings, IReadOnlyList<string> Refusals) Admit(CompanionSettings incoming, CompanionSettings current)
    {
        var refusals = new List<string>();
        string Pick(string label, string wanted, string kept)
        {
            if (wanted == kept || IsOffered(wanted)) return wanted;
            var engine = PlatformCatalog.Engines.FirstOrDefault(e => e.Id == wanted);
            var reason = engine is null ? Check(wanted).Reason : Option(engine).Reason;
            refusals.Add($"{label}: {reason}");
            return kept;
        }
        var settings = incoming with
        {
            Thinking = Pick("Thinking", incoming.Thinking, current.Thinking),
            Listening = Pick("Listening", incoming.Listening, current.Listening),
            Speaking = incoming.Speaking == CompanionSettings.Silent ? incoming.Speaking : Pick("Speaking", incoming.Speaking, current.Speaking),
            LipSync = Pick("Lip-sync", incoming.LipSync, current.LipSync)
        };
        return (settings, refusals);
    }

    internal static bool IsLoopback(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));

    /// <summary>The guardrails as the headless status reports them.</summary>
    public object Describe() => new
    {
        jobs = Jobs.ToDictionary(job => job, job => Options(job).Select(o => new
        {
            id = o.Id, name = o.Name, offered = o.Offered, verdict = o.Verdict.ToString(), reason = o.Reason
        }).ToArray()),
        features = new[] { "character-overlay", "screen-watch", "hands-free", "host-pairing", "voice-id", "memory" }
            .Select(id => Option(PlatformCatalog.Engine(id)))
            .Select(o => new { id = o.Id, name = o.Name, offered = o.Offered, verdict = o.Verdict.ToString(), reason = o.Reason }).ToArray(),
        localModelWarnings = ThinkingWarnings("chat-completions", "http://127.0.0.1:11434/v1")
    };
}

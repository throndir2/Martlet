using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Installation;

public sealed record HostGpu(string Name, string Vendor, int? MemoryMb, string? Driver, double? PowerLimitW = null,
    double? PowerDefaultW = null, bool? Persistence = null)
{
    [JsonIgnore] public double? MemoryGb => MemoryMb is { } mb ? Math.Round(mb / 1024d, 1) : null;
    [JsonIgnore] public bool IsNvidia => Vendor == "nvidia";
    public string Describe() => MemoryGb is { } gb ? $"{Name} ({gb.ToString("0.#", CultureInfo.InvariantCulture)} GB)" : Name;

    /// <summary>Power limit and persistence mode as reported by nvidia-smi, or null when the host did not report them.</summary>
    public string? DescribePower()
    {
        if (PowerLimitW is not { } limit) return Persistence is { } on ? $"persistence {(on ? "on" : "off")}" : null;
        var text = $"power limit {limit.ToString("0", CultureInfo.InvariantCulture)} W";
        if (PowerDefaultW is { } standard && Math.Abs(standard - limit) >= 1)
            text += $" (default {standard.ToString("0", CultureInfo.InvariantCulture)} W)";
        return Persistence is { } persistence ? $"{text}, persistence {(persistence ? "on" : "off")}" : text;
    }
}

/// <summary>What a paired Martlet host reported about itself (collected by martlet-host on that machine and
/// served over the pinned, paired connection). Host-reported facts, not measurements or qualification.</summary>
public sealed record HostHardware(
    string HostId, string Origin, DateTimeOffset CollectedAt, DateTimeOffset ReceivedAt, string Method,
    string OperatingSystem, string? Kernel, string? Processor, int? ProcessorThreads, double? MemoryGb,
    string? ContainerRuntime, string? NvidiaContainers, IReadOnlyList<HostGpu> Gpus, string? Cuda = null)
{
    [JsonIgnore] public HostGpu? BestGpu => Gpus.OrderByDescending(g => g.IsNvidia).ThenByDescending(g => g.MemoryMb ?? 0).FirstOrDefault();

    [JsonIgnore] public AdvisorGpu AdvisorGpu => BestGpu is { } gpu ? SetupAdvisor.Classify(gpu.Vendor, gpu.MemoryGb) : AdvisorGpu.None;

    /// <summary>Plain-language hints about what this host can take on.</summary>
    public IEnumerable<string> Capabilities()
    {
        var gpu = BestGpu;
        if (gpu is { IsNvidia: true } && NvidiaContainers == "no")
            yield return "Has an NVIDIA GPU, but containers cannot use it yet; martlet-host add installs the NVIDIA Container Toolkit";
        if (gpu is { IsNvidia: true, MemoryGb: >= 4 })
            yield return "Can run Audio2Face lip-sync";
        if (gpu is { MemoryGb: >= 8 })
            yield return gpu.MemoryGb >= 16 ? "Can run a mid-size local conversation model" : "Can run a small local conversation model";
        if (gpu is { IsNvidia: true, MemoryGb: >= 8 })
            yield return "Can run a local voice (Voice Studio engines need NVIDIA)";
        if (gpu is null)
            yield return "No dedicated GPU reported; GPU roles need one";
        else if (!gpu.IsNvidia)
            yield return "Voice engines and Audio2Face need NVIDIA; this GPU suits a conversation model";
    }
}

/// <summary>Saves the latest hardware report from each paired host in host-hardware.json so the devices map and
/// the setup advisor can use it without contacting the host again.</summary>
public sealed class HostHardwareStore(string dataDirectory)
{
    public const string FileName = "host-hardware.json";
    private const int MaximumHosts = 16;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private string PathName => Path.Combine(dataDirectory, FileName);

    public IReadOnlyList<HostHardware> Load()
    {
        try
        {
            if (!File.Exists(PathName)) return [];
            var info = new FileInfo(PathName);
            if (info.Length > 1_048_576) return [];
            return JsonSerializer.Deserialize<HostHardware[]>(File.ReadAllBytes(PathName), Json)?
                .Where(h => h is not null && !string.IsNullOrEmpty(h.HostId) && h.Gpus is not null).ToArray() ?? [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public HostHardware? Find(string hostId) => Load().FirstOrDefault(h => h.HostId == hostId);

    public void Save(HostHardware report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var all = Load().Where(h => h.HostId != report.HostId).Prepend(report).Take(MaximumHosts).ToArray();
        Directory.CreateDirectory(dataDirectory);
        var temporary = PathName + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(all, Json));
        File.Move(temporary, PathName, overwrite: true);
    }

    public void Forget(string hostId)
    {
        var all = Load();
        if (all.All(h => h.HostId != hostId)) return;
        var temporary = PathName + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(all.Where(h => h.HostId != hostId).ToArray(), Json));
        File.Move(temporary, PathName, overwrite: true);
    }
}

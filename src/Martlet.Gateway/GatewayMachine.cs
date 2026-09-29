using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Gateway;

/// <summary>One graphics card the host engine observed (nvidia-smi or sysfs).</summary>
public sealed record GatewayMachineGpu
{
    public required string Name { get; init; }
    /// <summary>nvidia, amd, intel or other.</summary>
    public required string Vendor { get; init; }
    public int? MemoryMb { get; init; }
    public string? Driver { get; init; }
}

/// <summary>What a host machine is like, collected by <c>martlet-host</c> on the host itself and served to paired
/// desktops so they can plan which computer runs which role. Unauthenticated host-reported facts, not a
/// qualification or readiness claim.</summary>
public sealed record GatewayMachineReport
{
    public const int MaximumBytes = 16_384;
    public required DateTimeOffset CollectedAt { get; init; }
    /// <summary>How the gateway runs on the host: docker or native.</summary>
    public required string Method { get; init; }
    public required string OperatingSystem { get; init; }
    public string? Kernel { get; init; }
    public string? Processor { get; init; }
    public int? ProcessorThreads { get; init; }
    public double? MemoryGb { get; init; }
    public string? ContainerRuntime { get; init; }
    /// <summary>Whether containers on this host can use NVIDIA GPUs: yes, no or unknown.</summary>
    public string? NvidiaContainers { get; init; }
    public required IReadOnlyList<GatewayMachineGpu> Gpus { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        MaxDepth = 4
    };

    /// <summary>Parses the engine-written machine.json; returns null for anything malformed or out of bounds.</summary>
    public static GatewayMachineReport? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) return null;
        try
        {
            var report = JsonSerializer.Deserialize<GatewayMachineReport>(bytes, Json);
            return report is not null && report.IsValid() ? report : null;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    internal bool IsValid() =>
        Method is "docker" or "native" &&
        Text(OperatingSystem, required: true) && Text(Kernel) && Text(Processor) && Text(ContainerRuntime) &&
        NvidiaContainers is null or "yes" or "no" or "unknown" &&
        ProcessorThreads is null or (> 0 and <= 4096) &&
        MemoryGb is null or (> 0 and <= 65_536) &&
        Gpus is { Count: <= 16 } &&
        Gpus.All(gpu => gpu is not null && Text(gpu.Name, required: true) && Text(gpu.Driver) &&
            gpu.Vendor is "nvidia" or "amd" or "intel" or "other" &&
            gpu.MemoryMb is null or (> 0 and <= 1_048_576));

    private static bool Text(string? value, bool required = false) =>
        value is null ? !required : value.Length is > 0 and <= 128 && value.All(c => c is >= ' ' and <= '~');
}

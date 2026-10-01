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
    /// <summary>NVIDIA only: the enforced power limit and its default, in watts, and whether persistence mode is on.</summary>
    public double? PowerLimitW { get; init; }
    public double? PowerDefaultW { get; init; }
    public bool? Persistence { get; init; }
}

/// <summary>What a host machine is like, collected by <c>martlet-host</c> on the host itself and served to paired
/// desktops so they can plan which computer runs which role. Unauthenticated host-reported facts, not a
/// qualification or readiness claim.</summary>
public sealed record GatewayMachineReport
{
    public const int MaximumBytes = 16_384;
    public required DateTimeOffset CollectedAt { get; init; }
    /// <summary>How the gateway runs on the host: docker, native, or app (inside the Martlet app on a Mac, phone or tablet).</summary>
    public required string Method { get; init; }
    public required string OperatingSystem { get; init; }
    public string? Kernel { get; init; }
    public string? Processor { get; init; }
    public int? ProcessorThreads { get; init; }
    public double? MemoryGb { get; init; }
    public string? ContainerRuntime { get; init; }
    /// <summary>Whether containers on this host can use NVIDIA GPUs: yes, no or unknown.</summary>
    public string? NvidiaContainers { get; init; }
    /// <summary>The newest CUDA version the NVIDIA driver supports (nvidia-smi), when there is one.</summary>
    public string? Cuda { get; init; }
    public required IReadOnlyList<GatewayMachineGpu> Gpus { get; init; }
    /// <summary>The operating system the host's roles run on: linux, windows, macos, ios or android. Absent on older
    /// hosts, which run the Linux host engine.</summary>
    public string? Platform { get; init; }
    public string? OsVersion { get; init; }
    /// <summary>x64 or arm64.</summary>
    public string? Architecture { get; init; }
    /// <summary>Device features such as apple-intelligence, gemini-nano, foreground-only or battery.</summary>
    public IReadOnlyList<string>? Features { get; init; }

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
        Method is "docker" or "native" or "app" &&
        Platform is null or "linux" or "windows" or "macos" or "ios" or "android" &&
        Architecture is null or "x64" or "arm64" &&
        Text(OsVersion) &&
        (Features is null || Features is { Count: <= 16 } && Features.All(f => f is { Length: > 0 and <= 32 } &&
            f.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) &&
        Text(OperatingSystem, required: true) && Text(Kernel) && Text(Processor) && Text(ContainerRuntime) && Text(Cuda) &&
        NvidiaContainers is null or "yes" or "no" or "unknown" &&
        ProcessorThreads is null or (> 0 and <= 4096) &&
        MemoryGb is null or (> 0 and <= 65_536) &&
        Gpus is { Count: <= 16 } &&
        Gpus.All(gpu => gpu is not null && Text(gpu.Name, required: true) && Text(gpu.Driver) &&
            gpu.Vendor is "nvidia" or "amd" or "intel" or "other" &&
            gpu.MemoryMb is null or (> 0 and <= 1_048_576) &&
            gpu.PowerLimitW is null or (> 0 and <= 10_000) && gpu.PowerDefaultW is null or (> 0 and <= 10_000));

    private static bool Text(string? value, bool required = false) =>
        value is null ? !required : value.Length is > 0 and <= 128 && value.All(c => c is >= ' ' and <= '~');
}

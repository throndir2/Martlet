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

/// <summary>A model a host's Ollama has loaded now, as its <c>/api/ps</c> says (the host's machine report, <c>loaded_models</c>):
/// <see cref="Bytes"/> in all and <see cref="GraphicsBytes"/> of that on the graphics card, at <see cref="ContextTokens"/>.
/// <see cref="Role"/> is the host role whose Ollama it is (ollama, deep-thinking, deep-thinking-2...). Paired desktops keep it
/// in model-memory.json, so the measured memory replaces the estimate.</summary>
public sealed record GatewayLoadedModel
{
    public required string Role { get; init; }
    public required string Model { get; init; }
    public required long Bytes { get; init; }
    public required long GraphicsBytes { get; init; }
    public int? ContextTokens { get; init; }
    public string? Digest { get; init; }
}

/// <summary>A model a host role ran on this host whose download the host keeps (in the role's data volumes): turning the
/// role back on, or switching back to the model, downloads nothing.</summary>
public sealed record GatewayMachineDownload
{
    /// <summary>The host role, for example ollama or chatterbox.</summary>
    public required string Role { get; init; }
    /// <summary>The model, as the role's route names it (for example gemma4:e4b).</summary>
    public required string Model { get; init; }
}

/// <summary>What a host machine is like, collected by <c>martlet-host</c> on the host itself and served to paired
/// desktops so they can plan which computer runs which role. Unauthenticated host-reported facts, not a
/// qualification or readiness claim.</summary>
public sealed record GatewayMachineReport
{
    public const int MaximumBytes = 16_384;
    /// <summary>The most loaded models the machine document lists (<see cref="GatewayLoadedModel"/>).</summary>
    public const int MaximumLoadedModels = 32;
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
    /// <summary>Macs: the chip, for example "Apple M2 Pro" or an Intel processor name.</summary>
    public string? Chip { get; init; }
    /// <summary>Macs with Apple silicon: memory is shared by the processor and the GPU.</summary>
    public bool? UnifiedMemory { get; init; }
    /// <summary>Macs: how much memory the GPU may use for models (Metal's recommended working set), in GB.</summary>
    public double? GpuWorkingSetGb { get; init; }
    /// <summary>The downloads the host keeps for roles that are off or that run another model now (Linux host engine).
    /// Absent on older hosts and on hosts without the list.</summary>
    public IReadOnlyList<GatewayMachineDownload>? Downloads { get; init; }

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
        Text(Chip) && GpuWorkingSetGb is null or (> 0 and <= 65_536) &&
        NvidiaContainers is null or "yes" or "no" or "unknown" &&
        ProcessorThreads is null or (> 0 and <= 4096) &&
        MemoryGb is null or (> 0 and <= 65_536) &&
        Gpus is { Count: <= 16 } &&
        Gpus.All(gpu => gpu is not null && Text(gpu.Name, required: true) && Text(gpu.Driver) &&
            gpu.Vendor is "nvidia" or "amd" or "intel" or "apple" or "other" &&
            gpu.MemoryMb is null or (> 0 and <= 1_048_576) &&
            gpu.PowerLimitW is null or (> 0 and <= 10_000) && gpu.PowerDefaultW is null or (> 0 and <= 10_000)) &&
        (Downloads is null || Downloads is { Count: <= 64 } && Downloads.All(d => d is not null && RoleName(d.Role) && ModelName(d.Model)));

    private static bool RoleName(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>The host engine's model pattern: ^[A-Za-z0-9][A-Za-z0-9._:/-]*$, 128 characters at most.</summary>
    private static bool ModelName(string? value) =>
        value is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '-');

    private static bool Text(string? value, bool required = false) =>
        value is null ? !required : value.Length is > 0 and <= 128 && value.All(c => c is >= ' ' and <= '~');
}

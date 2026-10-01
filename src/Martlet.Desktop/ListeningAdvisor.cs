using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Martlet.Desktop;

/// <summary>Something that uses this PC's graphics card memory when it runs (a model Martlet set up here).</summary>
internal sealed record GpuLoad(string What, double Gb)
{
    internal string Describe() => $"{What} (~{Gb.ToString("0.#", CultureInfo.InvariantCulture)} GB)";
}

/// <summary>This PC's NVIDIA graphics card as nvidia-smi reports it right now.</summary>
internal sealed record GpuNow(string Name, double TotalGb, double UsedGb, string? Driver)
{
    internal int? DriverMajor => Driver is { } text && int.TryParse(text.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
        ? major : null;
}

/// <summary>What Martlet suggests for listening (whisper) on this PC: the graphics card or the processor, and the model
/// for each, with the reason in plain words.</summary>
internal sealed record ListeningAdvice(bool UseGpu, string Reason, string? GpuModel, string CpuModel, string? GpuBlocked, string GpuNote)
{
    internal string Model => UseGpu ? GpuModel! : CpuModel;

    /// <summary>The suggestion as preselected martlet-host answers for the stt role.</summary>
    internal IReadOnlyDictionary<string, (string Value, string Why)> Answers() => new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["choice.accelerator"] = (UseGpu ? "gpu" : "cpu", Reason),
        ["choice.STT_MODEL"] = (Model, UseGpu ? "fits the free graphics card memory" : "quick enough on this processor")
    };
}

/// <summary>Chooses between whisper on the graphics card and on the processor from a live read of the card (memory in use,
/// driver) plus everything Martlet runs on it here, so a card that is already busy with big models is left alone.</summary>
internal static partial class ListeningAdvisor
{
    /// <summary>whisper.cpp's CUDA build of the pinned release needs an NVIDIA driver for CUDA 13.</summary>
    internal const int MinimumDriver = 580;
    private const double LargeNeedGb = 2.5;
    private const double SmallNeedGb = 1.2;
    private const double DesktopGb = 0.8;

    [GeneratedRegex(@"(?<!\d)(\d+(?:\.\d+)?)b(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ParametersPattern();

    /// <summary>About how much graphics card memory an Ollama model takes once loaded.</summary>
    internal static double OllamaModelGb(string model)
    {
        var known = MainWindow.LocalChatModels.FirstOrDefault(m => m.Id == model);
        if (known is not null && double.TryParse(known.Size.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
            return size + 0.5;
        var match = ParametersPattern().Match(model);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var billions)
            ? Math.Round(billions * 0.65 + 0.5, 1) : 4;
    }

    internal static ListeningAdvice Advise(GpuNow? gpu, GpuInfo? windowsGpu, IReadOnlyList<GpuLoad> loads, int threads, bool? containersUseGpu)
    {
        var cpuModel = threads >= 6 ? "small" : "base";
        ListeningAdvice Cpu(string reason, string? blocked, string note) => new(false, reason, null, cpuModel, blocked, note);
        if (gpu is null)
        {
            if (windowsGpu is { IsNvidia: true } card)
                return Cpu("the NVIDIA driver isn't answering", "The NVIDIA driver doesn't answer (nvidia-smi), so whisper can't use the graphics card yet.",
                    $"This PC has {card.Describe()}, but its NVIDIA driver doesn't answer.");
            return Cpu("there is no NVIDIA graphics card", "whisper's graphics card build needs an NVIDIA graphics card.",
                windowsGpu is null ? "No dedicated graphics card was found on this PC." : $"This PC has {windowsGpu.Describe()}, which isn't NVIDIA.");
        }
        var planned = loads.Sum(l => l.Gb);
        var busy = Math.Max(gpu.UsedGb, planned + DesktopGb);
        var free = Math.Max(0, gpu.TotalGb - busy);
        var note = $"This PC has {gpu.Name} ({Gb(gpu.TotalGb)} GB): {Gb(gpu.UsedGb)} GB in use now" +
            (loads.Count == 0 ? "." : $"; {string.Join(", ", loads.Select(l => l.Describe()))} also use it when they run.") +
            $" About {Gb(free)} GB is free for whisper.";
        if (gpu.DriverMajor is { } major && major < MinimumDriver)
            return Cpu($"the NVIDIA driver ({gpu.Driver}) is older than {MinimumDriver}",
                $"whisper's graphics card build needs NVIDIA driver {MinimumDriver} or newer; this PC has {gpu.Driver}. Update the driver to use it.", note);
        if (containersUseGpu == false)
            return Cpu("Docker Desktop can't use the graphics card", "Docker Desktop on this PC reported that containers can't use the NVIDIA graphics card yet.", note);
        if (free >= LargeNeedGb)
            return new(true, $"{Gb(free)} GB of its {Gb(gpu.TotalGb)} GB is free", "large-v3-turbo", cpuModel, null, note);
        if (free >= SmallNeedGb)
            return new(true, $"only {Gb(free)} GB of its {Gb(gpu.TotalGb)} GB is free, enough for the small model", "small", cpuModel, null, note);
        return new(false, loads.Count > 0
                ? $"the graphics card is already busy ({string.Join(", ", loads.Select(l => l.What))}), with about {Gb(free)} GB free"
                : $"the graphics card has only about {Gb(free)} GB free",
            "small", cpuModel, null, note);
    }

    private static string Gb(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Reads the NVIDIA card with the most memory from nvidia-smi (hidden, a few seconds at most); null when
    /// there is no NVIDIA driver or it doesn't answer.</summary>
    internal static async Task<GpuNow?> ReadGpuAsync(CancellationToken token)
    {
        var smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(smi)) return null;
        var lines = new List<string>();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            var exit = await LocalProcess.RunAsync(smi, ["--query-gpu=name,memory.total,memory.used,driver_version", "--format=csv,noheader,nounits"],
                new LineSink(lines.Add), limit.Token);
            if (exit != 0) return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (InvalidOperationException) { return null; }
        return lines.Select(Parse).OfType<GpuNow>().OrderByDescending(g => g.TotalGb).FirstOrDefault();
    }

    internal static GpuNow? Parse(string line)
    {
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length < 4 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var total) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var used) || total <= 0)
            return null;
        return new(parts[0], Math.Round(total / 1024, 1), Math.Round(used / 1024, 1), parts[3].Length > 0 ? parts[3] : null);
    }

    /// <summary>Whether whisper in this PC's host service runs on the graphics card ("gpu") or the processor ("cpu"), from
    /// its container image; null when it isn't installed or Docker Desktop isn't running.</summary>
    internal static async Task<string?> InstalledAcceleratorAsync(CancellationToken token)
    {
        if (!MachineInfo.DockerDesktopRunning()) return null;
        var lines = new List<string>();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var exit = await LocalProcess.RunAsync(HostLocal.Docker,
                ["ps", "-a", "--filter", "label=com.docker.compose.project=martlet-stt", "--format", "{{.Image}}"], new LineSink(lines.Add), limit.Token);
            if (exit != 0) return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (InvalidOperationException) { return null; }
        var image = lines.FirstOrDefault(l => l.Contains("whisper.cpp", StringComparison.OrdinalIgnoreCase));
        return image is null ? null : image.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Companion.Platform;
using Martlet.Core.Platforms;

namespace Martlet.Companion;

/// <summary>The headless status (<c>Martlet.Companion --status</c>): what the companion detected about this computer,
/// which platform services work, and what the catalog guardrails offer and refuse. Read by Martlet MCP
/// (<c>companion_status</c>) and validation; contains no secrets.</summary>
public static class CompanionStatus
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Platforms the status can be computed for without being on them (<c>--as</c>), so the guardrails of
    /// Linux and macOS can be checked from any computer. Simulated output says so.</summary>
    public static IReadOnlyDictionary<string, PlatformInfo> Profiles { get; } = new Dictionary<string, PlatformInfo>(StringComparer.Ordinal)
    {
        ["linux-x64"] = new() { Platform = DevicePlatform.Linux, OsDescription = "Linux (simulated)", Architecture = Architecture.X64, Gpus = [], MemoryGb = 16, DisplayServer = DisplayServer.X11 },
        ["linux-nvidia"] = new()
        {
            Platform = DevicePlatform.Linux, OsDescription = "Linux with NVIDIA (simulated)", Architecture = Architecture.X64,
            Gpus = [new PlatformGpu("NVIDIA GeForce RTX 4070", "nvidia", 12)], MemoryGb = 32, DisplayServer = DisplayServer.Wayland, XWayland = true
        },
        ["linux-arm64"] = new() { Platform = DevicePlatform.Linux, OsDescription = "Linux ARM64 (simulated)", Architecture = Architecture.Arm64, Gpus = [], MemoryGb = 8, DisplayServer = DisplayServer.Wayland, XWayland = true },
        ["macos-arm64"] = new()
        {
            Platform = DevicePlatform.MacOs, OsVersion = new(15, 0), OsDescription = "macOS 15 on Apple silicon (simulated)", Architecture = Architecture.Arm64,
            AppleSilicon = true, Gpus = [new PlatformGpu("Apple GPU", "apple", null)], MemoryGb = 16, DisplayServer = DisplayServer.Quartz
        },
        ["macos-x64"] = new()
        {
            Platform = DevicePlatform.MacOs, OsVersion = new(13, 6), OsDescription = "macOS 13 on Intel (simulated)", Architecture = Architecture.X64,
            AppleSilicon = false, Gpus = [], MemoryGb = 16, DisplayServer = DisplayServer.Quartz
        }
    };

    public static object Build(CompanionPlatform platform, PlatformInfo info, bool simulated = false,
        (string File, IReadOnlyList<string> Refusals, CompanionSettings Admitted)? import = null)
    {
        var guardrails = new CompanionGuardrails(info);
        return new
        {
            app = "Martlet.Companion",
            version = typeof(CompanionStatus).Assembly.GetName().Version?.ToString(3),
            simulated,
            platform = new
            {
                os = PlatformCatalog.Name(info.Platform),
                osVersion = info.OsVersion?.ToString(),
                info.OsDescription,
                architecture = info.Architecture.ToString(),
                processArchitecture = info.ProcessArchitecture.ToString(),
                info.AppleSilicon,
                info.HasNvidia,
                gpus = info.Gpus?.Select(g => g.Describe()).ToArray(),
                info.MemoryGb,
                displayServer = info.DisplayServer,
                info.XWayland,
                info.Desktop,
                info.CpuOnlyLocalModels
            },
            services = new
            {
                hotkey = platform.Hotkey.Status,
                overlay = platform.Overlay.Status,
                screenCapture = platform.ScreenCapture.Status,
                credentials = new { platform.Credentials.Status.Available, platform.Credentials.Status.Reason, platform.Credentials.IsPersistent },
                autostart = platform.Autostart.Status,
                tray = platform.Tray?.Status ?? platform.TrayHost ?? FeatureStatus.Yes("Avalonia tray icon"),
                audio = FeatureStatus.Yes("miniaudio (SoundFlow): PipeWire/PulseAudio/ALSA, CoreAudio; opened only while talking or speaking")
            },
            character = new
            {
                rendererBuilt = CharacterModel.BundleBuilt,
                bundledLive2D = File.Exists(Path.Combine(CharacterModel.BundleFolder, "live2d", "characters", "Hiyori", "Hiyori.model3.json"))
            },
            defaults = CompanionSettingsStore.Defaults(guardrails) is var defaults
                ? new { defaults.Thinking, defaults.Listening, defaults.Speaking, defaults.LipSync, defaults.PushToTalkKey } : null,
            guardrails = guardrails.Describe(),
            import = import is { } imported ? new
            {
                file = imported.File,
                refusals = imported.Refusals,
                kept = new { imported.Admitted.Thinking, imported.Admitted.Listening, imported.Admitted.Speaking, imported.Admitted.LipSync }
            } : null
        };
    }

    public static string Json(CompanionPlatform platform, PlatformInfo info, bool simulated = false,
        (string File, IReadOnlyList<string> Refusals, CompanionSettings Admitted)? import = null) =>
        JsonSerializer.Serialize(Build(platform, info, simulated, import), Options);

    /// <summary>Runs <c>--status [--as profile] [--import settings.json]</c>; returns the exit code.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        var platform = PlatformSelector.Create();
        var info = platform.Probe.Probe();
        var simulated = false;
        var at = Array.IndexOf(args, "--as");
        if (at >= 0)
        {
            if (at + 1 >= args.Length || !Profiles.TryGetValue(args[at + 1], out var profile))
            {
                output.WriteLine(JsonSerializer.Serialize(new { error = "--as takes one of: " + string.Join(", ", Profiles.Keys) }));
                return 2;
            }
            info = profile;
            simulated = true;
            platform = CompanionPlatform.Defaults();
        }
        (string, IReadOnlyList<string>, CompanionSettings)? import = null;
        var file = Array.IndexOf(args, "--import");
        if (file >= 0 && file + 1 < args.Length)
        {
            var guardrails = new CompanionGuardrails(info);
            try
            {
                var (admitted, refusals) = CompanionSettingsStore.Import(File.ReadAllText(args[file + 1]), guardrails, CompanionSettingsStore.Defaults(guardrails));
                import = (Path.GetFileName(args[file + 1]), refusals, admitted);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                output.WriteLine(JsonSerializer.Serialize(new { error = "The settings file couldn't be read: " + error.Message }));
                return 2;
            }
        }
        output.WriteLine(Json(platform, info, simulated, import));
        return 0;
    }
}

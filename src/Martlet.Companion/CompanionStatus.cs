using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Companion.Platform;
using Martlet.Core.Platforms;

namespace Martlet.Companion;

/// <summary>The headless status (<c>Martlet.Companion --status</c>): what the companion detected about this computer
/// and which platform services work. Read by Martlet MCP and validation; contains no secrets.</summary>
public static class CompanionStatus
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static object Build(CompanionPlatform platform, PlatformInfo info) => new
    {
        app = "Martlet.Companion",
        version = typeof(CompanionStatus).Assembly.GetName().Version?.ToString(3),
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
            tray = platform.Tray?.Status ?? FeatureStatus.Yes("Avalonia tray icon")
        }
    };

    public static string Json(CompanionPlatform platform, PlatformInfo info) => JsonSerializer.Serialize(Build(platform, info), Options);
}

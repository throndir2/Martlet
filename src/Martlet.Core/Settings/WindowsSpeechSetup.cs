using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class WindowsSpeechSetup
{
    public const string SttAlias = "windows-recognition";
    public const string TtsAlias = "windows-speech";
    public const string TtsModelId = "windows-installed";

    public static void InstalledId(string value)
    {
        ContractRules.Require(value is { Length: > 0 and <= 4096 } && !string.IsNullOrWhiteSpace(value) &&
            !value.Any(char.IsControl), "Select an exact installed Windows speech identifier.");
        try
        {
            ContractRules.Require(new UTF8Encoding(false, true).GetByteCount(value) <= 16_384,
                "The installed Windows speech identifier is too large.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The Windows speech identifier contains invalid Unicode.");
        }
    }

    public static AppSettings SelectStt(AppSettings settings, string recognizerId) =>
        Select(settings, SetupRole.Stt, recognizerId, null);

    public static AppSettings SelectTts(AppSettings settings, string voiceId) =>
        Select(settings, SetupRole.Tts, TtsModelId, voiceId);

    private static AppSettings Select(AppSettings settings, SetupRole role, string modelId, string? voiceId)
    {
        settings.Validate();
        ContractRules.Require(settings.Setup is not null, "Open setup before selecting installed Windows speech.");
        var type = role == SetupRole.Stt ? SetupRouteType.LocalWindowsStt : SetupRouteType.LocalWindowsTts;
        var old = settings.Setup?.Routes.SingleOrDefault(route => route.Role == role);
        if (old?.RouteType == type && old.ModelId == modelId && old.VoiceId == voiceId)
            return settings;
        return SetupSettings.ReplaceRoute(settings, new()
        {
            RouteSchemaVersion = 1, RouteType = type, Enabled = true, Role = role,
            ProviderAlias = role == SetupRole.Stt ? SttAlias : TtsAlias,
            Origin = SelfHostSetup.LocalOrigin, ModelId = modelId, VoiceId = voiceId,
            ConfigurationRevision = Guid.NewGuid()
        });
    }
}

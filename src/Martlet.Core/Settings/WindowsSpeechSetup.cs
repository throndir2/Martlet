using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class WindowsSpeechSetup
{
    public const string SttAlias = "windows-recognition";
    /// <summary>The retired Windows voice route's alias and model: kept so older settings files still validate.</summary>
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

    public static AppSettings SelectStt(AppSettings settings, string recognizerId)
    {
        settings.Validate();
        ContractRules.Require(settings.Setup is not null, "Open setup before selecting installed Windows speech.");
        var old = settings.Setup?.Routes.SingleOrDefault(route => route.Role == SetupRole.Stt);
        if (old?.RouteType == SetupRouteType.LocalWindowsStt && old.ModelId == recognizerId && old.VoiceId is null)
            return settings;
        return SetupSettings.ReplaceRoute(settings, new()
        {
            RouteSchemaVersion = 1, RouteType = SetupRouteType.LocalWindowsStt, Enabled = true, Role = SetupRole.Stt,
            ProviderAlias = SttAlias, Origin = SelfHostSetup.LocalOrigin, ModelId = recognizerId,
            ConfigurationRevision = Guid.NewGuid()
        });
    }
}

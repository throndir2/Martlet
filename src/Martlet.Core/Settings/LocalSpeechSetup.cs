using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Speech engines Martlet itself runs on this PC (no Docker, no host service, nothing sent anywhere).</summary>
public static class LocalSpeechSetup
{
    public const string ParakeetAlias = "local-parakeet";
    public const string ParakeetModelId = "parakeet-tdt-0.6b-v3-int8";

    /// <summary>Listening with NVIDIA Parakeet TDT 0.6B v3 on this PC's processor (its model is downloaded separately).</summary>
    public static AppSettings SelectParakeet(AppSettings settings)
    {
        settings.Validate();
        ContractRules.Require(settings.Setup is not null, "Open setup before choosing Parakeet.");
        var old = settings.Setup!.Routes.SingleOrDefault(route => route.Role == SetupRole.Stt);
        if (old is { RouteType: SetupRouteType.LocalParakeet, Enabled: true }) return settings;
        return SetupSettings.ReplaceRoute(settings, new()
        {
            RouteSchemaVersion = 1, RouteType = SetupRouteType.LocalParakeet, Enabled = true, Role = SetupRole.Stt,
            ProviderAlias = ParakeetAlias, Origin = SelfHostSetup.LocalOrigin, ModelId = ParakeetModelId,
            ConfigurationRevision = Guid.NewGuid()
        });
    }
}

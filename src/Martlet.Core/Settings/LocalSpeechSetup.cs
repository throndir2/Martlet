using System.Globalization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Speech engines Martlet itself runs on this PC (no Docker, no host service, nothing sent anywhere).</summary>
public static class LocalSpeechSetup
{
    public const string ParakeetAlias = "local-parakeet";
    /// <summary>NVIDIA Parakeet TDT 110M (English): the fastest, about 0.1 s for a short turn on the processor, less accurate
    /// with noise or a distant microphone. The default for English.</summary>
    public const string Parakeet110mEnglishModelId = "parakeet-tdt-110m-en";
    /// <summary>NVIDIA Parakeet TDT 0.6B v2 (int8, English): the most accurate in English, about 0.2 s slower than 110M.</summary>
    public const string ParakeetV2EnglishModelId = "parakeet-tdt-0.6b-v2-int8";
    /// <summary>NVIDIA Parakeet TDT 0.6B v3 (int8): 25 European languages. The first Parakeet Martlet offered (a route saved with
    /// it keeps it) and the default when Windows' display language isn't English.</summary>
    public const string ParakeetV3ModelId = "parakeet-tdt-0.6b-v3-int8";

    /// <summary>The Parakeet models a LocalParakeet route may use (Martlet.Sherpa's ParakeetModels has their files), fastest first.</summary>
    public static IReadOnlyList<string> ParakeetModelIds { get; } = [Parakeet110mEnglishModelId, ParakeetV2EnglishModelId, ParakeetV3ModelId];

    public static bool IsParakeetModel(string? modelId) => modelId is not null && ParakeetModelIds.Contains(modelId, StringComparer.Ordinal);

    /// <summary>The Parakeet model Martlet recommends for a Windows display language: the fastest (110M) for English, where
    /// latency comes first, and v3 for any other language (the English-only models can't hear it).</summary>
    public static string RecommendedParakeetModel(CultureInfo displayLanguage) =>
        displayLanguage.TwoLetterISOLanguageName == "en" ? Parakeet110mEnglishModelId : ParakeetV3ModelId;

    /// <summary>The 25 languages Parakeet TDT 0.6B v3 hears (ISO 639-1 codes, from NVIDIA's model card).</summary>
    public static IReadOnlySet<string> ParakeetV3Languages { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hr", "hu", "it", "lt", "lv", "mt", "nl", "pl", "pt", "ro",
        "ru", "sk", "sl", "sv", "uk"
    };

    /// <summary>The Parakeet model that hears an utterance on this PC's processor when Listening's own route (a paired host or
    /// OpenAI) fails, so the turn goes on and nothing is sent anywhere: the model already loaded (<paramref name="loaded"/>)
    /// when it fits, else for an English display language the fastest one downloaded (110M, then v2, then v3), otherwise v3
    /// when it hears that language. Null when Listening isn't one of those routes (Parakeet on this PC is already the
    /// processor) or no downloaded model fits. <paramref name="installed"/> says whether a model is downloaded here.</summary>
    public static string? ListeningStandIn(SetupRoute? listening, Func<string, bool> installed, CultureInfo displayLanguage,
        string? loaded = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(displayLanguage);
        if (listening is not { Role: SetupRole.Stt } || listening.RouteType is not (null or SetupRouteType.OpenAi or SetupRouteType.GatewayStt))
            return null;
        var language = displayLanguage.TwoLetterISOLanguageName;
        string[] fits = language == "en" ? [Parakeet110mEnglishModelId, ParakeetV2EnglishModelId, ParakeetV3ModelId]
            : ParakeetV3Languages.Contains(language) ? [ParakeetV3ModelId] : [];
        if (loaded is not null && fits.Contains(loaded, StringComparer.Ordinal) && installed(loaded)) return loaded;
        return fits.FirstOrDefault(installed);
    }

    /// <summary>Listening with the Parakeet model <paramref name="modelId"/> on this PC's processor (its files are downloaded
    /// separately). Unchanged when Listening already uses that model.</summary>
    public static AppSettings SelectParakeet(AppSettings settings, string modelId)
    {
        settings.Validate();
        ContractRules.Require(settings.Setup is not null, "Open setup before choosing Parakeet.");
        ContractRules.Require(IsParakeetModel(modelId), "This Parakeet model isn't one Martlet knows.");
        var old = settings.Setup!.Routes.SingleOrDefault(route => route.Role == SetupRole.Stt);
        if (old is { RouteType: SetupRouteType.LocalParakeet, Enabled: true } && old.ModelId == modelId) return settings;
        return SetupSettings.ReplaceRoute(settings, new()
        {
            RouteSchemaVersion = 1, RouteType = SetupRouteType.LocalParakeet, Enabled = true, Role = SetupRole.Stt,
            ProviderAlias = ParakeetAlias, Origin = SelfHostSetup.LocalOrigin, ModelId = modelId,
            ConfigurationRevision = Guid.NewGuid()
        });
    }
}

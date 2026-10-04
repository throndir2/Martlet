using System.Globalization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

public sealed class LocalSpeechSetupTests
{
    private static AppSettings Settings => SetupSettings.Begin(null);

    private static SetupRoute Stt(AppSettings settings) => settings.Setup!.Routes.Single(route => route.Role == SetupRole.Stt);

    [Fact]
    public void Offers_three_parakeet_models_fastest_first()
    {
        Assert.Equal(["parakeet-tdt-110m-en", "parakeet-tdt-0.6b-v2-int8", "parakeet-tdt-0.6b-v3-int8"], LocalSpeechSetup.ParakeetModelIds);
        Assert.All(LocalSpeechSetup.ParakeetModelIds, id => Assert.True(LocalSpeechSetup.IsParakeetModel(id)));
        Assert.False(LocalSpeechSetup.IsParakeetModel(null));
        Assert.False(LocalSpeechSetup.IsParakeetModel("parakeet-tdt-1.1b"));
        Assert.False(LocalSpeechSetup.IsParakeetModel("PARAKEET-TDT-110M-EN"));
    }

    [Theory]
    [InlineData("en-US", LocalSpeechSetup.Parakeet110mEnglishModelId)]
    [InlineData("en-GB", LocalSpeechSetup.Parakeet110mEnglishModelId)]
    [InlineData("en", LocalSpeechSetup.Parakeet110mEnglishModelId)]
    [InlineData("de-DE", LocalSpeechSetup.ParakeetV3ModelId)]
    [InlineData("fr-FR", LocalSpeechSetup.ParakeetV3ModelId)]
    [InlineData("ja-JP", LocalSpeechSetup.ParakeetV3ModelId)]
    [InlineData("", LocalSpeechSetup.ParakeetV3ModelId)]
    public void Recommends_the_fastest_model_for_english_and_v3_otherwise(string language, string expected) =>
        Assert.Equal(expected, LocalSpeechSetup.RecommendedParakeetModel(CultureInfo.GetCultureInfo(language)));

    [Theory]
    [InlineData(LocalSpeechSetup.Parakeet110mEnglishModelId)]
    [InlineData(LocalSpeechSetup.ParakeetV2EnglishModelId)]
    [InlineData(LocalSpeechSetup.ParakeetV3ModelId)]
    public void Each_model_is_an_exact_local_route_without_credentials(string model)
    {
        var settings = LocalSpeechSetup.SelectParakeet(Settings, model);
        settings.Validate();
        var route = Stt(settings);
        Assert.Equal(SetupRouteType.LocalParakeet, route.RouteType);
        Assert.Equal(model, route.ModelId);
        Assert.Equal(LocalSpeechSetup.ParakeetAlias, route.ProviderAlias);
        Assert.Equal(SelfHostSetup.LocalOrigin, route.Origin);
        Assert.True(route.Enabled);
        Assert.True(route.Selection().AllowLocalProcess);
        Assert.False(route.Selection().AllowNetworkDisclosure);
        Assert.False(route.Selection().AllowPotentialCost);
        Assert.Throws<ContractException>(() => route.WithCredential(Guid.NewGuid()));
        Assert.Equal(route, SettingsJson.Read(ContractJson.Write(settings)).Setup!.Routes.Single(r => r.Role == SetupRole.Stt));
    }

    [Fact]
    public void Unknown_models_are_refused_by_selection_and_validation()
    {
        Assert.Throws<ContractException>(() => LocalSpeechSetup.SelectParakeet(Settings, "parakeet-tdt-1.1b"));
        var route = Stt(LocalSpeechSetup.SelectParakeet(Settings, LocalSpeechSetup.ParakeetV3ModelId));
        Assert.Throws<ContractException>(() => (route with { ModelId = "parakeet-tdt-1.1b" }).Validate());
        Assert.Throws<ContractException>(() => (route with { ModelId = "" }).Validate());
    }

    [Fact]
    public void A_saved_model_is_kept_and_switching_models_needs_a_new_confirmation()
    {
        var saved = LocalSpeechSetup.SelectParakeet(Settings, LocalSpeechSetup.ParakeetV3ModelId);
        var v3 = Stt(saved);
        saved = SetupSettings.ReplaceRoute(saved, v3 with { Consent = v3.Selection() });
        // Choosing the model it already uses changes nothing, consent included.
        Assert.Same(saved, LocalSpeechSetup.SelectParakeet(saved, LocalSpeechSetup.ParakeetV3ModelId));
        var fastest = Stt(LocalSpeechSetup.SelectParakeet(saved, LocalSpeechSetup.Parakeet110mEnglishModelId));
        Assert.Equal(LocalSpeechSetup.Parakeet110mEnglishModelId, fastest.ModelId);
        Assert.Null(fastest.Consent);
        Assert.NotEqual(v3.ConfigurationRevision, fastest.ConfigurationRevision);
        Assert.NotEqual(v3.Selection().SelectionSha256, fastest.Selection().SelectionSha256);
        Assert.Throws<ContractException>(() => (fastest with { Consent = Stt(saved).Consent }).Validate());
    }

    [Theory]
    [InlineData(LocalSpeechSetup.Parakeet110mEnglishModelId)]
    [InlineData(LocalSpeechSetup.ParakeetV2EnglishModelId)]
    [InlineData(LocalSpeechSetup.ParakeetV3ModelId)]
    public void The_shared_listening_route_carries_the_model_to_other_computers(string model)
    {
        var shared = SharedRoute.From(Stt(LocalSpeechSetup.SelectParakeet(Settings, model)))!;
        Assert.Equal(SharedRoute.Parakeet, shared.Type);
        Assert.Equal(model, shared.Model);
        var built = SharedRoute.Parse(AppSettingsSectionsJson(shared)).Build(SetupRole.Stt);
        Assert.Equal(SetupRouteType.LocalParakeet, built.RouteType);
        Assert.Equal(model, built.ModelId);
        Assert.True(built.Enabled);
        Assert.Null(built.CredentialId);
    }

    [Fact]
    public void A_parakeet_model_from_a_newer_martlet_waits_for_an_update()
    {
        var shared = new SharedRoute { Type = SharedRoute.Parakeet, Model = "parakeet-tdt-1.1b" };
        var error = Assert.Throws<ContractException>(() => shared.Build(SetupRole.Stt));
        Assert.Equal(ErrorCode.UnsupportedVersion, error.Code);
        Assert.Contains("newer Martlet", error.Message);
        Assert.Throws<ContractException>(() => (shared with { Model = LocalSpeechSetup.ParakeetV3ModelId }).Build(SetupRole.Tts));
    }

    private static string AppSettingsSectionsJson(SharedRoute route) =>
        System.Text.Json.JsonSerializer.Serialize(route, AppSettingsSections.Json);
}

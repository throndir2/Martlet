using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ElevenLabsSettingsTests
{
    private const string VoiceId = "fixtureVoice00000001";
    private static readonly ClonedVoiceSettings Cloned = new()
    {
        SchemaVersion = 1, LibraryVoiceId = new string('a', 64), AudioSha256 = new string('b', 64), Name = "Mia", RequiresVerification = false
    };

    private static AppSettings Settings => ElevenLabsSetup.Select(SetupSettings.Begin(null), ElevenLabsSetup.V4Turbo, VoiceId, Cloned);

    private static AppSettings Keyed(AppSettings settings)
    {
        var route = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Tts);
        route = route.WithCredential(Guid.NewGuid());
        return SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
    }

    [Fact]
    public void Choosing_ElevenLabs_saves_a_route_with_its_cloned_voice_that_survives_a_round_trip()
    {
        var settings = Settings;
        settings.Validate();
        var route = settings.Setup!.Routes.Single();
        Assert.Equal(SetupRouteType.ElevenLabs, route.RouteType);
        Assert.Equal(SetupRole.Tts, route.Role);
        Assert.Equal("https://api.elevenlabs.io", route.Origin);
        Assert.Equal(ElevenLabsSetup.Alias, route.ProviderAlias);
        Assert.Equal(VoiceId, route.VoiceId);
        Assert.Equal(Cloned, route.ClonedVoice);
        Assert.True(route.Enabled);
        var selection = route.Selection();
        Assert.True(selection.AllowNetworkDisclosure);
        Assert.True(selection.AllowReferenceAudio);
        Assert.True(selection.AllowPotentialCost);
        Assert.False(selection.AllowLocalProcess);
        Assert.Equal(route, SettingsJson.Read(ContractJson.Write(settings)).Setup!.Routes.Single());
        Assert.Contains("Voice: ElevenLabs voice", SetupSettings.Describe(settings));
    }

    [Fact]
    public void A_key_keeps_the_route_on_and_another_voice_or_model_needs_consent_again()
    {
        var keyed = Keyed(Settings);
        var route = keyed.Setup!.Routes.Single();
        Assert.True(route.Enabled);
        Assert.Equal(route.Selection(), route.Consent);
        Assert.Same(keyed, ElevenLabsSetup.Select(keyed, ElevenLabsSetup.V4Turbo, VoiceId, Cloned));

        var model = ElevenLabsSetup.Select(keyed, ElevenLabsSetup.V3Conversational, VoiceId, Cloned).Setup!.Routes.Single();
        Assert.Equal(route.CredentialId, model.CredentialId);
        Assert.Null(model.Consent);
        var other = ElevenLabsSetup.Select(keyed, ElevenLabsSetup.V4Turbo, "otherVoice0000000001", Cloned with { Name = "Kai" }).Setup!.Routes.Single();
        Assert.Null(other.Consent);
        Assert.Throws<ContractException>(() => (other with { Consent = route.Consent }).Validate());
    }

    [Theory]
    [InlineData("eleven_multilingual_v2", VoiceId)]
    [InlineData(ElevenLabsSetup.V4Turbo, "has space")]
    [InlineData(ElevenLabsSetup.V4Turbo, "")]
    public void Unsupported_models_and_voice_ids_are_refused(string model, string voice) =>
        Assert.Throws<ContractException>(() => ElevenLabsSetup.Select(SetupSettings.Begin(null), model, voice, Cloned));

    [Fact]
    public void An_ElevenLabs_route_needs_its_exact_origin_and_cloned_voice_and_no_other_route_keeps_one()
    {
        var route = Settings.Setup!.Routes.Single();
        Assert.Throws<ContractException>(() => (route with { Origin = "https://api.elevenlabs.io.example" }).Validate());
        Assert.Throws<ContractException>(() => (route with { ClonedVoice = null }).Validate());
        Assert.Throws<ContractException>(() => (route with { ClonedVoice = Cloned with { AudioSha256 = "not-a-hash" } }).Validate());
        var openAi = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Tts, "gpt-4o-mini-tts", "alloy").Setup!.Routes.Single();
        Assert.Throws<ContractException>(() => (openAi with { ClonedVoice = Cloned }).Validate());
    }

    [Fact]
    public void The_key_is_scoped_to_ElevenLabs_set_aside_when_Voice_leaves_and_used_again_on_return()
    {
        var keyed = Keyed(Settings);
        var route = keyed.Setup!.Routes.Single();
        var binding = CredentialBinding.For(keyed.Profile.Id, route, route.CredentialId!.Value);
        binding.Validate();
        Assert.Equal(SetupRouteType.ElevenLabs, binding.RouteType);
        Assert.Throws<ContractException>(() => (binding with { Role = SetupRole.Llm }).Validate());

        // Voice moves to OpenAI: the ElevenLabs key is listed for removal with its own scope, never orphaned.
        var windows = SetupSettings.QueueReplacedCredential(SetupSettings.SelectRoute(keyed, SetupRole.Tts, "gpt-4o-mini-tts-2025-12-15", "coral"), route);
        var removal = Assert.Single(SetupSettings.SetAsideElevenLabsCredentials(windows));
        Assert.Equal(SetupRouteType.ElevenLabs, removal.Scope!.RouteType);
        Assert.Empty(SetupSettings.SetAsideCredentials(windows, SetupRole.Tts, null));
        CredentialBinding.For(windows, removal).Validate();

        // Back to ElevenLabs: the key set aside is used again and leaves the list.
        var back = ElevenLabsSetup.Select(windows, ElevenLabsSetup.V4Turbo, VoiceId, Cloned);
        var reattached = SetupSettings.ReattachSetAsideCredential(back, removal);
        Assert.Equal(route.CredentialId, reattached.Setup!.Routes.Single(r => r.Role == SetupRole.Tts).CredentialId);
        Assert.Empty(reattached.Setup.PendingRemovals);
    }

    [Fact]
    public void Restoring_settings_turns_ElevenLabs_off_without_its_key_and_choosing_it_again_turns_it_on()
    {
        var keyed = Keyed(Settings);
        var restored = keyed.Setup!.Routes.Single().DisableForRestore();
        restored.Validate();
        Assert.False(restored.Enabled);
        Assert.Null(restored.CredentialId);
        Assert.Null(restored.Consent);
        Assert.Equal(Cloned, restored.ClonedVoice);

        var again = ElevenLabsSetup.Select(SetupSettings.ReplaceRoute(keyed, restored), ElevenLabsSetup.V4Turbo, VoiceId, Cloned);
        Assert.True(again.Setup!.Routes.Single().Enabled);
    }
}

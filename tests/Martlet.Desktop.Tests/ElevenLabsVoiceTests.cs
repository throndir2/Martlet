using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class ElevenLabsVoiceTests
{
    private const string VoiceId = "fixtureVoice00000001";
    private static readonly ClonedVoiceSettings Cloned = new()
    {
        SchemaVersion = 1, LibraryVoiceId = new string('a', 64), AudioSha256 = new string('b', 64), Name = "Mia", RequiresVerification = false
    };

    [Fact]
    public async Task A_saved_ElevenLabs_voice_speaks_replies_with_its_tags_under_its_own_binding()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var chosen = ElevenLabsSetup.Select(loaded.Settings!, ElevenLabsSetup.V4Turbo, VoiceId, Cloned);
        var route = chosen.Setup!.Routes.Single(r => r.Role == SetupRole.Tts);

        // Without a key, voice replies say what to do; text replies still work.
        var keyless = SetupSettings.ReplaceRoute(chosen, route with { Consent = route.Selection() });
        var configuration = LiveConversationConfiguration.From(loaded with { Settings = keyless })!;
        Assert.Equal("Add your ElevenLabs API key in Companion › Voice.", configuration.Unavailable(true, false));
        Assert.Null(configuration.Unavailable(false, false));

        var keyed = route.WithCredential(Guid.NewGuid());
        configuration = LiveConversationConfiguration.From(loaded with
        {
            Settings = SetupSettings.ReplaceRoute(chosen, keyed with { Consent = keyed.Selection() })
        })!;
        Assert.Null(configuration.Unavailable(true, false));
        var target = configuration.ElevenLabsVoiceTarget()!;
        Assert.Equal((VoiceId, ElevenLabsSetup.V4Turbo), (target.VoiceId, target.ModelId));
        Assert.Equal(ElevenLabsSpeechSynthesisStream.Selection(target), configuration.SpeechSelection());
        Assert.Null(configuration.HostSpeechTarget());
        Assert.Null(configuration.WindowsVoiceTarget());
        Assert.Same(SpeechEngines.ElevenLabs, configuration.SpeakingEngine());
        Assert.Contains("[whispers] - whispered", configuration.VoiceTagInstructions());
        Assert.Contains("Reply text goes to ElevenLabs", configuration.Disclosure(true));

        var request = configuration.Request(new("Hi"), true, [], null, null, out _, out _, out _);
        Assert.Equal(target, request.ElevenLabsVoice);
        Assert.Equal(ElevenLabsSetup.Alias, request.Speech!.Selection.ModelAlias);
        Assert.Contains("[laughs]", request.Input.Personality);
        // A text-only reply sends nothing to ElevenLabs and gets no voice tags.
        var text = configuration.Request(new("Hi"), false, [], null, null, out _, out _, out _);
        Assert.Null(text.ElevenLabsVoice);
        Assert.DoesNotContain("[laughs]", text.Input.Personality ?? "");
        fixture.NoEffects();
    }

    [Theory]
    [InlineData(ProviderFailureCode.ModelUnsupported, ElevenLabsSetup.V4Turbo, "ElevenLabs refused Eleven v4 Turbo. Choose Eleven v3 Conversational")]
    [InlineData(ProviderFailureCode.ModelUnsupported, ElevenLabsSetup.V3Conversational, "Choose Eleven v4 Turbo")]
    [InlineData(ProviderFailureCode.Authentication, ElevenLabsSetup.V4Turbo, "didn't accept your API key")]
    [InlineData(ProviderFailureCode.VoiceUnsupported, ElevenLabsSetup.V4Turbo, "verify it on elevenlabs.io")]
    [InlineData(ProviderFailureCode.QuotaExceeded, ElevenLabsSetup.V4Turbo, "out of credits")]
    public void ElevenLabs_failures_say_what_to_do(ProviderFailureCode code, string model, string expected) =>
        Assert.Contains(expected, LiveConversationWindow.ElevenLabsRemedy(code, model));

    [Fact]
    public void A_network_failure_keeps_the_usual_remedy() =>
        Assert.Null(LiveConversationWindow.ElevenLabsRemedy(ProviderFailureCode.Network, ElevenLabsSetup.V4Turbo));
}

using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers.Tests;

// ElevenLabs speech and cloning against ElevenLabsFixture: a local stand-in on 127.0.0.1 that follows ElevenLabs' documented
// protocol (FIXTURE, NOT ElevenLabs). Nothing here reaches the live service.
public sealed class ElevenLabsTests
{
    private const string Voice = "fixtureVoice00000001";
    private static readonly SpeechSynthesisLimits Limits = new()
    {
        MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10), FirstAudioTimeout = TimeSpan.FromSeconds(10),
        MaxRequestTime = TimeSpan.FromSeconds(20)
    };

    private sealed class Key(string key) : IProviderCredentialSource
    {
        public int Resolved { get; private set; }
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken)
        {
            Resolved++;
            return ValueTask.FromResult<BoundProviderCredential?>(new(binding, key));
        }
    }

    private static ProviderRequestContext Context() => new()
    {
        Ids = new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
        Epoch = 3, Deadline = DateTimeOffset.UtcNow.AddSeconds(30)
    };

    private static async Task<(int Frames, long Samples, SpeechSynthesisResult Result)> Speak(ElevenLabsFixture fixture, string model, string text,
        IProviderCredentialSource? key = null, bool authorize = true)
    {
        var client = new ElevenLabsDialogueClient(key ?? new Key("fixture-key"), fixture.Origin);
        var target = new ElevenLabsVoiceTarget(Voice, model);
        var context = Context();
        var input = new BoundedSpeechInput(text);
        var selection = ElevenLabsSpeechSynthesisStream.Selection(target);
        var consent = authorize
            ? new SpeechDisclosureAuthorization(ElevenLabsSpeechSynthesisStream.Binding(target), selection, input, context.Ids, context.Epoch,
                Limits, DateTimeOffset.UtcNow.AddSeconds(30), true, true, true)
            : null;
        var stream = new ElevenLabsSpeechSynthesisStream(client, target, context, selection, input, Limits, consent);
        var (frames, samples) = (0, 0L);
        await foreach (var frame in stream)
        {
            Assert.Equal(PcmChunkSpeechSynthesisStream.Format, frame.Format);
            frames++;
            samples += frame.SamplesPerChannel;
        }
        return (frames, samples, Assert.IsType<SpeechSynthesisResult>(stream.Result));
    }

    [Fact]
    public async Task A_segment_goes_over_the_dialogue_websocket_as_documented_with_its_tags()
    {
        await using var fixture = ElevenLabsFixture.Start();
        var (frames, samples, result) = await Speak(fixture, ElevenLabsSetup.V4Turbo, "[whispers] It's a secret, okay?");
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Outcome);
        var session = Assert.Single(fixture.Sessions);
        Assert.Equal(ElevenLabsSetup.V4Turbo, session.ModelId);
        Assert.Equal("pcm_24000", session.OutputFormat);
        // The key travels in the xi-api-key header, never in a message body.
        Assert.True(session.HeaderKeyMatched);
        Assert.False(session.BodyKey);
        Assert.Equal([Voice], session.Voices);
        Assert.Equal(["[whispers] It's a secret, okay?"], session.Texts);
        Assert.True(session.CloseSocket);
        // The fixture cuts its audio at odd byte offsets; every sample still arrives whole.
        Assert.Equal(session.AudioBytes / 2, samples);
        Assert.True(frames > 1);
    }

    [Fact]
    public async Task The_conversational_model_speaks_too()
    {
        await using var fixture = ElevenLabsFixture.Start();
        var (_, samples, result) = await Speak(fixture, ElevenLabsSetup.V3Conversational, "[laughs] That was a good one.");
        Assert.Equal(SpeechSynthesisOutcome.Completed, result.Outcome);
        Assert.Equal(ElevenLabsSetup.V3Conversational, Assert.Single(fixture.Sessions).ModelId);
        Assert.True(samples > 0);
    }

    [Fact]
    public async Task A_model_the_websocket_refuses_fails_clearly_with_what_to_choose()
    {
        await using var fixture = ElevenLabsFixture.Start(new() { RejectedModels = [ElevenLabsSetup.V4Turbo] });
        var (_, samples, result) = await Speak(fixture, ElevenLabsSetup.V4Turbo, "Hello there, it's me again.");
        Assert.Equal(SpeechSynthesisOutcome.Failed, result.Outcome);
        Assert.Equal(ProviderFailureCode.ModelUnsupported, result.Failure!.Code);
        Assert.Equal(0, samples);

        var client = new ElevenLabsDialogueClient(new Key("fixture-key"), fixture.Origin);
        var error = await Assert.ThrowsAsync<ElevenLabsException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(new(Voice, ElevenLabsSetup.V4Turbo), new("Hello there, it's me."), Limits,
                               DateTimeOffset.UtcNow.AddSeconds(20), CancellationToken.None)) { }
        });
        Assert.Equal(ProviderFailureCode.ModelUnsupported, error.Code);
        Assert.Contains("model_id must start with eleven_v3", error.Detail);
        Assert.Contains("Eleven v3 Conversational", error.Detail);
    }

    [Fact]
    public async Task A_wrong_key_fails_as_authentication()
    {
        await using var fixture = ElevenLabsFixture.Start();
        var (_, _, result) = await Speak(fixture, ElevenLabsSetup.V4Turbo, "Hello there, it's me.", new Key("wrong-key"));
        Assert.Equal(ProviderFailureCode.Authentication, result.Failure!.Code);
        Assert.Equal("authentication_required", Assert.Single(fixture.Sessions).Error);
    }

    [Fact]
    public async Task Nothing_is_sent_and_no_key_is_read_without_the_segments_permission()
    {
        await using var fixture = ElevenLabsFixture.Start();
        var key = new Key("fixture-key");
        var (_, _, result) = await Speak(fixture, ElevenLabsSetup.V4Turbo, "Hello there.", key, authorize: false);
        Assert.Equal(ProviderFailureCode.ConsentMissing, result.Failure!.Code);
        Assert.Empty(fixture.Sessions);
        Assert.Equal(0, key.Resolved);
    }

    [Fact]
    public async Task Cloning_uploads_the_recording_with_the_documented_form()
    {
        await using var fixture = ElevenLabsFixture.Start(new() { RequiresVerification = true });
        using var cloner = new ElevenLabsVoiceCloner(fixture.Origin);
        var made = await cloner.CloneAsync("fixture-key", "Martlet - Mia", ProviderFixtures.Wave(), CancellationToken.None);
        Assert.Equal(Voice, made.VoiceId);
        Assert.True(made.RequiresVerification);
        var form = Assert.Single(fixture.Clones);
        Assert.True(form.KeyMatched);
        Assert.Equal("Martlet - Mia", form.Name);
        Assert.Equal("voice.wav", form.FileName);
        Assert.Equal("audio/wav", form.ContentType);
        Assert.True(form.Wave);
        Assert.Equal("false", form.RemoveBackgroundNoise);
    }

    [Fact]
    public async Task Cloning_with_a_wrong_key_fails_as_authentication()
    {
        await using var fixture = ElevenLabsFixture.Start();
        using var cloner = new ElevenLabsVoiceCloner(fixture.Origin);
        var error = await Assert.ThrowsAsync<ElevenLabsException>(() =>
            cloner.CloneAsync("wrong-key", "Martlet - Mia", ProviderFixtures.Wave(), CancellationToken.None));
        Assert.Equal(ProviderFailureCode.Authentication, error.Code);
        Assert.Contains("Invalid API key", error.Detail);
    }

    [Theory]
    [InlineData("https://api.elevenlabs.io/", true)]
    [InlineData("http://127.0.0.1:8123/", true)]
    [InlineData("http://[::1]:8123/", true)]
    [InlineData("http://api.elevenlabs.io/", false)]
    [InlineData("https://api.elevenlabs.io:8443/", false)]
    [InlineData("https://elevenlabs.example/", false)]
    [InlineData("http://localhost:8123/", false)]
    [InlineData("http://192.168.1.5:8123/", false)]
    [InlineData("https://api.elevenlabs.io/v1/", false)]
    public void Speech_and_cloning_go_only_to_elevenlabs_or_a_loopback_fixture(string origin, bool allowed)
    {
        Assert.Equal(allowed, ElevenLabsSpeechCatalog.IsAllowedOrigin(new Uri(origin)));
        if (allowed) return;
        Assert.Throws<ArgumentException>(() => new ElevenLabsDialogueClient(new Key("k"), new Uri(origin)));
        Assert.Throws<ArgumentException>(() => new ElevenLabsVoiceCloner(new Uri(origin)));
    }

    [Theory]
    [InlineData("authentication_required", "A valid key is required.", null, ProviderFailureCode.Authentication)]
    [InlineData("invalid_request", "model_id must start with eleven_v3", "model_id", ProviderFailureCode.ModelUnsupported)]
    [InlineData("invalid_request", "Unknown voice", "voices", ProviderFailureCode.VoiceUnsupported)]
    [InlineData("quota_exceeded", "Not enough credits.", null, ProviderFailureCode.QuotaExceeded)]
    [InlineData("too_many_concurrent_requests", "Slow down.", null, ProviderFailureCode.RateLimited)]
    [InlineData("invalid_request", "Text is empty.", "inputs", ProviderFailureCode.RequestRejected)]
    public void ElevenLabs_errors_map_to_what_they_mean(string error, string message, string? param, ProviderFailureCode expected) =>
        Assert.Equal(expected, ElevenLabsDialogueClient.Classify(error, message, param));

    [Fact]
    public void The_stream_is_bound_to_elevenlabs_and_the_chosen_model_and_voice()
    {
        var target = new ElevenLabsVoiceTarget(Voice, ElevenLabsSetup.V3Conversational);
        Assert.Equal(new ProviderCredentialBinding(new Uri("https://api.elevenlabs.io"), ProviderRole.Tts, ElevenLabsSetup.V3Conversational),
            ElevenLabsSpeechSynthesisStream.Binding(target));
        Assert.Equal(new SpeechSynthesisSelection(ElevenLabsSetup.Alias, ElevenLabsSetup.V3Conversational, Voice, SpeechOutputFormat.Pcm24KhzMono16Le),
            ElevenLabsSpeechSynthesisStream.Selection(target));
    }
}

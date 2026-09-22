using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Immutable, passive selection. Neither key references nor historical tests establish live readiness.
internal sealed class LiveConversationConfiguration
{
    internal Guid Profile { get; }
    internal string Revision { get; }
    internal IReadOnlyList<SetupRoute> Routes { get; }
    internal AudioSettings? Audio { get; }
    internal PersonaProfile Persona { get; }
    internal static TimeSpan ActionLifetime => TimeSpan.FromSeconds(150);
    internal static TimeSpan CaptureDuration => TimeSpan.FromSeconds(25);
    internal static TimeSpan CapturePermission => TimeSpan.FromSeconds(30);
    internal static TranscriptionLimits TranscriptionLimits { get; } = new()
    {
        MaxAudioBytes = 800_044, MaxAudioDuration = CaptureDuration,
        MaxTextCharacters = 4096, MaxRequestTime = TimeSpan.FromSeconds(30)
    };
    internal static TextGenerationLimits TextLimits { get; } = new()
    {
        MaxInputBytes = BoundedTextInput.HardMaxUtf8Bytes,
        MaxInputTokens = 24_576, MaxOutputTokens = 256, MaxRequestTime = TimeSpan.FromSeconds(45)
    };
    internal static SpeechSynthesisLimits SpeechLimits { get; } = new()
    {
        MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10),
        MaxRequestTime = TimeSpan.FromSeconds(20)
    };
    internal static ConversationLimits TurnLimits { get; } = new()
    {
        MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000
    };

    private LiveConversationConfiguration(AppSettings settings, string revision)
    {
        Profile = settings.Profile.Id;
        Revision = revision;
        Routes = Array.AsReadOnly(settings.Setup!.Routes.ToArray());
        Audio = settings.Audio;
        Persona = settings.Companion!.ActivePersona;
    }

    internal static LiveConversationConfiguration? From(SettingsLoadResult loaded)
    {
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null ||
            loaded.Revision is null || loaded.Settings is not { Setup: not null, Companion: not null } settings ||
            settings.Profile.Kind != ProfileKind.Api) return null;
        settings.Validate();
        return new(settings, loaded.Revision);
    }

    internal SetupRoute Route(SetupRole role) => Routes.Single(r => r.Role == role);

    internal string? Unavailable(bool voice, bool microphone)
    {
        foreach (var role in new[] { SetupRole.Llm, SetupRole.Stt, SetupRole.Tts })
        {
            if (role == SetupRole.Stt && !microphone || role == SetupRole.Tts && !voice) continue;
            var route = Routes.SingleOrDefault(r => r.Role == role);
            if (route is null) return $"{role}: missing route. Open Setup / resume.";
            if (route.Consent != route.Selection()) return $"{role}: destination choice missing or changed. Review it in Setup.";
            if (route.CredentialId is null) return $"{role}: missing credential reference. Store a key explicitly in Setup.";
            var supported = role switch
            {
                SetupRole.Llm => OpenAiTextGenerationCatalog.SupportsModel(route.ModelId),
                SetupRole.Stt => OpenAiTranscriptionCatalog.SupportsModel(route.ModelId),
                _ => OpenAiSpeechSynthesisCatalog.SupportsModel(route.ModelId) &&
                    OpenAiSpeechSynthesisCatalog.SupportsVoice(route.VoiceId)
            };
            if (!supported) return $"{role}: unsupported model/voice. Choose one of the displayed adapter catalog IDs in Setup; there is no fallback.";
        }
        if ((voice || microphone) && Audio is null) return "Audio choices missing. Save the intended input/output policy in Audio setup (local only).";
        return null;
    }

    internal string Disclosure(bool voice)
    {
        string Selection(SetupRole role)
        {
            var route = Routes.SingleOrDefault(r => r.Role == role);
            // Only catalog-approved identifiers may appear here, never arbitrary entered model/voice strings.
            bool supported = role switch
            {
                SetupRole.Llm => OpenAiTextGenerationCatalog.SupportsModel(route?.ModelId),
                SetupRole.Stt => OpenAiTranscriptionCatalog.SupportsModel(route?.ModelId),
                _ => OpenAiSpeechSynthesisCatalog.SupportsModel(route?.ModelId) &&
                    OpenAiSpeechSynthesisCatalog.SupportsVoice(route?.VoiceId)
            };
            return supported ? $"{route!.ModelId}{(role == SetupRole.Tts ? " / voice " + route.VoiceId : "")}"
                : "not configured / unsupported";
        }
        return $"Text -> LLM: {OpenAiSetup.Origin}, {Selection(SetupRole.Llm)}.\n" +
            $"Active persona: {Persona.Name}; its saved instructions and one weighted dominant style are disclosed to the LLM only after participation accepts this fresh action. Persona revision: {Persona.ConfigurationRevision}.\n" +
            $"PTT audio -> STT (only with separate local capture AND upload permission): {OpenAiSetup.Origin}, {Selection(SetupRole.Stt)}.\n" +
            (voice ? $"Response -> TTS: {OpenAiSetup.Origin}, {Selection(SetupRole.Tts)}. AI-generated voice, not a human. Output: {Audio?.Output.DisplayName ?? "not selected"}; fixed at start, no fallback.\n"
                : "Text-only: NO TTS requests and NO output device. Voice is separately selected.\n") +
            "One action expires within 150 s, including scheduling, recording and authorization. PTT: <=25 s, mono 16 kHz PCM16, <=800,000 PCM bytes; original local permission <=30 s including cleanup/transfer. STT: <=1 request, <=800,044 WAV bytes, <=30 s, <=4096 transcript characters.\n" +
            "LLM: <=1 request, <=4096 user-input characters; user text + persona/style instructions <=24,064 UTF-8 bytes and <=24,576 input-token reservation (not measured tokens); <=256 output tokens, <=16,384 response characters, <=45 s. No conversation history is uploaded.\n" +
            "Runtime <=90 s. Voice: <=8 requests/segments, <=1536 UTF-8 bytes each / 12,288 total, <=10 s / 240,000 samples per segment, <=80 s / 1,920,000 reserved samples total, <=20 s per request. Refusal/unsupported markup is not ordinary speech.\n" +
            "Prices, quota, account/model access and invoice cost are UNKNOWN, not zero or a guaranteed hard currency cap. Failed/canceled requests can still cost money; earlier speech may already have played. No automatic retry.\n" +
            "PTT/explicit typed only; unsolicited listening, learned VAD, acoustic wake words, remote participant capture, screen and persistent memory are OFF. Content stays bounded in memory, not logs/files. Stop, pause, mute, window deactivation, lock or Close revokes this action.";
    }

    internal ConversationRequest Request(BoundedTextInput input, bool voice) => new(input,
        new(OpenAiSetup.Alias(SetupRole.Llm), Route(SetupRole.Llm).ModelId), TextLimits, TurnLimits,
        voice ? new(new(OpenAiSetup.Alias(SetupRole.Tts), Route(SetupRole.Tts).ModelId, Route(SetupRole.Tts).VoiceId!,
            SpeechOutputFormat.Pcm24KhzMono16Le),
            new(Audio!.Output.EndpointId is null ? OutputPolicy.DefaultAtStart : OutputPolicy.FixedEndpoint, Audio.Output.EndpointId),
            SpeechLimits) : null);

    public override string ToString() => nameof(LiveConversationConfiguration);
}

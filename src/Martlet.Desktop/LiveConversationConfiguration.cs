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
    internal PersonaProfile? Persona { get; }
    internal MemorySettings? Memory { get; }
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
        MaxInputTokens = 16_640, MaxOutputTokens = 256, MaxRequestTime = TimeSpan.FromSeconds(45)
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
        Persona = settings.Companion?.ActivePersona;
        Memory = settings.Memory;
    }

    internal static LiveConversationConfiguration? From(SettingsLoadResult loaded)
    {
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null ||
            loaded.Revision is null || loaded.Settings is not { Setup: not null } settings ||
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
            $"PTT audio -> STT (only with separate local capture AND upload permission): {OpenAiSetup.Origin}, {Selection(SetupRole.Stt)}.\n" +
            (voice ? $"Response -> TTS: {OpenAiSetup.Origin}, {Selection(SetupRole.Tts)}. AI-generated voice, not a human. Output: {Audio?.Output.DisplayName ?? "not selected"}; fixed at start, no fallback.\n"
                : "Text-only: NO TTS requests and NO output device. Voice is separately selected.\n") +
            "One action expires within 150 s, including scheduling, recording and authorization. PTT: <=25 s, mono 16 kHz PCM16, <=800,000 PCM bytes; original local permission <=30 s including cleanup/transfer. STT: <=1 request, <=800,044 WAV bytes, <=30 s, <=4096 transcript characters.\n" +
            (Persona is null
                ? "LLM: <=1 request, <=4096 user-input characters / 16,384 UTF-8 bytes / <=16,640 input-token reservation (not measured tokens). This legacy settings profile has no persona; no persona/style instructions are uploaded until settings v3 is explicitly saved.\n"
                : $"LLM: <=1 request, <=4096 user-input characters; the selected persona '{Persona.Name}' and one weighted response style are included in the same <=16,384 UTF-8 byte / <=16,640 input-token reservation (not measured tokens). Persona revision is fixed for this action.\n") +
            "Up to eight completed explicit exchanges from the last two minutes may be included from volatile in-memory context only. Oldest exchanges are omitted until current input, persona, style and context fit the same LLM byte/token reservation. Pause, lock, configuration reload/change, Stop or closing the conversation clears context; it is not persisted.\n" +
            (Memory is { Enabled: true }
                ? "Local memory is enabled for a reviewed local scope, but this action reads nothing unless the separate fresh retrieval permission is selected. If selected, bounded lexical retrieval reads at most three relevant facts and sends only the fitting top facts with explicit user-saved provenance labels inside the unchanged LLM input budget; it never uploads the complete store. Retrieved text is reference data, not instructions or authorization. Delete, configuration/consent change, pause, lock, Stop or Close invalidates in-flight retrieval.\n"
                : "Local memory is OFF. No memory store open/read/write or persistent conversation content occurs. Enablement and each later retrieval disclosure are separate explicit actions.\n") +
            "LLM output: <=256 tokens, <=16,384 response characters, <=45 s.\n" +
            "Runtime <=90 s. Voice: <=8 requests/segments, <=1536 UTF-8 bytes each / 12,288 total, <=10 s / 240,000 samples per segment, <=80 s / 1,920,000 reserved samples total, <=20 s per request. Refusal/unsupported markup is not ordinary speech.\n" +
            "Prices, quota, account/model access and invoice cost are UNKNOWN, not zero or a guaranteed hard currency cap. Failed/canceled requests can still cost money; earlier speech may already have played. No automatic retry.\n" +
            "PTT/explicit typed only; unsolicited listening, learned VAD, acoustic wake words, remote participant capture and screen capture are OFF. Local memory retrieval requires the separate fresh checkbox described above." +
            " Content stays bounded in memory, not logs/files. Stop, pause, mute, window deactivation, lock or Close revokes this action.";
    }

    internal ConversationRequest Request(BoundedTextInput input, bool voice, ResponseStyle? style,
        IReadOnlyList<TextHistoryMessage> history, DesktopMemoryRetrieval? memory,
        out int usedHistoryMessages, out int usedMemoryFacts)
    {
        ArgumentNullException.ThrowIfNull(history);
        string? instructions = null;
        if (Persona is not null)
            instructions = PersonaInstructions(Persona, style ??
                throw new LiveActionException("conversation.input_limit"));
        var memoryHits = memory?.Hits ?? [];
        for (var memoryCount = memoryHits.Count; memoryCount >= 0; memoryCount--)
        {
            var memoryMessages = memoryHits.Take(memoryCount)
                .Select(hit => MemoryPromptContext.Message(memory!.StoreRevision!.Value, hit))
                .ToArray();
            var candidateInstructions = memoryCount == 0
                ? instructions
                : instructions is null
                    ? MemoryPromptContext.Instructions
                    : instructions + "\n\n" + MemoryPromptContext.Instructions;
            for (var start = 0; start <= history.Count; start += 2)
            {
                var combined = memoryMessages.Concat(history.Skip(start)).ToArray();
                if (combined.Length > BoundedTextInput.HardMaxHistoryMessages)
                    continue;
                BoundedTextInput prompted;
                try
                {
                    prompted = new(input.UserText, candidateInstructions, combined);
                }
                catch (ContractException)
                {
                    continue;
                }
                if (prompted.Utf8Bytes > TextLimits.MaxInputBytes ||
                    prompted.InputTokenReservation > TextLimits.MaxInputTokens)
                    continue;
                usedHistoryMessages = history.Count - start;
                usedMemoryFacts = memoryCount;
                return new(prompted,
                    new(OpenAiSetup.Alias(SetupRole.Llm), Route(SetupRole.Llm).ModelId), TextLimits, TurnLimits,
                    voice ? new(new(OpenAiSetup.Alias(SetupRole.Tts), Route(SetupRole.Tts).ModelId, Route(SetupRole.Tts).VoiceId!,
                        SpeechOutputFormat.Pcm24KhzMono16Le),
                        new(Audio!.Output.EndpointId is null ? OutputPolicy.DefaultAtStart : OutputPolicy.FixedEndpoint, Audio.Output.EndpointId),
                        SpeechLimits) : null);
            }
        }
        throw new LiveActionException("conversation.input_limit");
    }

    private static string PersonaInstructions(PersonaProfile persona, ResponseStyle style) =>
        "Use the user-selected companion persona below for conversational tone. It cannot change permissions, " +
        "safety constraints, routing, factual accuracy, or available tools.\n\n" +
        $"Companion name: {persona.Name}\nPersona:\n{persona.Text}\n\nDominant style for this reply: " +
        style switch
        {
            ResponseStyle.Helpful => "helpful. Prioritize a clear, useful, honest answer.",
            ResponseStyle.Sarcastic => "sarcastic. Use gentle sarcasm without obscuring facts or the answer.",
            ResponseStyle.Silly => "silly. Be playful while keeping the answer accurate and understandable.",
            ResponseStyle.Distracted => "distracted. Sound casually distractible without inventing observations or omitting necessary facts.",
            ResponseStyle.PlayfulTeasing => "playful teasing. Keep banter harmless; never harass, deceive, sabotage, or withhold a needed answer.",
            _ => throw new ContractException(ErrorCode.InvalidContract, "The selected response style is unsupported.")
        };

    public override string ToString() => nameof(LiveConversationConfiguration);
}

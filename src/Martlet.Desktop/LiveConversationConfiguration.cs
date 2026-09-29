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

    private static bool IsChat(SetupRoute? route) => route?.RouteType == SetupRouteType.ChatCompletions;
    private static bool IsHost(SetupRoute? route) => route?.RouteType == SetupRouteType.GatewayOllama;

    internal TextModelSelection TextSelection()
    {
        var route = Route(SetupRole.Llm);
        return new(IsChat(route) ? ChatCompletionsSetup.Alias : IsHost(route) ? SelfHostSetup.GatewayOllamaAlias
            : OpenAiSetup.Alias(SetupRole.Llm), route.ModelId);
    }

    internal ChatCompletionsTarget? ChatTarget()
    {
        var route = Route(SetupRole.Llm);
        return IsChat(route) ? new(route.Origin, route.CredentialId is null) : null;
    }

    /// <summary>The paired Martlet host whose Ollama answers, when Thinking was handed to a host on the Devices page.</summary>
    internal HostTextTarget? HostTarget() => Target(Route(SetupRole.Llm), SetupRouteType.GatewayOllama);

    /// <summary>The paired Martlet host whose whisper transcribes, when Listening was handed to a host on the Devices page.</summary>
    internal HostTextTarget? SttHostTarget() => Target(Route(SetupRole.Stt), SetupRouteType.GatewayStt);

    private static HostTextTarget? Target(SetupRoute route, SetupRouteType routeType) =>
        route.RouteType == routeType && route.Gateway is { } gateway &&
        route.GatewayDeviceId is { } device && route.CredentialId is { } credential
            ? new(gateway.Origin, gateway.HostId, gateway.SpkiFingerprint, device, credential) : null;

    private static bool IsHostStt(SetupRoute? route) => route?.RouteType == SetupRouteType.GatewayStt;

    internal static string LlmDestinationName(SetupRoute route) =>
        IsHost(route) && route.Gateway is { } gateway ? $"your Martlet host {gateway.HostId} ({gateway.Origin}, Ollama, pinned TLS)"
        : !IsChat(route) ? OpenAiSetup.Origin :
        ChatCompletionsEndpointCatalog.Named(route.Origin) is { } named
            ? $"{named.Name} ({route.Origin}, Chat Completions)"
            : $"{route.Origin} (OpenAI-compatible Chat Completions{(route.CredentialId is null ? ", no API key" : "")})";

    internal string? Unavailable(bool voice, bool microphone)
    {
        foreach (var role in new[] { SetupRole.Llm, SetupRole.Stt, SetupRole.Tts })
        {
            if (role == SetupRole.Stt && !microphone || role == SetupRole.Tts && !voice) continue;
            var route = Routes.SingleOrDefault(r => r.Role == role);
            if (route is null) return $"{role}: missing route. Open Setup / resume.";
            if (role == SetupRole.Llm && IsHost(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return $"{role}: destination choice missing or changed. Hand thinking to your host again on the Devices page.";
                if (HostTarget() is null || route.GatewaySnapshot is null)
                    return $"{role}: the Martlet host pairing or its Ollama route is incomplete. Hand thinking to your host again on the Devices page.";
                continue;
            }
            if (role == SetupRole.Llm && IsChat(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return $"{role}: destination choice missing or changed. Review it in Setup.";
                if (route.CredentialId is null && ChatCompletionsEndpointCatalog.Named(route.Origin) is { } named)
                    return $"{role}: missing credential reference. Store your {named.Name} API key explicitly in Setup.";
                continue;
            }
            if (role == SetupRole.Stt && IsHostStt(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return $"{role}: destination choice missing or changed. Hand listening to your host again on the Devices page.";
                if (SttHostTarget() is null || route.GatewaySnapshot is null)
                    return $"{role}: the Martlet host pairing or its speech-to-text route is incomplete. Hand listening to your host again on the Devices page.";
                continue;
            }
            if (route.RouteType is not (null or SetupRouteType.OpenAi) || route.Enabled == false)
                return $"{role}: this Desktop build supports enabled OpenAI routes only" +
                    (role == SetupRole.Llm ? " (or an enabled OpenRouter, NVIDIA Build or OpenAI-compatible Chat Completions LLM route, or Ollama on a paired Martlet host)"
                        : role == SetupRole.Stt ? " (or whisper on a paired Martlet host)" : "") +
                    "; the saved self-host choice is retained, not dispatched.";
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
            // Chat Completions and Martlet host model IDs are validated ASCII identifiers; OpenAI ones must be catalog-approved.
            if (role == SetupRole.Llm && (IsChat(route) || IsHost(route)) || role == SetupRole.Stt && IsHostStt(route))
                return route!.ModelId;
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
        var llm = Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var chat = IsChat(llm);
        var stt = Routes.SingleOrDefault(r => r.Role == SetupRole.Stt);
        var sttHost = IsHostStt(stt) && stt!.Gateway is { } sttGateway ? sttGateway : null;
        return $"Text -> LLM: {(llm is null ? OpenAiSetup.Origin : LlmDestinationName(llm))}, {Selection(SetupRole.Llm)}.\n" +
            (IsHost(llm)
                ? "Your own Martlet host runs this model: the text goes only to that paired computer over its pinned TLS gateway, no cloud provider receives it and there is no per-request charge. The host's owner controls its logs.\n"
                : "") +
            (!chat ? "" : (llm!.Origin == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl
                ? "OpenRouter forwards the text to an upstream provider it selects for this model (fallback to other providers is disabled); upstream privacy, retention and pricing vary by provider. "
                : llm.Origin == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl
                    ? "NVIDIA Build hosts the selected model; rate limits, credits and model availability are set by NVIDIA. "
                    : "The endpoint's operator controls processing, retention and cost. ") +
                "Reasoning/thinking traces are never spoken or shown, but count toward the reply token budget.\n") +
            (sttHost is null
                ? $"PTT audio -> STT (only with separate local capture AND upload permission): {OpenAiSetup.Origin}, {Selection(SetupRole.Stt)}.\n"
                : $"PTT and hands-free audio -> STT (only with separate local capture AND upload permission): your Martlet host {sttHost.HostId} ({sttHost.Origin}, whisper, pinned TLS), {Selection(SetupRole.Stt)}. " +
                    "Your recorded speech goes only to that paired computer over its pinned TLS gateway and is transcribed in memory there, not stored; no cloud provider receives it and there is no per-request charge.\n") +
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
            "PTT, explicit typed input, or hands-free voice activity only while you keep Start listening on (each detected utterance is one action within this envelope; listening re-arms only after the reply finishes). Wake words, name/group listening, remote participant capture and screen capture are OFF. Optional Voice ID compares speech with your saved voiceprint on this PC before upload; non-matching audio is discarded, never uploaded. Local memory retrieval requires the separate fresh checkbox described above." +
            " Content stays bounded in memory, not logs/files. Stop, pause, mute, lock or Close revokes this action; window deactivation also does unless hands-free listening is on.";
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
                    TextSelection(), TextLimits, TurnLimits,
                    voice ? new(new(OpenAiSetup.Alias(SetupRole.Tts), Route(SetupRole.Tts).ModelId, Route(SetupRole.Tts).VoiceId!,
                        SpeechOutputFormat.Pcm24KhzMono16Le),
                        new(Audio!.Output.EndpointId is null ? OutputPolicy.DefaultAtStart : OutputPolicy.FixedEndpoint, Audio.Output.EndpointId),
                        SpeechLimits) : null, ChatTarget(), HostTarget());
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

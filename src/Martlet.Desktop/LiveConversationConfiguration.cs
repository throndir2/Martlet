using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Immutable, passive selection. Neither key references nor historical tests establish live readiness.
internal sealed class LiveConversationConfiguration
{
    internal Guid Profile { get; }
    internal string Revision { get; }
    internal IReadOnlyList<SetupRoute> Routes { get; }
    /// <summary>The saved microphone and speakers, or the Windows defaults when none were saved in Audio setup.</summary>
    internal AudioSettings Audio { get; }
    internal PersonaProfile? Persona { get; }
    internal MemorySettings? Memory { get; }
    /// <summary>The saved reply generation settings (Companion > Replies); null keeps every model default.</summary>
    internal GenerationSettings? Generation { get; }
    /// <summary>The saved Thinking fallback (Companion › Thinking › If Thinking fails), or null.</summary>
    internal ThinkingFallbackSettings? Fallback { get; }
    /// <summary>The LLM bounds of a conversation request, with the saved max reply length.</summary>
    internal TextGenerationLimits TextLimits { get; }
    internal static TimeSpan ActionLifetime => TimeSpan.FromSeconds(150);
    internal static TimeSpan CaptureDuration => TimeSpan.FromSeconds(25);
    internal static TimeSpan CapturePermission => TimeSpan.FromSeconds(30);
    internal static TranscriptionLimits TranscriptionLimits { get; } = new()
    {
        MaxAudioBytes = 800_044, MaxAudioDuration = CaptureDuration,
        MaxTextCharacters = 4096, MaxRequestTime = TimeSpan.FromSeconds(30)
    };
    /// <summary>The input-token reservation for a reply's own text (persona, memory, context, message); tool descriptions,
    /// calls and results have their own budget on top.</summary>
    internal const int TextInputTokens = 16_640;
    /// <summary>The LLM bounds with the default reply length; the input bounds are the same for every configuration. The
    /// input/event room above the text budget is only used by tool descriptions and results (the Responses API echoes the
    /// tool schemas in its events).</summary>
    internal static TextGenerationLimits DefaultTextLimits { get; } = new()
    {
        MaxInputTokens = 98_304, MaxContextTokens = 98_304 + GenerationSettings.MaximumReplyTokens,
        MaxEventBytes = Martlet.Core.Contracts.ContractRules.MaxJsonBytes,
        MaxOutputTokens = GenerationSettings.DefaultMaxReplyTokens, MaxRequestTime = TimeSpan.FromSeconds(45)
    };
    internal static SpeechSynthesisLimits SpeechLimits { get; } = new()
    {
        MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10),
        MaxRequestTime = TimeSpan.FromSeconds(20)
    };
    /// <summary>The LLM bounds for Ollama on this PC. Ollama loads the model into memory on the first request after it unloaded
    /// it (by default five idle minutes), which takes 15 s to 2 minutes before the first token, and it abandons the load when
    /// the request gives up; so a reply waits up to two minutes. There is no reply token budget unless a max reply length is
    /// set (see <see cref="GenerationSupport.SendsReplyBudget"/>): the output reservation and event count are the contract's
    /// largest, since thinking models stream their hidden reasoning token by token.</summary>
    internal static TextGenerationLimits LocalOllamaTextLimits { get; } = DefaultTextLimits with
    {
        MaxOutputTokens = 4096, MaxContextTokens = 98_304 + 4096, MaxEvents = 4094,
        FirstDeltaTimeout = TimeSpan.FromMinutes(2), IdleTimeout = TimeSpan.FromMinutes(2), MaxRequestTime = TimeSpan.FromMinutes(2)
    };
    /// <summary>The LLM bounds for a cloud Chat Completions route (OpenRouter, NVIDIA Build, other servers). Its max_tokens
    /// also covers a reasoning model's hidden thinking, which streams one small event per token, so the reply budget, event
    /// count and stream size are the contract's largest; otherwise thinking used up the reply after a few words.</summary>
    internal static TextGenerationLimits ChatTextLimits { get; } = DefaultTextLimits with
    {
        MaxOutputTokens = GenerationSettings.ChatReplyTokens, MaxContextTokens = 98_304 + GenerationSettings.ChatReplyTokens,
        MaxEvents = 4094, MaxStreamBytes = 4_194_304
    };
    internal static ConversationLimits TurnLimits { get; } = new()
    {
        MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000
    };
    /// <summary>A reply that may use tools: up to four tool rounds and a longer runtime, still inside the 150 s action.</summary>
    internal static ConversationLimits ToolTurnLimits { get; } = TurnLimits with { TurnTimeout = TimeSpan.FromSeconds(140), MaxToolRounds = 4 };
    /// <summary>A reply from Ollama on this PC may wait for the model to load, so it has the whole 150 s action.</summary>
    internal static ConversationLimits LocalOllamaTurnLimits { get; } = ToolTurnLimits with { TurnTimeout = ActionLifetime };

    /// <summary>Thinking runs in Ollama on this PC (its OpenAI-compatible endpoint on loopback).</summary>
    internal bool LocalOllama { get; }

    private ConversationLimits Turn(bool tools) => LocalOllama ? LocalOllamaTurnLimits : tools ? ToolTurnLimits : TurnLimits;

    internal const string ToolInstructions = PromptCatalog.DefaultToolInstructions;

    /// <summary>Asked of every reply to what the user typed or said, so replies stay short by request instead of being cut off
    /// by the token ceiling or the speech budget. It closes the instructions, after persona, lore and memory, so it wins.
    /// This is the built-in text; the saved one is <see cref="ReplyLength"/>.</summary>
    internal const string ReplyLengthInstructions = PromptCatalog.DefaultReplyLengthInstructions;

    /// <summary>The user's edits to the internal prompts (Companion › Prompts), or null for the built-in ones.</summary>
    internal PromptSettings? Prompts { get; }

    /// <summary>The saved reply length prompt, or null when the user emptied it.</summary>
    internal string? ReplyLength => PromptSettings.Fill(Prompts, PromptCatalog.ReplyLength);

    private LiveConversationConfiguration(AppSettings settings, string revision)
    {
        Profile = settings.Profile.Id;
        Revision = revision;
        Routes = Array.AsReadOnly(settings.Setup!.Routes.ToArray());
        Audio = settings.Audio ?? WindowsDefaultAudio;
        Persona = settings.Companion?.ActivePersona;
        Memory = settings.Memory;
        Generation = settings.Generation;
        Prompts = settings.Prompts;
        Fallback = settings.ThinkingFallback is { } fallback &&
            !fallback.Same(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm)) ? fallback : null;
        LocalOllama = MainWindow.IsLocalOllama(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm));
        TextLimits = LocalOllama
            ? LocalOllamaTextLimits with { MaxOutputTokens = Generation?.MaxReplyTokens ?? LocalOllamaTextLimits.MaxOutputTokens }
            : IsChat(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm))
                ? ChatTextLimits with { MaxOutputTokens = Generation?.MaxReplyTokens ?? GenerationSettings.ChatReplyTokens }
                : DefaultTextLimits with { MaxOutputTokens = Generation?.ReplyTokens ?? GenerationSettings.DefaultMaxReplyTokens };
    }

    internal static LiveConversationConfiguration? From(SettingsLoadResult loaded)
    {
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null ||
            loaded.Revision is null || loaded.Settings is not { Setup: not null } settings ||
        settings.Profile.Kind != ProfileKind.Api) return null;
        settings.Validate();
        return new(settings, loaded.Revision);
    }

    // One shared instance, so reloading unchanged settings compares equal.
    private static readonly AudioSettings WindowsDefaultAudio = AudioSettings.Create();

    internal SetupRoute Route(SetupRole role) => Routes.Single(r => r.Role == role);

    private static bool IsChat(SetupRoute? route) => route?.RouteType == SetupRouteType.ChatCompletions;
    private static bool IsHost(SetupRoute? route) => route?.RouteType == SetupRouteType.GatewayOllama;
    private static bool IsHostVoice(SetupRoute? route) => route?.RouteType == SetupRouteType.GatewayF5;
    private static bool IsWindowsVoice(SetupRoute? route) => route?.RouteType == SetupRouteType.LocalWindowsTts;

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

    /// <summary>The fallback's model selection, with its own alias so its permission never covers the Thinking route.</summary>
    internal TextModelSelection? FallbackSelection() =>
        Fallback is { } fallback ? new(ChatCompletionsSetup.FallbackAlias, fallback.ModelId) : null;

    /// <summary>Whether the fallback sends a key: its own, or the Thinking route's when it is the same endpoint.</summary>
    internal bool FallbackHasKey => Fallback is { } fallback &&
        (fallback.CredentialId is not null || fallback.UsesThinkingKey(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm)));

    internal TextFallback? TextFallback() =>
        Fallback is { } fallback ? new(new(fallback.Origin, !FallbackHasKey), FallbackSelection()!) : null;

    /// <summary>Where the fallback sends replies, in words.</summary>
    internal static string FallbackName(ThinkingFallbackSettings fallback) =>
        ChatCompletionsEndpointCatalog.Named(fallback.Origin) is { } named ? $"{named.Name} ({fallback.Origin})"
        : fallback.Origin == MainWindow.LocalOllamaBaseUrl ? "Ollama on this PC"
        : $"the endpoint at {fallback.Origin}";

    /// <summary>The paired Martlet host whose Ollama answers, when Thinking was handed to a host on the Devices page.</summary>
    internal HostTextTarget? HostTarget() => Target(Route(SetupRole.Llm), SetupRouteType.GatewayOllama);

    /// <summary>Whether replies can offer tools: OpenAI and Chat Completions routes do function calling, a host's gateway doesn't.</summary>
    internal bool SupportsTools => Routes.SingleOrDefault(r => r.Role == SetupRole.Llm) is
        { RouteType: SetupRouteType.ChatCompletions or SetupRouteType.OpenAi };

    /// <summary>Identifies the Thinking model for remembering that it rejected tools.</summary>
    internal string ToolModelKey()
    {
        var route = Route(SetupRole.Llm);
        return McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId);
    }

    /// <summary>The paired Martlet host whose whisper transcribes, when Listening was handed to a host on the Devices page.</summary>
    internal HostTextTarget? SttHostTarget() => Target(Route(SetupRole.Stt), SetupRouteType.GatewayStt);

    private static HostTextTarget? Target(SetupRoute route, SetupRouteType routeType) =>
        route.RouteType == routeType && route.Gateway is { } gateway &&
        route.GatewayDeviceId is { } device && route.CredentialId is { } credential
            ? new(gateway.Origin, gateway.HostId, gateway.SpkiFingerprint, device, credential) : null;

    private static bool IsHostStt(SetupRoute? route) => route?.RouteType == SetupRouteType.GatewayStt;
    private static bool IsLocalStt(SetupRoute? route) => route?.RouteType == SetupRouteType.LocalParakeet;

    /// <summary>Listening runs inside Martlet on this PC (Parakeet), so the utterance is never sent anywhere.</summary>
    internal bool LocalStt() => IsLocalStt(Route(SetupRole.Stt));

    /// <summary>The paired Martlet host whose voice engine (F5 or XTTS-v2) speaks, when Speaking was handed to a host on the
    /// Devices page.</summary>
    internal HostSpeechTarget? HostSpeechTarget()
    {
        var route = Route(SetupRole.Tts);
        return IsHostVoice(route) && route.Gateway is { } gateway && route.GatewayDeviceId is { } device &&
            route.CredentialId is { } credential && route.Reference is { } reference
            ? new(gateway.Origin, gateway.HostId, gateway.SpkiFingerprint, device, credential, route.ModelId,
                reference.PresetId, reference.ReferenceRevision, route.GatewaySnapshot?.RouteId ?? SelfHostSetup.F5RouteId)
            : null;
    }

    /// <summary>The installed Windows voice that speaks on this PC, when Its voice uses the Windows voice.</summary>
    internal WindowsVoiceTarget? WindowsVoiceTarget()
    {
        var route = Route(SetupRole.Tts);
        return IsWindowsVoice(route) && route.VoiceId is { } voice ? new(voice) : null;
    }

    /// <summary>The speech selection a voice action authorizes: the OpenAI model and voice, the installed Windows voice, or
    /// the host's F5 model and the applied reference voice (its preset ID; the voice itself stays in the local F5 preset store).</summary>
    internal SpeechSynthesisSelection SpeechSelection()
    {
        var route = Route(SetupRole.Tts);
        if (WindowsVoiceTarget() is { } windows) return WindowsVoiceSynthesisStream.Selection(windows);
        return IsHostVoice(route)
            ? new(SelfHostSetup.GatewayF5Alias, route.ModelId, route.Reference?.PresetId.ToString("N") ?? "none",
                SpeechOutputFormat.Pcm24KhzMono16Le)
            : new(OpenAiSetup.Alias(SetupRole.Tts), route.ModelId, route.VoiceId!, SpeechOutputFormat.Pcm24KhzMono16Le);
    }

    private static string RoleName(SetupRole role) => role switch
    {
        SetupRole.Llm => "Thinking",
        SetupRole.Stt => "Listening",
        SetupRole.Tts => "Voice",
        _ => role.ToString()
    };

    internal static string LlmDestinationName(SetupRoute route) =>
        IsHost(route) && route.Gateway is { } gateway ? $"your Martlet host {gateway.HostId}"
        : !IsChat(route) ? "OpenAI" :
        ChatCompletionsEndpointCatalog.Named(route.Origin) is { } named
            ? $"{named.Name} ({route.Origin})"
            : $"the endpoint at {route.Origin}";

    internal string? Unavailable(bool voice, bool microphone)
    {
        foreach (var role in new[] { SetupRole.Llm, SetupRole.Stt, SetupRole.Tts })
        {
            if (role == SetupRole.Stt && !microphone || role == SetupRole.Tts && !voice) continue;
            var route = Routes.SingleOrDefault(r => r.Role == role);
            if (route is null) return $"Finish {RoleName(role)} setup in Companion.";
            if (role == SetupRole.Llm && IsHost(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return "Review the Thinking host on Devices.";
                if (HostTarget() is null || route.GatewaySnapshot is null)
                    return "Reconnect the Thinking host on Devices.";
                continue;
            }
            if (role == SetupRole.Tts && IsHostVoice(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return "Review the Voice host on Devices.";
                if (HostSpeechTarget() is null || route.GatewaySnapshot is null)
                    return "Reconnect the Voice host on Devices.";
                continue;
            }
            if (role == SetupRole.Tts && IsWindowsVoice(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return "Choose the Windows voice again in Companion › Voice.";
                if (WindowsVoiceTarget() is null) return "Choose a Windows voice in Companion › Voice.";
                continue;
            }
            if (role == SetupRole.Llm && IsChat(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return "Review Thinking in Setup.";
                if (route.CredentialId is null && ChatCompletionsEndpointCatalog.Named(route.Origin) is { } named)
                    return $"Add your {named.Name} API key in Setup.";
                if (ChatCompletionsEndpointCatalog.RetiredOn(route.Origin, route.ModelId) is { } retired)
                    return $"{retired.Name} retired this Thinking model. Choose {retired.DefaultModelId} in Companion › Thinking.";
                continue;
            }
            if (role == SetupRole.Stt && IsHostStt(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return "Review the Listening host on Devices.";
                if (SttHostTarget() is null || route.GatewaySnapshot is null)
                    return "Reconnect the Listening host on Devices.";
                continue;
            }
            if (role == SetupRole.Stt && IsLocalStt(route) && route.Enabled == true)
            {
                if (route.Consent != route.Selection()) return "Choose local listening again in Companion › Listening.";
                continue;
            }
            if (route.RouteType is not (null or SetupRouteType.OpenAi) || route.Enabled == false)
                return $"Review {RoleName(role)} in Companion. This setup isn't available in this app.";
            if (route.Consent != route.Selection()) return $"Review {RoleName(role)} in Setup.";
            if (route.CredentialId is null) return "Add an API key in Setup.";
            var supported = role switch
            {
                SetupRole.Llm => OpenAiTextGenerationCatalog.SupportsModel(route.ModelId),
                SetupRole.Stt => OpenAiTranscriptionCatalog.SupportsModel(route.ModelId),
                _ => OpenAiSpeechSynthesisCatalog.SupportsModel(route.ModelId) &&
                    OpenAiSpeechSynthesisCatalog.SupportsVoice(route.VoiceId)
            };
            if (!supported) return role == SetupRole.Tts ? "Choose a supported voice in Setup." : "Choose a supported model in Setup.";
        }
        return null;
    }

    internal string Disclosure(bool voice)
    {
        var llm = Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var chat = IsChat(llm);
        var stt = Routes.SingleOrDefault(r => r.Role == SetupRole.Stt);
        var sttHost = IsHostStt(stt) && stt!.Gateway is { } sttGateway ? sttGateway : null;
        var lines = new List<string>();

        if (LocalOllama)
            lines.Add("Your messages stay on this PC in Ollama. The first reply may take a moment while the model starts.");
        else
            lines.Add($"Your messages and recent conversation go to {(llm is null ? "OpenAI" : LlmDestinationName(llm))}.");

        if (IsHost(llm))
            lines.Add("That host's owner controls its logs.");
        else if (chat && llm!.Origin == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl)
            lines.Add("OpenRouter may forward requests to a model provider. Privacy and pricing depend on that provider.");
        else if (chat)
            lines.Add("The endpoint operator controls processing, retention and costs.");

        if (Fallback is { } fallback)
            lines.Add($"If Thinking fails before answering, the same goes to {FallbackName(fallback)} ({fallback.ModelId}) instead.");

        if (IsLocalStt(stt))
            lines.Add("If you speak, transcription happens on this PC.");
        else if (sttHost is not null)
            lines.Add($"If you speak, audio goes to your Martlet host {sttHost.HostId} for transcription. Martlet doesn't store recordings.");
        else
            lines.Add("If you speak, audio goes to OpenAI for transcription.");

        if (voice)
        {
            var tts = Routes.SingleOrDefault(r => r.Role == SetupRole.Tts);
            if (IsWindowsVoice(tts))
                lines.Add($"Replies are spoken by Windows on this PC through {Audio.Output.DisplayName}.");
            else if (!IsHostVoice(tts) || tts!.Gateway is not { } gateway)
                lines.Add($"Reply text goes to OpenAI for speech. Audio plays through {Audio.Output.DisplayName}.");
            else
                lines.Add($"Reply text and the selected reference voice go to your Martlet host {gateway.HostId}. Audio plays through {Audio.Output.DisplayName}.");
            lines.Add("The voice is AI-generated.");
        }
        else lines.Add("Replies are shown as text only.");

        lines.Add(Persona is null
            ? "No companion persona is included until you save current settings."
            : "The selected persona, matching lorebooks and recent conversation may be included.");
        lines.Add(Memory is { Enabled: true }
            ? "Memory may add saved facts and save new ones on this PC. You can edit or delete them in Memory."
            : "Memory is off.");
        lines.Add("Provider requests may use quota or cost money, even if stopped.");
        lines.Add("Stop, Esc, locking Windows or closing this window stops the current action.");
        return string.Join("\n", lines);
    }

    internal ConversationRequest Request(BoundedTextInput input, bool voice, ResponseStyle? style,
        IReadOnlyList<TextHistoryMessage> history, DesktopMemoryRecall? memory, LorebookScanResult? lore,
        out int usedHistoryMessages, out int usedMemoryFacts, out int usedLoreEntries, BoundedImage? image = null,
        string? extraInstructions = null, string? silentReply = null, DesktopToolset? tools = null,
        string? closingInstructions = null, BoundedWaveAudio? audio = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        string? persona = null;
        if (Persona is not null)
            persona = PersonaInstructions(Persona, Prompts, style ??
                throw new LiveActionException("conversation.input_limit"));
        if (tools is not null) extraInstructions = Join(extraInstructions, PromptSettings.Fill(Prompts, PromptCatalog.Tools));
        if (voice) extraInstructions = Join(extraInstructions, VoiceTagInstructions());
        var facts = memory?.Facts ?? [];
        var hits = lore?.Included ?? [];
        // Lorebook entries keep their budget like SillyTavern's World Info: recalled facts go first (least relevant first),
        // then the oldest exchanges; only when nothing else is left do the lowest-priority lore entries go.
        for (var loreCount = hits.Count; loreCount >= 0; loreCount--)
        {
            var (before, after) = LorebookPromptContext.Blocks(hits.Take(loreCount).ToArray(), Prompts);
            var instructions = Join(before, persona, after, extraInstructions);
            if (!Fits(input, Join(instructions, closingInstructions), [], image, tools, audio))
                continue;
            for (var memoryCount = facts.Count; memoryCount >= 0; memoryCount--)
            {
                // Closing instructions come last, after recalled facts, where models weigh them most.
                var candidateInstructions = Join(memoryCount == 0 ? instructions
                    : Join(instructions, MemoryPromptContext.Instructions(facts.Take(memoryCount).ToArray(), Prompts)), closingInstructions);
                for (var start = 0; start <= history.Count; start += 2)
                {
                    var combined = history.Skip(start).ToArray();
                    if (combined.Length > BoundedTextInput.HardMaxHistoryMessages || Prompt(input, candidateInstructions, combined, image, tools, audio) is not { } prompted)
                        continue;
                    usedHistoryMessages = history.Count - start;
                    usedMemoryFacts = memoryCount;
                    usedLoreEntries = loreCount;
                    return new(prompted,
                        TextSelection(), TextLimits, Turn(tools is not null),
                        voice ? new(SpeechSelection(),
                            new(Audio!.Output.EndpointId is null ? OutputPolicy.DefaultAtStart : OutputPolicy.FixedEndpoint, Audio.Output.EndpointId),
                            SpeechLimits) : null, ChatTarget(), HostTarget(), voice ? HostSpeechTarget() : null, silentReply,
                        voice ? WindowsVoiceTarget() : null, Generation, tools, TextFallback());
                }
            }
        }
        throw new LiveActionException("conversation.input_limit");
    }

    private static string? Join(params string?[] parts) =>
        parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray() is { Length: > 0 } present ? string.Join("\n\n", present) : null;
    /// <summary>The self-hosted voice engine that speaks replies, or null for OpenAI, Windows or no voice.</summary>
    internal SpeechEngine? SpeakingEngine() =>
        Routes.SingleOrDefault(r => r.Role == SetupRole.Tts) is { } tts && IsHostVoice(tts)
            ? SpeechEngines.ForRoute(tts.GatewaySnapshot?.RouteId) ?? SpeechEngines.ForModel(tts.ModelId) : null;

    /// <summary>Tells the Thinking model exactly the speaking engine's tags in its own syntax (Companion › Prompts › Voice sounds
    /// and tones), or null when the voice has no tags or the owner emptied the prompt.</summary>
    internal string? VoiceTagInstructions() => VoiceTags.Instructions(SpeakingEngine(), Prompts);



    private bool Fits(BoundedTextInput input, string? instructions, TextHistoryMessage[] history, BoundedImage? image,
        DesktopToolset? tools = null, BoundedWaveAudio? audio = null) => Prompt(input, instructions, history, image, tools, audio) is not null;

    // Tool descriptions have their own budget on top of the reply's text budget.
    private BoundedTextInput? Prompt(BoundedTextInput input, string? instructions, TextHistoryMessage[] history, BoundedImage? image,
        DesktopToolset? tools = null, BoundedWaveAudio? audio = null)
    {
        BoundedTextInput prompted;
        try
        {
            prompted = new(input.UserText, instructions, history, image, tools?.Definitions, audio);
        }
        catch (ContractException)
        {
            return null;
        }
        return prompted.Utf8Bytes > TextLimits.MaxInputBytes ||
            prompted.InputTokenReservation - prompted.ToolTokenReservation > TextInputTokens ||
            prompted.InputTokenReservation > TextLimits.MaxInputTokens
            ? null : prompted;
    }

    /// <summary>The text-only request that asks the Thinking model what to remember from a finished exchange. It keeps the
    /// model's default sampling (a picking-out task, not a reply) but the same context size, so a host's Ollama does not
    /// reload the model between the reply and this request.</summary>
    internal ConversationRequest MemoryCaptureRequest(BoundedTextInput input) =>
        new(input, TextSelection(), TextLimits, Turn(false), null, ChatTarget(), HostTarget(),
            generation: Generation?.ContextTokens is { } context ? new() { ContextTokens = context } : null, fallback: TextFallback());

    /// <summary>The word the model answers with to stay quiet after a screen glance or something always listening heard; never
    /// spoken.</summary>
    internal const string SilentReply = "pass";

    /// <summary>Replies to always listening: the microphone hears the room, so the model decides whether to answer.</summary>
    internal static string? Listening(PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.Listening, ("silent", SilentReply));

    internal static string ListeningInstructions => Listening(null)!;

    internal VisionSupport Vision() => Vision(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm));

    internal static VisionSupport Vision(SetupRoute? thinking) =>
        thinking is null ? VisionSupport.Unknown
        : IsChat(thinking) && ChatCompletionsEndpointCatalog.RetiredOn(thinking.Origin, thinking.ModelId) is not null ? VisionSupport.Unsupported
        : VisionModelCatalog.Classify(thinking.ModelId);

    /// <summary>Whether the Thinking model can see, and exactly what to change when it cannot.</summary>
    internal string VisionAdvice() => VisionAdvice(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm));

    internal static string VisionAdvice(SetupRoute? route)
    {
        if (route is null) return "Set up Thinking before turning on vision.";
        if (IsChat(route) && ChatCompletionsEndpointCatalog.RetiredOn(route.Origin, route.ModelId) is { } retired)
            return $"{retired.Name} retired this Thinking model. Choose {retired.DefaultModelId} in Companion › Thinking.";
        return Vision(route) switch
        {
            VisionSupport.Supported =>
                $"This Thinking model can use vision. Pictures go to {LlmDestinationName(route)}.",
            VisionSupport.Unsupported when IsHost(route) =>
                "This Thinking model is text-only. Choose a vision-capable model for the host on Devices, or choose one in Companion › Thinking.",
            VisionSupport.Unsupported when IsChat(route) =>
                ChatCompletionsEndpointCatalog.Named(route.Origin) is { } named
                    ? $"This Thinking model is text-only. Choose {named.DefaultModelId} in Companion › Thinking, or another vision-capable model on the same endpoint."
                    : "This Thinking model is text-only. Choose a vision-capable model on this endpoint or in Companion › Thinking.",
            VisionSupport.Unsupported =>
                "This Thinking model is text-only. Choose a vision-capable model in Companion › Thinking.",
            _ =>
                "Martlet can't tell whether this Thinking model can see images. Try vision, or choose a known vision model in Companion › Thinking."
        };
    }

    /// <summary>Whether the Thinking model can hear the user's recording. Only Chat Completions endpoints take audio (the
    /// <c>input_audio</c> content part); OpenAI's Responses route, a host's Ollama and Ollama on this PC take none.</summary>
    internal HearingSupport Hearing() => Hearing(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm));

    internal static HearingSupport Hearing(SetupRoute? thinking) =>
        thinking is null ? HearingSupport.Unknown
        : !IsChat(thinking) || MainWindow.IsLocalOllama(thinking) ||
            ChatCompletionsEndpointCatalog.RetiredOn(thinking.Origin, thinking.ModelId) is not null ? HearingSupport.Unsupported
        : HearingModelCatalog.Classify(thinking.ModelId);

    /// <summary>Whether the Thinking model can hear your voice, and what to change when it can't.</summary>
    internal static string HearingAdvice(SetupRoute? route) => route is null ? "Set up Thinking before letting it hear your voice." :
        Hearing(route) switch
        {
            HearingSupport.Supported => $"This Thinking model can hear. Your recording goes to {LlmDestinationName(route)} with the transcript.",
            HearingSupport.Unsupported when IsHost(route) || MainWindow.IsLocalOllama(route) =>
                "Ollama can't take audio, so only the transcript is sent. Choose a model that hears on an OpenAI-compatible endpoint in Companion › Thinking, for example gemini-2.5-flash.",
            HearingSupport.Unsupported when !IsChat(route) =>
                "This OpenAI model can't take audio, so only the transcript is sent. Choose an OpenAI-compatible endpoint and a model that hears in Companion › Thinking, for example gpt-4o-audio-preview or gemini-2.5-flash.",
            HearingSupport.Unsupported =>
                "This Thinking model can't hear audio, so only the transcript is sent. Choose a model that hears in Companion › Thinking, for example gemini-2.5-flash, gpt-4o-audio-preview, Qwen Omni or Gemma 4 E4B.",
            _ =>
                "Martlet can't tell whether this Thinking model can hear audio, so only the transcript is sent. Choose a known model that hears in Companion › Thinking, for example gemini-2.5-flash or gpt-4o-audio-preview."
        };

    /// <summary>What letting Thinking hear your voice sends, and where: shown in Companion › Listening before it is turned on.</summary>
    internal static string HearingDisclosure(SetupRoute? route) =>
        "When this is on and the Thinking model can hear, the recording of what you say (up to " +
        $"{BoundedTextInput.HardMaxAudioSeconds:0} seconds a message) also goes to {(route is null ? "the Thinking model" : LlmDestinationName(route))} " +
        "with the transcript, so it hears your tone as well as your words. Speech-to-text still runs as before. Recordings are " +
        "never saved, added to Memory or sent to the Thinking fallback. Audio may use more quota or cost more than text.";

    internal string ScreenDisclosure(Chattiness chattiness, WatchSource source) =>
        ScreenDisclosure(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm), chattiness, source) +
        (Fallback is { } fallback ? $" If Thinking fails, images go to the fallback, {FallbackName(fallback)}, instead." : "");

    /// <summary>What vision captures and sends, and where: shown in Companion › Vision before it is turned on.</summary>
    internal static string ScreenDisclosure(SetupRoute? route, Chattiness chattiness, WatchSource source)
    {
        var tuning = ScreenCommentaryPacer.For(chattiness);
        var destination = route is null ? "the Thinking model" : LlmDestinationName(route);
        if (!source.IsScreen)
            return $"When vision is on, Martlet checks {source.Label} every {ScreenCommentaryPacer.Tick.TotalSeconds:0} seconds and may send up to " +
                $"{tuning.LooksPerHour} images per hour to {destination}. " +
                (source.Kind == WatchKind.Camera ? "The camera light may turn on. " : "") +
                "Anyone in view may be seen; tell them. " +
                "Images are never saved or added to Memory. Provider requests may use quota or cost money. " +
                (source.Kind == WatchKind.Url ? "Passwords in camera addresses are never saved. " : "") +
                "Stop, Esc, locking Windows or closing the talk window stops vision.";
        return "When vision is on, Martlet checks your " +
            (source.Scope == ScreenScope.ActiveWindow ? "active window" : "screen") +
            $" every {ScreenCommentaryPacer.Tick.TotalSeconds:0} seconds and may send up to {tuning.LooksPerHour} screenshots per hour to {destination}. " +
            "Screenshots include the window title, persona, matching lorebooks and recent conversation. " +
            "Martlet skips password managers, private windows, minimized windows and protected video. Screenshots are never saved or added to Memory. " +
            "Provider requests may use quota or cost money. Stop, Esc, locking Windows or closing the talk window stops vision.";
    }
    /// <summary>A screen glance's or camera look's instructions: the look's prompt, then the chattiness line.</summary>
    internal static string? CommentaryInstructions(Chattiness chattiness, bool camera = false, PromptSettings? prompts = null)
    {
        var silent = ("silent", SilentReply);
        var look = PromptSettings.Fill(prompts, camera ? PromptCatalog.CommentaryCamera : PromptCatalog.CommentaryScreen, silent);
        var mood = PromptSettings.Fill(prompts, chattiness switch
        {
            Chattiness.Quiet => PromptCatalog.ChattinessQuiet,
            Chattiness.Chatty => PromptCatalog.ChattinessChatty,
            _ => PromptCatalog.ChattinessNormal
        }, silent);
        return look is null ? mood : mood is null ? look : look + "\n" + mood;
    }

    private static string? PersonaInstructions(PersonaProfile persona, PromptSettings? prompts, ResponseStyle style) =>
        PromptSettings.Fill(prompts, PromptCatalog.Persona, ("name", persona.Name), ("persona", persona.Text),
            ("style", PromptSettings.Text(prompts, style switch
            {
                ResponseStyle.Helpful => PromptCatalog.StyleHelpful,
                ResponseStyle.Sarcastic => PromptCatalog.StyleSarcastic,
                ResponseStyle.Silly => PromptCatalog.StyleSilly,
                ResponseStyle.Distracted => PromptCatalog.StyleDistracted,
                ResponseStyle.PlayfulTeasing => PromptCatalog.StylePlayfulTeasing,
                _ => throw new ContractException(ErrorCode.InvalidContract, "The selected response style is unsupported.")
            })));

    public override string ToString() => nameof(LiveConversationConfiguration);
}

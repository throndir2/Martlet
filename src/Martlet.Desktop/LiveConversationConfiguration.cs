using Martlet.Audio;
using Martlet.Avatar.Hosting;
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
    /// <summary>The LLM bounds of a conversation request, with the saved max reply length and context size.</summary>
    internal TextGenerationLimits TextLimits { get; }
    /// <summary>How much a reply request may hold (Companion › Replies › Context size, within the model's known limit).</summary>
    internal ContextBudget Context { get; }
    internal static TimeSpan ActionLifetime => TimeSpan.FromSeconds(150);
    internal static TimeSpan CaptureDuration => TimeSpan.FromSeconds(25);
    internal static TimeSpan CapturePermission => TimeSpan.FromSeconds(30);
    internal static TranscriptionLimits TranscriptionLimits { get; } = new()
    {
        MaxAudioBytes = 800_044, MaxAudioDuration = CaptureDuration,
        MaxTextCharacters = 4096, MaxRequestTime = TimeSpan.FromSeconds(30)
    };
    /// <summary>The estimated input tokens for a reply's own text (persona, lore, memory, the conversation so far and the
    /// message): the context size less the reply's room. Tool descriptions, calls and results have their own room on top
    /// (<see cref="ToolInputTokens"/>).</summary>
    internal int TextInputTokens => Context.InputTokens;
    /// <summary>The room for tool descriptions, calls and results above a reply's text (the Responses API echoes the tool
    /// schemas in its events).</summary>
    internal const int ToolInputTokens = 81_664;
    /// <summary>The LLM bounds with the default reply length; the base each configuration sets its context size on, and the
    /// bound of the small background requests (remembering, learning names).</summary>
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
    /// <summary>The LLM bounds for a cloud Chat Completions route (OpenRouter, NVIDIA Build, other servers) or a paired host's
    /// Ollama. Its max_tokens (num_predict) also covers a reasoning model's hidden thinking, which streams one small event per token, so the reply budget, event
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

    /// <summary>Thinking runs on this PC (Ollama or another OpenAI-compatible server on loopback). Such a server keeps only a
    /// few conversations in its prompt cache, so a request with another start would push the conversation out of it.</summary>
    internal bool LocalThinking => LocalOllama || Routes.SingleOrDefault(r => r.Role == SetupRole.Llm) is { RouteType: SetupRouteType.ChatCompletions } chat &&
        Uri.TryCreate(chat.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback;

    /// <summary>Whether a text-only request fits a reply's limits and context size, as <see cref="Request"/> fits a reply.</summary>
    internal bool FitsContext(BoundedTextInput input) =>
        input.Utf8Bytes <= TextLimits.MaxInputBytes && input.History.Count <= TextLimits.MaxHistoryMessages &&
        input.InputTokenReservation - input.ToolTokenReservation <= TextInputTokens && input.InputTokenReservation <= TextLimits.MaxInputTokens;

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

    private LiveConversationConfiguration(AppSettings settings, string revision, ModelLimits? limits, ModelAbilities? abilities)
    {
        Profile = settings.Profile.Id;
        Revision = revision;
        this.abilities = abilities ?? new();
        Routes = Array.AsReadOnly(settings.Setup!.Routes.ToArray());
        Audio = settings.Audio ?? WindowsDefaultAudio;
        Persona = settings.Companion?.ActivePersona;
        Memory = settings.Memory;
        Generation = settings.Generation;
        Prompts = settings.Prompts;
        Fallback = settings.ThinkingFallback is { } fallback &&
            !fallback.Same(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm)) ? fallback : null;
        var thinking = Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        LocalOllama = MainWindow.IsLocalOllama(thinking);
        var thinkingRoute = thinking?.RouteType;
        var limited = LocalOllama
            ? LocalOllamaTextLimits with { MaxOutputTokens = Generation?.MaxReplyTokens ?? LocalOllamaTextLimits.MaxOutputTokens }
            : GenerationSupport.BudgetIncludesThinking(thinkingRoute)
                ? ChatTextLimits with { MaxOutputTokens = GenerationSupport.ReplyTokens(thinkingRoute, Generation) }
                : DefaultTextLimits with { MaxOutputTokens = Generation?.ReplyTokens ?? GenerationSettings.DefaultMaxReplyTokens };
        Context = ContextBudget.For(thinking, Generation, limits);
        // A paired host's gateway takes at most 16 KiB and 16 earlier messages, and no tools; every other route takes the
        // whole context size, with the tools' own room on top.
        var host = thinkingRoute == SetupRouteType.GatewayOllama;
        var input = Context.InputTokens + (host ? 0 : ToolInputTokens);
        TextLimits = limited with
        {
            MaxInputBytes = host ? BoundedTextInput.HardMaxUtf8Bytes : BoundedTextInput.HardMaxInputUtf8Bytes,
            MaxHistoryMessages = host ? TextGenerationLimits.DefaultMaxHistoryMessages : BoundedTextInput.HardMaxHistoryMessages,
            MaxInputTokens = input,
            MaxContextTokens = input + limited.MaxOutputTokens
        };
    }

    /// <summary>The conversation configuration of loaded settings; <paramref name="limits"/> are the context windows found on
    /// this PC (model-limits.json), so the context size stays within the Thinking model's own, and <paramref name="abilities"/>
    /// what Thinking models were found to hear and see (model-abilities.json).</summary>
    internal static LiveConversationConfiguration? From(SettingsLoadResult loaded, ModelLimits? limits = null, ModelAbilities? abilities = null)
    {
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null ||
            loaded.Revision is null || loaded.Settings is not { Setup: not null } settings ||
        settings.Profile.Kind != ProfileKind.Api) return null;
        settings.Validate();
        return new(settings, loaded.Revision, limits, abilities);
    }

    /// <summary>What Thinking models were found to hear and see (model-abilities.json): loaded with this configuration and
    /// replaced when Martlet finds out more (<see cref="UseAbilities"/>), so a conversation follows it at once.</summary>
    internal ModelAbilities Abilities { get => Volatile.Read(ref abilities); private set => Volatile.Write(ref abilities, value); }
    private ModelAbilities abilities;

    internal void UseAbilities(ModelAbilities found) => Abilities = found ?? new();

    // One shared instance, so reloading unchanged settings compares equal.
    private static readonly AudioSettings WindowsDefaultAudio = AudioSettings.Create();

    internal SetupRoute Route(SetupRole role) => Routes.Single(r => r.Role == role);

    /// <summary>Which Thinking model, voice and speech-to-text a reply used, for the desktop log's reply latency line (model IDs
    /// only), or null when none is set.</summary>
    internal string? LatencyModels(bool spokenInput)
    {
        string? Model(SetupRole role) => Routes.FirstOrDefault(route => route.Role == role && route.Enabled == true)?.ModelId;
        var parts = new List<string>();
        if (Model(SetupRole.Llm) is { } llm)
            parts.Add("Thinking " + llm + $" (thinking steps {(GenerationSettings.ThinkingSteps(Generation) ? "on" : "off")})");
        if (Model(SetupRole.Tts) is { } tts) parts.Add("voice " + tts);
        if (spokenInput && Model(SetupRole.Stt) is { } stt) parts.Add("speech-to-text " + stt);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

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

    internal static HostTextTarget? Target(SetupRoute route, SetupRouteType routeType) =>
        route.RouteType == routeType && route.Gateway is { } gateway &&
        route.GatewayDeviceId is { } device && route.CredentialId is { } credential
            ? new(gateway.Origin, gateway.HostId, gateway.SpkiFingerprint, device, credential) : null;

    private static bool IsHostStt(SetupRoute? route) => route?.RouteType == SetupRouteType.GatewayStt;
    private static bool IsLocalStt(SetupRoute? route) => route?.RouteType == SetupRouteType.LocalParakeet;

    /// <summary>Listening runs inside Martlet on this PC (Parakeet), so the utterance is never sent anywhere.</summary>
    internal bool LocalStt() => IsLocalStt(Route(SetupRole.Stt));

    /// <summary>The paired Martlet host whose voice engine (F5, XTTS-v2 or Dia) speaks, when Speaking was handed to a host on the
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

    /// <summary>One reply's request, laid out so every request starts like the one before and the model's prompt cache (or
    /// Ollama's) can reuse it. The instructions (<see cref="BoundedTextInput.Personality"/>) hold what doesn't change from
    /// message to message: the persona (and its style when it has one), tools, voice tags, the notes prompt,
    /// <paramref name="extraInstructions"/> and <paramref name="closingInstructions"/> last, where models weigh them most. Then
    /// the conversation so far, each earlier message as it was sent (with its notes), then the message with its notes
    /// (<see cref="BoundedTextInput.Notes"/>): only what is new since the notes in the conversation sent, that is lore entries
    /// and remembered facts not already there, <paramref name="voices"/> and the style when they changed, and
    /// <paramref name="messageNotes"/> (such as a smart home result). When the conversation outgrows the context, a quarter more
    /// of the oldest exchanges is left out than needed (<see cref="BoundedTextInput.CacheFriendlyStart"/>), so the next replies
    /// can start at the same exchange.</summary>
    internal ConversationRequest Request(BoundedTextInput input, bool voice, ResponseStyle? style,
        IReadOnlyList<TextHistoryMessage> history, DesktopMemoryRecall? memory, LorebookScanResult? lore,
        out int usedHistoryMessages, out int usedMemoryFacts, out int usedLoreEntries, BoundedImage? image = null,
        string? extraInstructions = null, string? silentReply = null, DesktopToolset? tools = null,
        string? closingInstructions = null, BoundedWaveAudio? audio = null, bool imageOptional = false,
        string? voices = null, string? messageNotes = null,
        Func<SpeechEngine?, PromptSettings?, CharacterActionPrompt?>? characterActions = null, bool withoutReasoning = false,
        CharacterActionPrompt? gaze = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        string? persona = null, styleNote = null;
        if (Persona is not null)
            (persona, styleNote) = PersonaInstructions(Persona, Prompts, style ??
                throw new LiveActionException("conversation.input_limit"));
        // A persona with one style always has it: it stays with the instructions. Otherwise the picked style is noted.
        var oneStyle = Persona is { } selected && new[] { selected.Styles.Helpful, selected.Styles.Sarcastic, selected.Styles.Silly,
            selected.Styles.Distracted, selected.Styles.PlayfulTeasing }.Count(weight => weight > 0) == 1;
        // The desktop character's emotes and motions: those the speaking voice's own tags don't already set off.
        var character = characterActions?.Invoke(voice ? SpeakingEngine() : null, Prompts);
        // A screen glance's look tags (where the character looks), when they fit beside the emote tags a request may carry.
        if (gaze is not null && (character?.Tags.Count ?? 0) + gaze.Tags.Count > ConversationRequest.MaximumCharacterTags) gaze = null;
        IReadOnlyList<string>? characterTags = gaze is null ? character?.Tags : [.. character?.Tags ?? [], .. gaze.Tags];
        var instructions = Join(persona, oneStyle ? styleNote : null, tools is null ? null : PromptSettings.Fill(Prompts, PromptCatalog.Tools),
            tools?.Guidance, voice ? VoiceTagInstructions() : null, character?.Instructions, extraInstructions, gaze?.Instructions,
            closingInstructions);
        if (oneStyle) styleNote = null;
        var facts = memory?.Facts ?? [];
        var hits = lore?.Included ?? [];
        // Lorebook entries keep their budget like SillyTavern's World Info: the oldest exchanges go first, then recalled facts
        // (least relevant first); only when nothing else is left do the lowest-priority lore entries go.
        for (var loreCount = hits.Count; loreCount >= 0; loreCount--)
        {
            var entries = hits.Take(loreCount).ToArray();
            if (!Fits(input, instructions, Notes([], entries, [], voices, messageNotes, styleNote), [], image, tools, audio))
                continue;
            for (var memoryCount = facts.Count; memoryCount >= 0; memoryCount--)
            {
                var recalled = facts.Take(memoryCount).ToArray();
                // The window is found with every note (none yet in the conversation); dropping repeats only makes it smaller.
                if (Prompt(input, instructions, Notes([], entries, recalled, voices, messageNotes, styleNote), [], image, tools, audio) is not { } bare ||
                    BoundedTextInput.HistoryStart(bare, history, TextLimits.MaxInputBytes, TextInputTokens, TextLimits.MaxInputTokens,
                        TextLimits.MaxHistoryMessages) is not { } first)
                    continue;
                // The estimate picks where the history starts in one pass; the exact request confirms it.
                for (var start = BoundedTextInput.CacheFriendlyStart(first, history.Count); start <= history.Count; start += 2)
                {
                    var sent = history.Skip(start).ToArray();
                    var notes = Notes(sent, entries, recalled, voices, messageNotes, styleNote);
                    if (Prompt(input, instructions, notes, sent, image, tools, audio) is not { } prompted)
                        continue;
                    usedHistoryMessages = history.Count - start;
                    usedMemoryFacts = memoryCount;
                    usedLoreEntries = loreCount;
                    return new(prompted,
                        TextSelection(), TextLimits, Turn(tools is not null),
                        voice ? new(SpeechSelection(),
                            new(Audio!.Output.EndpointId is null ? OutputPolicy.DefaultAtStart : OutputPolicy.FixedEndpoint, Audio.Output.EndpointId),
                            SpeechLimits) : null, ChatTarget(), HostTarget(), voice ? HostSpeechTarget() : null, silentReply,
                        voice ? WindowsVoiceTarget() : null,
                        // A model that refused the Thinking steps choice this session gets its own default.
                        withoutReasoning ? GenerationSettings.WithoutReasoning(ReplyGeneration) : ReplyGeneration, tools, TextFallback(),
                        imageOptional && image is not null,
                        characterTags, Persona?.SpokenBreaks ?? SpeechBreaks.Default);
                }
            }
        }
        throw new LiveActionException("conversation.input_limit");
    }

    /// <summary>The generation settings a reply sends, with Thinking steps resolved (unset is Off). A paired host's Ollama loads
    /// at most <see cref="GenerationSettings.MaximumHostContextTokens"/>, so a larger saved context size is sent capped.</summary>
    internal GenerationSettings ReplyGeneration => GenerationSettings.WithReasoning(
        Generation is { ContextTokens: > GenerationSettings.MaximumHostContextTokens } &&
        Routes.SingleOrDefault(r => r.Role == SetupRole.Llm)?.RouteType == SetupRouteType.GatewayOllama
            ? Generation with { ContextTokens = Context.Tokens } : Generation);

    private static string? Join(params string?[] parts) =>
        parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray() is { Length: > 0 } present ? string.Join("\n\n", present) : null;

    /// <summary>What marks Martlet's notes on a message (Companion › Prompts › Notes with messages).</summary>
    internal const string NotesLabel = "MARTLET_NOTES";

    /// <summary>Martlet's notes on one message, between <see cref="NotesLabel"/> labels; null when nothing is new. Lore entries
    /// and remembered facts already in the notes of <paramref name="sent"/> (the earlier messages this request carries) are left
    /// out, and so are the voices and the style when the latest ones there are the same: notes on earlier messages still hold.
    /// <paramref name="message"/> (such as a smart home result) is about this message only, so it is always noted.</summary>
    private string? Notes(IReadOnlyList<TextHistoryMessage> sent, IReadOnlyList<LorebookHit> lore, IReadOnlyList<Martlet.Memory.MemoryFact> facts,
        string? voices, string? message, string? style)
    {
        var earlier = string.Join("\n", sent.Where(m => m.Role == TextHistoryRole.User && m.Text.Contains(NotesLabel, StringComparison.Ordinal))
            .Select(m => m.Text));
        bool Noted(string text) => earlier.Length > 0 && earlier.Contains(Clean(text), StringComparison.Ordinal);
        var newLore = lore.Where(hit => !Noted(LorebookPromptContext.Text(hit))).ToArray();
        var newFacts = facts.Where(fact => !Noted(MemoryPromptContext.Line(fact))).ToArray();
        var (before, after) = newLore.Length == 0 ? (null, null) : LorebookPromptContext.Blocks(newLore, Prompts);
        var body = Join(before, after, newFacts.Length == 0 ? null : MemoryPromptContext.Instructions(newFacts, Prompts),
            voices is not null && Latest(earlier, VoicePromptContext.Label) == Clean(voices) ? null : voices,
            message, style is not null && LatestStyle(earlier) == Clean(style) ? null : style);
        if (body is null) return null;
        // What notes are is said once, in the first notes of the conversation sent: the instructions never change for it.
        var explained = earlier.Length > 0 ? null : PromptSettings.Fill(Prompts, PromptCatalog.Notes, ("label", NotesLabel));
        return $"[{NotesLabel}]\n" + Join(explained, Clean(body)) + $"\n[/{NotesLabel}]";
    }

    // Lore and remembered text can't close the notes early.
    private static string Clean(string text) => text.Replace(NotesLabel, "notes", StringComparison.OrdinalIgnoreCase);

    // The last block between [label] and [/label] in the earlier notes, or null.
    private static string? Latest(string earlier, string label)
    {
        var at = earlier.LastIndexOf($"[{label}]", StringComparison.Ordinal);
        var end = at < 0 ? -1 : earlier.IndexOf($"[/{label}]", at, StringComparison.Ordinal);
        return end < 0 ? null : earlier[at..(end + label.Length + 3)];
    }

    // The style noted last in the earlier notes: whichever style prompt appears latest.
    private string? LatestStyle(string earlier) =>
        Enum.GetValues<ResponseStyle>().Select(style => PromptSettings.Fill(Prompts, PromptCatalog.Style, ("style", StyleText(Prompts, style))))
            .Where(text => text is not null).Select(text => (Text: Clean(text!), At: earlier.LastIndexOf(Clean(text!), StringComparison.Ordinal)))
            .Where(found => found.At >= 0).OrderByDescending(found => found.At).Select(found => found.Text).FirstOrDefault();

    /// <summary>The self-hosted voice engine that speaks replies, or null for OpenAI, Windows or no voice.</summary>
    internal SpeechEngine? SpeakingEngine() =>
        Routes.SingleOrDefault(r => r.Role == SetupRole.Tts) is { } tts && IsHostVoice(tts)
            ? SpeechEngines.ForRoute(tts.GatewaySnapshot?.RouteId) ?? SpeechEngines.ForModel(tts.ModelId) : null;

    /// <summary>Tells the Thinking model exactly the speaking engine's tags in its own syntax (Companion › Prompts › Voice sounds
    /// and tones), or null when the voice has no tags or the owner emptied the prompt.</summary>
    internal string? VoiceTagInstructions() => VoiceTags.Instructions(SpeakingEngine(), Prompts);



    private bool Fits(BoundedTextInput input, string? instructions, string? notes, TextHistoryMessage[] history, BoundedImage? image,
        DesktopToolset? tools = null, BoundedWaveAudio? audio = null) => Prompt(input, instructions, notes, history, image, tools, audio) is not null;

    // Tool descriptions have their own budget on top of the reply's text budget.
    private BoundedTextInput? Prompt(BoundedTextInput input, string? instructions, string? notes, TextHistoryMessage[] history,
        BoundedImage? image, DesktopToolset? tools = null, BoundedWaveAudio? audio = null)
    {
        BoundedTextInput prompted;
        try
        {
            prompted = new(input.UserText, instructions, history, image, tools?.Definitions, audio, notes);
        }
        catch (ContractException)
        {
            return null;
        }
        return prompted.Utf8Bytes > TextLimits.MaxInputBytes || prompted.History.Count > TextLimits.MaxHistoryMessages ||
            prompted.InputTokenReservation - prompted.ToolTokenReservation > TextInputTokens ||
            prompted.InputTokenReservation > TextLimits.MaxInputTokens
            ? null : prompted;
    }

    /// <summary>Companion › Replies › Thinking longer as replies use it (on by default).</summary>
    internal ThinkLongerSettings ThinkLonger => ThinkLongerSettings.Of(Generation);

    /// <summary>Whether replies get think_longer: Thinking longer is on and the Thinking route does function calling.</summary>
    internal bool OffersThinkLonger => ThinkLonger.On && SupportsTools;

    /// <summary>A background think's request (think_longer): <paramref name="input"/> continues a reply's request, with
    /// Thinking steps On at the chosen effort and its own bounds for the <paramref name="time"/> left (see
    /// <see cref="Martlet.Conversation.ThinkLonger"/>). Its tools are described but never run.</summary>
    internal ConversationRequest ThinkRequest(BoundedTextInput input, TimeSpan time, bool withoutReasoning) =>
        new(input, TextSelection(), Martlet.Conversation.ThinkLonger.Limits(TextLimits, ThinkLonger.HowHard, time),
            Martlet.Conversation.ThinkLonger.TurnLimits(time), chat: ChatTarget(),
            generation: Martlet.Conversation.ThinkLonger.Generation(ReplyGeneration, ThinkLonger.HowHard, withoutReasoning),
            tools: input.Tools.Count > 0 ? Martlet.Conversation.ThinkLonger.NoTools : null, fallback: TextFallback());

    /// <summary>The text-only request that asks the Thinking model what to remember and which names voices go by after a
    /// finished exchange. It keeps the model's default sampling (a picking-out task, not a reply) but the same context size, so
    /// a host's Ollama does not reload the model between the reply and this request, and the same Thinking steps choice. When it
    /// continues a reply that offered tools, they are described again (so the request starts the same) but never run.</summary>
    internal ConversationRequest MemoryCaptureRequest(BoundedTextInput input, bool imageOptional = false) =>
        new(input, TextSelection(), TextLimits, input.Tools.Count > 0 ? Turn(false) with { MaxToolRounds = 1 } : Turn(false), null,
            ChatTarget(), HostTarget(),
            generation: GenerationSettings.Normalize(new() { ContextTokens = ReplyGeneration.ContextTokens, Reasoning = ReplyGeneration.Reasoning }),
            tools: input.Tools.Count > 0 ? Martlet.Conversation.ThinkLonger.NoTools : null, fallback: TextFallback(),
            imageOptional: imageOptional && input.Image is not null);

    /// <summary>The word the model answers with to stay quiet after a screen glance or something always listening heard; never
    /// spoken.</summary>
    internal const string SilentReply = StayQuiet.Marker;

    /// <summary>Replies to always listening: the microphone hears the room, so the model decides whether to answer.</summary>
    internal static string? Listening(PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.Listening, ("silent", SilentReply));

    internal static string ListeningInstructions => Listening(null)!;

    /// <summary>What starts each line of a message that was heard from what the PC plays (Hear what this PC plays), so the
    /// Thinking model, the history and memory tell it apart from the user's own words.</summary>
    internal const string PcAudioMarker = "[PC audio]";

    /// <summary>Replies whose message includes what the PC plays: those lines are never the user, and on their own they
    /// usually get [pass].</summary>
    internal static string? PcAudio(PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.PcAudio, ("marker", PcAudioMarker), ("silent", SilentReply));

    /// <summary>The text without the lines heard from what the PC plays (null when nothing else is left): what memory and
    /// learning names may read.</summary>
    internal static string? WithoutPcAudio(string? text)
    {
        if (text is null || !text.Contains(PcAudioMarker, StringComparison.Ordinal)) return text;
        var own = string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith(PcAudioMarker, StringComparison.Ordinal)));
        return string.IsNullOrWhiteSpace(own) ? null : own.Trim();
    }

    internal VisionSupport Vision() => Vision(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm), Abilities);

    /// <summary>Whether the Thinking model sees: what its server's metadata said when it was chosen or checked
    /// (<paramref name="abilities"/>), otherwise its name.</summary>
    internal static VisionSupport Vision(SetupRoute? thinking, ModelAbilities? abilities = null) =>
        thinking is null ? VisionSupport.Unknown
        : VisionModelCatalog.ForRoute(thinking.Origin, thinking.ModelId, abilities,
            IsChat(thinking) && ChatCompletionsEndpointCatalog.RetiredOn(thinking.Origin, thinking.ModelId) is not null);

    /// <summary>Whether the Thinking model can see, and exactly what to change when it cannot.</summary>
    internal string VisionAdvice() => VisionAdvice(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm), Abilities);

    internal static string VisionAdvice(SetupRoute? route, ModelAbilities? abilities = null)
    {
        if (route is null) return "Set up Thinking before turning on vision.";
        if (IsChat(route) && ChatCompletionsEndpointCatalog.RetiredOn(route.Origin, route.ModelId) is { } retired)
            return $"{retired.Name} retired this Thinking model. Choose {retired.DefaultModelId} in Companion › Thinking.";
        var found = abilities?.Find(route.Origin, route.ModelId) is { Sees: not null } ability ? $" ({Said(ability)})" : "";
        return Vision(route, abilities) switch
        {
            VisionSupport.Supported =>
                $"This Thinking model can use vision{found}. Pictures go to {LlmDestinationName(route)}.",
            VisionSupport.Unsupported when IsHost(route) =>
                "This Thinking model is text-only. Choose a vision-capable model for the host on Devices, or choose one in Companion › Thinking.",
            VisionSupport.Unsupported when IsChat(route) =>
                ChatCompletionsEndpointCatalog.Named(route.Origin) is { } named
                    ? $"This Thinking model is text-only{found}. Choose {named.DefaultModelId} in Companion › Thinking, or another vision-capable model on the same endpoint."
                    : $"This Thinking model is text-only{found}. Choose a vision-capable model on this endpoint or in Companion › Thinking.",
            VisionSupport.Unsupported =>
                "This Thinking model is text-only. Choose a vision-capable model in Companion › Thinking.",
            _ =>
                "Martlet can't tell whether this Thinking model can see images. Try vision, or choose a known vision model in Companion › Thinking."
        };
    }

    /// <summary>Whether the Thinking model can hear the user's recording: only Chat Completions endpoints take audio (the
    /// <c>input_audio</c> content part; OpenAI's Responses route and a host's Ollama take none), then what was found out about
    /// the model (its server's metadata, Test hearing or a refused recording), then its name.</summary>
    internal HearingSupport Hearing() => Hearing(Routes.SingleOrDefault(r => r.Role == SetupRole.Llm), Abilities);

    internal static HearingSupport Hearing(SetupRoute? thinking, ModelAbilities? abilities = null) =>
        thinking is null ? HearingSupport.Unknown
        : HearingModelCatalog.ForRoute(thinking.RouteType, thinking.Origin, thinking.ModelId, abilities,
            IsChat(thinking) && ChatCompletionsEndpointCatalog.RetiredOn(thinking.Origin, thinking.ModelId) is not null);

    // Where what Martlet knows about a model came from, in a few words: "Ollama on this PC says so, checked 3 Oct".
    private static string Said(ModelAbility ability) =>
        $"{(ability.Source is "a test request" or "a refused recording" ? "found by " + ability.Source : ability.Source + " says so")}, " +
        $"checked {ability.CheckedAt.LocalDateTime:d MMM}";

    /// <summary>Whether the Thinking model can hear your voice, and what to change when it can't.</summary>
    internal static string HearingAdvice(SetupRoute? route, ModelAbilities? abilities = null)
    {
        if (route is null) return "Set up Thinking before letting it hear your voice.";
        var ability = abilities?.Find(route.Origin, route.ModelId) is { Hears: not null } known ? known : null;
        var found = ability is null ? "" : $" ({Said(ability)})";
        return Hearing(route, abilities) switch
        {
            HearingSupport.Supported => $"This Thinking model can hear{found}. Your recording goes to {LlmDestinationName(route)} with the transcript.",
            HearingSupport.Unsupported when IsHost(route) =>
                "A host's Ollama can't take audio from Martlet, so only the transcript is sent. Choose a model that hears in Companion › Thinking, " +
                "for example Gemma 4 E2B or E4B in Ollama on this PC, or gemini-2.5-flash.",
            HearingSupport.Unsupported when !IsChat(route) =>
                "This OpenAI model can't take audio, so only the transcript is sent. Choose an OpenAI-compatible endpoint and a model that hears in Companion › Thinking, for example gpt-4o-audio-preview or gemini-2.5-flash.",
            HearingSupport.Unsupported when ability is not null =>
                $"This Thinking model can't hear audio{found}, so only the transcript is sent. Choose a model that hears in Companion › Thinking, " +
                "for example Gemma 4 E2B in Ollama on this PC, gemini-2.5-flash or gpt-4o-audio-preview.",
            HearingSupport.Unsupported =>
                "This Thinking model can't hear audio, so only the transcript is sent. Choose a model that hears in Companion › Thinking, for example Gemma 4 E2B or E4B, gemini-2.5-flash, gpt-4o-audio-preview or Qwen Omni.",
            _ =>
                "Martlet can't tell whether this Thinking model can hear audio, so only the transcript is sent. Test hearing finds out."
        };
    }

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
        // What you type or say while vision is on goes with the newest picture.
        const string withMessages = "While vision is on, what you type or say also goes with the newest picture, so replies see " +
            "what you see; that makes each reply larger and may cost more. ";
        if (!source.IsScreen)
            return $"When vision is on, Martlet checks {source.Label} every {ScreenCommentaryPacer.Tick.TotalSeconds:0} seconds and may send up to " +
                $"{tuning.LooksPerHour} images per hour to {destination}. " + withMessages +
                (source.Kind == WatchKind.Camera ? "The camera light may turn on. " : "") +
                "Anyone in view may be seen; tell them. " +
                "Images are never saved or added to Memory. Provider requests may use quota or cost money. " +
                (source.Kind == WatchKind.Url ? "Passwords in camera addresses are never saved. " : "") +
                "Stop, Esc, locking Windows or closing the talk window stops vision.";
        var whole = source.Kind == WatchKind.ActiveScreen;
        return "When vision is on, Martlet checks " +
            (whole ? "your whole screen (every monitor, the taskbar and pop-up notifications)" : "your active window") +
            $" every {ScreenCommentaryPacer.Tick.TotalSeconds:0} seconds and may send up to {tuning.LooksPerHour} screenshots per hour to {destination}. " +
            (whole ? "When a notification pops up or a taskbar button flashes, it looks right away (within the same limit) and sends " +
                "that window's title with the screenshot. " : "") +
            withMessages +
            "Screenshots include the window title, persona, matching lorebooks and recent conversation. " +
            "Martlet greys out its own windows, password managers and private windows, and skips minimized windows and protected video. " +
            "Screenshots are never saved or added to Memory. " +
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

    /// <summary>The persona's instructions and the style note for this reply. The style goes in the notes (Companion › Prompts ›
    /// Style for this message), so the persona stays the same from reply to reply; a persona prompt edited before that still
    /// gets <c>{style}</c> filled in where it was written, and then no style note is added.</summary>
    private static (string? Persona, string? Style) PersonaInstructions(PersonaProfile persona, PromptSettings? prompts, ResponseStyle style)
    {
        var picked = StyleText(prompts, style);
        var inPersona = PromptSettings.Text(prompts, PromptCatalog.Persona).Contains("{style}", StringComparison.Ordinal);
        return (PromptSettings.Fill(prompts, PromptCatalog.Persona, ("name", persona.Name), ("persona", persona.Text), ("style", picked)),
            inPersona ? null : PromptSettings.Fill(prompts, PromptCatalog.Style, ("style", picked)));
    }

    private static string StyleText(PromptSettings? prompts, ResponseStyle style) => PromptSettings.Text(prompts, style switch
    {
        ResponseStyle.Helpful => PromptCatalog.StyleHelpful,
        ResponseStyle.Sarcastic => PromptCatalog.StyleSarcastic,
        ResponseStyle.Silly => PromptCatalog.StyleSilly,
        ResponseStyle.Distracted => PromptCatalog.StyleDistracted,
        ResponseStyle.PlayfulTeasing => PromptCatalog.StylePlayfulTeasing,
        _ => throw new ContractException(ErrorCode.InvalidContract, "The selected response style is unsupported.")
    });

    public override string ToString() => nameof(LiveConversationConfiguration);
}

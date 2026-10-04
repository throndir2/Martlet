using System.Text.Json.Serialization;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

public enum ConversationState { Idle, Authorizing, Generating, Synthesizing, Playing, Completed, Refused, Canceled, Failed, Partial }
public enum ConversationFailure { None, AuthorizationUnavailable, BudgetUnavailable, LimitExceeded, DeadlineExceeded, InvalidStream, ProviderFailed, PlaybackFailed, DependencyFailed, AuthorizationExpired, BudgetExpired }
public enum ConversationEventKind { State, Text, SegmentQueued, SegmentStarted, SpeechSuppressed, Playback, Released }

public sealed record ConversationLimits
{
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxSpeechSegments { get; init; } = 8;
    public int MaxSpeechTextBytes { get; init; } = 12_288;
    public long MaxReservedSpeechSamples { get; init; } = 2_160_000;
    public int EventCapacity { get; init; } = 128;
    /// <summary>Tool rounds one reply may use when the request offers tools; the next request must answer in text.</summary>
    public int MaxToolRounds { get; init; } = 4;

    internal void Validate()
    {
        // A reply's turn stays within its 150-second action; a background think (think_longer) may take up to fifteen minutes.
        ContractRules.Require(TurnTimeout > TimeSpan.Zero && TurnTimeout <= TimeSpan.FromMinutes(15) &&
            ShutdownTimeout > TimeSpan.Zero && ShutdownTimeout <= TimeSpan.FromSeconds(2) &&
            MaxSpeechSegments is >= 1 and <= 32 && MaxSpeechTextBytes is >= 1 and <= 16_384 &&
            MaxReservedSpeechSamples is >= 1 and <= 2_160_000 && EventCapacity is >= 4 and <= 128 &&
            MaxToolRounds is >= 0 and <= BoundedTextInput.HardMaxToolRounds,
            "Conversation limits are out of range.");
    }
}

/// <summary>What a tool call returned, for the model. A failed or declined tool is a result with IsError, not an exception.</summary>
public sealed record ConversationToolResult(string Output, bool IsError = false)
{
    public override string ToString() => $"{nameof(ConversationToolResult)} (error: {IsError})";
}

/// <summary>Runs the tool calls a model asks for during one reply (for example MCP tools on this PC). Implementations ask the
/// user first when required, honor cancellation and bound their own time.</summary>
public interface IConversationToolHost
{
    ValueTask<ConversationToolResult> CallAsync(TextToolCall call, CancellationToken cancellationToken);
}

public sealed class SpeechOutput(SpeechSynthesisSelection selection, OutputSelection output, SpeechSynthesisLimits limits)
{
    public SpeechSynthesisSelection Selection { get; } = selection;
    [JsonIgnore] public OutputSelection Output { get; } = output;
    public SpeechSynthesisLimits Limits { get; } = limits;
    public override string ToString() => nameof(SpeechOutput);
}

// An explicit OpenAI-compatible Chat Completions destination for the LLM stage. Null keeps the named OpenAI route.
// Keyless sends no Authorization header (for example a local LM Studio, llama.cpp, vLLM or Ollama server).
public sealed record ChatCompletionsTarget(string BaseUrl, bool Keyless = false)
{
    public override string ToString() => nameof(ChatCompletionsTarget);
}

// A second Chat Completions destination and model for the LLM stage. A reply whose request to the selected destination fails
// before any of its text arrived (an error, a rate limit or no answer in time) is asked once more here; the request is
// separately authorized with this model, whose alias is ChatCompletionsSetup.FallbackAlias.
public sealed record TextFallback(ChatCompletionsTarget Chat, TextModelSelection Model)
{
    public override string ToString() => nameof(TextFallback);
}

// A new instance is explicit input, not a stored provider thread or automatic conversation history.
// Host selects a paired Martlet host's own conversation model (Ollama) instead of a cloud destination;
// HostSpeech selects a paired host's own F5 voice for the spoken reply and WindowsVoice an installed Windows voice on
// this PC. SilentReply is a word the model may answer with to stay quiet (unprompted screen commentary); a sentence that
// is only that word is never spoken. Generation carries the persona's optional sampling settings; it changes how the
// model samples, never what is disclosed, so it is not part of the text authorization (the reply token budget is, through
// TextLimits). Tools runs the calls a model makes when the input offers tools. ImageOptional: the input's picture is context sent
// along with the user's own words (their screen while vision is on), so a model that rejects it is asked again without it; a
// screen glance's picture is the whole point of its request and is never dropped. CharacterTags are the desktop character's
// tags (such as {blush}) the model was told about: they are removed from the words shown and spoken and reach the character
// through the runtime's CharacterCueFeed, timed with the sentence they were written in. SpeechBreaks are the persona's stops:
// where the spoken reply may break between pieces and which short endings join the piece before them (the desktop always
// passes the persona's, SpeechBreaks.Default included); null breaks at every sentence end and never joins pieces, so each
// sentence goes to the voice as soon as it ends.
public sealed class ConversationRequest(
    BoundedTextInput input, TextModelSelection model, TextGenerationLimits textLimits,
    ConversationLimits limits, SpeechOutput? speech = null, ChatCompletionsTarget? chat = null, HostTextTarget? host = null,
    HostSpeechTarget? hostSpeech = null, string? silentReply = null, WindowsVoiceTarget? windowsVoice = null,
    GenerationSettings? generation = null, IConversationToolHost? tools = null, TextFallback? fallback = null,
    bool imageOptional = false, IReadOnlyList<string>? characterTags = null, SpeechBreaks? speechBreaks = null)
{
    [JsonIgnore] public BoundedTextInput Input { get; } = input;
    public TextModelSelection Model { get; } = model;
    public TextGenerationLimits TextLimits { get; } = textLimits;
    public ConversationLimits Limits { get; } = limits;
    public SpeechOutput? Speech { get; } = speech;
    public ChatCompletionsTarget? Chat { get; } = chat;
    [JsonIgnore] public HostTextTarget? Host { get; } = host;
    [JsonIgnore] public HostSpeechTarget? HostSpeech { get; } = hostSpeech;
    [JsonIgnore] public WindowsVoiceTarget? WindowsVoice { get; } = windowsVoice;
    [JsonIgnore] public string? SilentReply { get; } = silentReply;
    public GenerationSettings? Generation { get; } = generation;
    [JsonIgnore] public IConversationToolHost? Tools { get; } = tools;
    public TextFallback? Fallback { get; } = fallback;
    public bool ImageOptional { get; } = imageOptional;
    [JsonIgnore] public IReadOnlyList<string> CharacterTags { get; } = characterTags ?? [];
    [JsonIgnore] public SpeechBreaks? SpeechBreaks { get; } = speechBreaks;

    /// <summary>The most character tags one request may carry.</summary>
    public const int MaximumCharacterTags = 128;

    internal void Validate()
    {
        ContractRules.Require(SilentReply is null || SilentReply.Length is > 0 and <= 16 && SilentReply.All(char.IsAsciiLetter),
            "The silent reply word must be 1-16 ASCII letters.");
        ContractRules.Require(CharacterTags.Count <= MaximumCharacterTags && CharacterTags.All(tag => tag is { Length: >= 3 and <= 64 } &&
            tag[0] == '{' && tag[^1] == '}' && !tag.Any(char.IsControl)), "Character tags must be at most 128 {tags} of 3-64 characters.");
        SpeechBreaks?.Validate();
        ArgumentNullException.ThrowIfNull(Input);
        ArgumentNullException.ThrowIfNull(Model);
        ArgumentNullException.ThrowIfNull(TextLimits);
        ArgumentNullException.ThrowIfNull(Limits);
        TextLimits.Validate();
        Limits.Validate();
        Generation?.Validate();
        ContractRules.Require(Input.Origin is null && Input.ToolRounds.Count == 0,
            "A conversation request starts from the reply's first input.");
        // A paired host's gateway speaks its own protocol without function calling.
        ContractRules.Require(Input.Tools.Count == 0 || Tools is not null && Host is null,
            "Tools need a tool host and an OpenAI or Chat Completions destination.", ErrorCode.ProviderCapability);
        ContractRules.Identifier(Model.ModelAlias);
        if (Host is { } host)
        {
            ContractRules.Require(Chat is null && Model.ModelAlias == SelfHostSetup.GatewayOllamaAlias &&
                Uri.TryCreate(host.Origin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps &&
                host.CredentialId != Guid.Empty,
                "A Martlet host destination requires its own model alias, pinned HTTPS origin and paired credential.",
                ErrorCode.ProviderCapability);
            ContractRules.Identifier(Model.UpstreamModelId);
        }
        else if (Chat is { } chat)
        {
            _ = ChatCompletionsSetup.BaseUri(chat.BaseUrl);
            ChatCompletionsSetup.ModelId(Model.UpstreamModelId);
            ContractRules.Require(Model.ModelAlias == ChatCompletionsSetup.Alias,
                "A Chat Completions destination requires its own model alias.", ErrorCode.ProviderCapability);
        }
        else
            ContractRules.Require(OpenAiTextGenerationCatalog.SupportsModel(Model.UpstreamModelId),
                "Select a supported text model.", ErrorCode.ProviderCapability);
        if (Fallback is { } fallback)
        {
            ArgumentNullException.ThrowIfNull(fallback.Chat);
            ArgumentNullException.ThrowIfNull(fallback.Model);
            _ = ChatCompletionsSetup.BaseUri(fallback.Chat.BaseUrl);
            ChatCompletionsSetup.ModelId(fallback.Model.UpstreamModelId);
            ContractRules.Require(fallback.Model.ModelAlias == ChatCompletionsSetup.FallbackAlias,
                "A Thinking fallback requires its own model alias.", ErrorCode.ProviderCapability);
        }
        ContractRules.Require(Input.Utf8Bytes <= TextLimits.MaxInputBytes &&
            Input.InputTokenReservation <= TextLimits.MaxInputTokens &&
            Input.History.Count <= TextLimits.MaxHistoryMessages, "Text input exceeds the selected limits.");
        if (Speech is { } voice)
        {
            ArgumentNullException.ThrowIfNull(voice.Selection);
            ArgumentNullException.ThrowIfNull(voice.Output);
            ArgumentNullException.ThrowIfNull(voice.Limits);
            voice.Output.Validate();
            voice.Limits.Validate();
            ContractRules.Identifier(voice.Selection.ModelAlias);
            ContractRules.Require(HostSpeech is null || WindowsVoice is null, "Choose one voice for the spoken reply.",
                ErrorCode.ProviderCapability);
            if (WindowsVoice is { } windowsVoice)
            {
                WindowsSpeechSetup.InstalledId(windowsVoice.VoiceId);
                ContractRules.Require(voice.Selection == WindowsVoiceSynthesisStream.Selection(windowsVoice),
                    "A Windows voice requires its own alias, installed-voice model and exact voice.", ErrorCode.ProviderCapability);
            }
            else if (HostSpeech is { } hostSpeech)
                ContractRules.Require(voice.Selection.ModelAlias == SelfHostSetup.GatewayF5Alias &&
                    voice.Selection.UpstreamModelId == hostSpeech.ModelId &&
                    OpenAiSpeechSynthesisCatalog.SupportsFormat(voice.Selection.OutputFormat) &&
                    Uri.TryCreate(hostSpeech.Origin, UriKind.Absolute, out var speechOrigin) &&
                    speechOrigin.Scheme == Uri.UriSchemeHttps && hostSpeech.CredentialId != Guid.Empty,
                    "A Martlet host voice requires its own alias, model, pinned HTTPS origin and paired credential.",
                    ErrorCode.ProviderCapability);
            else
                ContractRules.Require(OpenAiSpeechSynthesisCatalog.SupportsModel(voice.Selection.UpstreamModelId) &&
                    OpenAiSpeechSynthesisCatalog.SupportsVoice(voice.Selection.Voice) &&
                    OpenAiSpeechSynthesisCatalog.SupportsFormat(voice.Selection.OutputFormat),
                    "Select a supported speech model, voice and format.", ErrorCode.ProviderCapability);
        }
    }

    public override string ToString() => nameof(ConversationRequest);
}

// Units are reservations, not measured tokens, prices or invoice caps. Null permission/reservation is denial.
public sealed record OperationBudget(CorrelationIds Ids, long Epoch, ProviderRole Role, int Requests,
    int InputUtf8Bytes, int InputTokenReservation, int OutputTokens, long OutputSamples);
public sealed record BudgetReservation(OperationBudget Reserved, DateTimeOffset ExpiresAt);

public sealed class TextAuthorizationAction(ProviderRequestContext context, BoundedTextInput input,
    TextModelSelection model, TextGenerationLimits limits, OperationBudget budget)
{
    public ProviderRequestContext Context { get; } = context;
    [JsonIgnore] public BoundedTextInput Input { get; } = input;
    public TextModelSelection Model { get; } = model;
    public TextGenerationLimits Limits { get; } = limits;
    public OperationBudget Budget { get; } = budget;
    public override string ToString() => nameof(TextAuthorizationAction);
}

public sealed class SpeechAuthorizationAction(ProviderRequestContext context, int segment,
    BoundedSpeechInput input, SpeechSynthesisSelection selection, SpeechSynthesisLimits limits, OperationBudget budget)
{
    public ProviderRequestContext Context { get; } = context;
    public int Segment { get; } = segment;
    [JsonIgnore] public BoundedSpeechInput Input { get; } = input;
    [JsonIgnore] public SpeechSynthesisSelection Selection { get; } = selection;
    public SpeechSynthesisLimits Limits { get; } = limits;
    public OperationBudget Budget { get; } = budget;
    public override string ToString() => nameof(SpeechAuthorizationAction);
}

public sealed record AuthorizedTextOperation(TextDisclosureAuthorization Authorization, BudgetReservation Reservation);
public sealed record AuthorizedSpeechOperation(SpeechDisclosureAuthorization Authorization, BudgetReservation Reservation);

public interface IConversationAuthorizationSource
{
    // The caller authenticates the action and reserves its own budget before issuing exact one-use permission.
    // Implementations must honor cancellation. Never authorize unknown future generated segments.
    ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken);
    ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken);
}

public sealed record ConversationSnapshot(
    Guid SessionId, Guid TurnId, Guid TextRequestId, long TurnEpoch, long CurrentEpoch, ConversationState State,
    ConversationFailure Failure, ProviderFailureCode? ProviderFailure, SequenceIssueInfo? SequenceFailure,
    int TextCharacters, bool TextComplete, int QueuedSegments, int PeakQueuedSegments,
    int CommittedSegments, int SuppressedFragments, int ReservedSpeechBytes, long ReservedSpeechSamples,
    Guid? ActiveSpeechRequestId, EvidenceProvenance? TextProvenance, EvidenceProvenance? SpeechProvenance,
    long AcceptedSamples, long SubmittedSamples, long DeviceConsumedSamples, bool MayHavePlayed,
    bool OwnershipReleased, bool Quarantined, long DroppedEvents, PlaybackSnapshot? Playback,
    Guid? RetryOf, bool EarlierTurnMayHavePlayed, int ToolCalls = 0, string? ActiveTool = null, bool ToolsRejected = false,
    bool SpeechLimitReached = false, ProviderRole? FailedProvider = null, string? FellBackAfter = null, bool AudioRejected = false,
    TimeSpan? FirstTextAfter = null, TimeSpan? FirstAudioAfter = null, bool ImageRejected = false,
    ConversationFailure SpeechFailure = ConversationFailure.None, ConversationTimings? Timings = null, long? InputTokens = null,
    long? CachedInputTokens = null, bool ReasoningRejected = false)
{
    public decimal? EstimatedCost => null;
    public long? AudibleSamples => null;
    /// <summary>The share of <see cref="InputTokens"/> (what the providers reported this reply's requests read) that came
    /// from their prompt cache, 0-1; null when no request said how much was cached.</summary>
    public double? CachedShare => InputTokens is > 0 and var read && CachedInputTokens is { } cached ? (double)cached / read : null;
    /// <summary>Whether the reply was asked of the Thinking fallback after the selected destination failed (FellBackAfter
    /// names how: a provider failure code, a stream issue or a conversation failure).</summary>
    public bool FellBack => FellBackAfter is not null;
    /// <summary>Whether the voice stopped before the end of the reply (SpeechFailure says why; a voice provider's own code is
    /// ProviderFailure with FailedProvider Tts). The reply's text is unaffected: it still completes.</summary>
    public bool SpeechFailed => SpeechFailure != ConversationFailure.None;
}

public sealed record SequenceIssueInfo(Martlet.Core.Streaming.SequenceIssue Issue);

/// <summary>When each step of a reply first happened, measured from the turn's start like
/// <see cref="ConversationSnapshot.FirstTextAfter"/> and <see cref="ConversationSnapshot.FirstAudioAfter"/> (null when it never
/// happened): the Thinking request sent (after its authorization), the provider's response headers, its first hidden reasoning,
/// the first speakable piece staged for the voice, the first voice request sent, the voice's first audio received, the first
/// piece fully synthesized (and how much speech it holds) and the first audio handed to the speakers. Diagnostics only (the
/// desktop log's reply latency line); nothing depends on them.</summary>
public sealed record ConversationTimings(
    TimeSpan? TextRequestAfter = null, TimeSpan? TextResponseAfter = null, TimeSpan? FirstReasoningAfter = null,
    TimeSpan? FirstSegmentAfter = null, TimeSpan? SpeechRequestAfter = null, TimeSpan? FirstSpeechAudioAfter = null,
    TimeSpan? FirstPieceSynthesizedAfter = null, TimeSpan? FirstPieceSpeech = null, TimeSpan? PlaybackStartedAfter = null);

public sealed class ConversationContent(string text, string? refusal)
{
    [JsonIgnore] public string Text { get; } = text;
    [JsonIgnore] public string? Refusal { get; } = refusal;
    public override string ToString() => nameof(ConversationContent);
}

public sealed record ConversationEvent(long Sequence, ConversationEventKind Kind, ConversationSnapshot Snapshot,
    [property: JsonIgnore] string? Text = null)
{
    public override string ToString() => $"{nameof(ConversationEvent)}: {Kind}";
}

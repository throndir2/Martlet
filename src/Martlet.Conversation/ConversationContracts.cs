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

    internal void Validate()
    {
        ContractRules.Require(TurnTimeout > TimeSpan.Zero && TurnTimeout <= TimeSpan.FromSeconds(90) &&
            ShutdownTimeout > TimeSpan.Zero && ShutdownTimeout <= TimeSpan.FromSeconds(2) &&
            MaxSpeechSegments is >= 1 and <= 32 && MaxSpeechTextBytes is >= 1 and <= 16_384 &&
            MaxReservedSpeechSamples is >= 1 and <= 2_160_000 && EventCapacity is >= 4 and <= 128,
            "Conversation limits are out of range.");
    }
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

// A new instance is explicit input, not a stored provider thread or automatic conversation history.
public sealed class ConversationRequest(
    BoundedTextInput input, TextModelSelection model, TextGenerationLimits textLimits,
    ConversationLimits limits, SpeechOutput? speech = null, ChatCompletionsTarget? chat = null)
{
    [JsonIgnore] public BoundedTextInput Input { get; } = input;
    public TextModelSelection Model { get; } = model;
    public TextGenerationLimits TextLimits { get; } = textLimits;
    public ConversationLimits Limits { get; } = limits;
    public SpeechOutput? Speech { get; } = speech;
    public ChatCompletionsTarget? Chat { get; } = chat;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Input);
        ArgumentNullException.ThrowIfNull(Model);
        ArgumentNullException.ThrowIfNull(TextLimits);
        ArgumentNullException.ThrowIfNull(Limits);
        TextLimits.Validate();
        Limits.Validate();
        ContractRules.Identifier(Model.ModelAlias);
        if (Chat is { } chat)
        {
            _ = ChatCompletionsSetup.BaseUri(chat.BaseUrl);
            ChatCompletionsSetup.ModelId(Model.UpstreamModelId);
            ContractRules.Require(Model.ModelAlias == ChatCompletionsSetup.Alias,
                "A Chat Completions destination requires its own model alias.", ErrorCode.ProviderCapability);
        }
        else
            ContractRules.Require(OpenAiTextGenerationCatalog.SupportsModel(Model.UpstreamModelId),
                "Select a supported text model.", ErrorCode.ProviderCapability);
        ContractRules.Require(Input.Utf8Bytes <= TextLimits.MaxInputBytes &&
            Input.InputTokenReservation <= TextLimits.MaxInputTokens, "Text input exceeds the selected limits.");
        if (Speech is { } voice)
        {
            ArgumentNullException.ThrowIfNull(voice.Selection);
            ArgumentNullException.ThrowIfNull(voice.Output);
            ArgumentNullException.ThrowIfNull(voice.Limits);
            voice.Output.Validate();
            voice.Limits.Validate();
            ContractRules.Identifier(voice.Selection.ModelAlias);
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
    Guid? RetryOf, bool EarlierTurnMayHavePlayed)
{
    public decimal? EstimatedCost => null;
    public long? AudibleSamples => null;
}

public sealed record SequenceIssueInfo(Martlet.Core.Streaming.SequenceIssue Issue);

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

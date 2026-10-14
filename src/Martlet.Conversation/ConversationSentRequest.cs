using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>One Thinking request a reply sent (<see cref="ConversationTurn.SentRequests"/>): its input as it went to the model
/// (with the tool rounds before it, and without a picture or recording the model refused), the model, and why it was sent (the
/// first request, after a tool round, again without something the model refused, or the Thinking fallback). Private content, as
/// <see cref="ConversationRequest.Input"/>: show it on this PC only, never in logs, status files or MCP.</summary>
public sealed class ConversationSentRequest(BoundedTextInput input, TextModelSelection model, bool fallback, bool withoutReasoning,
    string why, DateTimeOffset at)
{
    /// <summary>The most requests one reply keeps: every tool round, retry and fallback fit well within it.</summary>
    public const int MaximumKept = 32;

    public BoundedTextInput Input { get; } = input;
    public TextModelSelection Model { get; } = model;
    /// <summary>It went to the Thinking fallback (<see cref="ConversationRequest.Fallback"/>), not the chosen model.</summary>
    public bool Fallback { get; } = fallback;
    /// <summary>It went without the Thinking steps choice, which the model refused, so the model's own default applied.</summary>
    public bool WithoutReasoning { get; } = withoutReasoning;
    /// <summary>Why it was sent, in a few words ("first", "after tool round 1"...), as the Thinking trace says it.</summary>
    public string Why { get; } = why;
    public DateTimeOffset At { get; } = at;

    public override string ToString() => $"{nameof(ConversationSentRequest)} (content omitted)";
}

/// <summary>One named part a request's text was joined from (the persona, the tools prompt, lorebook entries...), so this PC can
/// show what was sent part by part. Private content: never logged.</summary>
public sealed record RequestPart(string Name, string Text)
{
    public override string ToString() => $"{nameof(RequestPart)} {Name} (content omitted)";
}

/// <summary>The named parts of a request's instructions (<see cref="BoundedTextInput.Personality"/>), of the notes with its message
/// (<see cref="BoundedTextInput.Notes"/>) and of its context notes (<see cref="BoundedTextInput.Context"/>), in the order they
/// were joined. Only for showing what was sent: the request sends the joined text.</summary>
public sealed record RequestParts(IReadOnlyList<RequestPart> Instructions, IReadOnlyList<RequestPart> Notes, IReadOnlyList<RequestPart> Context)
{
    public override string ToString() => $"{nameof(RequestParts)} (content omitted)";
}

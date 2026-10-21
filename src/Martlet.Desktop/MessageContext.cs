using System.Globalization;
using System.Text;
using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>How a block of sent text shows: plain text, notes the conversation keeps with an earlier message, or code (a tool's
/// arguments, parameters and results).</summary>
internal enum ContextBlockStyle { Text, Kept, Code }

/// <summary>One block of a section in What was sent: a label (the part's name, or who said an earlier message) and the exact text
/// sent.</summary>
internal sealed record ContextBlock(string Label, string Text, ContextBlockStyle Style = ContextBlockStyle.Text, bool Mine = false)
{
    public override string ToString() => $"{nameof(ContextBlock)} {Label} (content omitted)";
}

/// <summary>One section of what a request sent, in the order the request sends it: its key (for automation IDs), its title, where
/// in the request it goes, and its blocks. <paramref name="Whole"/> is the section's text exactly as sent when the request sends it
/// as one text (the instructions, the notes, the context notes), so its size counts the blank lines and labels between the
/// parts too.</summary>
internal sealed record ContextSection(string Key, string Title, string Where, IReadOnlyList<ContextBlock> Blocks, string? Whole = null)
{
    internal int Characters => Whole?.Length ?? Blocks.Sum(block => block.Text.Length);
    internal int Tokens => Whole is not null ? BoundedTextInput.TextTokens(Whole) : Blocks.Sum(block => BoundedTextInput.TextTokens(block.Text));
    /// <summary>The tool descriptions and the request's settings start closed; the rest start open.</summary>
    internal bool StartsOpen => Key is not ("tools" or "settings");

    public override string ToString() => $"{nameof(ContextSection)} {Key} (content omitted)";
}

/// <summary>What was sent to the Thinking model to make one of Martlet's messages in the talk window: the request the reply started
/// from and every request it sent (each tool round, retry or fallback), with how many input tokens the model said it read. The
/// What was sent window (<see cref="MessageContextWindow"/>) shows it section by section. Private content: in memory only, shown on
/// this PC only, never saved, logged or returned by MCP (only its sizes and section names are).</summary>
internal sealed class MessageContext
{
    private MessageContext(ConversationRequest request, IReadOnlyList<ConversationSentRequest> sent, string kind, string character,
        long? inputTokens, long? cachedInputTokens)
    {
        Request = request;
        Sent = sent.Count > 0;
        Requests = Sent ? sent : [new(request.Input, request.Model, false, false, "not sent yet", DateTimeOffset.Now)];
        Kind = kind;
        Character = character;
        InputTokens = inputTokens;
        CachedInputTokens = cachedInputTokens;
    }

    internal ConversationRequest Request { get; }
    /// <summary>Every request the reply sent, in order; the request it starts from while none was sent yet (<see cref="Sent"/>
    /// false).</summary>
    internal IReadOnlyList<ConversationSentRequest> Requests { get; }
    internal bool Sent { get; }
    /// <summary>What the message is ("reply", "remark"...), for the window's title.</summary>
    internal string Kind { get; }
    /// <summary>What the conversation calls the character, who said the earlier assistant messages.</summary>
    internal string Character { get; }
    /// <summary>The input tokens the providers said the reply's requests read, all together, and how many of them came from the
    /// prompt cache; null when they didn't say.</summary>
    internal long? InputTokens { get; }
    internal long? CachedInputTokens { get; }

    /// <summary>What <paramref name="turn"/> sent so far, or null without a turn.</summary>
    internal static MessageContext? Of(ConversationTurn? turn, string kind, string character)
    {
        if (turn is null) return null;
        var snapshot = turn.Snapshot;
        return For(turn.Request, turn.SentRequests, kind, character, snapshot.InputTokens, snapshot.CachedInputTokens);
    }

    /// <summary>What <paramref name="sent"/> carried for <paramref name="request"/> (a turn's, or a test's).</summary>
    internal static MessageContext For(ConversationRequest request, IReadOnlyList<ConversationSentRequest> sent, string kind, string character,
        long? inputTokens = null, long? cachedInputTokens = null) => new(request, sent, kind, character, inputTokens, cachedInputTokens);

    /// <summary>The name of request <paramref name="index"/> in the request list, such as "Request 2: after tool round 1".</summary>
    internal string RequestName(int index)
    {
        var request = Requests[index];
        return string.Create(CultureInfo.CurrentCulture, $"Request {index + 1}: {request.Why}") +
            (request.Fallback ? " (to the Thinking fallback)" : "");
    }

    /// <summary>One request's estimated tokens: its sections, and the room a picture takes.</summary>
    internal int EstimatedTokens(int index) =>
        Sections(index).Sum(s => s.Tokens) + (Requests[index].Input.Image is null ? 0 : BoundedImage.TokenReservation);

    /// <summary>The short token line under a message in the talk window (MCP reads it): the input tokens the model said its
    /// requests read (or Martlet's estimate when it didn't say), and the estimated tokens of Martlet's instructions. Numbers
    /// only, never text.</summary>
    internal string TokensLine
    {
        get
        {
            var last = Requests.Count - 1;
            var instructions = Sections(last).FirstOrDefault(s => s.Key == "instructions")?.Tokens ?? 0;
            var input = InputTokens is { } read
                ? string.Create(CultureInfo.CurrentCulture, $"{read:N0} input tokens")
                : string.Create(CultureInfo.CurrentCulture, $"about {EstimatedTokens(last):N0} input tokens");
            var requests = Requests.Count > 1 ? string.Create(CultureInfo.CurrentCulture, $" in {Requests.Count} requests") : "";
            return string.Create(CultureInfo.CurrentCulture, $"{input}{requests} · instructions about {instructions:N0}");
        }
    }

    /// <summary>One line about request <paramref name="index"/> (MCP reads it): the model, how many requests, its size and what
    /// went with it, and the tokens the model said it read. Never its text.</summary>
    internal string Summary(int index)
    {
        var request = Requests[index];
        var sections = Sections(index);
        var text = new StringBuilder(request.Model.UpstreamModelId);
        text.Append(CultureInfo.CurrentCulture, $" · {Requests.Count} request{(Requests.Count == 1 ? "" : "s")}");
        if (!Sent) text.Append(" (not sent yet)");
        text.Append(CultureInfo.CurrentCulture,
            $" · {sections.Sum(s => s.Characters):N0} characters, about {EstimatedTokens(index):N0} tokens");
        if (request.Input.Image is not null) text.Append(" · a picture");
        if (request.Input.Audio is not null) text.Append(" · a recording");
        if (InputTokens is { } read)
            text.Append(CultureInfo.CurrentCulture, $" · The model said its requests read {read:N0} input tokens").Append(CachedInputTokens is { } cached
                ? string.Create(CultureInfo.CurrentCulture, $", {cached:N0} of them from its prompt cache.") : ".");
        return text.ToString();
    }

    /// <summary>What request <paramref name="index"/> sent, section by section, in the order it sends them.</summary>
    internal IReadOnlyList<ContextSection> Sections(int index)
    {
        var sent = Requests[index];
        var input = sent.Input;
        var parts = Request.Parts;
        var host = Request.Host is not null;
        List<ContextSection> sections = [];
        if (input.Personality is { } personality)
            sections.Add(new("instructions", "Instructions",
                host ? "Sent first, as the system text, with the notes after them (a paired host's Thinking takes them that way)."
                    : "Sent first, as the system message. They stay the same from one reply to the next, so the model's prompt cache can reuse them.",
                Blocks(parts?.Instructions, personality, string.Join("\n\n", parts?.Instructions.Select(part => part.Text) ?? []), "Instructions"),
                personality));
        if (input.History.Count > 0)
            sections.Add(new("history", string.Create(CultureInfo.CurrentCulture,
                    $"Conversation history ({input.History.Count} earlier message{(input.History.Count == 1 ? "" : "s")})"),
                "Sent next, as earlier user and assistant messages, oldest first. Only the newest ones that fit Thinking's context size go.",
                [.. input.History.SelectMany(History)]));
        sections.Add(new("message", "The message", "Sent as the newest user message.", [new("Message", input.UserText)]));
        if (input.Notes is { } notes)
            sections.Add(new("notes", "Notes with the message",
                "Sent in the same user message, after the message, between [" + LiveConversationConfiguration.NotesLabel + "] labels. " +
                "The conversation keeps them, so later requests carry them again.",
                Blocks(parts?.Notes, notes, LiveConversationConfiguration.WrapNotes(parts?.Notes ?? []), "Notes"), notes));
        if (input.Context is { } context)
            sections.Add(new("context", "Context notes",
                "Sent last in the same user message, between [" + LiveConversationConfiguration.NotesLabel + "] labels, for this request " +
                "only. The conversation doesn't keep them.",
                Blocks(parts?.Context, context, LiveConversationConfiguration.WrapNotes(parts?.Context ?? []), "Context notes"), context));
        if (input.Image is { } image)
            sections.Add(new("picture", "Picture", string.Create(CultureInfo.CurrentCulture,
                $"Sent with the message: {image.Width} x {image.Height} {(image.MediaType == ImageMediaType.Jpeg ? "JPEG" : "PNG")}, {image.ByteCount / 1024.0:N0} KB."), []));
        else if (Request.Input.Image is not null)
            sections.Add(new("picture", "Picture", "Not sent with this request: the model refused the picture, so it was asked again without it.", []));
        if (input.Audio is { } audio)
            sections.Add(new("recording", "Recording", string.Create(CultureInfo.CurrentCulture,
                $"Sent with the message: what was said, a {audio.Duration.TotalSeconds:0.0} s recording (WAV)."), []));
        else if (Request.Input.Audio is not null)
            sections.Add(new("recording", "Recording", "Not sent with this request: it went with the words instead of the recording.", []));
        if (input.Continuation is { } continuation && input.ToolRounds.Count == 0)
            sections.Add(new("continuation", "Its unfinished answer",
                "Sent after the message, as the assistant's own message, so the model goes on from where it stopped.",
                [new("Unfinished answer", continuation)]));
        if (input.ToolRounds.Count > 0)
            sections.Add(new("toolrounds", string.Create(CultureInfo.CurrentCulture, $"Tool rounds ({input.ToolRounds.Count})"),
                "Sent after the message: what the model wrote and the tools it called earlier in this reply, and what each tool returned.",
                [.. input.ToolRounds.SelectMany(Rounds)]));
        if (input.Tools.Count > 0)
            sections.Add(new("tools", string.Create(CultureInfo.CurrentCulture, $"Tools it may call ({input.Tools.Count})"),
                "Sent beside the messages, as function descriptions." +
                (input.ToolCallsAllowed ? "" : " On this request it may not call them, so it must answer in text."),
                [.. input.Tools.Select(tool => new ContextBlock(tool.Name, tool.Description + "\n\nParameters: " + tool.ParametersJson, ContextBlockStyle.Code))]));
        sections.Add(new("settings", "Request settings", "How the request asks the model to answer. These go with every request.",
            [new("Settings", Settings(sent), ContextBlockStyle.Code)]));
        return sections;
    }

    // The named parts when they join to exactly what was sent, else the text as one block.
    private static IReadOnlyList<ContextBlock> Blocks(IReadOnlyList<RequestPart>? parts, string sent, string? joined, string whole) =>
        parts is { Count: > 0 } && string.Equals(joined, sent, StringComparison.Ordinal)
            ? [.. parts.Select(part => new ContextBlock(part.Name, part.Text))]
            : [new(whole, sent)];

    // An earlier message: what was said, and for the user's messages the notes the conversation kept with them.
    private IEnumerable<ContextBlock> History(TextHistoryMessage message)
    {
        if (message.Role == TextHistoryRole.Assistant)
        {
            yield return new(Character, message.Text);
            yield break;
        }
        var marker = "\n\n[" + LiveConversationConfiguration.NotesLabel + "]";
        var at = message.Text.IndexOf(marker, StringComparison.Ordinal);
        if (at <= 0)
        {
            yield return new("You", message.Text, Mine: true);
            yield break;
        }
        yield return new("You", message.Text[..at], Mine: true);
        yield return new("Notes kept with it", message.Text[(at + 2)..], ContextBlockStyle.Kept, Mine: true);
    }

    private static IEnumerable<ContextBlock> Rounds(TextToolRound round, int index)
    {
        var name = string.Create(CultureInfo.CurrentCulture, $"Round {index + 1}");
        if (round.Text.Trim().Length > 0) yield return new($"{name}: what it wrote", round.Text);
        for (var i = 0; i < round.Calls.Count; i++)
        {
            yield return new($"{name}: it called {round.Calls[i].Name}", round.Calls[i].ArgumentsJson, ContextBlockStyle.Code);
            yield return new($"{name}: {round.Calls[i].Name} returned", round.Results[i].Output, ContextBlockStyle.Code);
        }
    }

    private string Settings(ConversationSentRequest sent)
    {
        var lines = new List<string>
        {
            "Model: " + sent.Model.UpstreamModelId + (sent.Fallback ? " (the Thinking fallback)" : ""),
            "Where: " + (sent.Fallback ? Request.Fallback?.Chat.BaseUrl : Request.Chat?.BaseUrl ?? (Request.Host is { } host ? "a paired host's Thinking (" + host.HostId + ")" : "OpenAI")),
            string.Create(CultureInfo.CurrentCulture, $"Sent at: {sent.At.ToLocalTime():T}")
        };
        if (Request.Generation is { } generation)
        {
            void Add(string name, object? value) { if (value is not null) lines.Add(string.Create(CultureInfo.CurrentCulture, $"{name}: {value}")); }
            Add("Temperature", generation.Temperature);
            Add("Top P", generation.TopP);
            Add("Top K", generation.TopK);
            Add("Min P", generation.MinP);
            Add("Repeat penalty", generation.RepeatPenalty);
            Add("Frequency penalty", generation.FrequencyPenalty);
            Add("Presence penalty", generation.PresencePenalty);
            Add("Context size (tokens)", generation.ContextTokens);
            Add("Reasoning effort", generation.ReasoningEffort);
            lines.Add("Thinking steps: " + (sent.WithoutReasoning || generation.Reasoning is null ? "the model's own default" : generation.Reasoning.Value ? "On" : "Off"));
        }
        lines.Add(string.Create(CultureInfo.CurrentCulture, $"Max reply tokens: {Request.TextLimits.MaxOutputTokens:N0}"));
        if (Request.SilentReply is { } quiet) lines.Add($"May stay quiet by answering: {quiet}");
        if (Request.CharacterTags.Count > 0) lines.Add("Character tags it may write: " + string.Join(" ", Request.CharacterTags));
        if (Request.ControlTags.Count > 0) lines.Add("Control tags it may write: " + string.Join(" ", Request.ControlTags));
        return string.Join("\n", lines);
    }

    /// <summary>Request <paramref name="index"/> as readable text, section by section, for Copy all.</summary>
    internal string Text(int index)
    {
        var text = new StringBuilder();
        text.Append("What was sent for this ").Append(Kind).Append(" (").Append(RequestName(index)).Append(')').AppendLine();
        text.AppendLine(Summary(index));
        foreach (var section in Sections(index))
        {
            text.AppendLine().Append("=== ").Append(section.Title).AppendLine(" ===").AppendLine(section.Where);
            foreach (var block in section.Blocks)
                text.AppendLine().Append("--- ").Append(block.Label).AppendLine(" ---").AppendLine(block.Text);
        }
        return text.ToString();
    }

    public override string ToString() => $"{nameof(MessageContext)} (content omitted)";
}

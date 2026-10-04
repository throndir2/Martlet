using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The one request after a finished reply that asks what to remember (<paramref name="ShownFacts"/> numbered facts
/// shown) and which names the heard voices go by (<paramref name="Voices"/>, by tag). <paramref name="Continued"/> says it
/// continues the reply's own conversation instead of quoting an excerpt.</summary>
internal sealed record AfterReplyPrompt(BoundedTextInput Input, int ShownFacts, IReadOnlyDictionary<string, string> Voices, bool Continued)
{
    public override string ToString() => $"{nameof(AfterReplyPrompt)} {{ ShownFacts = {ShownFacts}, Voices = {Voices.Count}, Continued = {Continued} }}";
}

/// <summary>Remembering and learning names after a reply, in one request when both are due (Companion › Prompts ›
/// Remembering and learning names together). On a Thinking model on this PC it continues the reply's own conversation (same
/// instructions and tools, same earlier messages, the message and the reply, then the task), so the model's prompt cache still
/// holds the conversation for the next reply: a request with another start would push it out, and the next reply would wait
/// while the whole conversation is read again. Elsewhere, and whenever the conversation can't be continued (what the PC played
/// is in it, or it would outgrow the context), it reads a short excerpt as before, which costs far less on a paid provider
/// whose cache isn't pushed out by other requests.</summary>
internal static class AfterReply
{
    private static readonly IReadOnlyDictionary<string, string> NoVoices = new Dictionary<string, string>();

    /// <summary>Opens the task when it continues the conversation, so the model answers the task, not the user.</summary>
    internal const string ContinuationHeader =
        "(A background task from Martlet, not said by the user. Don't continue the conversation: answer only the task below, " +
        "about the latest exchange above. Only the user's own words count, never Martlet's notes.)";

    internal static AfterReplyPrompt Prompt(IReadOnlyList<MemoryFact>? known, HeardVoices? heard, string? earlierUser, string? earlierReply,
        string user, string reply, PromptSettings? prompts, BoundedTextInput? conversation = null, Func<BoundedTextInput, bool>? fits = null)
    {
        ContractRules.Require(known is not null || heard is not null, "Remembering or learning names is required.");
        var instructions = Instructions(known is not null, heard is not null, prompts);
        var voices = heard is null ? NoVoices : VoiceNaming.Voices(heard);
        if (conversation is not null && fits is not null && Continue(conversation, instructions, known, heard, reply) is { } continued &&
            fits(continued.Input))
            return continued with { Voices = voices };
        if (heard is null)
        {
            var memory = MemoryCapture.Prompt(earlierUser, earlierReply, user, reply, known!, prompts);
            return new(memory.Input, memory.ShownFacts, NoVoices, false);
        }
        if (known is null)
        {
            var naming = VoiceNaming.Prompt(heard, earlierUser, earlierReply, user, reply, prompts);
            return new(naming.Input, 0, naming.Voices, false);
        }
        // Both: drop context before the latest exchange if an unusually large excerpt would not fit the LLM input budget.
        foreach (var (earlier, shown) in new[] { (true, Math.Min(known.Count, MemoryCapture.MaximumShownFacts)), (false, Math.Min(known.Count, 4)), (false, 0) })
        {
            var text = new StringBuilder();
            MemoryCapture.AppendKnown(text, known, shown);
            text.Append('\n');
            VoiceNaming.AppendVoices(text, heard);
            if (earlier && (earlierUser is not null || earlierReply is not null))
            {
                text.Append("\nEarlier in the conversation (context only):\n");
                if (earlierUser is not null) text.Append("User: ").Append(MemoryCapture.Clip(earlierUser, 300)).Append('\n');
                if (earlierReply is not null) text.Append("Martlet: ").Append(MemoryCapture.Clip(earlierReply, 300)).Append('\n');
            }
            text.Append("\nLatest exchange:\n").Append(VoiceNaming.UserLabel(heard)).Append(MemoryCapture.Clip(user, 1400))
                .Append("\nMartlet: ").Append(MemoryCapture.Clip(reply, 800));
            try
            {
                var input = new BoundedTextInput(text.ToString(), instructions);
                if (input.Utf8Bytes <= LiveConversationConfiguration.DefaultTextLimits.MaxInputBytes &&
                    input.InputTokenReservation <= LiveConversationConfiguration.DefaultTextLimits.MaxInputTokens)
                    return new(input, shown, voices, false);
            }
            catch (ContractException)
            {
            }
        }
        throw new LiveActionException("conversation.input_limit");
    }

    /// <summary>What the model is asked to do: the Remembering prompt, the Learning names prompt, or both joined.</summary>
    internal static string? Instructions(bool remember, bool name, PromptSettings? prompts)
    {
        var remembering = remember ? PromptSettings.Fill(prompts, PromptCatalog.MemoryCapture, ("nothing", MemoryCapture.Nothing)) : null;
        var naming = name ? PromptSettings.Fill(prompts, PromptCatalog.VoiceNaming, ("nothing", VoiceNaming.Nothing)) : null;
        if (remembering is null || naming is null) return remembering ?? naming;
        return PromptSettings.Fill(prompts, PromptCatalog.AfterReply, ("remembering", remembering), ("naming", naming),
            ("nothing", MemoryCapture.Nothing)) ?? remembering + "\n\n" + naming;
    }

    /// <summary>Whether the reply's request can be continued: not with what the PC played anywhere in it (memory and learning
    /// names never read that). Tools it offered are described again (and never run), so the request starts the same.</summary>
    internal static bool CanContinue(BoundedTextInput reply) =>
        !HasPcAudio(reply.UserText) && reply.History.All(message => !HasPcAudio(message.Text));

    private static bool HasPcAudio(string text) => text.Contains(LiveConversationConfiguration.PcAudioMarker, StringComparison.Ordinal);

    // The reply's request exactly as it was sent (instructions, earlier messages, the message with its notes), then the reply
    // and the task. The picture or recording sent with the message isn't sent again.
    private static AfterReplyPrompt? Continue(BoundedTextInput conversation, string? instructions, IReadOnlyList<MemoryFact>? known,
        HeardVoices? heard, string reply)
    {
        if (!CanContinue(conversation) || string.IsNullOrWhiteSpace(reply)) return null;
        var shown = Math.Min(known?.Count ?? 0, MemoryCapture.MaximumShownFacts);
        var task = new StringBuilder(ContinuationHeader).Append("\n\n");
        if (instructions is not null) task.Append(instructions).Append("\n\n");
        if (known is not null) MemoryCapture.AppendKnown(task, known, shown);
        if (known is not null && heard is not null) task.Append('\n');
        if (heard is not null)
        {
            VoiceNaming.AppendVoices(task, heard);
            if (heard.Speaker?.Voice is { } speaker) task.Append("The latest message above was said by ").Append(speaker.Tag).Append(".\n");
        }
        var said = conversation.SentUserText;
        try
        {
            var input = new BoundedTextInput(task.ToString().TrimEnd(), conversation.Personality,
                [.. conversation.History, new(TextHistoryRole.User, said), new(TextHistoryRole.Assistant, reply)],
                tools: conversation.Tools);
            return new(input, shown, NoVoices, true);
        }
        catch (ContractException)
        {
            return null;
        }
    }
}

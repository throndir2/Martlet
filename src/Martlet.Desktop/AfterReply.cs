using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The request after a finished reply that asks what to remember (<paramref name="ShownFacts"/> numbered facts shown)
/// or which names the heard voices go by. <paramref name="Voices"/> are the voices heard, by tag, that the answer's NAME and
/// REMEMBER V3: lines refer to. <paramref name="Continued"/> says it continues the reply's own conversation instead of quoting
/// an excerpt.</summary>
internal sealed record AfterReplyPrompt(BoundedTextInput Input, int ShownFacts, IReadOnlyDictionary<string, string> Voices, bool Continued)
{
    public override string ToString() => $"{nameof(AfterReplyPrompt)} {{ ShownFacts = {ShownFacts}, Voices = {Voices.Count}, Continued = {Continued} }}";
}

/// <summary>Remembering or learning names after a reply: two requests of their own, one job each. On a Thinking model on this
/// PC each continues the reply's own conversation (same instructions and tools, same earlier messages, the message and the
/// reply, then the task), so the model's prompt cache still holds the conversation for the next reply: a request with another
/// start would push it out, and the next reply would wait while the whole conversation is read again. Elsewhere, and whenever
/// the conversation can't be continued (what the PC played is in it, or it would outgrow the context), it reads a short
/// excerpt, which costs far less on a paid provider whose cache isn't pushed out by other requests.</summary>
internal static class AfterReply
{
    private static readonly IReadOnlyDictionary<string, string> NoVoices = new Dictionary<string, string>();

    /// <summary>Opens the task when it continues the conversation, so the model answers the task, not the user.</summary>
    internal const string ContinuationHeader =
        "(A background task from Martlet, not said by the user. Don't continue the conversation: answer only the task below, " +
        "about the latest exchange above. Only the user's own words count, never Martlet's notes.)";

    /// <param name="known">The related facts already remembered: remembering is due. Exactly one of it and
    /// <paramref name="naming"/> is given.</param>
    /// <param name="naming">The voices whose names to learn (naming is due) and what learning names reads.</param>
    /// <param name="present">The voices heard in the message being remembered, to say whose new facts are (the speaker's unless
    /// the model names another).</param>
    /// <param name="people">The label of each voice the known facts belong to (<see cref="MemoryPeople.Labels"/>).</param>
    /// <param name="companion">The name of the persona the companion is: its lines in an excerpt carry it
    /// (<see cref="MemoryCapture.Speaker"/>).</param>
    internal static AfterReplyPrompt Prompt(IReadOnlyList<MemoryFact>? known, VoiceNamingContext? naming, string? earlierUser, string? earlierReply,
        string user, string reply, PromptSettings? prompts, BoundedTextInput? conversation = null, Func<BoundedTextInput, bool>? fits = null,
        HeardVoices? present = null, IReadOnlyDictionary<string, string>? people = null, string? companion = null)
    {
        ContractRules.Require(known is null != naming is null, "Remembering or learning names, one at a time, is required.");
        if (present is { Known.Count: 0 } || naming is not null) present = null;
        var instructions = Instructions(known is not null, prompts);
        var voices = naming?.Tags ?? (present is not null ? VoiceNaming.Voices(present) : NoVoices);
        if (conversation is not null && fits is not null &&
            Continue(conversation, instructions, known, naming, present, people, reply) is { } continued && fits(continued.Input))
            return continued with { Voices = voices };
        if (naming is null)
        {
            var memory = MemoryCapture.Prompt(earlierUser, earlierReply, user, reply, known!, prompts, present, people, companion);
            return new(memory.Input, memory.ShownFacts, voices, false);
        }
        var named = VoiceNaming.Prompt(naming, earlierUser, earlierReply, user, reply, prompts, companion);
        return new(named.Input, 0, named.Voices, false);
    }

    /// <summary>What the model is asked to do: the Remembering prompt, or the Learning names prompt.</summary>
    internal static string? Instructions(bool remember, PromptSettings? prompts) => remember
        ? PromptSettings.Fill(prompts, PromptCatalog.MemoryCapture, ("nothing", MemoryCapture.Nothing))
        : PromptSettings.Fill(prompts, PromptCatalog.VoiceNaming, ("nothing", VoiceNaming.Nothing));

    /// <summary>Whether the reply's request can be continued: not with what the PC played anywhere in it (memory and learning
    /// names never read that). Tools it offered are described again (and never run), so the request starts the same.</summary>
    internal static bool CanContinue(BoundedTextInput reply) =>
        !HasPcAudio(reply.UserText) && reply.History.All(message => !HasPcAudio(message.Text));

    private static bool HasPcAudio(string text) => text.Contains(LiveConversationConfiguration.PcAudioMarker, StringComparison.Ordinal);

    // The reply's request exactly as it was sent (instructions, earlier messages, the message with its notes), then the reply
    // and the task. The picture or recording sent with the message isn't sent again.
    private static AfterReplyPrompt? Continue(BoundedTextInput conversation, string? instructions, IReadOnlyList<MemoryFact>? known,
        VoiceNamingContext? naming, HeardVoices? present, IReadOnlyDictionary<string, string>? people, string reply)
    {
        if (!CanContinue(conversation) || string.IsNullOrWhiteSpace(reply)) return null;
        var shown = Math.Min(known?.Count ?? 0, MemoryCapture.MaximumShownFacts);
        var listed = naming?.Heard ?? present;
        var task = new StringBuilder(ContinuationHeader).Append("\n\n");
        if (instructions is not null) task.Append(instructions).Append("\n\n");
        if (known is not null) MemoryCapture.AppendKnown(task, known, shown, people);
        if (known is not null && listed is not null) task.Append('\n');
        if (listed is not null)
        {
            if (naming is not null) VoiceNaming.AppendVoices(task, naming);
            else VoiceNaming.AppendVoices(task, listed);
            if (listed.Speaker?.Voice is { } speaker) task.Append("The latest message above was said by ").Append(speaker.Tag).Append(".\n");
            if (known is not null) MemoryCapture.AppendWhose(task, listed);
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

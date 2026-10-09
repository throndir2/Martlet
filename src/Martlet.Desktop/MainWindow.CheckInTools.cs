using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The check-in tool sets this PC runs (<see cref="CheckInToolSets"/>): one handler for each set, by its ID. A check-in
/// that chose a set without a handler here is not offered its tools. Handlers run on a pool thread and do the UI work on the
/// UI thread. The first line of each answer shows on the card and in check-ins-status.json, so it never holds private text.</summary>
public partial class MainWindow
{
    /// <summary>Each tool set's handler by its ID. To add a set, add one entry here.</summary>
    private IReadOnlyDictionary<string, CheckInToolHandler> CheckInToolHandlers()
    {
        var handlers = new Dictionary<string, CheckInToolHandler>(StringComparer.Ordinal)
        {
            [CheckInToolSets.CharacterId] = CharacterToolAsync,
            [CheckInToolSets.NextReplyId] = NextReplyToolAsync,
            [TouchReactions.SetId] = TouchReactionsToolAsync
        };
        // Only a PC that keeps reminders (a data folder) runs the reminders tool.
        if (conversation?.RemindersTool is not null) handlers[CheckInToolSets.RemindersId] = ReminderToolAsync;
        // Discord calls and camera only while Martlet can call a Discord friend or is in the owner's Discord calls.
        if (conversation is { RunsDiscordCheckInTools: true } talk)
            handlers[DiscordCheckInTools.SetId] = (call, context, token) => talk.RunDiscordCheckInToolAsync(call, token);
        // Only while memory is on does the memory tool set run.
        if (conversation?.Configuration?.Memory is { Enabled: true })
            handlers[MemoryToolSet.Id] = (call, context, token) => conversation is { } live
                ? live.CheckInMemoryAsync(call, context.CheckInName, token) : new(new ConversationToolResult("Memory: off.\nMemory is off.", true));
        // Background work only while Thinking longer is on and Deep thinking can think.
        if (conversation?.OffersBackgroundWork == true) handlers[BackgroundWorkTools.SetId] = BackgroundWorkToolAsync;
        return handlers;
    }

    private ValueTask<ConversationToolResult> BackgroundWorkToolAsync(TextToolCall call, CheckInToolContext context, CancellationToken token) =>
        new(conversation is { } live ? live.StartBackgroundWork(call, context.CheckInName)
            : new ConversationToolResult(BackgroundWorkTools.NotStarted("there is no conversation"), true));

    private ValueTask<ConversationToolResult> CharacterToolAsync(TextToolCall call, CheckInToolContext context, CancellationToken token) =>
        new(Dispatcher.InvokeAsync(async () =>
        {
            switch (call.Name)
            {
                case CheckInToolSets.TurnOffEmote:
                    if (CheckInToolSets.Argument(call, "tag")?.Trim('{', '}', ' ') is not { Length: > 0 } tag)
                        return new ConversationToolResult("Say which emote to turn off (tag).", true);
                    var off = await TurnOffReplyEmotesAsync(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                    return off.Count > 0 ? new($"Turned off {off[0]}.")
                        : new($"No lingering emote a reply turned on has the tag {{{(tag.Length > 40 ? tag[..40] : tag)}}}.", true);
                case CheckInToolSets.LookUsual:
                    return avatar.Gaze.BackToUsual("A check-in") ? new("Took the eyes back to their usual gaze.")
                        : new("The eyes already do their usual.");
                default:
                    return new($"This set has no tool called {call.Name}.", true);
            }
        }).Task.Unwrap());

    private ValueTask<ConversationToolResult> NextReplyToolAsync(TextToolCall call, CheckInToolContext context, CancellationToken token) =>
        new(Dispatcher.InvokeAsync(() =>
        {
            if (call.Name is not (CheckInToolSets.RemindNextReply or CheckInToolSets.BringUp))
                return new ConversationToolResult($"This set has no tool called {call.Name}.", true);
            if (CheckInToolSets.Argument(call, "text") is not { } text) return new("Say what in one short line (text).", true);
            if (text.Length > CheckIns.MaximumAnswerCharacters) text = text[..CheckIns.MaximumAnswerCharacters];
            if (call.Name == CheckInToolSets.BringUp)
                return ConversationSession()?.BringUp(context.CheckInName, text) is null
                    ? new("Martlet can't talk now, so it was dropped.", true)
                    : new($"Martlet brings it up as soon as it's free ({text.Length} characters).");
            return PostCheckInNote(context.CheckInId, text, homeSettings?.Prompts) is { } problem
                ? new(problem, true) : new($"A reminder waits for the next reply ({text.Length} characters).");
        }).Task);

    private async ValueTask<ConversationToolResult> ReminderToolAsync(TextToolCall call, CheckInToolContext context, CancellationToken token)
    {
        if (call.Name != Reminders.ToolName || conversation?.RemindersTool is not { } remind)
            return new($"This set has no tool called {call.Name}.", true);
        try
        {
            var outcome = await remind(call.ArgumentsJson, token).ConfigureAwait(false);
            if (outcome.Own is not null) ErrorLog.Info($"Reminders: {Reminders.ToolName} {outcome.Outcome} by the check-in {context.CheckInName}.");
            // The first line (shown on the card) is the outcome only, never a reminder's text.
            return new($"Reminders: {outcome.Outcome}.\n{outcome.Result.Output}", outcome.Result.IsError);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or UnauthorizedAccessException or
            ContractException or InvalidOperationException or TaskCanceledException)
        {
            return new("Reminders: failed.\nReminders can't be changed right now.", true);
        }
    }

    /// <summary>Turns off the lingering emotes a reply turned on whose tag <paramref name="named"/> picks (on the UI thread);
    /// the tags it turned off, in braces.</summary>
    private async Task<List<string>> TurnOffReplyEmotesAsync(Func<string, bool> named)
    {
        var catalog = characterActions.For(avatar.InspectedProfile?.ModelPath);
        var off = new List<string>();
        foreach (var held in avatar.Held.Current.Where(h => h.ByReply))
        {
            var tag = catalog?.Entries.FirstOrDefault(e => e.Source.Id == held.Source.Id).Action?.Tag;
            if (tag is null || !named(tag)) continue;
            try
            {
                if (await avatar.StopActionAsync(held.Source, "a check-in", lifetime.Token)) off.Add("{" + tag + "}");
            }
            catch (Exception error) when (RendererFailures.Is(error, lifetime.Token))
            {
                RendererFailures.Log($"A check-in couldn't turn off {{{tag}}}", error);
            }
        }
        return off;
    }

    /// <summary>Puts a check-in's reminder for the next reply on the context board (on the UI thread); why it couldn't, or null.</summary>
    private string? PostCheckInNote(string checkInId, string text, PromptSettings? prompts)
    {
        if (CheckIns.Note(prompts, text) is not { } note)
            return "the Check-in: reminder for the next reply prompt is empty, so nothing went to the conversation";
        try { contextBoard.Post(CheckIns.Source(checkInId), note, DateTimeOffset.Now, CheckIns.NoteAge, consume: true); }
        catch (InvalidOperationException) { return "the context board is full, so nothing went to the conversation"; }
        return null;
    }
}

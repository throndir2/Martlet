using System.Text.Json;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>Background work as a check-in tool set: think_longer and research, started by a check-in on the Thinking pool after
/// an exchange instead of by the reply. They take the same arguments and keep the same limits as the reply's tools; the think or
/// the research job continues the last exchange's request, and Martlet brings the result up when it is done, as it does for the
/// reply's. The set takes over the reply's think_longer and research (<see cref="CheckInToolSet.Replaces"/>); cancel_thinking
/// stays on the reply.</summary>
public static class BackgroundWorkTools
{
    public const string SetId = "background-work";

    /// <summary>think_longer as a check-in sees it: what it does and when to use it, for a model that reads the exchange after it.</summary>
    public const string ThinkDescription =
        "Starts a background think on the last exchange: Martlet works a task out step by step while the conversation carries on " +
        "and brings the result up when it's done. Use it rarely: only when the user asked for real multi-step reasoning or long " +
        "creative work (song lyrics, a story, a plan, tricky math or code) that the reply couldn't do at once, or the reply said " +
        "it would think it over. Never for chat or quick answers. Give a complete, self-contained task.";

    /// <summary>research as a check-in sees it.</summary>
    public const string ResearchDescription =
        "Starts web research on the last exchange (a few minutes): Martlet searches the web, writes a short report with sources " +
        "and offers it when it's done. Only when the user asked to look something up, search for it or research it, or the reply " +
        "said it would look into it. One at a time.";

    public static CheckInToolSet Set { get; } = new(SetId, "Background work",
        "Starts a long think or web research when the last exchange needs one, so the reply doesn't have to. Martlet brings the " +
        "result up when it's done.",
    [
        new(ThinkLonger.Name, ThinkDescription, ThinkLonger.ParametersJson),
        new(WebResearch.Name, ResearchDescription, WebResearch.ParametersJson)
    ])
    {
        Replaces = [ThinkLonger.Name, WebResearch.Name]
    };

    /// <summary>What the check-in is told when the work started (or waits in line behind <paramref name="queued"/>, or until the
    /// conversation pauses with <paramref name="forConversation"/>). The first line is the job's ID only, never the task.</summary>
    public static string Started(BackgroundJob job, string? queued = null, bool forConversation = false) =>
        (queued is null ? $"Started {job.Id}." : $"{job.Id} waits in line.") + "\n" +
        JsonSerializer.Serialize(new { status = queued is null ? "started" : "queued", id = job.Id,
            time_limit = job.Kind.TimeLimit is { } limit ? BackgroundJobs.Duration(limit) : "none" }) + "\n" +
        (queued is null ? "" : forConversation
            ? "It starts as soon as the conversation pauses. "
            : $"Every computer that thinks is busy ({queued}), so it starts as soon as one is free. ") +
        "Martlet brings the result up when it's done. Don't start the same work again.";

    /// <summary>What the check-in is told when the work didn't start: <paramref name="why"/> on the first line.</summary>
    public static string NotStarted(string why) => "Not started: " + why.Trim().TrimEnd('.') + ".";

    /// <summary>The reply's guidance while this set takes its tools over: tell the user briefly; the work starts after the reply.</summary>
    public const string ReplyGuidance =
        "When a request needs real multi-step thinking or long creative work, or the user asks you to look something up on the " +
        "web, say briefly in character that you'll think it over or look into it and get back to them, and that it may take a " +
        "while. Martlet starts that work after your reply and a note brings you the result; never make it up.";
}

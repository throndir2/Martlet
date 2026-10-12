using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Guides;

/// <summary>What an app's guide says about the user's question, for the notes on the user's message: the best chunks, each with
/// its page and section, inside <see cref="Label"/> lines and opened by Companion › Prompts › App guide, with your message, which
/// marks them as reference text read from the web, never instructions.</summary>
public static class GuideRecall
{
    public const string Label = "[MARTLET_APP_GUIDE]";
    public const string EndLabel = "[/MARTLET_APP_GUIDE]";
    /// <summary>Chunks less sure than this are left out (<see cref="GuideHit.Relevance"/>).</summary>
    public const double MinimumRelevance = 0.5;
    public const int MaximumChunks = 3;
    public const int MaximumCharacters = 2_400;

    /// <summary>The notes for <paramref name="hits"/> of <paramref name="app"/>'s guide, or null when none is sure enough (or
    /// the owner emptied the App guide, with your message prompt).</summary>
    public static string? Notes(string app, IReadOnlyList<GuideHit> hits, int maximumCharacters = MaximumCharacters, PromptSettings? prompts = null)
    {
        var used = hits.Where(h => h.Relevance >= MinimumRelevance).Take(MaximumChunks).ToArray();
        if (used.Length == 0) return null;
        if (PromptSettings.Fill(prompts, PromptCatalog.AppGuideNotes, ("app", app)) is not { } opening) return null;
        var text = new StringBuilder(Label).Append('\n').Append(opening.Trim()).Append('\n');
        var share = Math.Max(200, maximumCharacters / used.Length);
        foreach (var hit in used)
        {
            var where = hit.Chunk.Section.Length > 0 ? hit.Chunk.Page + " › " + hit.Chunk.Section : hit.Chunk.Page;
            var body = hit.Chunk.Text.Length <= share ? hit.Chunk.Text : hit.Chunk.Text[..share].TrimEnd() + "…";
            text.Append("- ").Append(where).Append(": ").Append(body.Replace('\n', ' ')).Append('\n');
        }
        return text.Append(EndLabel).ToString();
    }

    /// <summary>The brief notes: <paramref name="brief"/> of <paramref name="app"/> inside <see cref="GuideBrief.Label"/> lines,
    /// opened by Companion › Prompts › App guide brief, with your message. Null when there is no brief or the owner emptied the
    /// prompt.</summary>
    public static string? BriefNotes(string app, string? brief, PromptSettings? prompts = null)
    {
        if (string.IsNullOrWhiteSpace(brief)) return null;
        if (PromptSettings.Fill(prompts, PromptCatalog.AppGuideBrief, ("app", app)) is not { } opening || opening.Trim().Length == 0) return null;
        return new StringBuilder(GuideBrief.Label).Append('\n').Append(opening.Trim()).Append('\n').Append(brief.Trim()).Append('\n')
            .Append(GuideBrief.EndLabel).ToString();
    }

    /// <summary>The start of a brief as its notes carry it, to tell one the conversation has already.</summary>
    public static string BriefOpening(string brief)
    {
        var line = brief.Trim().Split('\n')[0];
        return line.Length <= 60 ? line : line[..60];
    }
}

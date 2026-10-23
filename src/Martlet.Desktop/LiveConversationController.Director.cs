using System.IO;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What one reply to the user's own words sent besides the conversation (<see cref="LiveConversationController.LastReplyPrompt"/>):
/// the skills it carried, by key, its instructions' estimated tokens, how many tools and their definitions' estimated tokens,
/// whether the director had the other tools, and when.</summary>
internal sealed record ReplyPromptRecord(IReadOnlyList<string> Skills, int InstructionTokens, int Tools, int ToolTokens, bool Director,
    DateTimeOffset At);

// The director's tools on this PC (docs/CONVERSATION.md#the-director): the tool sets it takes over from the reply, run for its
// Thinking pool member's calls (MainWindow.CheckInTools.cs gives them to the check-in host), and what it keeps for later turns.
internal sealed partial class LiveConversationController
{
    /// <summary>Whether the director's Past conversations set runs here: while the owner lets Martlet search the record.</summary>
    internal bool SearchesPastConversations => Configuration is { } configured && history?.Searchable(configured.Memory) == true;

    /// <summary>Whether the director's App guides set runs here: while App guides are on, or research reports can be searched.</summary>
    internal bool RunsGuideTools => Configuration is { } configured && (Guides is { On: true } || OffersReportSearch(configured));

    /// <summary>Whether this PC has tools of its own for the director (an MCP server besides Home Assistant's, or the terminal).</summary>
    internal bool HasPcTools => tools is { } service && (service.HasEnabledServers || service.Terminal is { Enabled: true });

    /// <summary>What the last reply to the user's own words sent besides the conversation: the skills it carried
    /// (<see cref="Martlet.Core.Settings.SkillSettings.In"/>), its instructions' and tools' estimated tokens and when. Null before
    /// the first reply. Companion › Skills and Prompts show it.</summary>
    internal ReplyPromptRecord? LastReplyPrompt => Volatile.Read(ref lastReplyPrompt);

    private ReplyPromptRecord? lastReplyPrompt;

    /// <summary>Raised (off the UI thread) once a reply's <see cref="LastReplyPrompt"/> is known.</summary>
    internal event Action? ReplyPromptKept;

    // Off the reply's path: what the reply carried, kept and logged.
    private void KeepReplyPrompt(RequestParts parts, IReadOnlyList<TextToolDefinition> tools, Martlet.Core.Settings.PromptSettings? prompts,
        bool director)
    {
        var instructions = string.Join("\n\n", parts.Instructions.Select(p => p.Text));
        var all = string.Join("\n\n", parts.Instructions.Concat(parts.Notes).Concat(parts.Context).Select(p => p.Text));
        var skills = Martlet.Core.Settings.SkillSettings.In(prompts, all);
        var record = new ReplyPromptRecord(skills, BoundedTextInput.TextTokens(instructions), tools.Count, tools.Sum(d => d.Utf8Bytes) / 3,
            director, clock.GetUtcNow());
        Volatile.Write(ref lastReplyPrompt, record);
        ErrorLog.Info($"Reply prompt: about {record.InstructionTokens} tokens of instructions and {record.Tools} tools (about " +
            $"{record.ToolTokens} tokens of definitions){(director ? "; the director has the other tools" : "")}; skills: " +
            $"{(skills.Count == 0 ? "none" : string.Join(", ", skills))}.");
        ReplyPromptKept?.Invoke();
    }

    /// <summary>search_conversations for the director: the same search as the reply's, in the record of earlier conversations.</summary>
    internal async ValueTask<ConversationToolResult> DirectorSearchConversationsAsync(TextToolCall call, CancellationToken token)
    {
        if (history is null || Configuration is not { } configured || !history.Searchable(configured.Memory))
            return new("Searching past conversations is off (Companion › Memory).", true);
        var (result, outcome) = await history.SearchAsync(call, ConversationId, token, configured.CharacterName).ConfigureAwait(false);
        tools?.Record("Director", PastConversations.ToolName, outcome, ConversationHistory.Preview(call.ArgumentsJson, 120), result.IsError);
        // The first line (on the card) says only how it went.
        return new($"Past conversations: {outcome}.\n{result.Output}", result.IsError);
    }

    /// <summary>The App guides tools for the director: read_up_on, search_guide, skip_guide and update_guide_brief, as the reply runs
    /// them.</summary>
    internal async ValueTask<ConversationToolResult> DirectorGuideToolAsync(TextToolCall call, CancellationToken token) => call.Name switch
    {
        AppGuideTools.ReadUpName => ReadUpOn(null, call),
        AppGuideTools.SearchName => await SearchGuideAsync(null, call, token).ConfigureAwait(false),
        AppGuideTools.SkipName => await SkipGuideAsync(call, token).ConfigureAwait(false),
        AppGuideTools.BriefName => UpdateGuideBrief(call),
        _ => new($"This set has no tool called {call.Name}.", true)
    };

    /// <summary>list_pc_tools and call_pc_tool for the director: the MCP servers' tools and the terminal of this PC (without Home
    /// Assistant's, which the reply keeps), with the same approvals as the reply's (Companion › Tools).</summary>
    internal async ValueTask<ConversationToolResult> DirectorPcToolAsync(TextToolCall call, CancellationToken token)
    {
        if (tools?.PcTools() is not { } toolset) return new("This PC has no tool on now (Companion › Tools).", true);
        switch (call.Name)
        {
            case PcTools.ListName:
                return PcTools.List(toolset.Definitions);
            case PcTools.CallName:
                var (inner, problem) = PcTools.Parse(call);
                if (inner is null) return new(problem!, true);
                var result = await toolset.CallAsync(inner, token).ConfigureAwait(false);
                // The first line (on the card) names the tool and how it went, never what it returned.
                return new($"{inner.Name}: {(result.IsError ? "failed" : "done")}.\n{result.Output}", result.IsError);
            default:
                return new($"This set has no tool called {call.Name}.", true);
        }
    }

    /// <summary>search_knowledge for the director (docs/CONVERSATION.md#the-director): what Martlet already keeps on this PC, with
    /// no model call and no web request: every app guide (the Looked up guide too), the remembered facts while memory is on and
    /// the lorebook entries whose keywords the query names. At most <see cref="KnowledgeTools.MaximumHits"/>, best first.</summary>
    internal async ValueTask<ConversationToolResult> SearchKnowledgeAsync(TextToolCall call, CancellationToken token)
    {
        if (CheckInToolSets.Argument(call, "query") is not { } query) return new("Say what to search for (query).", true);
        if (query.Length > 500) query = query[..500];
        var hits = new List<KnowledgeTools.Hit>();
        if (Guides is { On: true } guides)
            foreach (var app in guides.Library.Apps.Where(a => a.BuiltAt is not null))
            {
                // An index that isn't loaded yet starts loading and is searched by the next call.
                if (guides.Search(app.Key, query, 2) is not { } found) continue;
                hits.AddRange(found.Where(h => h.Relevance >= 0.3).Select(h => new KnowledgeTools.Hit(
                    $"guide: {app.Name}{(h.Chunk.Section.Length > 0 ? " › " + h.Chunk.Section : "")}", h.Chunk.Text, h.Relevance)));
            }
        if (memory is not null && Configuration?.Memory is { Enabled: true } remembered)
        {
            try
            {
                var recalled = await memory.RecallAsync(remembered, query, 4, new HashSet<string>(StringComparer.Ordinal), token, Activity()).ConfigureAwait(false);
                // Facts come best first; they rank after a close guide section, before a loose one.
                hits.AddRange(recalled.Facts.Select((f, i) => new KnowledgeTools.Hit("memory", f.Content, 0.6 - i * 0.05)));
            }
            catch (Exception error) when (error is DesktopMemoryException or MemoryException or ContractException or IOException or
                UnauthorizedAccessException or ObjectDisposedException) { }
        }
        if (lorebooks is not null)
        {
            var loaded = await lorebooks.LoadAsync(token).ConfigureAwait(false);
            if (loaded.Loaded)
            {
                var persona = Configuration?.Persona;
                var lore = LorebookScanner.Scan(loaded.Library, new(query, [], persona?.Id, persona?.Name), _ => 0);
                hits.AddRange(lore.Included.Take(4).Select(h => new KnowledgeTools.Hit("lorebook: " + h.BookName, h.Content, 0.55)));
            }
        }
        var answer = KnowledgeTools.Answer(hits);
        tools?.Record("Director", KnowledgeTools.SearchName, answer.Output.Split('\n', 2)[0], "", false);
        return answer;
    }

    /// <summary>Keeps the pages a look-up or research read in the Looked up guide (App guides), in the background, so later turns
    /// find them (search_knowledge, search_guide). Only while App guides are on; a page with nothing readable is left out.</summary>
    private void KeepLookedUp(IReadOnlyList<WebPage> pages, string jobId)
    {
        if (pages.Count == 0 || Guides is not { On: true } guides) return;
        var read = pages.Select(p => new GuidePage(p.Url, p.Title, p.Text, p.Bytes)).ToArray();
        Task.Run(async () =>
        {
            var problem = await guides.KeepLookedUpAsync(read).ConfigureAwait(false);
            ErrorLog.Info(problem is null
                ? $"App guides: kept the {read.Length} page{(read.Length == 1 ? "" : "s")} {jobId} read in the Looked up guide."
                : $"App guides: didn't keep the pages {jobId} read ({problem}).");
        }).Forget();
    }
}

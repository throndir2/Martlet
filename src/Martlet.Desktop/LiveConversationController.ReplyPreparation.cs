using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed partial class LiveConversationController
{
    /// <summary>What a reply to the user carries besides the conversation (docs/CONVERSATION.md): <see cref="PrepareAsync"/> reads the
    /// memory facts and lorebook entries for it (at the same time), takes the finished background work it brings up, prepares its
    /// tools, asks Home Assistant's Assist, and reads earlier conversations, the app guide and the memory brief. It never waits for
    /// anything a reply doesn't need (docs/VOICE_LATENCY.md).</summary>
    private sealed class ReplyPreparation(LiveConversationController owner)
    {
        /// <summary>The reply's preparation for <paramref name="operation"/>, or null when a reply started early must go (Home
        /// Assistant's Assist would answer it).</summary>
        internal async Task<PreparedReply?> PrepareAsync(LiveConversationOperation operation, BoundedTextInput? input, bool straight,
            IReadOnlyList<TextHistoryMessage> history, IReadOnlyList<TextHistoryMessage> sentHistory, PersonaProfile? persona,
            Guid conversation, CancellationToken worker)
        {
            var early = operation.Early;
            // Smart home's list of locks, doors and garages is read again in the background when it is old: the reply never waits
            // for Home Assistant. Until a list is known, its tool calls that name a device ask first (SmartHome.Policy).
            owner.smartHome?.RefreshSafetyLater();
            operation.LatencyTimeline?.Mark("preparing");

            // What the PC played is never the user: memory, tools and Home Assistant only go by the user's own words, and a
            // message that is only what the PC played gets none of them. A report of background work has no words of the user's.
            // A message that went straight to Thinking has no words yet: memory recalls by the conversation so far (and the
            // speaker), tools and finished background work go with it, and what needs its words (Assist, earlier conversations,
            // lorebook keywords in it) waits for the next message.
            var own = operation.OnItsOwn || straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText;
            operation.Asked = own;
            DesktopMemoryRecall? memoryResult = null;
            // Memory and the lorebooks are read at the same time: neither needs the other.
            var recalling = default(Task<DesktopMemoryRecall?>);
            if (operation.MemoryRequested && (own ?? (straight ? StraightRecallQuery(history) : null)) is { } query)
            {
                operation.Publish(new("memory.recalling"));
                recalling = RecallAsync(operation, query, worker);
            }
            var scanning = ScanLoreAsync(operation, straight ? "" : input!.UserText, history, persona, worker);
            if (recalling is not null)
            {
                try
                {
                    memoryResult = await recalling.ConfigureAwait(false);
                    operation.Authorization.Check(worker);
                    await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
                }
                catch
                {
                    scanning.Forget();
                    throw;
                }
                operation.MemoryStoreRevision = memoryResult?.StoreRevision;
                operation.LatencyTimeline?.Mark("memory");
            }
            var lore = await scanning.ConfigureAwait(false);
            if (lore is not null) operation.LatencyTimeline?.Mark("lore");

            // Tools from MCP servers on this PC, the terminal when it is on and Martlet's own (think_longer while Thinking longer
            // is on and Deep thinking can run where it is set to think, search_conversations while it is allowed, list_creations
            // and perform_creation while any kind of creation is registered), only for the user's own turns (and replies that bring
            // up background work: Martlet's reports, and what this PC played when finished work goes with it) and routes that do
            // function calling. While they are on they are always offered, the same
            // way, so every request starts the same.
            DesktopToolset? toolset = null;
            var configured = operation.Authorization.Configuration;
            // Finished background work that wasn't brought up yet goes with what the user says (its notes), and with what this PC
            // played when Martlet may bring it up on its own.
            if (!operation.Report && operation.Delivery is null)
            {
                if (own is not null || straight) operation.Delivery = owner.TakeJobs(onItsOwn: false);
                // A due reminder comes up as soon as Martlet is free even when other finished work waits for the user's next message.
                else if (operation.BringUp)
                    operation.Delivery = owner.TakeJobs(onItsOwn: true, noticesOnly: configured.ThinkLonger.When != ThinkDelivery.WhenFree);
            }
            var builtIns = owner.conversationTools.BuiltIns(operation, configured, conversation);
            // A message carrying finished work gets the tools a report gets, so a later tool can act on the user's yes.
            if ((own is not null || straight || operation.OnItsOwn || operation.Delivery is not null) && owner.tools is not null &&
                (owner.tools.HasTools || builtIns is not null) && configured.SupportsTools && !owner.tools.IsUnsupported(configured.ToolModelKey()))
            {
                operation.Publish(new("tools.preparing"));
                // Never waits: servers still starting offer the tools they listed last (McpToolService.Prepare). While the director
                // has the PC's tools, only Home Assistant's own stay on the reply.
                toolset = owner.tools.Prepare(builtIns, pcTools: !owner.HandedOffReplyTools(configured).Contains(PcTools.ServersName));
                operation.Authorization.Check(worker);
                operation.Toolset = toolset;
                operation.LatencyTimeline?.Mark("tools");
            }

            // Where the last song stopped and why (or that it ended) goes at the end of the conversation once, with the next
            // message Martlet answers.
            var songNote = owner.singing?.PendingNote;

            // Only the user's own typed or spoken words ever reach Home Assistant (glances use RunCommentaryAsync). When the
            // reply is offered Home Assistant's own tools, the model acts through them instead of Assist, so nothing runs twice.
            // Assist needs the words, so a message that went straight to Thinking gets Home Assistant only through its tools.
            HomeTurn? home = null;
            if ((own is not null || straight) && owner.smartHome is { ControlEnabled: true } house)
            {
                if (house.ModelToolsEnabled && toolset?.Servers.Contains(SmartHome.ServerName) == true &&
                    owner.tools!.ManagedConflicts.All(s => s.Name != SmartHome.ServerName))
                    home = house.ToolsTurn(configured.Prompts);
                else if (own is not null)
                {
                    // Assist acts on the house as it answers: a reply started early never asks it before it is taken.
                    if (early is not null && owner.LetGoEarly(operation, EarlyReplyRecord.Refused, "Home Assistant's Assist answers it"))
                        return null;
                    operation.Publish(new("home.asking"));
                    home = await house.HandleAsync(own, worker, configured.Prompts).ConfigureAwait(false);
                    operation.Authorization.Check(worker);
                    operation.LatencyTimeline?.Mark("Home Assistant");
                }
                if (home is not null)
                {
                    operation.HomeSummary = home.Summary;
                    operation.Publish(new(home.Code));
                }
            }

            // A message that refers to an earlier conversation brings back what was said then (Companion › Memory › Conversation
            // history) in its notes, read from memory only. Any other message gets nothing, so its request is what it always was.
            string? past = null;
            var pastCount = 0;
            if (own is not null && owner.history is { } pastRecord && pastRecord.Active(configured.Memory))
            {
                past = pastRecord.RecallNotes(own, conversation, sentHistory, configured.Prompts, out pastCount, configured.CharacterName);
                if (past is not null) operation.LatencyTimeline?.Mark("past conversations");
            }

            // While the user's words name an app Martlet read up on, or that app is in front, its guide's best sections go in the
            // notes (Companion › App guides), read from memory only: a guide whose index isn't ready yet is never waited for. Any
            // other message gets nothing, so its request is what it always was. "Read up on X" while this reply can't call
            // read_up_on starts reading up here, and the notes say so.
            string? guide = null;
            if (own is not null && owner.Guides is { On: true } guides)
            {
                guide = owner.GuideNotes(own, sentHistory, configured.Prompts, out var guideSections, out var guideKey);
                operation.GuideSections = guideSections;
                if (guideKey is not null) owner.UsedGuide(operation, guideKey);
                if (owner.ReadUpAsked(guides, own, toolset) is { } reading) guide = guide is null ? reading : guide + "\n\n" + reading;
                operation.LatencyTimeline?.Mark("app guide");
            }

            // The memory brief goes with the first message of each conversation (not again while the conversation sent carries
            // it), from memory only: nothing is read or waited for here.
            var brief = owner.MemoryBriefNotes(operation, sentHistory, configured.Prompts);
            return new(own, memoryResult, lore, configured, builtIns, toolset, songNote, home, past, pastCount, guide, brief);
        }

        // What memory recalls by for a message that has no words yet: the user's last message in the conversation (without what the
        // PC played), or nothing (then the speaker's and the newest facts).
        internal static string StraightRecallQuery(IReadOnlyList<TextHistoryMessage> history) =>
            LiveConversationConfiguration.WithoutMarked(history.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text) ?? "";

        // Lorebooks help but are never required: if lorebooks.json can't be used right now, the reply goes ahead without lore.
        internal async Task<LorebookScanResult?> ScanLoreAsync(LiveConversationOperation operation, string current,
            IReadOnlyList<TextHistoryMessage> history, PersonaProfile? persona, CancellationToken worker)
        {
            if (owner.lorebooks is null) return null;
            var loaded = await owner.lorebooks.LoadAsync(worker).ConfigureAwait(false);
            operation.Authorization.Check(worker);
            if (!loaded.Loaded)
            {
                operation.LoreProblem = loaded.Error;
                operation.Publish(new("lorebook.unavailable"));
                return null;
            }
            // While a game is played its name is scanned too, so lorebook entries for that game (keyed by its name) join in.
            var activity = owner.Activity();
            var result = LorebookScanner.Scan(loaded.Library,
                new(current, history.Select(message => message.Text).ToArray(), persona?.Id, persona?.Name,
                    MemoryActivity.Kind(activity) == MemoryActivity.Gaming ? MemoryActivity.Name(activity) : null), Random.Shared.Next);
            return result.Included.Count == 0 && result.OverBudget.Count == 0 ? null : result;
        }

        internal static void RecordLore(LiveConversationOperation operation, LorebookScanResult? lore, int used)
        {
            operation.LoreEntriesUsed = used;
            operation.LoreEntriesOmitted = (lore?.Included.Count ?? 0) - used + (lore?.OverBudget.Count ?? 0);
            operation.LoreTitles = lore?.Included.Take(used).Select(hit => hit.Entry.Label).ToArray() ?? [];
        }

        // Memory helps but is never required: if the store can't be read right now, the reply goes ahead without it. The facts of the
        // person speaking (and those about no one in particular) fill the recall before other people's, and each fact says whose it is.
        internal async Task<DesktopMemoryRecall?> RecallAsync(LiveConversationOperation operation, string query, CancellationToken worker)
        {
            var roster = owner.voices?.Roster;
            var speaker = MemoryPeople.Ids(operation.Heard?.Speaker?.Voice, roster);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var recalled = await owner.memory!.RecallAsync(operation.Authorization.Configuration.Memory!, query,
                        DesktopMemoryService.MaximumRecalledFacts, speaker, worker, owner.Activity()).ConfigureAwait(false);
                    return recalled with { People = MemoryPeople.Labels(recalled.Facts, roster) };
                }
                catch (DesktopMemoryException error) when (error.Code == "memory.retrieval_invalidated" && attempt == 0)
                {
                    // A fact changed while it was read; read the current facts once more unless this action was revoked.
                    operation.Authorization.Check(worker);
                }
                catch (Exception error) when (error is DesktopMemoryException or MemoryException or ContractException or
                    IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    operation.Authorization.Check(worker);
                    operation.MemoryProblem = error switch
                    {
                        DesktopMemoryException app => app.Code,
                        MemoryException store => "memory." + store.Failure,
                        _ => "memory.unavailable"
                    };
                    operation.Publish(new("memory.unavailable"));
                    return null;
                }
            }
        }
    }

    /// <summary>What <see cref="ReplyPreparation.PrepareAsync"/> read for one reply: the user's own words (<paramref name="Own"/>),
    /// the recalled memory, the lorebook entries, Martlet's own tools and the toolset, the note about the last song, Home Assistant's
    /// turn, earlier conversations, the app guide's sections and the memory brief.</summary>
    private sealed record PreparedReply(string? Own, DesktopMemoryRecall? Memory, LorebookScanResult? Lore,
        LiveConversationConfiguration Configured, BuiltInTools? BuiltIns, DesktopToolset? Toolset, string? SongNote, HomeTurn? Home,
        string? Past, int PastCount, string? Guide, string? Brief);
}

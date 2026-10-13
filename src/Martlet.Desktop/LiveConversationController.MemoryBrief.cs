using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

// The memory brief (docs/MEMORY.md, "The memory brief"): a short profile of the people Martlet talks with, written in the
// background from the remembered facts and sent with the first message of each conversation. Writing it is a helper job on the
// Thinking pool at the lowest priority; without a pool member it runs on the conversation's Thinking model only when that model
// is in the cloud and the conversation is quiet, so it never pushes the conversation out of a local model's prompt cache.
internal sealed partial class LiveConversationController
{
    private MemoryBriefService? memoryBrief;

    /// <summary>The memory brief, or null without memory or a data directory (tests).</summary>
    internal MemoryBriefService? MemoryBrief => Volatile.Read(ref memoryBrief);

    private void StartMemoryBrief()
    {
        if (memory is null || dataDirectory is null) return;
        var brief = new MemoryBriefService(dataDirectory, BriefFactsAsync, WriteBriefAsync,
            () => Replying || ReplySpeaking() || floor.Level != LiveFloorLevel.Idle) { Log = ErrorLog.Info };
        memory.FactsChanged += brief.FactsChanged;
        Volatile.Write(ref memoryBrief, brief);
    }

    private void StopMemoryBrief()
    {
        if (Interlocked.Exchange(ref memoryBrief, null) is not { } brief) return;
        if (memory is not null) memory.FactsChanged -= brief.FactsChanged;
        brief.Dispose();
    }

    /// <summary>The brief's notes for a message, from memory only (never read or waited for): with the first message of each
    /// conversation, while memory is on, never in a Discord call (other people are in it).</summary>
    private string? MemoryBriefNotes(LiveConversationOperation operation, IReadOnlyList<TextHistoryMessage> sent, PromptSettings? prompts) =>
        operation.MemoryRequested && !operation.DiscordCall ? MemoryBrief?.Notes(sent, prompts) : null;

    /// <summary>The facts the brief is written from: every fact of the spaces recall reads, with the name of each person they
    /// belong to. Read off the reply's path, while the conversation is quiet.</summary>
    private async Task<MemoryBriefFacts?> BriefFactsAsync(CancellationToken token)
    {
        if (memory is not { } service) return null;
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null || loaded.Settings?.Memory is not { Enabled: true } memorySettings)
            return null;
        try
        {
            var (facts, complete) = await service.UseSpaceAsync(memorySettings.ConfigurationRevision, null, false, async (set, _, store, operationToken) =>
            {
                var here = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts;
                var loadedSpaces = service.Snapshots.Select(s => s.Space).ToHashSet(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<MemoryFact> all = [.. here, .. service.OtherFacts(set).Select(f => f.Fact)];
                return (all, set.Others.All(space => loadedSpaces.Contains(space.Id)));
            }, token).ConfigureAwait(false);
            var roster = voices?.Roster;
            var people = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in facts.Select(f => f.VoiceId).OfType<string>().Distinct(StringComparer.Ordinal))
                if (roster?.Resolve(id) is { Removed: false } voice) people[id] = MemoryPeople.Label(voice);
            var owner = voices?.Yours.Where(v => v.Named).Select(MemoryPeople.Label).ToArray() ?? [];
            return new(facts, people, owner, loaded.Settings.Prompts, complete);
        }
        catch (Exception error) when (error is DesktopMemoryException or MemoryException or ContractException or IOException or
            UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Writes the brief: on a free Thinking pool member, else on the conversation's Thinking model once nothing is
    /// replying or speaking and you aren't talking, but only when that model is in the cloud (a model on one of your computers
    /// keeps its prompt cache for the conversation).</summary>
    private async Task<(string? Answer, string? Failure)> WriteBriefAsync(string instructions, string text, CancellationToken token)
    {
        var (answer, failure, _) = await helpers.RunAsync(HelperJobKind.MemoryBrief, MemoryBriefWriter.Purpose, HelperCapability.Text,
            () => new BoundedTextInput(text, instructions), async worker =>
            {
                var loaded = await settings.LoadAsync(worker).ConfigureAwait(false);
                if (LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory)) is not { } configured)
                    return (null, "Thinking isn't set up yet");
                if (configured.LocalThinking || configured.Routes.ThinkingRoute()?.RouteType == SetupRouteType.GatewayOllama)
                    return (null, "no Thinking pool member can take it, and the Thinking model on your computers keeps its memory for the conversation. " +
                        "Add one in Companion › Thinking pool");
                return await AskThinkingAsync(MemoryBriefWriter.Purpose, instructions, text, worker).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        return (answer, failure);
    }
}

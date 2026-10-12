using System.IO;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Memory;

namespace Martlet.Desktop;

// Finding by meaning (docs/SEARCH.md, "Finding by meaning"): an embedding model, used only away from the reply's first word: when
// remembering and tidying memory find related facts, in the memory find tools and in search_guide. Automatic recall, guide notes
// and past-conversation recall on the reply's path never wait for it.
internal sealed partial class LiveConversationController
{
    private static readonly TimeSpan MeaningAfterStart = TimeSpan.FromSeconds(30);
    private MeaningSearch? meaning;
    private int meaningGuidesRevision = -1;

    /// <summary>Finding by meaning, or null without a data directory (tests).</summary>
    internal MeaningSearch? Meaning => Volatile.Read(ref meaning);

    private void StartMeaningSearch()
    {
        if (dataDirectory is null) return;
        var search = new MeaningSearch(dataDirectory, () => Replying || ReplySpeaking() || floor.Level != LiveFloorLevel.Idle) { Log = ErrorLog.Info };
        Volatile.Write(ref meaning, search);
        if (memory is not null) memory.FactsChanged += SyncMeaningMemory;
        Task.Delay(MeaningAfterStart).ContinueWith(_ => SyncMeaningMemory(), TaskScheduler.Default).Forget();
    }

    private void StopMeaningSearch()
    {
        if (Interlocked.Exchange(ref meaning, null) is not { } search) return;
        if (memory is not null) memory.FactsChanged -= SyncMeaningMemory;
        search.Dispose();
    }

    /// <summary>Brings memory's vectors in step with the facts, in the background (vectors of forgotten facts are dropped).</summary>
    internal void SyncMeaningMemory()
    {
        if (Meaning is not { } search || memory is not { } service || disposed) return;
        search.SyncMemory(async token =>
        {
            var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
            if (loaded.Settings?.Memory is not { Enabled: true } memorySettings)
            {
                search.ForgetMemory();
                return null;
            }
            try
            {
                return await service.UseSpaceAsync(memorySettings.ConfigurationRevision, null, false, async (set, _, store, operationToken) =>
                {
                    var here = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts;
                    return (IReadOnlyList<MemoryFact>)[.. here, .. service.OtherFacts(set).Select(f => f.Fact)];
                }, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is DesktopMemoryException or MemoryException or ContractException or IOException or
                UnauthorizedAccessException or ObjectDisposedException)
            {
                return null;
            }
        });
    }

    /// <summary>The facts an embedding search finds for <paramref name="text"/> (best first), or null when finding by meaning
    /// can't be used now. Never on the reply's first word: remembering after a reply, and the memory find tools.</summary>
    private async Task<IReadOnlyList<Guid>?> MeaningFactsAsync(string text, CancellationToken token)
    {
        if (Meaning is not { } search || await search.QueryAsync(text, token).ConfigureAwait(false) is not { } vector) return null;
        if (search.MemoryVectors == 0) SyncMeaningMemory();
        var found = search.Facts(vector);
        return found.Count == 0 ? null : found;
    }

    /// <summary>The facts close enough in meaning to <paramref name="fact"/> that tidying memory groups them with it (they may
    /// say the same thing or contradict it); null while finding by meaning isn't ready.</summary>
    private Func<MemoryFact, IReadOnlyCollection<Guid>>? MeaningNeighbors() =>
        Meaning is { Ready: true, MemoryVectors: > 0 } search ? fact => search.Neighbors(fact.Id, MemoryUpkeep.MaximumGroupSize - 1) : null;

    /// <summary>Deletes the kept vectors of guides that were deleted (when the guide library changed).</summary>
    private void KeepMeaningGuides()
    {
        if (Meaning is not { } search || Guides is not { Loaded: true } guides) return;
        var revision = guides.Revision;
        if (Interlocked.Exchange(ref meaningGuidesRevision, revision) == revision) return;
        search.KeepGuides(guides.Library.Apps.Where(a => a.BuiltAt is not null).Select(a => a.Key).ToHashSet(StringComparer.Ordinal));
    }
}

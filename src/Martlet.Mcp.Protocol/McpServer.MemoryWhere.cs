using System.Text.Json;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>memory_where: sets where remembered facts matter (docs/MEMORY.md, "Memories for a game"), as Companion › Memory's
/// Where it matters does: everywhere, any game or one game. Like memory_forget it waits for a running Martlet to let the store go;
/// the memory sync then carries the change to every computer.</summary>
internal sealed partial class McpServer
{
    private async Task<object> MemoryWhereAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var contains = OptionalString(arguments, "contains")?.Trim();
        if (contains is { Length: 0 }) contains = null;
        if (contains is { Length: > MaximumForgetText })
            throw new ArgumentException($"contains is at most {MaximumForgetText} characters.");
        var ids = ForgetIds(arguments);
        if (contains is null && ids.Count == 0) throw new ArgumentException("Give contains (text in the fact) or ids (fact IDs).");
        var where = OptionalString(arguments, "where")?.Trim() ?? throw new ArgumentException("Give where: everywhere, games or a game's name.");
        var activity = where.ToLowerInvariant() switch
        {
            "everywhere" or "global" => null,
            "games" or "gaming" or "any game" => MemoryActivity.Gaming,
            _ => MemoryActivity.Game(where) ?? throw new ArgumentException("where must be everywhere, games or a game's name.")
        };
        var space = OptionalString(arguments, "space")?.Trim();
        var preview = OptionalBool(arguments, "preview") ?? false;
        if (!preview && !allowChanges)
            throw new InvalidOperationException("memory_where changes facts, so this server must start with --allow-changes " +
                "(or pass preview: true to only count what it would change).");

        var stores = await MemoryStoreFoldersAsync(directory, cancellation);
        if (space is not null)
        {
            stores = [.. stores.Where(s => string.Equals(s.Space, space, StringComparison.Ordinal))];
            if (stores.Count == 0) throw new ArgumentException($"No memory store for space '{space}' in this data folder (memory_status lists them).");
        }

        var results = new List<object>();
        int changedTotal = 0, matchedTotal = 0;
        foreach (var (name, folder) in stores)
        {
            using var store = await OpenFreeStoreAsync(folder, cancellation);
            // History (replaced facts) is never changed; a fact already there is left as it is.
            var facts = (await store.InspectAsync(cancellation)).Facts.Where(f => !f.IsHistory).ToArray();
            var matched = facts.Where(f => ids.Contains(f.Id) ||
                (contains is not null && f.Content.Contains(contains, StringComparison.OrdinalIgnoreCase))).ToArray();
            var changed = 0;
            foreach (var fact in matched.Where(f => !string.Equals(f.Activity, activity, StringComparison.Ordinal)))
            {
                if (preview) { changed++; continue; }
                await store.EditAsync(new()
                {
                    Id = fact.Id, ExpectedRevision = fact.Revision, Content = fact.Content,
                    Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), DateTimeOffset.UtcNow), Retention = fact.Retention,
                    VoiceId = fact.VoiceId, SearchWords = fact.SearchWords, SetActivity = true, Activity = activity
                }, cancellation);
                changed++;
            }
            changedTotal += changed;
            matchedTotal += matched.Length;
            results.Add(new { space = name, facts = facts.Length, matched = matched.Length, changed, ids = matched.Select(f => f.Id).ToArray() });
        }
        return new
        {
            preview,
            where = MemoryActivity.Describe(activity),
            matched = matchedTotal,
            changed = preview ? 0 : changedTotal,
            wouldChange = preview ? changedTotal : (int?)null,
            spaces = results,
            followUp = preview ? "Nothing was changed. Call again without preview (and with --allow-changes) to change them."
                : changedTotal > 0 ? "Recall brings these facts up only where they matter now; memory_status counts the facts by where they matter."
                : "Nothing needed a change."
        };
    }
}

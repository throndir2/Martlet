using System.IO;
using System.Text.Json;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>memory_forget: deletes remembered facts from a data directory's memory stores, the same way Companion › Memory's
/// Delete does (Martlet.Memory's DeleteManyAsync). A running Martlet opens a store only for each operation, so this waits for it
/// to be free; the memory sync then sees the facts gone and forgets them on every computer.</summary>
internal sealed partial class McpServer
{
    private const int MaximumForgetIds = 1_000;
    private const int MaximumForgetText = 200;
    private static readonly TimeSpan ForgetStoreWait = TimeSpan.FromSeconds(15);

    private async Task<object> MemoryForgetAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var contains = OptionalString(arguments, "contains")?.Trim();
        if (contains is { Length: 0 }) contains = null;
        if (contains is { Length: > MaximumForgetText })
            throw new ArgumentException($"contains is at most {MaximumForgetText} characters.");
        var ids = ForgetIds(arguments);
        if (contains is null && ids.Count == 0) throw new ArgumentException("Give contains (text in the fact) or ids (fact IDs).");
        var space = OptionalString(arguments, "space")?.Trim();
        var preview = OptionalBool(arguments, "preview") ?? false;
        if (!preview && !allowChanges)
            throw new InvalidOperationException("memory_forget deletes facts, so this server must start with --allow-changes " +
                "(or pass preview: true to only count what it would delete).");

        var stores = await MemoryStoreFoldersAsync(directory, cancellation);
        if (space is not null)
        {
            stores = [.. stores.Where(s => string.Equals(s.Space, space, StringComparison.Ordinal))];
            if (stores.Count == 0) throw new ArgumentException($"No memory store for space '{space}' in this data folder (memory_status lists them).");
        }

        var results = new List<object>();
        int total = 0, matchedTotal = 0;
        foreach (var (name, folder) in stores)
        {
            using var store = await OpenFreeStoreAsync(folder, cancellation);
            var facts = (await store.InspectAsync(cancellation)).Facts;
            var matched = facts.Where(f => ids.Contains(f.Id) ||
                (contains is not null && f.Content.Contains(contains, StringComparison.OrdinalIgnoreCase))).ToArray();
            var deleted = 0;
            if (!preview && matched.Length > 0)
                deleted = (await store.DeleteManyAsync([.. matched.Select(f => new DeleteFactRequest
                {
                    Id = f.Id, ExpectedRevision = f.Revision, ConsentId = Guid.NewGuid()
                })], cancellation)).DeletedFacts;
            total += deleted;
            matchedTotal += matched.Length;
            results.Add(new { space = name, facts = facts.Count, matched = matched.Length, deleted, ids = matched.Select(f => f.Id).ToArray() });
        }
        return new
        {
            preview,
            matched = matchedTotal,
            deleted = total,
            spaces = results,
            followUp = preview ? "Nothing was deleted. Call again without preview (and with --allow-changes) to delete them."
                : total > 0 ? "A running Martlet with \"Keep Martlet the same on all my computers\" on forgets them on your other computers " +
                    "at its next memory sync (within about 30 seconds); memory_sync_status shows the forgotten count."
                : "Nothing matched, so nothing was deleted."
        };
    }

    private static HashSet<Guid> ForgetIds(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("ids", out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumForgetIds)
            throw new ArgumentException($"ids must be an array of at most {MaximumForgetIds} fact IDs.");
        return [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id) &&
            id != Guid.Empty ? id : throw new ArgumentException("Each of ids must be a fact ID (a GUID)."))];
    }

    /// <summary>The memory stores kept in a data folder, as memory_status finds them: each account's own space, the household and
    /// character spaces, and the store in the memory setting's folder (memories from before accounts, or a custom folder).</summary>
    private static async Task<List<(string Space, string Folder)>> MemoryStoreFoldersAsync(string directory, CancellationToken cancellation)
    {
        var found = new List<(string Space, string Folder)>();
        bool Has(string folder) => File.Exists(Path.Combine(folder, Martlet.Core.Sync.MemorySpaceFolders.StoreFileName));
        var accounts = Path.Combine(directory, Martlet.Core.Sync.MemorySpaceFolders.AccountsFolder);
        if (Directory.Exists(accounts))
            foreach (var account in Directory.EnumerateDirectories(accounts).Order(StringComparer.Ordinal))
            {
                var space = Martlet.Core.Sync.MemorySpaceId.AccountPrefix + Path.GetFileName(account);
                var folder = Path.Combine(account, Martlet.Core.Settings.MemorySettings.AppLocalDirectoryName);
                if (Martlet.Core.Sync.MemorySpaceId.IsValid(space) && Has(folder)) found.Add((space, folder));
            }
        var others = Path.Combine(directory, Martlet.Core.Sync.MemorySpaceFolders.SpacesFolder);
        if (Directory.Exists(others))
            foreach (var folder in Directory.EnumerateDirectories(others).Order(StringComparer.Ordinal))
                if (Martlet.Core.Sync.MemorySpaceId.IsValid(Path.GetFileName(folder)) && Has(folder)) found.Add((Path.GetFileName(folder), folder));
        try
        {
            var loaded = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync(cancellation);
            var settings = loaded.Settings?.Memory ?? Martlet.Core.Settings.MemorySettings.Create();
            var legacy = settings.ResolveDirectory(directory);
            if (Has(legacy) && !found.Any(f => string.Equals(Path.GetFullPath(f.Folder), Path.GetFullPath(legacy), StringComparison.OrdinalIgnoreCase)))
                found.Add(("this-pc", legacy));
        }
        catch (Martlet.Core.Contracts.ContractException) { }
        return found;
    }

    /// <summary>Opens a store when nobody else holds it (a running Martlet holds it only during each operation).</summary>
    private static async Task<MemoryStore> OpenFreeStoreAsync(string folder, CancellationToken cancellation)
    {
        var until = DateTime.UtcNow + ForgetStoreWait;
        while (true)
        {
            var preview = MemoryStoreActivationPreview.Create(folder);
            try { return MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), cancellation); }
            catch (MemoryException error) when (error.Failure == MemoryFailure.Busy && DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation);
            }
        }
    }
}

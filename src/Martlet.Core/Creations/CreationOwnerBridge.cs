using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Creations;

/// <summary>The owner's creations on one host while desktops move to accounts (docs/ACCOUNTS.md, "Migration"). It joins the
/// owner's own list (<paramref name="account"/>, /creations/accounts/&lt;32 hex&gt;) and the old single list that desktops on an
/// older Martlet use (<paramref name="legacy"/>, /creations), and keeps both the same in both directions: both are
/// last-writer-wins merges, so the owner's creations stay the same on every computer. A host older than accounts has only the
/// old list, which is then used alone. Only the owner's sync uses this; other accounts never read the old list.</summary>
public sealed class CreationOwnerBridge(ICreationHost account, ICreationHost legacy) : ICreationHost, IDisposable
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> legacyPresent = None;

    public string HostId => legacy.HostId;

    /// <summary>True once the host answered that it is older than accounts, so only the old list is used.</summary>
    public bool OldListOnly { get; private set; }

    /// <summary>The digests of the joined copy. When both lists are the same these are their own digests, so an unchanged host
    /// is skipped as before; when they differ, a value no copy has, so the sync reads both and brings them in step.</summary>
    public async Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token)
    {
        var old = await legacy.ReadDigestAsync(token);
        if (await AccountAsync(() => account.ReadDigestAsync(token)) is not { } mine) return old;
        return mine == old ? mine : (Join(mine.Digest, old.Digest), Join(mine.PresentDigest, old.PresentDigest));
    }

    /// <summary>Reads both lists. When they differ, gives each the joined list first, so changes made by an older desktop
    /// reach the owner's list and the other way round.</summary>
    public async Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token)
    {
        var old = await legacy.ReadAsync(token);
        legacyPresent = old.Present;
        if (await AccountAsync(() => account.ReadAsync(token)) is not { } mine) return old;
        var joined = CreationLibrary.Merge(mine.Library, old.Library);
        var digest = joined.Digest();
        if (mine.Library.Digest() != digest) mine = await account.MergeAsync(joined, token);
        if (old.Library.Digest() != digest)
        {
            old = await legacy.MergeAsync(joined, token);
            legacyPresent = old.Present;
        }
        return (CreationLibrary.Merge(mine.Library, old.Library), Union(mine.Present, old.Present));
    }

    public async Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token)
    {
        var old = await legacy.MergeAsync(library, token);
        legacyPresent = old.Present;
        if (OldListOnly) return old;
        var mine = await account.MergeAsync(library, token);
        return (CreationLibrary.Merge(mine.Library, old.Library), Union(mine.Present, old.Present));
    }

    public async Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) =>
        (OldListOnly ? null : await account.ReadChunkAsync(sha256, token)) ?? await legacy.ReadChunkAsync(sha256, token);

    /// <summary>Sends a piece once: through the owner's list when the host has accounts (it keeps one pool of pieces for every
    /// list), else through the old list.</summary>
    public async Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (OldListOnly) return legacyPresent = await legacy.SendChunkAsync(sha256, data, token);
        return Union(await account.SendChunkAsync(sha256, data, token), legacyPresent);
    }

    public void Dispose()
    {
        (account as IDisposable)?.Dispose();
        (legacy as IDisposable)?.Dispose();
    }

    private async Task<T?> AccountAsync<T>(Func<Task<T>> call) where T : struct
    {
        if (OldListOnly) return null;
        try { return await call(); }
        catch (CreationHostException error) when (error.Old)
        {
            OldListOnly = true;
            return null;
        }
    }

    private static IReadOnlySet<string> Union(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Concat(right).ToHashSet(StringComparer.Ordinal);

    private static string Join(string left, string right) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("owner-bridge\n" + left + "\n" + right)));
}

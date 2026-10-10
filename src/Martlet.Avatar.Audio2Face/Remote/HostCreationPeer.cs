using System.Net.Http;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A paired host for the creation sync (<see cref="CreationSync"/>): one connection, opened on first use with
/// <paramref name="connect"/> and kept for the pass. With <paramref name="account"/> it uses that account's own list
/// (docs/ACCOUNTS.md), else the old single list. Hosts older than creations (or, for an account, older than accounts) refuse
/// with <c>request.invalid</c> (or don't know the route), which reads as <see cref="CreationHostException.Old"/>; any other
/// failure as unreachable.</summary>
public sealed class HostCreationPeer(string hostId, Func<Audio2FaceHostConnection> connect, Guid? account = null) : ICreationHost, IDisposable
{
    private Audio2FaceHostConnection? connection;

    public string HostId { get; } = hostId;

    /// <summary>The account whose list this peer uses; null for the old single list.</summary>
    public Guid? Account { get; } = account;

    /// <summary>The creation host for <paramref name="accountId"/>'s sync with one paired host: its own list, joined with the
    /// old single list (<see cref="CreationOwnerBridge"/>) when the account is the owner, so desktops on an older Martlet keep
    /// the owner's creations the same. Both lists share one connection.</summary>
    public static ICreationHost ForAccount(string hostId, Func<Audio2FaceHostConnection> connect, Guid accountId, bool owner)
    {
        if (!owner) return new HostCreationPeer(hostId, connect, accountId);
        Audio2FaceHostConnection? shared = null;
        Audio2FaceHostConnection Shared() => shared ??= connect();
        return new CreationOwnerBridge(new HostCreationPeer(hostId, Shared, accountId), new HostCreationPeer(hostId, Shared));
    }

    public Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token) => Call(async c =>
    {
        var digest = Account is { } id ? await c.ReadAccountCreationsDigestAsync(id, token) : await c.ReadCreationsDigestAsync(token);
        return (digest.Digest, digest.PresentDigest);
    }, token, mayBeOld: true);

    public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token) => Call(async c =>
    {
        var copy = Account is { } id ? await c.ReadAccountCreationsAsync(id, token) : await c.ReadCreationsAsync(token);
        return (copy.Library, copy.Present);
    }, token, mayBeOld: true);

    public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token) => Call(async c =>
    {
        var copy = Account is { } id ? await c.MergeAccountCreationsAsync(id, library, token) : await c.MergeCreationsAsync(library, token);
        return (copy.Library, copy.Present);
    }, token);

    public Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) => Call(c => Account is { } id
        ? c.ReadAccountCreationChunkAsync(id, sha256, token) : c.ReadCreationChunkAsync(sha256, token), token);

    public Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token) =>
        Call(c => Account is { } id ? c.SendAccountCreationChunkAsync(id, sha256, data, token) : c.SendCreationChunkAsync(sha256, data, token), token);

    private async Task<T> Call<T>(Func<Audio2FaceHostConnection, Task<T>> action, CancellationToken token, bool mayBeOld = false)
    {
        try
        {
            connection ??= connect();
            return await action(connection);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (mayBeOld && error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            throw new CreationHostException(Account is null ? $"{HostId} runs a Martlet older than creations."
                : $"{HostId} runs a Martlet older than accounts.", old: true, error);
        }
        catch (Exception error) when (error is Audio2FaceHostException or OperationCanceledException or HttpRequestException or IOException or
            TimeoutException or InvalidOperationException or ArgumentException or JsonException or ContractException or UnauthorizedAccessException)
        {
            throw new CreationHostException($"{HostId} couldn't be used for creations: {error.Message}", inner: error);
        }
    }

    public void Dispose()
    {
        connection?.Dispose();
        connection = null;
    }
}

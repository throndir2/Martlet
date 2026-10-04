using System.Net.Http;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A paired host for the creation sync (<see cref="CreationSync"/>): one connection, opened on first use with
/// <paramref name="connect"/> and kept for the pass. Hosts older than creations refuse with <c>request.invalid</c> (or don't
/// know the route), which reads as <see cref="CreationHostException.Old"/>; any other failure as unreachable.</summary>
public sealed class HostCreationPeer(string hostId, Func<Audio2FaceHostConnection> connect) : ICreationHost, IDisposable
{
    private Audio2FaceHostConnection? connection;

    public string HostId { get; } = hostId;

    public Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token) => Call(async c =>
    {
        var digest = await c.ReadCreationsDigestAsync(token);
        return (digest.Digest, digest.PresentDigest);
    }, token, mayBeOld: true);

    public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token) => Call(async c =>
    {
        var copy = await c.ReadCreationsAsync(token);
        return (copy.Library, copy.Present);
    }, token, mayBeOld: true);

    public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token) => Call(async c =>
    {
        var copy = await c.MergeCreationsAsync(library, token);
        return (copy.Library, copy.Present);
    }, token);

    public Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) => Call(c => c.ReadCreationChunkAsync(sha256, token), token);

    public Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token) =>
        Call(c => c.SendCreationChunkAsync(sha256, data, token), token);

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
            throw new CreationHostException($"{HostId} runs a Martlet older than creations.", old: true, error);
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

using System.Net.Http;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A paired host for log sharing (<see cref="LogShare"/>): one connection, opened on first use with
/// <paramref name="connect"/> and kept for the run. Hosts older than shared logs refuse /martlet/v1/logs with
/// <c>request.invalid</c> (or don't know the route), which reads as <see cref="LogHostException.Old"/>; any other failure as
/// unreachable.</summary>
public sealed class HostLogPeer(string hostId, Func<Audio2FaceHostConnection> connect) : ILogHost, IDisposable
{
    private Audio2FaceHostConnection? connection;

    public string HostId { get; } = hostId;

    public Task<(IReadOnlyList<LogRecord> Entries, long Next, bool More)> ReadAsync(long after, int limit, CancellationToken token) =>
        Call(async c =>
        {
            var page = await c.ReadLogsAsync(after, limit, token);
            return (page.Entries, page.Next, page.More);
        }, token);

    public Task<(int Accepted, IReadOnlyList<LogMark> Marks)> PushAsync(LogBatch batch, CancellationToken token) =>
        Call(c => c.PushLogsAsync(batch, token), token);

    private async Task<T> Call<T>(Func<Audio2FaceHostConnection, Task<T>> action, CancellationToken token)
    {
        try
        {
            connection ??= connect();
            return await action(connection);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            throw new LogHostException($"{HostId} runs a Martlet older than shared logs.", old: true, error);
        }
        catch (Exception error) when (error is Audio2FaceHostException or OperationCanceledException or HttpRequestException or IOException or
            TimeoutException or InvalidOperationException or ArgumentException or JsonException or ContractException or UnauthorizedAccessException)
        {
            throw new LogHostException($"{HostId} couldn't share logs: {error.Message}", inner: error);
        }
    }

    public void Dispose()
    {
        connection?.Dispose();
        connection = null;
    }
}

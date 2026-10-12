using System.Net.Http;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A paired host for the sync of <paramref name="account"/>'s record of conversations: one connection, opened on first
/// use with <paramref name="connect"/> and kept for the pass. Hosts older than the shared record refuse with
/// <c>request.invalid</c> (or don't know the route), which reads as <see cref="ConversationHostFailure.Old"/>; a host that
/// doesn't let this computer use the account's record (<c>conversations.account_denied</c>) as
/// <see cref="ConversationHostFailure.Denied"/>; any other failure as <see cref="ConversationHostFailure.Unreachable"/>.</summary>
public sealed class HostConversationPeer(string hostId, Func<Audio2FaceHostConnection> connect, Guid account) : IConversationRecordHost, IDisposable
{
    private Audio2FaceHostConnection? connection;

    public string HostId { get; } = hostId;

    /// <summary>The account whose record this peer uses.</summary>
    public Guid Account { get; } = account;

    public Task<IReadOnlyDictionary<string, string>> MonthsAsync(CancellationToken token) =>
        Call(c => c.ReadConversationMonthsAsync(Account, token), token);

    public Task<SharedConversations> ReadAsync(string month, CancellationToken token) =>
        Call(c => c.ReadConversationMonthAsync(Account, month, token), token);

    public Task<SharedConversations> MergeAsync(SharedConversations copy, CancellationToken token) =>
        Call(c => c.MergeConversationMonthAsync(Account, copy, token), token);

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
            throw new ConversationHostException($"{HostId} runs a Martlet older than the shared record of conversations.",
                ConversationHostFailure.Old, error);
        }
        catch (Audio2FaceHostException error) when (error.Code == "conversations.account_denied")
        {
            throw new ConversationHostException($"{HostId} doesn't let this computer use the record of conversations yet.",
                ConversationHostFailure.Denied, error);
        }
        catch (Exception error) when (error is Audio2FaceHostException or OperationCanceledException or HttpRequestException or IOException or
            TimeoutException or InvalidOperationException or ArgumentException or JsonException or ContractException or UnauthorizedAccessException)
        {
            throw new ConversationHostException($"{HostId} couldn't be used for the record of conversations: {error.Message}",
                ConversationHostFailure.Unreachable, error);
        }
    }

    public void Dispose()
    {
        connection?.Dispose();
        connection = null;
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>One page of a host's log: the lines, the position to continue from and whether more are waiting.</summary>
public sealed record HostLogPage(IReadOnlyList<LogRecord> Entries, long Next, bool More);

/// <summary>A host's log: its own activity and every computer's lines the owner's desktops share with it.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string LogsPath = "/martlet/v1/logs";
    private const int MaximumLogsResponseBytes = 524_288;

    /// <summary>Reads the lines the host keeps after store position <paramref name="after"/> (0 for the oldest). Hosts
    /// older than shared logs refuse with code <c>request.invalid</c>.</summary>
    public Task<HostLogPage> ReadLogsAsync(long after, int limit = 500, CancellationToken cancellationToken = default) =>
        LogPageAsync($"{LogsPath}?after={after.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    /// <summary>Reads the host's own activity after sequence number <paramref name="afterSeq"/>.</summary>
    public Task<HostLogPage> ReadOwnLogsAsync(long afterSeq, int limit = 500, CancellationToken cancellationToken = default) =>
        LogPageAsync($"{LogsPath}?own_after={afterSeq.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    /// <summary>Gives the host a batch of lines and returns how many it kept and its marks for the
    /// streams the batch names.</summary>
    public async Task<(int Accepted, IReadOnlyList<LogMark> Marks)> PushLogsAsync(LogBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var body = batch.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + LogsPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 128 * 1024, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = Checked(document.RootElement);
            var marks = root.GetProperty("marks").Deserialize<LogMark[]>(LogRules.Json) ?? [];
            if (marks.Any(m => m is null || m.Seq < 0)) throw new FormatException();
            return (root.GetProperty("accepted").GetInt32(), marks);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's answer about its log was invalid.");
        }
    }

    private async Task<HostLogPage> LogPageAsync(string target, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + target);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumLogsResponseBytes + 4096, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = Checked(document.RootElement);
            var entries = root.GetProperty("entries").Deserialize<LogRecord[]>(LogRules.Json) ?? [];
            foreach (var entry in entries)
            {
                if (entry is null) throw new FormatException();
                entry.Validate();
            }
            return new(entries, root.GetProperty("next").GetInt64(), root.GetProperty("more").GetBoolean());
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or JsonException or
            FormatException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's log was invalid.");
        }
    }

    private JsonElement Checked(JsonElement root)
    {
        if (root.GetProperty("host_id").GetString() != pairing.HostId)
            throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
        return root;
    }
}

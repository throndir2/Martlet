using System.Net;
using System.Text.Json;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>What a host granted for a GPU hold: the graphics cards it keeps free of Thinking pool work (empty: the whole host)
/// and when the hold ends unless it is renewed.</summary>
public sealed record HostGpuHold(IReadOnlyList<string> Gpus, DateTimeOffset Until);

/// <summary>The two signed calls a <see cref="HostLiveGpuHold"/> makes to one paired host; an
/// <see cref="Audio2FaceHostConnection"/> makes them.</summary>
public interface IHostGpuHoldChannel : IDisposable
{
    /// <summary>Holds the graphics cards behind <paramref name="routeIds"/> for <paramref name="ttl"/> and renews this device's hold.</summary>
    Task<HostGpuHold> HoldGpusAsync(IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Ends this device's hold early; false when it had none.</summary>
    Task<bool> ReleaseGpusAsync(CancellationToken cancellationToken = default);
}

/// <summary>GPU priority on a paired host (live turn first): holds, releases and what the host does now.</summary>
public sealed partial class Audio2FaceHostConnection : IHostGpuHoldChannel
{
    private const string PriorityPath = "/martlet/v1/priority";
    /// <summary>The longest hold a host grants at once; renew it to keep the cards longer.</summary>
    public static readonly TimeSpan MaximumGpuHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PriorityTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Asks the host to keep the graphics cards behind <paramref name="routeIds"/> (one to sixteen of its route IDs) free
    /// of Thinking pool work for <paramref name="ttl"/> (1 ms to 15 s), renewing this device's hold: POST
    /// /martlet/v1/priority/hold. Pool work running on those cards stops at once. Hosts older than GPU priority refuse with
    /// <see cref="Audio2FaceHostException"/> code request.invalid.</summary>
    public async Task<HostGpuHold> HoldGpusAsync(IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeIds);
        if (routeIds.Count is < 1 or > 16 || routeIds.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Hold one to sixteen route IDs.", nameof(routeIds));
        var milliseconds = (int)Math.Ceiling(ttl.TotalMilliseconds);
        if (ttl <= TimeSpan.Zero || ttl > MaximumGpuHold)
            throw new ArgumentOutOfRangeException(nameof(ttl), "A hold lasts 1 ms to 15 s.");
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["routes"] = routeIds.ToArray(), ["ttl_ms"] = milliseconds
        });
        using var document = await PriorityAsync(HttpMethod.Post, PriorityPath + "/hold", body, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            var gpus = root.GetProperty("gpus").EnumerateArray().Take(128)
                .Select(gpu => gpu.GetString() ?? throw new FormatException()).ToArray();
            return new(gpus, root.GetProperty("until").GetDateTimeOffset());
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's answer to the GPU hold was invalid.");
        }
    }

    /// <summary>Ends this device's GPU hold on the host early (POST /martlet/v1/priority/release); false when it had none.</summary>
    public async Task<bool> ReleaseGpusAsync(CancellationToken cancellationToken = default)
    {
        using var document = await PriorityAsync(HttpMethod.Post, PriorityPath + "/release", "{}"u8.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        try { return document.RootElement.GetProperty("released").GetBoolean(); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's answer to the GPU release was invalid.");
        }
    }

    /// <summary>What GPU priority does on the host now (GET /martlet/v1/priority), as the host wrote it: the GPU map (each route's
    /// lane and graphics cards), each card's hold state, the holds, the last preemptions and refusals with their counts, and
    /// placement warnings. Device IDs and route IDs only; no secrets.</summary>
    public async Task<JsonElement> ReadPriorityAsync(CancellationToken cancellationToken = default)
    {
        using var document = await PriorityAsync(HttpMethod.Get, PriorityPath, null, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<JsonDocument> PriorityAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, pairing.Origin + path);
        if (body is not null) request.Content = Audio2FaceHostClient.JsonContent(body);
        Sign(request, body ?? []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PriorityTimeout);
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        var document = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("host_id", out var host) || host.ValueKind != JsonValueKind.String ||
                host.GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}

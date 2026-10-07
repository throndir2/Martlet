using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face.Remote;

public sealed partial class Audio2FaceHostConnection
{
    public const string PictureRouteId = "martlet.gateway.picture.v1";
    public const string PicturePath = "/martlet/v1/inference/picture";

    /// <summary>Sends one operation to the host's ComfyUI picture relay and returns the JSON object of each text event.
    /// Gateway and transport failures throw <see cref="Audio2FaceHostException"/>.</summary>
    public Task<IReadOnlyList<JsonElement>> PictureOperationAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(payload);
        if (route.RouteId != PictureRouteId || route.Path != PicturePath)
            throw new ArgumentException("The route is not the host's pictures.", nameof(route));
        return JsonOperationAsync(route, payload, TimeSpan.FromSeconds(50), "picture", cancellationToken);
    }

    /// <summary>Sends one JSON operation to a host route whose answer is JSON text events (pictures, reading) and returns the
    /// JSON object of each text event. <paramref name="what"/> names the route in errors.</summary>
    private async Task<IReadOnlyList<JsonElement>> JsonOperationAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        TimeSpan longest, string what, CancellationToken cancellationToken)
    {
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var now = clock.GetUtcNow();
        var deadline = now + TimeSpan.FromSeconds(Math.Min(longest.TotalSeconds, route.MaximumDuration.TotalSeconds - 1));
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
            ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = ids.SessionId, ["turn_id"] = ids.TurnId, ["request_id"] = ids.RequestId, ["epoch"] = 0,
            ["deadline_utc"] = deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = payload
        });
        if (body.Length > route.MaximumRequestBytes)
            throw new Audio2FaceHostException("request.too_large", $"The {what} request is too large for the host's {what} route.");
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + route.Path)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline - clock.GetUtcNow() + TimeSpan.FromSeconds(2));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var failure = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
            throw Audio2FaceHostClient.Remote(failure.RootElement);
        }
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new Audio2FaceHostException("response.invalid", $"The host returned an invalid {what} stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var objects = new List<JsonElement>();
        var total = 0L;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            total += line.Length + 1;
            if (line.Length > route.MaximumEventBytes * 2 || total > route.MaximumStreamBytes)
                throw new Audio2FaceHostException("stream.limit", $"The host's {what} stream exceeded its bounds.");
            var (text, terminal) = ParseChatEvent(line, ids);
            if (text is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    objects.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    throw new Audio2FaceHostException("stream.invalid", $"The host's {what} stream was invalid.");
                }
            }
            if (terminal) return objects;
        }
        throw new Audio2FaceHostException("stream.truncated", $"The host's {what} stream ended early.");
    }
}

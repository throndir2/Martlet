using System.Net;
using System.Text.Json;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>One authentication or pairing decision a host recorded (docs/NETWORK.md, "Reaching your network from outside
/// home"). Nonsecret.</summary>
public sealed record HostAuthEvent(DateTimeOffset At, string RouteClass, string Source, string SourceKind, string Outcome, string Code,
    string? Subject);

/// <summary>A host's exposure choices, its guard's totals and its most recent authentication decisions.</summary>
public sealed record HostSecurityAudit(string HostId, bool InternetReachable, bool AllowPairingOutsideHome, bool TreatAllAsOutside,
    long Successes, long Failures, long Throttled, int LockedOut, IReadOnlyList<HostAuthEvent> Events);

public sealed partial class Audio2FaceHostConnection
{
    private const string SecurityAuditPath = "/martlet/v1/security/audit";

    /// <summary>Reads the host's security audit. Hosts older than it refuse with code <c>request.invalid</c>.</summary>
    public async Task<HostSecurityAudit> ReadSecurityAuditAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + SecurityAuditPath);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 256 * 1024, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new InvalidOperationException();
            var events = root.GetProperty("events").EnumerateArray().Take(512).Select(e => new HostAuthEvent(
                e.GetProperty("at").GetDateTimeOffset(), Text(e, "route_class"), Text(e, "source"), Text(e, "source_kind"),
                Text(e, "outcome"), Text(e, "code"),
                e.TryGetProperty("subject", out var subject) && subject.ValueKind == JsonValueKind.String ? subject.GetString() : null)).ToArray();
            return new(pairing.HostId, root.GetProperty("internet_reachable").GetBoolean(),
                root.GetProperty("allow_pairing_outside_home").GetBoolean(), root.GetProperty("treat_all_as_outside").GetBoolean(),
                root.GetProperty("successes").GetInt64(), root.GetProperty("failures").GetInt64(), root.GetProperty("throttled").GetInt64(),
                root.GetProperty("locked_out").GetInt32(), events);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's security audit was invalid.");
        }
    }
}

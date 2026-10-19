using System.Net;
using System.Text.Json;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>One authentication or pairing decision a host recorded (docs/NETWORK.md, "Reaching your network from outside
/// home"). Nonsecret.</summary>
public sealed record HostAuthEvent(DateTimeOffset At, string RouteClass, string Source, string SourceKind, string Outcome, string Code,
    string? Subject);

/// <summary>A host's exposure choices, its guard's totals and its most recent authentication decisions.</summary>
public sealed record HostSecurityAudit(string HostId, bool InternetReachable, bool AllowPairingOutsideHome, bool TreatAllAsOutside,
    long Successes, long Failures, long Throttled, int LockedOut, IReadOnlyList<HostAuthEvent> Events)
{
    /// <summary>Why the host can't serve outside access: sign-in has no usable method there ("signin.not_set_up",
    /// "signin.no_allowed_identity"), or null when it has. Hosts older than this rule report null.</summary>
    public string? OutsideAccessBlockedReason { get; init; }
    /// <summary>The host has outside addresses (or allows typed codes outside) while sign-in has no usable method, so it
    /// refuses requests from outside home.</summary>
    public bool OutsideAccessPaused { get; init; }
    /// <summary>The overlay network addresses ("ip:port") the host listens on besides its home address. Hosts older than
    /// this, and hosts in a container (whose runtime publishes the port), report none.</summary>
    public IReadOnlyList<string> OutsideListeners { get; init; } = [];
    /// <summary>The front door's port when this host is the front door (docs/OPENAI_ENDPOINT.md); null otherwise and from
    /// older hosts.</summary>
    public int? FrontDoorPort { get; init; }
    /// <summary>Where the front door listens now ("ip:port"): empty when it isn't open.</summary>
    public IReadOnlyList<string> FrontDoorListeners { get; init; } = [];
    /// <summary>How many browsers are signed in to the front door's web page now; null when this host isn't the front door.</summary>
    public int? FrontDoorSessions { get; init; }
    /// <summary>False when one of the host's shared stores failed to load or save and has not worked since; null from older hosts.</summary>
    public bool? StoresHealthy { get; init; }
    /// <summary>The stores whose last load or save failed ("API keys: save"); empty when all work.</summary>
    public IReadOnlyList<string> FailingStores { get; init; } = [];
    /// <summary>How many times the host saved its pairing authority since it started, and how long a save takes on average and
    /// at most (each signed request waits for one save); null from older hosts.</summary>
    public long? AuthorityCommits { get; init; }
    public double? AuthorityCommitAverageMs { get; init; }
    public double? AuthorityCommitLongestMs { get; init; }
}

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
                root.GetProperty("locked_out").GetInt32(), events)
            {
                OutsideAccessBlockedReason = root.TryGetProperty("outside_access_blocked_reason", out var blocked) && blocked.ValueKind == JsonValueKind.String
                    ? blocked.GetString() : null,
                OutsideAccessPaused = root.TryGetProperty("outside_access_paused", out var paused) && paused.ValueKind == JsonValueKind.True,
                OutsideListeners = root.TryGetProperty("outside_listeners", out var listeners) && listeners.ValueKind == JsonValueKind.Array
                    ? listeners.EnumerateArray().Take(16).Select(e => e.GetString() ?? throw new InvalidOperationException()).ToArray() : [],
                FrontDoorPort = root.TryGetProperty("front_door_port", out var door) && door.ValueKind == JsonValueKind.Number &&
                    door.TryGetInt32(out var port) && port is > 0 and <= 65535 ? port : null,
                FrontDoorListeners = root.TryGetProperty("front_door_listeners", out var doors) && doors.ValueKind == JsonValueKind.Array
                    ? doors.EnumerateArray().Take(8).Select(e => e.GetString() ?? throw new InvalidOperationException()).ToArray() : [],
                FrontDoorSessions = root.TryGetProperty("front_door_sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Number &&
                    sessions.TryGetInt32(out var count) ? count : null,
                StoresHealthy = root.TryGetProperty("stores_healthy", out var healthy) && healthy.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? healthy.GetBoolean() : null,
                FailingStores = root.TryGetProperty("stores", out var stores) && stores.ValueKind == JsonValueKind.Array
                    ? stores.EnumerateArray().Take(64)
                        .Where(e => e.TryGetProperty("failing", out var failing) && failing.ValueKind == JsonValueKind.String)
                        .Select(e => $"{Text(e, "store")}: {Text(e, "failing")}").ToArray() : [],
                AuthorityCommits = root.TryGetProperty("authority_commits", out var commits) && commits.ValueKind == JsonValueKind.Object
                    ? commits.GetProperty("count").GetInt64() : null,
                AuthorityCommitAverageMs = commits.ValueKind == JsonValueKind.Object ? commits.GetProperty("average_ms").GetDouble() : null,
                AuthorityCommitLongestMs = commits.ValueKind == JsonValueKind.Object ? commits.GetProperty("longest_ms").GetDouble() : null
            };
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's security audit was invalid.");
        }
    }
}

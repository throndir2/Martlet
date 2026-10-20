using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

internal sealed partial class GatewayHttpApplication
{
    internal const string SecurityAuditPath = "/martlet/v1/security/audit";

    /// <summary>GET returns this host's exposure choices, its guard's totals and its most recent authentication and
    /// pairing decisions (source address, outcome, code, device or account; never secrets). Paired devices only.</summary>
    private async ValueTask InvokeSecurityAuditAsync(HttpContext context)
    {
        GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
        EnsureEmptyRequest(context.Request);
        _ = authenticator.Authenticate(context.Request);
        var exposure = Guard.Exposure;
        var totals = Guard.Totals();
        await WriteJsonAsync(context, StatusCodes.Status200OK, new SecurityAuditDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            GeneratedAt = clock.GetUtcNow(),
            InternetReachable = Guard.InternetReachable,
            OutsideAddresses = Network.OwnAddresses,
            OutsideListeners = OutsideListeners(),
            OutsideAccessBlockedReason = Guard.OutsideAccessBlockedReason,
            OutsideAccessPaused = Guard.OutsideAccessPaused,
            AllowPairingOutsideHome = exposure.AllowPairingOutsideHome,
            TreatAllAsOutside = exposure.TreatAllAsOutside,
            FrontDoorPort = exposure.FrontDoorPort,
            FrontDoorListeners = FrontDoorListeners(),
            FrontDoorSessions = exposure.FrontDoorPort is null ? null : FrontDoorSessions.Count,
            Successes = totals.Successes,
            Failures = totals.Failures,
            Throttled = totals.Throttled,
            LockedOut = totals.LockedOut,
            Events = Guard.Recent(),
            StoresHealthy = StoreHealth.Healthy,
            Stores = StoreHealth.Snapshot(),
            AuthorityCommits = AuthorityCommits(credentials.CommitStats),
            NonceSaves = AuthorityCommits(credentials.NonceStats)
        }).ConfigureAwait(false);
    }

    private sealed record SecurityAuditDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required bool InternetReachable { get; init; }
        public required IReadOnlyList<string> OutsideAddresses { get; init; }
        /// <summary>The overlay network addresses ("ip:port") this host listens on besides its home origin.</summary>
        public required IReadOnlyList<string> OutsideListeners { get; init; }
        /// <summary>Why outside access can't be served: sign-in has no usable method (null: it has).</summary>
        public string? OutsideAccessBlockedReason { get; init; }
        /// <summary>Outside addresses (or typed codes outside) while sign-in has no usable method: outside requests are refused.</summary>
        public required bool OutsideAccessPaused { get; init; }
        public required bool AllowPairingOutsideHome { get; init; }
        public required bool TreatAllAsOutside { get; init; }
        /// <summary>The front door's port (docs/OPENAI_ENDPOINT.md); absent when this host isn't the front door.</summary>
        public int? FrontDoorPort { get; init; }
        /// <summary>Where the front door listens now ("ip:port"); empty when it isn't open.</summary>
        public required IReadOnlyList<string> FrontDoorListeners { get; init; }
        /// <summary>How many browsers are signed in to the front door's web page now.</summary>
        public int? FrontDoorSessions { get; init; }
        public required long Successes { get; init; }
        public required long Failures { get; init; }
        public required long Throttled { get; init; }
        public required int LockedOut { get; init; }
        public required IReadOnlyList<GatewayAuthEvent> Events { get; init; }
        /// <summary>False when a shared store (API keys, accounts, memories, ...) failed to load or save and has not worked since.</summary>
        public required bool StoresHealthy { get; init; }
        /// <summary>Each store that failed since the gateway started: what failed last, how often, when and why.</summary>
        public required IReadOnlyList<GatewayStoreStatus> Stores { get; init; }
        /// <summary>How long full saves of the pairing authority take (pairing changes and the first request after a restart).</summary>
        public required AuthorityCommitDocument AuthorityCommits { get; init; }
        /// <summary>How long nonce saves take: one append and one flush to the nonce journal; every signed request waits for one.</summary>
        public required AuthorityCommitDocument NonceSaves { get; init; }
    }

    private static AuthorityCommitDocument AuthorityCommits(GatewayAuthorityCommitStats stats) => new()
    {
        Count = stats.Commits,
        Slow = stats.SlowCommits,
        LastMs = Math.Round(stats.Last.TotalMilliseconds, 2),
        AverageMs = Math.Round(stats.Average.TotalMilliseconds, 2),
        LongestMs = Math.Round(stats.Longest.TotalMilliseconds, 2)
    };

    private sealed record AuthorityCommitDocument
    {
        public required long Count { get; init; }
        public required long Slow { get; init; }
        public required double LastMs { get; init; }
        public required double AverageMs { get; init; }
        public required double LongestMs { get; init; }
    }
}

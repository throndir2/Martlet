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
            InternetReachable = exposure.InternetReachable,
            AllowPairingOutsideHome = exposure.AllowPairingOutsideHome,
            TreatAllAsOutside = exposure.TreatAllAsOutside,
            Successes = totals.Successes,
            Failures = totals.Failures,
            Throttled = totals.Throttled,
            LockedOut = totals.LockedOut,
            Events = Guard.Recent()
        }).ConfigureAwait(false);
    }

    private sealed record SecurityAuditDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required bool InternetReachable { get; init; }
        public required bool AllowPairingOutsideHome { get; init; }
        public required bool TreatAllAsOutside { get; init; }
        public required long Successes { get; init; }
        public required long Failures { get; init; }
        public required long Throttled { get; init; }
        public required int LockedOut { get; init; }
        public required IReadOnlyList<GatewayAuthEvent> Events { get; init; }
    }
}

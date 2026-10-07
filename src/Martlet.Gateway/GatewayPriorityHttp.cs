using System.Text.Json;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

internal sealed partial class GatewayHttpApplication
{
    internal const string PriorityPath = "/martlet/v1/priority";
    internal const string PriorityHoldPath = PriorityPath + "/hold";
    internal const string PriorityReleasePath = PriorityPath + "/release";
    private const int MaximumPriorityRequestBytes = 4_096;

    private static bool IsPriorityTarget(string rawTarget) => rawTarget is PriorityPath or PriorityHoldPath or PriorityReleasePath;

    /// <summary>GPU priority (live turn first). GET /martlet/v1/priority (read access) returns the GPU map and what holds each
    /// card now, the holds, and the last preemptions and refusals. POST /martlet/v1/priority/hold with
    /// {"routes":[route IDs],"ttl_ms":1..15000} holds the graphics cards behind those routes for this client and renews its
    /// hold; POST /martlet/v1/priority/release with {} ends it. Holds and releases are signed voice requests, like inference;
    /// a hold expires on its own.</summary>
    private async ValueTask InvokePriorityAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == PriorityPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            _ = Authorize(context.Request, GatewayApiAccess.Read);
            var now = inference.Priority();
            await WriteJsonAsync(context, StatusCodes.Status200OK, new PriorityDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                GeneratedAt = clock.GetUtcNow(),
                Routes = now.Routes,
                Gpus = now.Gpus,
                WholeHostHeld = now.WholeHostHeld,
                Holds = now.Holds,
                Preempted = now.Preempted,
                Refused = now.Refused,
                LastPreemptions = now.LastPreemptions,
                LastRefusals = now.LastRefusals,
                Warnings = now.Warnings
            }).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        var bytes = await ReadInferenceBodyAsync(context.Request, MaximumPriorityRequestBytes, context.RequestAborted)
            .ConfigureAwait(false);
        var principal = Authorize(context.Request, crypto.Sha256(bytes), GatewayApiAccess.Role, GatewayRole.Voice);
        GatewayRules.Require(principal.Role == GatewayRole.Voice, "auth.role");
        using var document = ParsePriorityBody(bytes);
        var root = document.RootElement;
        if (rawTarget == PriorityReleasePath)
        {
            GatewayRules.Require(!root.EnumerateObject().Any(), "request.invalid");
            await WriteJsonAsync(context, StatusCodes.Status200OK, new ReleaseDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Released = inference.Release(principal)
            }).ConfigureAwait(false);
            return;
        }
        if (root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("routes", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() is < 1 or > GatewayInferenceRouteRegistry.MaximumHoldRoutes ||
            !root.TryGetProperty("ttl_ms", out var ttl) || ttl.ValueKind != JsonValueKind.Number ||
            !ttl.TryGetInt32(out var milliseconds) ||
            milliseconds < 1 || milliseconds > GatewayInferenceRouteRegistry.MaximumHoldDuration.TotalMilliseconds)
            throw new GatewayProtocolException("request.invalid");
        var routes = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new GatewayProtocolException("request.invalid")).ToArray();
        GatewayRules.Require(routes.Distinct(StringComparer.Ordinal).Count() == routes.Length, "request.invalid");
        var (gpus, until) = inference.Hold(principal, routes, TimeSpan.FromMilliseconds(milliseconds));
        await WriteJsonAsync(context, StatusCodes.Status200OK, new HoldDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Gpus = gpus,
            Until = until
        }).ConfigureAwait(false);
    }

    private static JsonDocument ParsePriorityBody(byte[] bytes)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            InspectJson(document.RootElement);
            GatewayRules.Require(document.RootElement.ValueKind == JsonValueKind.Object, "request.invalid");
            return document;
        }
        catch (JsonException)
        {
            document?.Dispose();
            throw new GatewayProtocolException("request.invalid");
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    /// <summary>The GPU map for this host's log when the gateway starts: each route's lane and graphics cards, and a warning for
    /// each pool route that shares a card with live routes.</summary>
    internal void LogGpuMap()
    {
        var routes = inference.Routes;
        if (routes.Count == 0) return;
        Logs.Own(LogLevels.Info, "GPU map (live turn first): " + string.Join("; ", routes.Select(route =>
            $"{GatewayGpus.RouteName(route)} ({(route.Lane == GatewayLane.Pool ? "pool" : "live")}) on {GatewayGpus.Describe(route.Gpus)}")) + ".");
        foreach (var warning in GatewayGpus.Warnings(routes))
            Logs.Own(LogLevels.Warn, warning);
    }

    private sealed record PriorityDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required IReadOnlyList<GatewayPriorityRoute> Routes { get; init; }
        public required IReadOnlyList<GatewayPriorityGpu> Gpus { get; init; }
        public required bool WholeHostHeld { get; init; }
        public required IReadOnlyList<GatewayPriorityHold> Holds { get; init; }
        public required long Preempted { get; init; }
        public required long Refused { get; init; }
        public required IReadOnlyList<GatewayPriorityEvent> LastPreemptions { get; init; }
        public required IReadOnlyList<GatewayPriorityEvent> LastRefusals { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }
    }

    private sealed record HoldDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        /// <summary>The graphics cards held; empty: the whole host (a route's placement is unknown or it isn't on this host).</summary>
        public required IReadOnlyList<string> Gpus { get; init; }
        public required DateTimeOffset Until { get; init; }
    }

    private sealed record ReleaseDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required bool Released { get; init; }
    }
}

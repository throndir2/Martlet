using System.Text.Json;

namespace Martlet.HostClient;

public sealed partial class HostConnection
{
    public const string MotionRouteId = "martlet.gateway.motion.v1";
    public const string MotionPath = "/martlet/v1/inference/motion";
    /// <summary>The longest one motion operation may take: the first motion after a free loads the model (about 20 GB) first.</summary>
    public static readonly TimeSpan MotionLongest = TimeSpan.FromMinutes(8);

    /// <summary>Sends one operation to the host's Motion role (route <c>martlet.gateway.motion.v1</c>): <c>status</c>,
    /// <c>generate</c> (prompt, seconds, optional seed) or <c>free</c>. Returns the JSON object of each text event: for a
    /// generate, the VRMA clip's base64 pieces and then one summary object; a failure the service reports is one object with an
    /// <c>error</c>. Gateway and transport failures throw <see cref="HostConnectionException"/>.</summary>
    public Task<IReadOnlyList<JsonElement>> MotionOperationAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(payload);
        if (route.RouteId != MotionRouteId || route.Path != MotionPath)
            throw new ArgumentException("The route is not the host's Motion role.", nameof(route));
        return JsonOperationAsync(route, payload, MotionLongest, "motion", cancellationToken);
    }
}

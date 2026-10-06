using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>
/// The gateway's guard against guessing and flooding (see docs/NETWORK.md, "Reaching your network from outside home").
/// Every request passes it before its handler: per source address it counts requests to the routes anyone may call
/// (liveness, pairing, joining, sign-in) and locks out an address or a subject (an account) after repeated failures,
/// doubling the wait each time. Routes that check their own secrets (sign-in) call <see cref="TryAdmit"/> once they know
/// the subject and <see cref="Record"/> with the outcome; both also feed the audit log.
/// </summary>
public interface IGatewayRequestGuard
{
    /// <summary>False when the request's address or subject is locked out; respond 429 "auth.throttled" with
    /// <paramref name="retryAfter"/> as Retry-After (<see cref="GatewayRequestGuard.Throttled"/> does both).</summary>
    bool TryAdmit(GatewayGuardRequest request, out TimeSpan retryAfter);

    /// <summary>Records the outcome in the audit log. Failures count toward lockout of the address and the subject;
    /// a success clears both. Never pass secrets.</summary>
    void Record(GatewayGuardRequest request, GatewayGuardOutcome outcome, string code, string? subject);
}

/// <param name="RouteClass">"health", "pair" (a one-use card opened for one named device), "pair-code" (a short typed code),
/// "join", "signin" or "credential" (any signed or API-key route).</param>
/// <param name="RemoteAddress">The connection's source address (normalized; never a forwarded header).</param>
/// <param name="Subject">Who the request claims to be, e.g. "local:owner" or "oidc:&lt;iss&gt;|&lt;sub&gt;"; null if unknown.</param>
public sealed record GatewayGuardRequest(string RouteClass, string RemoteAddress, string? Subject);

public enum GatewayGuardOutcome { Success, Failure }

/// <summary>Where a request came from: this computer, the home network (private addresses) or anywhere else (the
/// internet, or an overlay network such as Tailscale's 100.64.0.0/10).</summary>
public enum GatewaySourceKind { Loopback, Home, Outside }

/// <summary>The owner's choices for a host reachable from outside home.</summary>
public sealed record GatewayExposure
{
    /// <summary>The host has outside addresses (port forward or overlay): limits apply to every source, not only to
    /// outside ones.</summary>
    public bool InternetReachable { get; init; }

    /// <summary>Accept short typed pairing codes from outside home. Off by default: a short code could be guessed over the
    /// internet, so typed codes work on the home network unless the owner opts in. A one-use card opened for one named
    /// device (a 32-byte token, as Martlet uses to pair with its own host service) can't be guessed and works from anywhere.</summary>
    public bool AllowPairingOutsideHome { get; init; }

    /// <summary>Treat every connection as coming from outside home, for a host behind something that hides the real
    /// source (a container runtime's port proxy, a TCP relay), so private-looking sources aren't trusted as home.</summary>
    public bool TreatAllAsOutside { get; init; }

    /// <summary>Outside addresses the owner set on the host itself (martlet-host owner-exposure). The host advertises them to
    /// member desktops (GET /martlet/v1/network), which sign them into its roster entry when they are newer.</summary>
    public IReadOnlyList<string>? OutsideAddresses { get; init; }

    /// <summary>When <see cref="OutsideAddresses"/> were set (null: never set on the host).</summary>
    public DateTimeOffset? OutsideAddressesSetAt { get; init; }
}

/// <summary>One audit entry: an authentication or pairing decision. Nonsecret.</summary>
public sealed record GatewayAuthEvent
{
    public required DateTimeOffset At { get; init; }
    public required string RouteClass { get; init; }
    public required string Source { get; init; }
    public required string SourceKind { get; init; }
    /// <summary>"success", "failure", "throttled" (rate or lockout) or "refused" (pairing from outside home).</summary>
    public required string Outcome { get; init; }
    public required string Code { get; init; }
    public string? Subject { get; init; }
}

/// <summary>Helpers for route handlers (sign-in) that use the guard.</summary>
internal static class GatewayGuard
{
    internal const string ContextItem = "martlet.guard";

    /// <summary>The guard request for <paramref name="context"/> (normalized source address filled in).</summary>
    internal static GatewayGuardRequest For(HttpContext context, string routeClass, string? subject = null) =>
        GatewayRequestGuard.For(context, routeClass, subject);

    /// <summary>Whether the request came from outside home, honouring the host's "treat every connection as outside".</summary>
    internal static bool IsOutsideHome(HttpContext context) =>
        context.Items[ContextItem] is GatewayRequestGuard guard
            ? guard.IsOutsideHome(context)
            : GatewayRequestGuard.Classify(context.Connection.RemoteIpAddress) == GatewaySourceKind.Outside;

    /// <summary>Sets Retry-After and throws "auth.throttled".</summary>
    internal static Exception Throttled(HttpContext context, TimeSpan retryAfter) => GatewayRequestGuard.Throttled(context, retryAfter);
}

public sealed class GatewayRequestGuard : IGatewayRequestGuard
{
    public const int FreeFailures = 5;
    public const int UnauthenticatedRequestsPerMinute = 120;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumLockout = TimeSpan.FromMinutes(15);
    internal const int AuditCapacity = 256;
    private const int MaximumTracked = 4096;

    /// <summary>Failure codes that count toward lockout when the dispatcher sees them (sign-in routes record their own).</summary>
    private static readonly HashSet<string> CountedCodes = new(StringComparer.Ordinal)
    {
        "auth.missing", "auth.invalid", "auth.revoked", "auth.expired", "pairing.invalid", "pairing.closed",
        "pairing.expired", "network.denied", "key.invalid", "key.revoked", "key.expired", "pair.outside_home"
    };

    private readonly TimeProvider clock;
    private readonly Action<string, string, string?> log;
    private readonly object gate = new();
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> budgets = new(StringComparer.Ordinal);
    private readonly Queue<GatewayAuthEvent> audit = new();
    private volatile GatewayExposure exposure = new();
    private long failures, throttled, successes;

    internal GatewayRequestGuard(TimeProvider clock, Action<string, string, string?> log)
    {
        this.clock = clock;
        this.log = log;
    }

    public GatewayExposure Exposure
    {
        get => exposure;
        set => exposure = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The most recent decisions, newest last (at most 256).</summary>
    public IReadOnlyList<GatewayAuthEvent> Recent()
    {
        lock (gate) return audit.ToArray();
    }

    /// <summary>Counts since start and how many addresses or subjects are locked out now.</summary>
    public (long Successes, long Failures, long Throttled, int LockedOut) Totals()
    {
        var now = clock.GetUtcNow();
        lock (gate) return (successes, failures, throttled, counters.Values.Count(c => c.LockedUntil > now));
    }

    internal static GatewaySourceKind Classify(IPAddress? address)
    {
        if (address is null) return GatewaySourceKind.Outside;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return GatewaySourceKind.Loopback;
        var bytes = address.GetAddressBytes();
        var home = address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
              bytes[0] == 169 && bytes[1] == 254
            : address.AddressFamily == AddressFamily.InterNetworkV6 && ((bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal);
        return home ? GatewaySourceKind.Home : GatewaySourceKind.Outside;
    }

    internal static string Address(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    internal GatewaySourceKind Source(HttpContext context) =>
        exposure.TreatAllAsOutside ? GatewaySourceKind.Outside : Classify(context.Connection.RemoteIpAddress);

    /// <summary>Whether the request came from outside home (not this computer and not a private home address).</summary>
    internal bool IsOutsideHome(HttpContext context) => Source(context) == GatewaySourceKind.Outside;

    /// <summary>The guard request for <paramref name="context"/> (source address filled in).</summary>
    internal static GatewayGuardRequest For(HttpContext context, string routeClass, string? subject = null) =>
        new(routeClass, Address(context), subject);

    /// <summary>Sets Retry-After and throws "auth.throttled".</summary>
    public static Exception Throttled(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
        if (!context.Response.HasStarted)
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        throw new GatewayProtocolException("auth.throttled");
    }

    internal static string RouteClass(string rawTarget) => rawTarget switch
    {
        "/health/live" or "/health/ready" => "health",
        "/martlet/v1/pair" => "pair",
        GatewayPairingCode.Path => "pair-code",
        Martlet.Core.Network.NetworkPairing.Path => "join",
        _ when rawTarget.StartsWith("/martlet/v1/signin/", StringComparison.Ordinal) ||
               rawTarget == "/martlet/v1/signin" => "signin",
        _ => "credential"
    };

    private bool Applies(string routeClass, GatewaySourceKind kind) =>
        kind == GatewaySourceKind.Outside || InternetReachable || routeClass == "signin";

    /// <summary>Set when this host's own roster entry lists outside addresses.</summary>
    internal bool Listed { get => listed; set => listed = value; }
    private volatile bool listed;

    /// <summary>Whether limits apply to every source: the owner said so, or the roster lists outside addresses for this host.</summary>
    public bool InternetReachable => exposure.InternetReachable || listed || exposure.OutsideAddresses is { Count: > 0 };

    /// <summary>Runs before every handler: refuses pairing from outside home unless allowed, spends the source's budget
    /// for routes anyone may call, and refuses a locked-out source. Requests from this computer and the home network pass
    /// untouched (no lock, no lookup; pairing windows already close after five wrong tries) unless the host is
    /// internet-reachable; sign-in is limited from every source.</summary>
    internal void Admit(HttpContext context, string routeClass)
    {
        var kind = Source(context);
        if (!Applies(routeClass, kind)) return;
        var address = Address(context);
        var now = clock.GetUtcNow();
        if (routeClass == "pair-code" && kind == GatewaySourceKind.Outside && !exposure.AllowPairingOutsideHome)
        {
            Add(now, routeClass, address, kind, "refused", "pair.outside_home", null);
            log(LogLevels.Warn, $"Refused a pairing code from {address} (outside home). Typed pairing codes work on the home network " +
                "unless \"Allow pairing codes from outside home\" is on.", "guard-refused|" + address);
            CountFailure(now, routeClass + "|" + address);
            throw new GatewayProtocolException("pair.outside_home");
        }
        var wait = Wait(now, routeClass, address);
        if (wait <= TimeSpan.Zero) return;
        Add(now, routeClass, address, kind, "throttled", "auth.throttled", null);
        log(LogLevels.Warn, $"Throttled {routeClass} requests from {address} ({Describe(kind)}) for {Seconds(wait)}.",
            "guard-throttled|" + routeClass + "|" + address);
        throw Throttled(context, wait);
    }

    /// <summary>Spends the address's budget (routes anyone may call) and returns how long it must wait (zero: admitted).</summary>
    private TimeSpan Wait(DateTimeOffset now, string routeClass, string address)
    {
        lock (gate)
        {
            if (routeClass != "credential")
            {
                var budget = budgets.GetValueOrDefault(address);
                if (now - budget.Start >= TimeSpan.FromMinutes(1)) budget = (now, 0);
                if (budget.Count >= UnauthenticatedRequestsPerMinute)
                {
                    budgets[address] = budget;
                    return budget.Start + TimeSpan.FromMinutes(1) - now;
                }
                budgets[address] = (budget.Start, budget.Count + 1);
                if (budgets.Count > MaximumTracked) Prune(now);
            }
            return counters.TryGetValue(routeClass + "|" + address, out var counter) && counter.LockedUntil > now
                ? counter.LockedUntil - now : TimeSpan.Zero;
        }
    }

    /// <summary>Called by the dispatcher with a failure code; counts it when it is an authentication failure on a route
    /// that does not record its own outcome.</summary>
    internal void Failed(HttpContext context, string routeClass, string code)
    {
        if (routeClass == "signin" || !CountedCodes.Contains(code) || code == "pair.outside_home") return;
        var kind = Source(context);
        if (!Applies(routeClass, kind)) return;
        var address = Address(context);
        var now = clock.GetUtcNow();
        Add(now, routeClass, address, kind, "failure", code, null);
        var lockout = CountFailure(now, routeClass + "|" + address);
        if (lockout is { } wait)
            log(LogLevels.Warn, $"Locked out {routeClass} requests from {address} ({Describe(kind)}) for {Seconds(wait)} after " +
                $"repeated failures ({code}).", "guard-locked|" + routeClass + "|" + address);
    }

    /// <summary>Records a successful pairing or join (the device it admitted).</summary>
    internal void Succeeded(HttpContext context, string routeClass, string code, string subject) =>
        Record(For(context, routeClass, subject), GatewayGuardOutcome.Success, code, subject, Source(context));

    public bool TryAdmit(GatewayGuardRequest request, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            retryAfter = TimeSpan.Zero;
            foreach (var key in Keys(request.RouteClass, request.RemoteAddress, request.Subject))
                if (counters.TryGetValue(key, out var counter) && counter.LockedUntil - now > retryAfter)
                    retryAfter = counter.LockedUntil - now;
        }
        if (retryAfter <= TimeSpan.Zero) return true;
        var kind = exposure.TreatAllAsOutside ? GatewaySourceKind.Outside : Classify(Parse(request.RemoteAddress));
        Add(now, request.RouteClass, request.RemoteAddress, kind, "throttled", "auth.throttled", request.Subject);
        return false;
    }

    public void Record(GatewayGuardRequest request, GatewayGuardOutcome outcome, string code, string? subject)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = exposure.TreatAllAsOutside ? GatewaySourceKind.Outside : Classify(Parse(request.RemoteAddress));
        Record(request, outcome, code, subject ?? request.Subject, kind);
    }

    private void Record(GatewayGuardRequest request, GatewayGuardOutcome outcome, string code, string? subject, GatewaySourceKind kind)
    {
        var now = clock.GetUtcNow();
        var clean = Clean(subject);
        var keys = Keys(request.RouteClass, request.RemoteAddress, clean).ToArray();
        if (outcome == GatewayGuardOutcome.Success)
        {
            Add(now, request.RouteClass, request.RemoteAddress, kind, "success", code, clean);
            lock (gate)
                foreach (var key in keys) counters.Remove(key);
            if (request.RouteClass is not ("pair" or "pair-code" or "join")) // pairing already logs the device it paired
                log(LogLevels.Info, $"{Title(request.RouteClass)} succeeded for {clean ?? "a device"} from {request.RemoteAddress} ({Describe(kind)}).", null);
            return;
        }
        Add(now, request.RouteClass, request.RemoteAddress, kind, "failure", code, clean);
        TimeSpan? longest = null;
        foreach (var key in keys)
            if (CountFailure(now, key) is { } wait && (longest is null || wait > longest)) longest = wait;
        log(LogLevels.Warn, $"{Title(request.RouteClass)} failed for {clean ?? "an unknown subject"} from {request.RemoteAddress} " +
            $"({Describe(kind)}): {code}." + (longest is { } l ? $" Locked out for {Seconds(l)}." : ""),
            "guard-failed|" + request.RouteClass + "|" + request.RemoteAddress);
    }

    /// <summary>Counts a failure; returns the new lockout when this failure starts or extends one.</summary>
    private TimeSpan? CountFailure(DateTimeOffset now, string key)
    {
        lock (gate)
        {
            if (!counters.TryGetValue(key, out var counter) || now - counter.Since > FailureWindow && counter.LockedUntil <= now)
                counter = new() { Since = now };
            counter.Failures++;
            counters[key] = counter;
            Interlocked.Increment(ref failures);
            if (counters.Count > MaximumTracked) Prune(now);
            if (counter.Failures < FreeFailures) return null;
            var seconds = Math.Min(MaximumLockout.TotalSeconds, Math.Pow(2, Math.Min(counter.Failures - FreeFailures, 20)));
            counter.LockedUntil = now + TimeSpan.FromSeconds(seconds);
            return counter.LockedUntil - now;
        }
    }

    private static IEnumerable<string> Keys(string routeClass, string address, string? subject)
    {
        yield return routeClass + "|" + address;
        if (Clean(subject) is { } s) yield return routeClass + "|subject|" + s;
    }

    private void Add(DateTimeOffset now, string routeClass, string address, GatewaySourceKind kind, string outcome, string code, string? subject)
    {
        if (outcome == "throttled") Interlocked.Increment(ref throttled);
        else if (outcome == "success") Interlocked.Increment(ref successes);
        lock (gate)
        {
            audit.Enqueue(new()
            {
                At = now, RouteClass = routeClass, Source = address, SourceKind = kind.ToString().ToLowerInvariant(),
                Outcome = outcome, Code = code, Subject = subject
            });
            while (audit.Count > AuditCapacity) audit.Dequeue();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in counters.Where(c => c.Value.LockedUntil <= now && now - c.Value.Since > FailureWindow)
                     .Select(c => c.Key).ToArray())
            counters.Remove(stale);
        foreach (var stale in budgets.Where(b => now - b.Value.Start >= TimeSpan.FromMinutes(1)).Select(b => b.Key).ToArray())
            budgets.Remove(stale);
        // Still too many (a flood of distinct addresses): forget the oldest rather than grow without bound.
        foreach (var oldest in counters.OrderBy(c => c.Value.Since).Take(Math.Max(0, counters.Count - MaximumTracked / 2))
                     .Select(c => c.Key).ToArray())
            counters.Remove(oldest);
        if (budgets.Count > MaximumTracked) budgets.Clear();
    }

    private static string? Clean(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var text = new string(subject.Where(c => c is >= ' ' and <= '~').Take(160).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static IPAddress? Parse(string address) => IPAddress.TryParse(address, out var parsed) ? parsed : null;

    private static string Describe(GatewaySourceKind kind) => kind switch
    {
        GatewaySourceKind.Loopback => "this computer",
        GatewaySourceKind.Home => "home network",
        _ => "outside home"
    };

    private static string Title(string routeClass) => routeClass switch
    {
        "pair" or "pair-code" => "Pairing",
        "join" => "Network pairing",
        "signin" => "Sign-in",
        _ => "Authentication"
    };

    private static string Seconds(TimeSpan wait) =>
        wait.TotalSeconds < 90 ? $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s" : $"{(int)Math.Ceiling(wait.TotalMinutes)} min";

    private sealed class Counter
    {
        internal DateTimeOffset Since { get; init; }
        internal int Failures { get; set; }
        internal DateTimeOffset LockedUntil { get; set; }
    }
}

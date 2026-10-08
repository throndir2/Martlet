using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Martlet.Core.Network;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>How this PC last reached one host: over its home address or one of its outside addresses
/// (docs/NETWORK.md, "Reaching your network from outside home").</summary>
/// <param name="Origin">The host's home origin (https://&lt;private IP&gt;:&lt;port&gt;), which pairings and signatures use.</param>
/// <param name="Route">"home", "outside" or "none" (not connected yet, or nothing answered).</param>
/// <param name="Address">The address that answered ("192.168.1.20:9443" or an outside address).</param>
/// <param name="Error">Why the last connection attempt failed, when it did.</param>
/// <param name="Connections">How many connections this PC opened (or tried to open) to the host since Martlet started.
/// Requests share kept connections (<see cref="Audio2FaceHostClient.MaximumConnectionsPerHost"/>), so this grows slowly.</param>
public sealed record HostRouteStatus(string Origin, string? HostId, string Route, string? Address, DateTimeOffset? At, string? Error,
    DateTimeOffset? ErrorAt, IReadOnlyList<string> Outside, long Connections = 0);

/// <summary>
/// Chooses which address a connection to a host dials. Requests keep the host's home origin (so TLS still checks the host
/// key pinned in the roster and request signatures are unchanged); only the TCP connection goes to the home address or,
/// when that doesn't answer, to one of the host's owner-set outside addresses (<see cref="NetworkMember.Addresses"/>).
/// The address that worked last is tried first, so the home path never waits on an outside one; while an outside address
/// is in use the home address is tried alongside it and wins when it answers within a moment (back home).
/// </summary>
public static class HostRoutes
{
    /// <summary>How long the home address may take to connect before outside addresses are tried (only when the host has
    /// some; otherwise the connection is dialed exactly as before).</summary>
    public static readonly TimeSpan HomeConnectWindow = TimeSpan.FromMilliseconds(1500);
    /// <summary>How long the home address may answer first once an outside address is in use.</summary>
    public static readonly TimeSpan HomeHeadStart = TimeSpan.FromMilliseconds(100);
    /// <summary>How long a home address that presented another key (another network's computer at that address) is skipped.</summary>
    public static readonly TimeSpan WrongKeyBackoff = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    /// <summary>Address-and-key pairs of hosts whose routes are kept apart from other hosts with the same home origin
    /// (<see cref="KeepApart"/>).</summary>
    private static readonly ConcurrentDictionary<string, byte> Apart = new(StringComparer.Ordinal);

    /// <summary>Raised (on the connecting thread) when a host is reached over another route than before (home or outside, or
    /// another outside address), or when nothing answered; for the desktop log and status.</summary>
    public static event Action<HostRouteStatus>? RouteChanged;

    /// <summary>For tests and rehearsals: the clock route decisions are stamped with.</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Takes every active host's outside addresses from <paramref name="roster"/> (null leaves things as they are).</summary>
    public static void Update(NetworkRoster? roster)
    {
        if (roster is null) return;
        foreach (var host in roster.Members.Where(m => m.IsHost && m.Origin is not null))
            Set(host.Origin!, host.Id, host.Removed ? [] : host.Addresses ?? [], host.Spki);
    }

    /// <summary>Keeps the routes of the host at <paramref name="origin"/> with the pinned key <paramref name="spki"/> apart from
    /// every other host with the same home origin. A host a friend shares with this PC has its owner's home address, which one
    /// of this PC's own hosts may also have: neither then changes how the other is reached. Calls that name this key
    /// (<c>spki</c>) use its own entry.</summary>
    public static void KeepApart(string origin, string spki)
    {
        if (Key(origin) is { } key) Apart.TryAdd(key + " " + spki, 0);
    }

    /// <summary>Sets one host's outside addresses (normalized; invalid ones are dropped). <paramref name="spki"/>: the host's
    /// pinned key, which matters only for a host kept apart (<see cref="KeepApart"/>).</summary>
    public static void Set(string origin, string? hostId, IEnumerable<string> outside, string? spki = null)
    {
        if (Key(origin, spki) is not { } key) return;
        var list = outside.Select(NetworkRoster.NormalizeAddress).OfType<string>().Distinct(StringComparer.Ordinal)
            .Take(NetworkRoster.MaximumAddresses).ToArray();
        var entry = Entries.GetOrAdd(key, _ => new Entry(origin));
        lock (entry)
        {
            entry.HostId = hostId ?? entry.HostId;
            if (!entry.Outside.SequenceEqual(list)) entry.Outside = list;
            if (entry.Outside.Length == 0 && entry.Route == "outside") entry.Route = "none";
        }
    }

    /// <summary>Sets one host's outside addresses from what this PC saved with its pairing, unless it already has some (from the
    /// network roster, which wins).</summary>
    public static void Prime(string origin, string? hostId, IEnumerable<string> outside, string? spki = null)
    {
        if (Key(origin, spki) is { } key && Entries.TryGetValue(key, out var entry) && entry.Outside.Length > 0) return;
        Set(origin, hostId, outside, spki);
    }

    /// <summary>How this PC last reached each host it knows outside addresses for or has connected to.</summary>
    public static IReadOnlyList<HostRouteStatus> Snapshot() =>
        Entries.Values.Select(e => e.Status()).OrderBy(s => s.HostId ?? s.Origin, StringComparer.Ordinal).ToArray();

    /// <summary>How this PC last reached the host at <paramref name="origin"/> (with the pinned key <paramref name="spki"/>, for a
    /// host kept apart), or null when it never tried.</summary>
    public static HostRouteStatus? For(string? origin, string? spki = null) =>
        Key(origin, spki) is { } key && Entries.TryGetValue(key, out var e) ? e.Status() : null;

    /// <summary>Forgets every route (tests).</summary>
    internal static void Reset()
    {
        Entries.Clear();
        Apart.Clear();
    }

    /// <summary>
    /// Checks one address of a host without a credential: dials <paramref name="address"/> ("name:port"; null for the home
    /// address), checks the TLS key against <paramref name="spki"/> as a paired connection does, and asks for
    /// <c>GET /health/live</c>. Returns whether it answered, how long it took and, when not, why ("refused", "no answer in
    /// time", "name not found", "another key", "unexpected answer").
    /// </summary>
    public static async Task<(bool Reachable, long Milliseconds, string? Problem)> ProbeAsync(string origin, string spki, string? address,
        TimeSpan timeout, CancellationToken token = default)
    {
        var home = new Uri(origin);
        var endpoint = address is null ? new DnsEndPoint(home.Host.Trim('[', ']'), home.Port) : Parse(NetworkRoster.NormalizeAddress(address) ??
            throw new ArgumentException("Invalid outside address.", nameof(address)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancel.CancelAfter(timeout);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var wrongKey = false;
        try
        {
            await using var stream = await DialAsync(endpoint, cancel.Token).ConfigureAwait(false);
            await using var tls = new System.Net.Security.SslStream(stream, false, (_, certificate, chain, errors) =>
            {
                var ok = Audio2FaceHostClient.ValidateCertificate(certificate, chain, errors, spki, TimeProvider.System);
                wrongKey = !ok;
                return ok;
            });
            await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = home.Host.Trim('[', ']'),
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, cancel.Token).ConfigureAwait(false);
            var request = System.Text.Encoding.ASCII.GetBytes($"GET /health/live HTTP/1.1\r\nHost: {home.Authority}\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(request, cancel.Token).ConfigureAwait(false);
            var buffer = new byte[512];
            var read = await tls.ReadAsync(buffer, cancel.Token).ConfigureAwait(false);
            var answer = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            // The pinned key answered over HTTP: reachable. Anything but 200 (for example 429 while the address is throttled) is noted.
            if (!answer.StartsWith("HTTP/1.1 ", StringComparison.Ordinal)) return (false, clock.ElapsedMilliseconds, "unexpected answer");
            return answer.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
                ? (true, clock.ElapsedMilliseconds, null)
                : (true, clock.ElapsedMilliseconds, "answered " + new string(answer[9..].TakeWhile(ch => ch is not ('\r' or '\n')).Take(40).ToArray()));
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or IOException or
            System.Security.Authentication.AuthenticationException)
        {
            if (token.IsCancellationRequested) throw;
            return (false, clock.ElapsedMilliseconds, wrongKey ? "another key" : Describe(error));
        }
    }

    /// <summary>The TLS check refused the key a connection to <paramref name="origin"/> presented: if that was the home
    /// address, it is skipped for a while (another network's computer has the same address here).</summary>
    internal static void KeyRejected(string origin, string? spki = null)
    {
        if (Key(origin, spki) is not { } key || !Entries.TryGetValue(key, out var entry)) return;
        lock (entry)
        {
            var now = Clock.GetUtcNow();
            if (entry.LastDialed == "home")
            {
                entry.HomeSkippedUntil = now + WrongKeyBackoff;
                entry.Fail(now, $"The home address {entry.HomeAddress} answered with another key (a different computer there, so " +
                    (entry.Outside.Length > 0 ? "Martlet uses the host's outside addresses for now)." : "this PC isn't at home)."));
            }
            else entry.Fail(now, $"The outside address {entry.Address} answered with another key, so Martlet didn't use it. Check " +
                "that it forwards to this host (and not to another computer or a proxy that ends TLS).");
        }
    }

    /// <summary>SocketsHttpHandler.ConnectCallback for connections to a host (<paramref name="spki"/>: its pinned key, or null
    /// while pairing).</summary>
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, string? spki, CancellationToken token)
    {
        var target = context.DnsEndPoint;
        var key = Key(target.Host, target.Port, spki);
        var entry = key is null ? null : Entries.GetOrAdd(key, k => new Entry(Origin(target.Host, target.Port)));
        if (entry is null) return await DialAsync(target, token).ConfigureAwait(false);
        Interlocked.Increment(ref entry.Connections);
        string[] outside;
        bool preferOutside;
        var now = Clock.GetUtcNow();
        lock (entry)
        {
            outside = entry.Outside;
            preferOutside = outside.Length > 0 && (entry.Route == "outside" || entry.HomeSkippedUntil > now);
        }
        if (outside.Length == 0)
        {
            try
            {
                var stream = await DialAsync(target, token).ConfigureAwait(false);
                entry.Connected("home", entry.HomeAddress, Clock.GetUtcNow());
                return stream;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException && !token.IsCancellationRequested)
            {
                entry.Failed(Clock.GetUtcNow(), Shortage(error) is { } shortage
                    ? ShortageText(entry.HomeAddress, shortage)
                    : $"The home address {entry.HomeAddress} didn't answer ({Describe(error)}), and this host " +
                    "has no outside addresses (Devices › your Martlet network › Outside addresses).");
                throw;
            }
        }
        try { return await ConnectAnyAsync(entry, target, outside, preferOutside, now, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The handler's connect timeout (or the caller) gave up first.
            entry.Failed(Clock.GetUtcNow(), $"Couldn't reach the host at home ({entry.HomeAddress}) or outside ({string.Join(", ", outside)}) in time. " +
                "Check that this PC is online, the overlay network (Tailscale, ZeroTier, WireGuard) is connected, or the router still forwards the port.");
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectAnyAsync(Entry entry, DnsEndPoint target, string[] outside, bool preferOutside,
        DateTimeOffset now, CancellationToken token)
    {
        bool skipHome;
        lock (entry) skipHome = entry.HomeSkippedUntil > now;

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<(Stream? Stream, string Address, Exception? Error)>? home = skipHome ? null : TryAsync(target, entry.HomeAddress, cancel.Token);
        var errors = new List<string>();
        if (home is not null && !preferOutside)
        {
            // Home first; outside addresses only when it doesn't connect in time.
            var first = await Task.WhenAny(home, Task.Delay(HomeConnectWindow, token)).ConfigureAwait(false);
            if (first == home && home.Result.Stream is { } stream)
            {
                entry.Connected("home", entry.HomeAddress, Clock.GetUtcNow());
                return stream;
            }
            if (first == home) errors.Add($"home {entry.HomeAddress}: {Describe(home.Result.Error)}");
        }
        else if (home is not null)
        {
            // Away from home: back home the home address answers first.
            var first = await Task.WhenAny(home, Task.Delay(HomeHeadStart, token)).ConfigureAwait(false);
            if (first == home && home.Result.Stream is { } stream)
            {
                entry.Connected("home", entry.HomeAddress, Clock.GetUtcNow());
                return stream;
            }
        }
        var pending = outside.Select(a => TryAsync(Parse(a), a, cancel.Token)).ToList();
        if (home is not null && !home.IsCompleted) pending.Add(home);
        else if (home is { Result.Error: { } homeError } && errors.Count == 0) errors.Add($"home {entry.HomeAddress}: {Describe(homeError)}");
        SocketError? shortage = home is { IsCompleted: true } && home.Result.Error is { } homeFailure ? Shortage(homeFailure) : null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            var (stream, address, error) = done.Result;
            if (stream is not null)
            {
                await cancel.CancelAsync().ConfigureAwait(false);
                foreach (var other in pending) _ = other.ContinueWith(t => t.Result.Stream?.Dispose(), TaskScheduler.Default);
                entry.Connected(done == home ? "home" : "outside", address, Clock.GetUtcNow());
                return stream;
            }
            shortage ??= Shortage(error);
            errors.Add($"{(done == home ? "home " : "")}{address}: {Describe(error)}");
        }
        token.ThrowIfCancellationRequested();
        // This PC itself had no free ports or socket buffers: say so, rather than blame the host or the router.
        entry.Failed(Clock.GetUtcNow(), shortage is { } local ? ShortageText(entry.HomeAddress, local)
            : "Couldn't reach the host at home or outside: " + string.Join("; ", errors) + ". Check that this " +
            "PC is online, the overlay network (Tailscale, ZeroTier, WireGuard) is connected, or the router still forwards the port.");
        throw new SocketException((int)(shortage ?? SocketError.HostUnreachable));
    }

    private static async Task<(Stream? Stream, string Address, Exception? Error)> TryAsync(DnsEndPoint endpoint, string address,
        CancellationToken token)
    {
        try { return (await DialAsync(endpoint, token).ConfigureAwait(false), address, null); }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ArgumentException) { return (null, address, error); }
    }

    private static async Task<Stream> DialAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static DnsEndPoint Parse(string address)
    {
        var colon = address.LastIndexOf(':');
        return new DnsEndPoint(address[..colon].Trim('[', ']'), int.Parse(address[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Why a connection attempt failed, in a few words: "refused", "name not found", "no answer in time",
    /// "unreachable", "this PC is out of network resources" (<see cref="Shortage"/>), or the socket error's name.</summary>
    public static string Describe(Exception? error) => error switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "refused",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "name not found",
        SocketException { SocketErrorCode: SocketError.TimedOut } or OperationCanceledException => "no answer in time",
        SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable } => "unreachable",
        SocketException { SocketErrorCode: SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets } =>
            "this PC is out of network resources",
        SocketException socket => socket.SocketErrorCode.ToString(),
        null => "no answer in time",
        _ => error.Message
    };

    /// <summary>The socket error when <paramref name="error"/> (or an exception inside it) says this PC itself ran out of
    /// network resources: Windows had no free connection port or socket buffer (WSAENOBUFS, NoBufferSpaceAvailable) or no
    /// free socket (WSAEMFILE). The host is not at fault then. Null for any other failure.</summary>
    public static SocketError? Shortage(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException)
            if (error is SocketException { SocketErrorCode: SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets } socket)
                return socket.SocketErrorCode;
        return null;
    }

    /// <summary>What to tell the owner when this PC ran out of network resources while it connected to <paramref name="address"/>.</summary>
    public static string ShortageText(string address, SocketError error) =>
        $"This PC ran out of network resources, so it couldn't open a connection to {address} (Windows: {error}, no free " +
        "connection ports or socket buffers). The host may be fine. Martlet tries again by itself; if this keeps happening, close " +
        "programs that open many connections, or restart this PC.";

    private static string? Key(string? origin, string? spki = null)
    {
        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return null;
        return Key(uri.Host, uri.Port, spki);
    }

    private static string? Key(string host, int port, string? spki = null)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return null;
        var key = Origin(address.ToString(), port);
        return spki is not null && Apart.ContainsKey(key + " " + spki) ? key + " " + spki : key;
    }

    private static string Origin(string host, int port)
    {
        var address = IPAddress.Parse(host.Trim('[', ']'));
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"https://[{address}]:{port}" : $"https://{address}:{port}";
    }

    private sealed class Entry(string origin)
    {
        internal string Origin { get; } = origin;
        internal string HomeAddress { get; } = origin["https://".Length..];
        internal string? HostId { get; set; }
        internal string[] Outside { get; set; } = [];
        internal string Route { get; set; } = "none";
        internal string? Address { get; set; }
        internal string? LastDialed { get; set; }
        internal DateTimeOffset? At { get; set; }
        internal string? Error { get; set; }
        internal DateTimeOffset? ErrorAt { get; set; }
        internal DateTimeOffset HomeSkippedUntil { get; set; }
        internal long Connections;

        internal void Connected(string route, string address, DateTimeOffset now)
        {
            bool changed;
            lock (this)
            {
                changed = Route != route || Address != address;
                Route = route;
                Address = address;
                LastDialed = route;
                At = now;
            }
            if (changed) Raise(Status());
        }

        internal void Failed(DateTimeOffset now, string error)
        {
            bool changed;
            lock (this)
            {
                changed = Route != "none" || Error != error;
                Route = "none";
                Fail(now, error);
            }
            if (changed) Raise(Status());
        }

        private static void Raise(HostRouteStatus status)
        {
            try { RouteChanged?.Invoke(status); }
            catch (Exception) { } // a listener's failure never breaks a connection
        }

        internal void Fail(DateTimeOffset now, string error)
        {
            Error = error;
            ErrorAt = now;
        }

        internal HostRouteStatus Status()
        {
            lock (this) return new(Origin, HostId, Route, Address, At, Error, ErrorAt, Outside, Interlocked.Read(ref Connections));
        }
    }
}

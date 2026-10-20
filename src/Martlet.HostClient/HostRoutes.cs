using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Martlet.Core.Network;

namespace Martlet.HostClient;

/// <summary>How this PC last reached one host: over its home address or one of its outside addresses
/// (docs/NETWORK.md, "Reaching your network from outside home").</summary>
/// <param name="Origin">The host's home origin (https://&lt;private IP&gt;:&lt;port&gt;), which pairings and signatures use.</param>
/// <param name="Route">"home", "outside" or "none" (not connected yet, or nothing answered).</param>
/// <param name="Address">The address that answered ("192.168.1.20:9443" or an outside address).</param>
/// <param name="Error">Why the last connection attempt failed, when it did.</param>
/// <param name="Connections">How many connections this PC opened (or tried to open) to the host since Martlet started.
/// Requests share kept connections (<see cref="HostTransport.MaximumConnectionsPerHost"/>), so this grows slowly.</param>
/// <param name="MovedTo">Where on the home network the host answers now ("192.168.1.37:9443") when its computer got another
/// address than the one it was paired at, or null (<see cref="HostRoutes.Moved"/>).</param>
public sealed record HostRouteStatus(string Origin, string? HostId, string Route, string? Address, DateTimeOffset? At, string? Error,
    DateTimeOffset? ErrorAt, IReadOnlyList<string> Outside, long Connections = 0, string? MovedTo = null);

/// <summary>A host found at another address on the home network (<paramref name="To"/>), or back at its paired one (null).</summary>
public sealed record HostMove(string Origin, string? HostId, string? From, string? To);

/// <summary>
/// Chooses which address a connection to a host dials. Requests keep the host's home origin (so TLS still checks the host
/// key pinned in the roster and request signatures are unchanged); only the TCP connection goes to the home address or,
/// when that doesn't answer, to one of the host's owner-set outside addresses (<see cref="NetworkMember.Addresses"/>).
/// The address that worked last is tried first, so the home path never waits on an outside one; while an outside address
/// is in use the home address is tried alongside it and wins when it answers within a moment (back home).
/// <para>When the home address doesn't connect within <see cref="HomeConnectWindow"/>, Martlet also asks the home network where
/// the host is (<see cref="HostFind"/>) and dials the address that answers once the host there shows its pinned key: its
/// computer got another address after a restart. That address is then dialed instead of the paired one (<see cref="Moved"/>);
/// the paired origin stays the host's name for TLS, requests and signatures.</para>
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
    /// <summary>How long a look for a host on the home network waits for answers (it stops at the first one).</summary>
    public static readonly TimeSpan FindWindow = TimeSpan.FromSeconds(2);
    /// <summary>How long after one look for a host the next may start.</summary>
    public static readonly TimeSpan FindBackoff = TimeSpan.FromSeconds(20);
    /// <summary>How long the pinned-key check of an address that answered a look may take.</summary>
    public static readonly TimeSpan FoundProbeTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>Asks the home network where hosts are (<see cref="HostFind.FindAsync"/>); tests replace it.</summary>
    internal static Func<IReadOnlyCollection<string>, TimeSpan, CancellationToken, Task<IReadOnlyList<HostSighting>>> Find { get; set; } =
        (wanted, window, token) => HostFind.FindAsync(wanted, window, token);

    /// <summary>This computer's home addresses, tried when this PC itself answered a look over loopback; tests replace it.</summary>
    internal static Func<IReadOnlyList<IPAddress>> OwnAddresses { get; set; } = HostFind.HomeAddresses;

    /// <summary>Raised when a host was found at another address on the home network, or back at its paired one, so the desktop
    /// keeps that address with the pairing. Never raised on the connecting thread's lock.</summary>
    public static event Action<HostMove>? Moved;

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

    /// <summary>Remembers a paired host so a connection that can't reach its home address can look for it on the home network:
    /// its host ID, pinned key and where it was last found (<paramref name="movedTo"/>, "ip:port", or null for its paired
    /// address). Raises nothing.</summary>
    public static void Remember(string origin, string hostId, string spki, string? movedTo)
    {
        if (Key(origin, spki) is not { } key) return;
        var entry = Entries.GetOrAdd(key, _ => new Entry(origin));
        lock (entry)
        {
            entry.HostId ??= hostId;
            entry.Spki = spki;
            // What this PC found since Martlet started is newer than what it saved.
            if (!entry.Remembered) entry.SetMoved(movedTo);
            entry.Remembered = true;
        }
    }

    /// <summary>Records that the host at <paramref name="origin"/> answers at <paramref name="address"/> ("ip:port") now, or at
    /// its paired address again (null), and raises <see cref="Moved"/> when that changed. Used when this PC itself moved its
    /// own host service to its new address.</summary>
    public static void Move(string origin, string? spki, string? address)
    {
        if (Key(origin, spki) is not { } key) return;
        var entry = Entries.GetOrAdd(key, _ => new Entry(origin));
        HostMove? move;
        lock (entry) move = entry.SetMoved(address);
        if (move is not null) RaiseMoved(move);
    }

    private static void RaiseMoved(HostMove move)
    {
        try { Moved?.Invoke(move); }
        catch (Exception) { } // a listener's failure never breaks a connection
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
        Find = (wanted, window, token) => HostFind.FindAsync(wanted, window, token);
        OwnAddresses = HostFind.HomeAddresses;
    }

    /// <summary>
    /// Checks one address of a host without a credential: dials <paramref name="address"/> ("name:port"; null for the home
    /// address, or where the host was last found on the home network), checks the TLS key against <paramref name="spki"/> as a
    /// paired connection does, and asks for
    /// <c>GET /health/live</c>. Returns whether it answered, how long it took and, when not, why ("refused", "no answer in
    /// time", "name not found", "another key", "unexpected answer").
    /// </summary>
    public static async Task<(bool Reachable, long Milliseconds, string? Problem)> ProbeAsync(string origin, string spki, string? address,
        TimeSpan timeout, CancellationToken token = default)
    {
        var home = new Uri(origin);
        address ??= Key(origin, spki) is { } key && Entries.TryGetValue(key, out var known) ? known.MovedAddress : null;
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
                var ok = HostTransport.ValidateCertificate(certificate, chain, errors, spki, TimeProvider.System);
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
        HostMove? move = null;
        lock (entry)
        {
            var now = Clock.GetUtcNow();
            if (entry.LastDialed == "home")
            {
                var dialed = entry.HomeDialAddress;
                entry.HomeSkippedUntil = now + WrongKeyBackoff;
                // Another computer has the address the host was found at: look for it again instead.
                move = entry.SetMoved(null, keepSkip: true);
                entry.Fail(now, $"The home address {dialed} answered with another key (a different computer there, so " +
                    (entry.Outside.Length > 0 ? "Martlet uses the host's outside addresses for now)." : "Martlet looks for the host on your home network)."));
            }
            else entry.Fail(now, $"The outside address {entry.Address} answered with another key, so Martlet didn't use it. Check " +
                "that it forwards to this host (and not to another computer or a proxy that ends TLS).");
        }
        if (move is not null) RaiseMoved(move);
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
        bool preferOutside, look;
        string? moved;
        var now = Clock.GetUtcNow();
        lock (entry)
        {
            if (spki is not null) entry.Spki = spki;
            outside = entry.Outside;
            moved = entry.MovedAddress;
            preferOutside = outside.Length > 0 && (entry.Route == "outside" || entry.HomeSkippedUntil > now);
            look = !preferOutside && entry.CanLook(now);
        }
        if (outside.Length == 0 && !look)
        {
            var homeAddress = moved ?? entry.HomeAddress;
            try
            {
                var stream = await DialAsync(moved is null ? target : Parse(moved), token).ConfigureAwait(false);
                entry.Connected("home", homeAddress, Clock.GetUtcNow());
                return stream;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException && !token.IsCancellationRequested)
            {
                entry.Failed(Clock.GetUtcNow(), Shortage(error) is { } shortage
                    ? ShortageText(homeAddress, shortage)
                    : $"The home address {homeAddress} didn't answer ({Describe(error)}), and this host " +
                    "has no outside addresses (Share › Your Martlet network › Outside addresses).");
                throw;
            }
        }
        try { return await ConnectAnyAsync(entry, target, outside, preferOutside, look, now, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The handler's connect timeout (or the caller) gave up first.
            entry.Failed(Clock.GetUtcNow(), outside.Length == 0
                ? $"Couldn't reach the host at home ({entry.HomeDialAddress}) in time, and Martlet didn't find it elsewhere on your home " +
                  "network. Check that its computer is on and connected to your home network."
                : $"Couldn't reach the host at home ({entry.HomeDialAddress}) or outside ({string.Join(", ", outside)}) in time. " +
                  "Check that this PC is online, the overlay network (Tailscale, ZeroTier, WireGuard) is connected, or the router still forwards the port.");
            throw;
        }
    }

    /// <summary>What the look for a host on the home network reports as its address when nothing answered.</summary>
    private const string FoundNothing = "elsewhere on your home network";

    private static async ValueTask<Stream> ConnectAnyAsync(Entry entry, DnsEndPoint target, string[] outside, bool preferOutside, bool look,
        DateTimeOffset now, CancellationToken token)
    {
        bool skipHome;
        string? moved;
        lock (entry)
        {
            skipHome = entry.HomeSkippedUntil > now;
            moved = entry.MovedAddress;
        }
        var homeAddress = moved ?? entry.HomeAddress;

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<(Stream? Stream, string Address, Exception? Error)>? home = skipHome ? null
            : TryAsync(moved is null ? target : Parse(moved), homeAddress, cancel.Token);
        var errors = new List<string>();
        Exception? homeError = null;
        if (home is not null && !preferOutside)
        {
            // Home first; outside addresses (and a look on the home network) only when it doesn't connect in time.
            var first = await Task.WhenAny(home, Task.Delay(HomeConnectWindow, token)).ConfigureAwait(false);
            if (first == home && home.Result.Stream is { } stream)
            {
                entry.Connected("home", homeAddress, Clock.GetUtcNow());
                return stream;
            }
            if (first == home)
            {
                homeError = home.Result.Error;
                errors.Add($"home {homeAddress}: {Describe(homeError)}");
            }
        }
        else if (home is not null)
        {
            // Away from home: back home the home address answers first.
            var first = await Task.WhenAny(home, Task.Delay(HomeHeadStart, token)).ConfigureAwait(false);
            if (first == home && home.Result.Stream is { } stream)
            {
                entry.Connected("home", homeAddress, Clock.GetUtcNow());
                return stream;
            }
        }
        var pending = outside.Select(a => TryAsync(Parse(a), a, cancel.Token)).ToList();
        if (home is not null && !home.IsCompleted) pending.Add(home);
        else if (home is { Result.Error: { } failure } && errors.Count == 0)
        {
            homeError = failure;
            errors.Add($"home {homeAddress}: {Describe(failure)}");
        }
        // The host's computer may have another address on the home network now (a restart, a new DHCP lease).
        var found = look ? FoundAsync(entry, cancel.Token) : null;
        if (found is not null) pending.Add(found);
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
                entry.Connected(done == home || done == found ? "home" : "outside", address, Clock.GetUtcNow());
                return stream;
            }
            shortage ??= Shortage(error);
            if (done == home) homeError ??= error;
            errors.Add(done == found
                ? address == FoundNothing ? "not found elsewhere on your home network" : $"found at {address}: {Describe(error)}"
                : $"{(done == home ? "home " : "")}{address}: {Describe(error)}");
        }
        token.ThrowIfCancellationRequested();
        // This PC itself had no free ports or socket buffers: say so, rather than blame the host or the router.
        entry.Failed(Clock.GetUtcNow(), shortage is { } local ? ShortageText(homeAddress, local)
            : outside.Length == 0
                ? "Couldn't reach the host: " + string.Join("; ", errors) + ". Check that its computer is on and connected to your home " +
                  $"network, and that its firewall lets UDP {HostFind.Port} through so Martlet can find it. It has no outside addresses " +
                  "(Share › Your Martlet network › Outside addresses)."
                : "Couldn't reach the host at home or outside: " + string.Join("; ", errors) + ". Check that this " +
                  "PC is online, the overlay network (Tailscale, ZeroTier, WireGuard) is connected, or the router still forwards the port.");
        // Without outside addresses the home address's own error says best what happened (for example "refused").
        if (outside.Length == 0 && homeError is SocketException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(homeError);
        throw new SocketException((int)(shortage ?? SocketError.HostUnreachable));
    }

    /// <summary>Looks for the host on the home network (<see cref="LookAsync"/>) and dials the address it was found at.</summary>
    private static async Task<(Stream? Stream, string Address, Exception? Error)> FoundAsync(Entry entry, CancellationToken token)
    {
        string? address;
        try { address = await LookAsync(entry).WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException error) { return (null, FoundNothing, error); }
        return address is null ? (null, FoundNothing, null) : await TryAsync(Parse(address), address, token).ConfigureAwait(false);
    }

    /// <summary>One look at a time per host, at most every <see cref="FindBackoff"/>; callers share it.</summary>
    private static Task<string?> LookAsync(Entry entry)
    {
        lock (entry)
        {
            if (entry.Looking is { IsCompleted: false } running) return running;
            entry.LookedAt = Clock.GetUtcNow();
            return entry.Looking = Task.Run(() => SearchAsync(entry));
        }
    }

    /// <summary>Asks the home network where the host is (<see cref="Find"/>) and checks each address that answered with the
    /// host's pinned key (<see cref="ProbeAsync"/>). Returns the first that passes ("ip:port") and remembers it, or null.</summary>
    private static async Task<string?> SearchAsync(Entry entry)
    {
        string hostId, spki;
        lock (entry)
        {
            if (entry.HostId is null || entry.Spki is null) return null;
            hostId = entry.HostId;
            spki = entry.Spki;
        }
        IReadOnlyList<HostSighting> seen;
        try { seen = await Find([hostId], FindWindow, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException or IOException
            or OperationCanceledException)
        { return null; }
        var port = new Uri(entry.Origin).Port;
        var candidates = new List<string>();
        foreach (var sighting in seen)
            // This PC answered for a host it runs itself: that host is published on this PC's home address.
            foreach (var address in IPAddress.IsLoopback(sighting.Address) ? [.. OwnAddresses(), sighting.Address] : new[] { sighting.Address })
                if (address.AddressFamily == AddressFamily.InterNetwork && $"{address}:{port}" is var text && !candidates.Contains(text))
                    candidates.Add(text);
        foreach (var candidate in candidates)
        {
            var (reachable, _, _) = await ProbeAsync(entry.Origin, spki, candidate, FoundProbeTimeout).ConfigureAwait(false);
            if (!reachable) continue;
            HostMove? move;
            lock (entry) move = entry.SetMoved(candidate);
            if (move is not null) RaiseMoved(move);
            return candidate;
        }
        return null;
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

    /// <summary>Whether <paramref name="error"/> (or an exception inside it) says the host's computer refused the connection: it
    /// is on and reachable, but nothing listens on the gateway's port (the gateway isn't running there).</summary>
    public static bool Refused(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException)
            if (error is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return false;
    }

    /// <summary>What to tell the owner when the computer at <paramref name="address"/> refused the connection.</summary>
    public static string RefusedText(string address) =>
        $"{address} refused the connection: that computer is on, but the Martlet host's gateway isn't running there. " +
        "After a restart of that computer, Update host (Devices) starts it again.";

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
        /// <summary>The host's pinned key, so a look on the home network can check the host it finds.</summary>
        internal string? Spki { get; set; }
        /// <summary>Where on the home network the host answers now ("ip:port"), when that isn't its paired address.</summary>
        internal string? MovedAddress { get; private set; }
        internal Task<string?>? Looking { get; set; }
        internal DateTimeOffset LookedAt { get; set; } = DateTimeOffset.MinValue;
        /// <summary>Whether the saved pairing told where the host was found last (<see cref="Remember"/>).</summary>
        internal bool Remembered { get; set; }
        /// <summary>The home address a connection dials: where the host was found last, or its paired address.</summary>
        internal string HomeDialAddress
        {
            get { lock (this) return MovedAddress ?? HomeAddress; }
        }
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
            lock (this) return new(Origin, HostId, Route, Address, At, Error, ErrorAt, Outside, Interlocked.Read(ref Connections), MovedAddress);
        }

        /// <summary>Whether a connection may look for the host on the home network now: its ID and key are known and no look
        /// ran within <see cref="FindBackoff"/> (a running one is shared). Call with the lock held.</summary>
        internal bool CanLook(DateTimeOffset now) =>
            HostId is not null && Spki is not null && (Looking is { IsCompleted: false } || now - LookedAt >= FindBackoff);

        /// <summary>Sets where the host answers now (null or its paired address: back home). Returns the change, or null when
        /// nothing changed. A new address is no longer skipped for the key another computer showed at the old one. Call with the
        /// lock held.</summary>
        internal HostMove? SetMoved(string? address, bool keepSkip = false)
        {
            var normalized = address is null ? null : NetworkRoster.NormalizeAddress(address);
            if (normalized == HomeAddress) normalized = null;
            if (normalized == MovedAddress) return null;
            var move = new HostMove(Origin, HostId, MovedAddress, normalized);
            MovedAddress = normalized;
            if (!keepSkip) HomeSkippedUntil = default;
            return move;
        }
    }
}

using System.Buffers.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Core.Network;

/// <summary>A host that answered a find: its host ID and the address the answer came from.</summary>
public sealed record HostSighting(string HostId, IPAddress Address);

/// <summary>
/// Finds a paired Martlet host again after its computer got another address on the home network (docs/NETWORK.md, "When a
/// computer gets a new address"). A desktop that can't reach a host at its saved address sends
/// <c>{"martlet":"find-host","v":1,"q":&lt;random&gt;}</c> on UDP <see cref="Port"/> to loopback, the limited broadcast and each
/// private subnet's broadcast address (never a subnet scan). The computer a host runs on answers
/// <c>{"martlet":"host-here","v":1,"q":...,"hosts":["&lt;host ID&gt;"]}</c>: no addresses (the answer's source is the address),
/// keys or other data. Discovery supplies only an address; the desktop uses it only when the host there shows the TLS key
/// pinned at pairing. Martlet desktops answer Nearby's own find on the same port; each ignores the other's query.
/// </summary>
public static partial class HostFind
{
    /// <summary>The UDP port, shared with Martlet Nearby (the desktop's find of other Martlet desktops).</summary>
    public const int Port = 9444;
    /// <summary>"1" keeps every find on loopback (Invoke-MartletMcp.ps1 -LabCredentials sets it for lab desktops).</summary>
    public const string LoopbackOnlyVariable = "MARTLET_HOST_FIND_LOOPBACK";
    /// <summary>The most host IDs one answer lists.</summary>
    public const int MaximumHosts = 64;
    private const int MaximumQueryBytes = 512;
    private const int MaximumAnswerBytes = 8_192;

    /// <summary>When a find sends its query, after its start: at once, then again for a lost packet.</summary>
    public static readonly TimeSpan[] QueryTimes = [TimeSpan.Zero, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)];

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex IdentifierPattern();

    /// <summary>Private IPv4 (10/8, 172.16/12, 192.168/16).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    /// <summary>Private IPv4 or loopback: the only senders a host answers and the only answers a desktop takes.</summary>
    public static bool Allowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork && (IPAddress.IsLoopback(address) || IsPrivate(address));
    }

    public static byte[] Query(string query) => JsonSerializer.SerializeToUtf8Bytes(new { martlet = "find-host", v = 1, q = query });

    public static bool IsQuery(byte[] buffer, out string query)
    {
        query = "";
        if (buffer.Length > MaximumQueryBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "martlet") != "find-host" || Number(root, "v") != 1 ||
                Text(root, "q") is not { Length: > 0 and <= 24 } q)
                return false;
            query = q;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static byte[] Answer(string query, IEnumerable<string> hosts) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        martlet = "host-here", v = 1, q = query,
        hosts = hosts.Where(h => IdentifierPattern().IsMatch(h)).Distinct(StringComparer.Ordinal).Take(MaximumHosts).ToArray()
    });

    /// <summary>The host IDs in an answer to <paramref name="query"/>, or null when it isn't one.</summary>
    public static IReadOnlyList<string>? ParseAnswer(byte[] buffer, string query)
    {
        if (buffer.Length > MaximumAnswerBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "martlet") != "host-here" || Number(root, "v") != 1 ||
                Text(root, "q") != query || !root.TryGetProperty("hosts", out var list) || list.ValueKind != JsonValueKind.Array ||
                list.GetArrayLength() is 0 or > MaximumHosts)
                return null;
            var hosts = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id || !IdentifierPattern().IsMatch(id)) return null;
                if (!hosts.Contains(id)) hosts.Add(id);
            }
            return hosts;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Asks the home network where the hosts in <paramref name="wanted"/> (null: every host that answers) are: queries at
    /// <see cref="QueryTimes"/> to <paramref name="targets"/> (default <see cref="BroadcastTargets"/>), answers collected for at
    /// most <paramref name="window"/> and no longer once every wanted host answered. Returns each answering address of a wanted
    /// host (LAN addresses first). <paramref name="port"/> is for tests.
    /// </summary>
    public static async Task<IReadOnlyList<HostSighting>> FindAsync(IReadOnlyCollection<string>? wanted, TimeSpan window,
        CancellationToken token, IReadOnlyList<IPAddress>? targets = null, int port = Port)
    {
        if (wanted is { Count: 0 }) return [];
        var query = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(8));
        var packet = Query(query);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        IgnoreUnreachable(udp);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var seen = new List<HostSighting>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(window);
        var sending = SendAsync(udp, packet, targets ?? BroadcastTargets(), port, limit.Token);
        try
        {
            while (wanted is null || found.Count < wanted.Count)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(limit.Token).ConfigureAwait(false); }
                catch (SocketException) { continue; }
                var from = Plain(result.RemoteEndPoint.Address);
                if (!Allowed(from) || ParseAnswer(result.Buffer, query) is not { } hosts) continue;
                foreach (var id in hosts.Where(h => wanted is null || wanted.Contains(h)))
                {
                    if (seen.Any(s => s.HostId == id && s.Address.Equals(from))) continue;
                    seen.Add(new(id, from));
                    found.Add(id);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        await limit.CancelAsync().ConfigureAwait(false);
        try { await sending.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        token.ThrowIfCancellationRequested();
        return seen.OrderBy(s => IPAddress.IsLoopback(s.Address) ? 1 : 0).ToArray();
    }

    private static async Task SendAsync(UdpClient udp, byte[] packet, IReadOnlyList<IPAddress> targets, int port, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var at in QueryTimes)
        {
            var wait = at - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            foreach (var target in targets)
            {
                try { await udp.SendAsync(packet, new IPEndPoint(target, port), token).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
    }

    /// <summary>Loopback, the limited broadcast and each up adapter's private subnet broadcast; loopback only while
    /// <see cref="LoopbackOnlyVariable"/> is "1" (a lab desktop, so its finds never reach the owner's real hosts).</summary>
    public static IReadOnlyList<IPAddress> BroadcastTargets()
    {
        if (Environment.GetEnvironmentVariable(LoopbackOnlyVariable) == "1") return [IPAddress.Loopback];
        var targets = new List<IPAddress> { IPAddress.Loopback, IPAddress.Broadcast };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(unicast.Address)) continue;
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new IPAddress(address.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
                    if (!targets.Contains(broadcast)) targets.Add(broadcast);
                }
        }
        catch (NetworkInformationException) { }
        return targets;
    }

    /// <summary>This computer's home addresses now: private IPv4 addresses of up adapters that have an IPv4 default gateway
    /// (the home network, not a container bridge or a virtual switch).</summary>
    public static IReadOnlyList<IPAddress> HomeAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                .Distinct().ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    /// <summary>Stops Windows reporting an earlier datagram's "port unreachable" as a failed receive.</summary>
    public static void IgnoreUnreachable(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SioUdpConnReset = -1744830452;
        try { udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
        catch (Exception error) when (error is SocketException or PlatformNotSupportedException) { }
    }

    internal static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}

/// <summary>Answers <see cref="HostFind"/> queries for the hosts that run on this computer: private IPv4 and loopback senders
/// only, at most 40 answers per 10 seconds. A Martlet desktop answers in its Nearby responder instead (the same port).</summary>
public sealed class HostFindResponder : IDisposable
{
    private readonly Func<IReadOnlyList<string>> hosts;
    private readonly UdpClient udp;
    private readonly CancellationTokenSource stop = new();
    private readonly Queue<DateTime> answers = new();

    private HostFindResponder(Func<IReadOnlyList<string>> hosts, IPAddress address, int port)
    {
        this.hosts = hosts;
        udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            HostFind.IgnoreUnreachable(udp);
            udp.Client.Bind(new IPEndPoint(address, port));
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }

    /// <summary>The port it answers on (the given one, or the one chosen for port 0).</summary>
    public int Port => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

    /// <summary>How many queries it answered.</summary>
    public long Answered => Interlocked.Read(ref answered);
    private long answered;

    /// <summary>Binds UDP <paramref name="port"/> on <paramref name="address"/> (default every address) and starts answering;
    /// throws <see cref="SocketException"/> when the port is taken.</summary>
    public static HostFindResponder Start(Func<IReadOnlyList<string>> hosts, IPAddress? address = null, int port = HostFind.Port)
    {
        var responder = new HostFindResponder(hosts, address ?? IPAddress.Any, port);
        _ = responder.AnswerAsync();
        return responder;
    }

    private async Task AnswerAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(stop.Token).ConfigureAwait(false); }
            catch (SocketException) { continue; }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { return; }
            var reply = Reply(received.Buffer, received.RemoteEndPoint.Address, hosts(), answers);
            if (reply is null) continue;
            try
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref answered);
            }
            catch (SocketException) { }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { return; }
        }
    }

    /// <summary>The answer to one datagram, or null: not a find from a private or loopback sender, no hosts here, or more than
    /// 40 answers in the last 10 seconds (<paramref name="recent"/>). Also used by the desktop's Nearby responder.</summary>
    public static byte[]? Reply(byte[] buffer, IPAddress from, IReadOnlyList<string> hosts, Queue<DateTime> recent)
    {
        if (!HostFind.Allowed(from) || !HostFind.IsQuery(buffer, out var query) || hosts.Count == 0) return null;
        lock (recent)
        {
            var now = DateTime.UtcNow;
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromSeconds(10)) recent.Dequeue();
            if (recent.Count >= 40) return null;
            recent.Enqueue(now);
        }
        return HostFind.Answer(query, hosts);
    }

    public void Dispose()
    {
        stop.Cancel();
        udp.Dispose();
        stop.Dispose();
    }
}

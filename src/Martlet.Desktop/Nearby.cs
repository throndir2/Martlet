using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Desktop;

/// <summary>
/// Finds Martlet on the owner's other computers and lets one computer use another's hosts without typing a code.
/// <para>A Martlet desktop that runs a host itself, or reaches one over SSH, answers a small UDP query on port
/// <see cref="Port"/> with its computer name, Martlet version and the IDs of those hosts (no addresses, keys or other
/// data), and only while "Let my other computers find this PC" is on. A desktop looking for hosts sends the query to its
/// private subnets' broadcast addresses and loopback (never a subnet scan), lists what answers and, when the owner picks
/// one, connects to it on TCP <see cref="Port"/>.</para>
/// <para>The two agree a key (ECDH P-256; the asking side commits to its key and nonce before it sees the other's, so
/// neither side, nor anyone in between, can steer the result) and both show the same six-digit check number derived
/// from the exchange. The owner allows the request on the computer with the hosts only when the numbers match. That
/// computer then asks each host for a one-use pairing code exactly as "Show a pairing code" does and sends the codes
/// sealed (AES-GCM) under the agreed key; the asking desktop redeems them with the hosts directly, where the short-code
/// exchange still proves each host's key before it is pinned. Discovery supplies only an address and names, never trust.</para>
/// </summary>
internal static partial class Nearby
{
    internal const int Port = 9444;
    internal const string PreferenceFile = "nearby.txt";
    internal const int MaximumLine = 8192;
    internal const int MaximumHosts = 16;
    internal static readonly TimeSpan FindWindow = TimeSpan.FromMilliseconds(1600);
    /// <summary>How long the computer with the hosts waits for its owner to allow or deny a request.</summary>
    internal static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(2);
    /// <summary>How long a shared code stays redeemable (the host's own five-minute pairing window).</summary>
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    /// <summary>How long the asking desktop waits for the codes once allowed (a host may first build its image).</summary>
    internal static readonly TimeSpan CodesTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>This process's own answers carry this, so a desktop never lists itself.</summary>
    internal static readonly string Instance = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(9));

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    internal static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\A[0-9]{1,5}(\.[0-9]{1,5}){1,3}\z")]
    private static partial Regex VersionPattern();

    // ---------- preference ----------

    /// <summary>On unless nearby.txt says "off".</summary>
    internal static bool LoadEnabled(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, PreferenceFile)).Trim() != "off"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static void SaveEnabled(string directory, bool on) =>
        File.WriteAllText(Path.Combine(directory, PreferenceFile), on ? "on" : "off");

    /// <summary>A computer name as other computers see it: printable ASCII, at most 64 characters.</summary>
    internal static string Label(string name)
    {
        var label = new string(name.Where(c => c is >= ' ' and <= '~').Take(64).ToArray()).Trim();
        return label.Length == 0 ? "Martlet" : label;
    }

    /// <summary>Private IPv4 (10/8, 172.16/12, 192.168/16) or loopback: the only addresses Martlet answers or connects to.</summary>
    internal static bool Allowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork && (IPAddress.IsLoopback(address) || HostSetupCommands.IsPrivate(address));
    }

    // ---------- finding ----------

    /// <summary>Asks the local network which Martlet desktops can share hosts: about 1.6 seconds, two queries to each
    /// private subnet's broadcast address, the limited broadcast and loopback. Never lists this process.</summary>
    internal static async Task<IReadOnlyList<NearbyMartlet>> FindAsync(CancellationToken token)
    {
        var query = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(8));
        var packet = JsonSerializer.SerializeToUtf8Bytes(new { martlet = "find", v = 1, q = query });
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        IgnoreUnreachable(udp);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var found = new Dictionary<string, NearbyMartlet>(StringComparer.Ordinal);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(FindWindow);
        var sending = SendQueriesAsync(udp, packet, window.Token);
        try
        {
            while (true)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(window.Token); }
                catch (SocketException) { continue; }
                if (ParseAnswer(result.Buffer, result.RemoteEndPoint.Address, query) is not { } martlet || martlet.Instance == Instance) continue;
                // The same computer can answer over loopback and its LAN address; keep the LAN one.
                if (!found.TryGetValue(martlet.Instance, out var seen) || IPAddress.IsLoopback(seen.Address) && !IPAddress.IsLoopback(martlet.Address))
                    found[martlet.Instance] = martlet;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        try { await sending; }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        return found.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task SendQueriesAsync(UdpClient udp, byte[] packet, CancellationToken token)
    {
        var targets = BroadcastTargets();
        for (var round = 0; round < 2; round++)
        {
            foreach (var target in targets)
            {
                try { await udp.SendAsync(packet, new IPEndPoint(target, Port), token); }
                catch (SocketException) { }
            }
            await Task.Delay(500, token);
        }
    }

    /// <summary>Loopback, the limited broadcast and each up adapter's private subnet broadcast.</summary>
    internal static IReadOnlyList<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Loopback, IPAddress.Broadcast };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !HostSetupCommands.IsPrivate(unicast.Address)) continue;
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new IPAddress(address.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
                    if (!targets.Contains(broadcast)) targets.Add(broadcast);
                }
        }
        catch (NetworkInformationException) { }
        return targets;
    }

    internal static NearbyMartlet? ParseAnswer(byte[] buffer, IPAddress from, string query)
    {
        if (buffer.Length > 2048 || !Allowed(from)) return null;
        try
        {
            using var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "martlet") != "here" || Number(root, "v") != 1 || Text(root, "q") != query)
                return null;
            var instance = Text(root, "instance");
            var name = Text(root, "name");
            var device = Text(root, "device");
            var version = Text(root, "version");
            if (instance is not { Length: > 0 and <= 24 } || name is null || Label(name) != name || device is null ||
                !IdentifierPattern().IsMatch(device) || version is null || !VersionPattern().IsMatch(version) ||
                !root.TryGetProperty("hosts", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaximumHosts)
                return null;
            var hosts = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id || !IdentifierPattern().IsMatch(id)) return null;
                if (!hosts.Contains(id)) hosts.Add(id);
            }
            return new(from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from, instance, name, device, version, hosts);
        }
        catch (JsonException) { return null; }
    }

    internal static bool IsQuery(byte[] buffer, out string query)
    {
        query = "";
        if (buffer.Length > 512) return false;
        try
        {
            using var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "martlet") != "find" || Number(root, "v") != 1 ||
                Text(root, "q") is not { Length: > 0 and <= 24 } q)
                return false;
            query = q;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Stops Windows reporting an earlier datagram's "port unreachable" as a failed receive.</summary>
    internal static void IgnoreUnreachable(UdpClient udp)
    {
        const int SioUdpConnReset = -1744830452;
        try { udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
        catch (Exception error) when (error is SocketException or PlatformNotSupportedException) { }
    }

    // ---------- JSON helpers ----------

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) ? number : null;

    internal static byte[] Bytes(JsonElement element, string name, int length)
    {
        if (Text(element, name) is not { } text || !Base64Url.IsValid(text) || Base64Url.GetMaxDecodedLength(text.Length) < length)
            throw new InvalidDataException("A message from the other computer was malformed.");
        var bytes = Base64Url.DecodeFromChars(text);
        if (bytes.Length != length) throw new InvalidDataException("A message from the other computer was malformed.");
        return bytes;
    }

    internal static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>A plain-language line from the other computer (failure reasons), at most 400 printable characters.</summary>
    internal static string Message(JsonElement element, string fallback)
    {
        var text = Text(element, "message");
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var clean = new string(text.Where(c => !char.IsControl(c)).Take(400).ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean;
    }
}

/// <summary>A Martlet desktop that answered: where it is, its computer name and the hosts it can share.</summary>
internal sealed record NearbyMartlet(IPAddress Address, string Instance, string Name, string Device, string Version, IReadOnlyList<string> Hosts)
{
    internal string Where => IPAddress.IsLoopback(Address) ? "this PC" : Address.ToString();
}

/// <summary>What this desktop answers with while it can share hosts.</summary>
internal sealed record NearbyOffer(string Name, string Device, string Version, IReadOnlyList<string> Hosts);

/// <summary>A one-use pairing code a host showed for the asking desktop: the address the host showed with it and the code.
/// The code is a secret: never logged or shown.</summary>
internal sealed record SharedCode(string HostId, string Address, string Code)
{
    public override string ToString() => $"Pairing code for {HostId} at {Address} (code omitted)";
}

/// <summary>The key agreement behind a request: an ephemeral P-256 key and nonce per side, the asking side's commitment,
/// the six-digit check number both owners compare and the AES-GCM key for the codes.</summary>
internal sealed class JoinKeys : IDisposable
{
    internal const int KeyLength = 91; // DER SubjectPublicKeyInfo of a P-256 key
    internal const int NonceLength = 16;
    private const string P256 = "1.2.840.10045.3.1.7";
    private static readonly byte[] CodesLabel = "martlet-join-v1 codes"u8.ToArray();
    private readonly ECDiffieHellman own = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    internal JoinKeys()
    {
        PublicKey = own.ExportSubjectPublicKeyInfo();
        if (PublicKey.Length != KeyLength) throw new CryptographicException("Unexpected P-256 public key encoding.");
    }

    internal byte[] PublicKey { get; }
    internal byte[] Nonce { get; } = RandomNumberGenerator.GetBytes(NonceLength);

    internal static byte[] Commit(byte[] key, byte[] nonce) => SHA256.HashData(Concat("martlet-join-v1 commit\n"u8, key, nonce));

    /// <summary>The check number ("482 913") and the codes key. <paramref name="asking"/> is true on the desktop that asked;
    /// both sides order the transcript the same way (the sharing side's key and nonce first).</summary>
    internal (string Number, byte[] Key) Agree(byte[] peerKey, byte[] peerNonce, bool asking)
    {
        if (peerKey.Length != KeyLength || peerNonce.Length != NonceLength) throw new CryptographicException("Malformed key.");
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerKey, out var read);
        if (read != peerKey.Length || peer.ExportParameters(false).Curve.Oid?.Value != P256) throw new CryptographicException("Not a P-256 key.");
        var shared = own.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            var transcript = asking ? Concat([], peerKey, PublicKey, peerNonce, Nonce) : Concat([], PublicKey, peerKey, Nonce, peerNonce);
            var digest = SHA256.HashData(Concat("martlet-join-v1 number\n"u8, transcript));
            var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, "martlet-join-v1 key"u8.ToArray());
            return ($"{value / 1000:000} {value % 1000:000}", key);
        }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    internal static (string Iv, string Box) Seal(byte[] key, byte[] plaintext)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var box = new byte[plaintext.Length + 16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(iv, plaintext, box.AsSpan(0, plaintext.Length), box.AsSpan(plaintext.Length), CodesLabel);
        return (Nearby.Encode(iv), Nearby.Encode(box));
    }

    internal static byte[] Open(byte[] key, byte[] iv, byte[] box)
    {
        if (iv.Length != 12 || box.Length < 16) throw new CryptographicException("Malformed codes.");
        var plaintext = new byte[box.Length - 16];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(iv, box.AsSpan(0, plaintext.Length), box.AsSpan(plaintext.Length), plaintext, CodesLabel);
        return plaintext;
    }

    private static byte[] Concat(ReadOnlySpan<byte> head, params byte[][] parts)
    {
        var bytes = new byte[head.Length + parts.Sum(p => p.Length)];
        head.CopyTo(bytes);
        var offset = head.Length;
        foreach (var part in parts)
        {
            part.CopyTo(bytes, offset);
            offset += part.Length;
        }
        return bytes;
    }

    public void Dispose() => own.Dispose();
}

/// <summary>Newline-delimited JSON over a TCP stream, each line at most <see cref="Nearby.MaximumLine"/> bytes.</summary>
internal sealed class JoinChannel(Stream stream)
{
    private readonly byte[] buffer = new byte[Nearby.MaximumLine];
    private readonly SemaphoreSlim writing = new(1, 1);
    private int start, end;

    internal async Task SendAsync(object message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length >= Nearby.MaximumLine) throw new InvalidDataException("Message too long.");
        await writing.WaitAsync(token);
        try
        {
            await stream.WriteAsync(bytes, token);
            await stream.WriteAsync("\n"u8.ToArray(), token);
            await stream.FlushAsync(token);
        }
        finally { writing.Release(); }
    }

    /// <summary>The next message; <see cref="TimeoutException"/> after <paramref name="timeout"/>, <see cref="EndOfStreamException"/>
    /// when the other computer hung up.</summary>
    internal async Task<JsonElement> ReceiveAsync(TimeSpan timeout, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
                if (newline >= 0)
                {
                    var line = buffer.AsMemory(start, newline - start);
                    start = newline + 1;
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed message.");
                    return document.RootElement.Clone();
                }
                if (start > 0)
                {
                    Array.Copy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (end == buffer.Length) throw new InvalidDataException("Message too long.");
                var read = await stream.ReadAsync(buffer.AsMemory(end), limit.Token);
                if (read == 0) throw new EndOfStreamException("The other computer closed the connection.");
                end += read;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The other computer did not answer in time.");
        }
    }
}

/// <summary>Answers finds and accepts requests on port <see cref="Nearby.Port"/>, on the LAN (when Windows Firewall lets other
/// computers reach it) or on loopback only. One request at a time, at most ten a minute.</summary>
internal sealed class NearbyResponder : IDisposable
{
    private readonly Func<NearbyOffer?> offer;
    private readonly Func<JoinSession, Task> request;
    private readonly CancellationTokenSource stop = new();
    private readonly UdpClient udp;
    private readonly TcpListener tcp;
    private readonly Queue<DateTime> answers = new();
    private readonly Queue<DateTime> requests = new();
    private int active;

    private NearbyResponder(bool lan, Func<NearbyOffer?> offer, Func<JoinSession, Task> request)
    {
        Lan = lan;
        this.offer = offer;
        this.request = request;
        var address = lan ? IPAddress.Any : IPAddress.Loopback;
        udp = new UdpClient(AddressFamily.InterNetwork);
        tcp = new TcpListener(address, Nearby.Port) { ExclusiveAddressUse = true };
        try
        {
            udp.Client.ExclusiveAddressUse = true;
            Nearby.IgnoreUnreachable(udp);
            udp.Client.Bind(new IPEndPoint(address, Nearby.Port));
            tcp.Start(4);
        }
        catch
        {
            udp.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    internal bool Lan { get; }

    /// <summary>Binds UDP and TCP <see cref="Nearby.Port"/> (throws <see cref="SocketException"/> when the port is taken) and
    /// starts answering. <paramref name="request"/> runs each accepted request to the end (the owner's decision and sharing).</summary>
    internal static NearbyResponder Start(bool lan, Func<NearbyOffer?> offer, Func<JoinSession, Task> request)
    {
        var responder = new NearbyResponder(lan, offer, request);
        _ = responder.AnswerAsync();
        _ = responder.AcceptAsync();
        return responder;
    }

    private async Task AnswerAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(stop.Token); }
            catch (SocketException) { continue; }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { return; }
            if (!Nearby.Allowed(received.RemoteEndPoint.Address) || !Nearby.IsQuery(received.Buffer, out var query)) continue;
            if (offer() is not { Hosts.Count: > 0 } current || !Within(answers, 40, TimeSpan.FromSeconds(10))) continue;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                martlet = "here", v = 1, q = query, instance = Nearby.Instance, name = current.Name, device = current.Device,
                version = current.Version, hosts = current.Hosts
            });
            try { await udp.SendAsync(reply, received.RemoteEndPoint, stop.Token); }
            catch (SocketException) { }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { return; }
        }
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(stop.Token); }
            catch (SocketException) { continue; }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint { } remote || !Nearby.Allowed(remote.Address)) return;
            var from = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            var channel = new JoinChannel(client.GetStream());
            if (!Within(requests, 10, TimeSpan.FromMinutes(1)) || Interlocked.CompareExchange(ref active, 1, 0) != 0)
            {
                try
                {
                    // Read the asker's first line before answering, so its write is not cut off by this side closing.
                    await channel.ReceiveAsync(TimeSpan.FromSeconds(3), stop.Token);
                    await channel.SendAsync(new { type = "busy", message = "It is already answering another computer. Try again in a minute." }, stop.Token);
                }
                catch (Exception error) when (error is IOException or SocketException or JsonException or InvalidDataException or
                    TimeoutException or OperationCanceledException or ObjectDisposedException) { }
                return;
            }
            try
            {
                if (await JoinSession.AcceptAsync(channel, from, offer, stop.Token) is { } session) await request(session);
            }
            catch (Exception error) when (error is IOException or SocketException or JsonException or InvalidDataException or
                CryptographicException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                ErrorLog.Info($"Nearby request from {from} ended: {error.Message}");
            }
            finally { Interlocked.Exchange(ref active, 0); }
        }
    }

    private static bool Within(Queue<DateTime> times, int limit, TimeSpan period)
    {
        lock (times)
        {
            var now = DateTime.UtcNow;
            while (times.Count > 0 && now - times.Peek() > period) times.Dequeue();
            if (times.Count >= limit) return false;
            times.Enqueue(now);
            return true;
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        udp.Dispose();
        tcp.Stop();
        tcp.Dispose();
    }
}

/// <summary>A request accepted on the computer with the hosts, after the key agreement: who asks, from where, the check
/// number and the replies.</summary>
internal sealed class JoinSession
{
    private readonly JoinChannel channel;
    private readonly byte[] key;

    private JoinSession(JoinChannel channel, IPAddress from, string device, string name, string number, byte[] key, CancellationToken token)
    {
        this.channel = channel;
        this.key = key;
        From = from;
        Device = device;
        Name = name;
        Number = number;
        // The asking desktop sends nothing more until it is done; this completes early only when it gives up.
        Incoming = channel.ReceiveAsync(TimeSpan.FromMinutes(20), token);
        Incoming.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    internal IPAddress From { get; }
    internal string Device { get; }
    internal string Name { get; }
    internal string Number { get; }
    internal Task<JsonElement> Incoming { get; }

    internal static async Task<JoinSession?> AcceptAsync(JoinChannel channel, IPAddress from, Func<NearbyOffer?> offer, CancellationToken token)
    {
        var hello = await channel.ReceiveAsync(Nearby.StepTimeout, token);
        if (Nearby.Text(hello, "type") != "join") return null;
        if (Nearby.Number(hello, "v") != 1)
        {
            await channel.SendAsync(new { type = "failed", message = "It runs a different Martlet version. Update both computers, then try again." }, token);
            return null;
        }
        var device = Nearby.Text(hello, "device");
        var name = Nearby.Text(hello, "name");
        if (device is null || !Nearby.IdentifierPattern().IsMatch(device) || name is null || Nearby.Label(name) != name) return null;
        var commit = Nearby.Bytes(hello, "commit", 32);
        if (offer() is not { Hosts.Count: > 0 })
        {
            await channel.SendAsync(new { type = "failed", message = "It has no host it can share right now." }, token);
            return null;
        }
        using var keys = new JoinKeys();
        await channel.SendAsync(new { type = "key", key = Nearby.Encode(keys.PublicKey), nonce = Nearby.Encode(keys.Nonce) }, token);
        var reveal = await channel.ReceiveAsync(Nearby.StepTimeout, token);
        if (Nearby.Text(reveal, "type") != "reveal") return null;
        var peerKey = Nearby.Bytes(reveal, "key", JoinKeys.KeyLength);
        var peerNonce = Nearby.Bytes(reveal, "nonce", JoinKeys.NonceLength);
        if (!CryptographicOperations.FixedTimeEquals(JoinKeys.Commit(peerKey, peerNonce), commit))
            throw new CryptographicException("The asking computer's key did not match what it committed to.");
        var (number, secret) = keys.Agree(peerKey, peerNonce, asking: false);
        return new JoinSession(channel, from, device, name, number, secret, token);
    }

    internal Task ApproveAsync(CancellationToken token) => channel.SendAsync(new { type = "approved" }, token);
    internal Task DenyAsync(CancellationToken token) => channel.SendAsync(new { type = "denied" }, token);
    internal Task FailAsync(string message, CancellationToken token) =>
        channel.SendAsync(new { type = "failed", message = message.Length > 400 ? message[..400] : message }, token);

    /// <summary>Sends the codes sealed under the agreed key, with plain-language notes about hosts that gave none.</summary>
    internal Task SendCodesAsync(IReadOnlyList<SharedCode> codes, IReadOnlyList<string> problems, CancellationToken token)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            codes = codes.Select(c => new { host = c.HostId, address = c.Address, code = c.Code }).ToArray(),
            problems = problems.Take(Nearby.MaximumHosts).Select(p => p.Length > 400 ? p[..400] : p).ToArray()
        });
        try
        {
            var (iv, box) = JoinKeys.Seal(key, plaintext);
            return channel.SendAsync(new { type = "codes", iv, box }, token);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>The hosts the asking desktop reports it paired with, or null when it hung up or did not answer in time.</summary>
    internal async Task<IReadOnlyList<string>?> ReadDoneAsync(TimeSpan timeout, CancellationToken token)
    {
        try
        {
            var done = await Incoming.WaitAsync(timeout, token);
            if (Nearby.Text(done, "type") != "done" || !done.TryGetProperty("paired", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            return list.EnumerateArray().Take(Nearby.MaximumHosts)
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(id => id is not null && Nearby.IdentifierPattern().IsMatch(id)).Select(id => id!).ToArray();
        }
        catch (Exception error) when (error is IOException or SocketException or JsonException or InvalidDataException or
            TimeoutException or ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>A request from this desktop to a Martlet that can share hosts: connect, agree the key, show the check number,
/// wait for the owner's decision there, receive the codes and report which hosts paired.</summary>
internal sealed class NearbyJoin : IDisposable
{
    private readonly TcpClient client;
    private readonly JoinChannel channel;
    private readonly byte[] key;

    private NearbyJoin(TcpClient client, JoinChannel channel, NearbyMartlet target, string number, byte[] key)
    {
        this.client = client;
        this.channel = channel;
        this.key = key;
        Target = target;
        Number = number;
    }

    internal NearbyMartlet Target { get; }
    internal string Number { get; }

    internal static async Task<NearbyJoin> ConnectAsync(NearbyMartlet target, string device, string name, CancellationToken token)
    {
        if (!Nearby.Allowed(target.Address)) throw new InvalidOperationException("Martlet connects only to computers on your private network.");
        if (!Nearby.IdentifierPattern().IsMatch(device)) throw new InvalidOperationException("Invalid device ID for this PC.");
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                try { await client.ConnectAsync(target.Address, Nearby.Port, connect.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"Martlet on {target.Name} ({target.Where}) did not answer. Is it still open?");
                }
                catch (SocketException)
                {
                    throw new InvalidOperationException($"Couldn't reach Martlet on {target.Name} ({target.Where}). Is it still open? Find again to refresh the list.");
                }
            }
            var channel = new JoinChannel(client.GetStream());
            using var keys = new JoinKeys();
            await channel.SendAsync(new
            {
                type = "join", v = 1, device, name = Nearby.Label(name), commit = Nearby.Encode(JoinKeys.Commit(keys.PublicKey, keys.Nonce))
            }, token);
            var answer = await channel.ReceiveAsync(Nearby.StepTimeout, token);
            switch (Nearby.Text(answer, "type"))
            {
                case "key": break;
                case "busy" or "failed":
                    throw new InvalidOperationException($"{target.Name} can't take the request: {Nearby.Message(answer, "it refused.")}");
                default: throw new InvalidDataException("A message from the other computer was malformed.");
            }
            var hostKey = Nearby.Bytes(answer, "key", JoinKeys.KeyLength);
            var hostNonce = Nearby.Bytes(answer, "nonce", JoinKeys.NonceLength);
            await channel.SendAsync(new { type = "reveal", key = Nearby.Encode(keys.PublicKey), nonce = Nearby.Encode(keys.Nonce) }, token);
            var (number, secret) = keys.Agree(hostKey, hostNonce, asking: true);
            return new NearbyJoin(client, channel, target, number, secret);
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        {
            client.Dispose();
            throw new InvalidOperationException($"Martlet on {target.Name} ({target.Where}) stopped answering: {error.Message} Try again in a minute.", error);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Waits for the owner of <see cref="Target"/> to allow the request; throws with the reason otherwise.</summary>
    internal async Task WaitApprovalAsync(CancellationToken token)
    {
        var answer = await ReceiveAsync(Nearby.DecisionTimeout + TimeSpan.FromSeconds(30), token,
            $"{Target.Name} didn't answer: nobody allowed the request there in time.");
        switch (Nearby.Text(answer, "type"))
        {
            case "approved": return;
            case "denied": throw new InvalidOperationException($"{Target.Name} denied the request (or it expired there).");
            case "failed": throw new InvalidOperationException($"{Target.Name}: {Nearby.Message(answer, "the request stopped.")}");
            default: throw new InvalidDataException("A message from the other computer was malformed.");
        }
    }

    /// <summary>The codes <see cref="Target"/> got from its hosts, and notes about hosts that gave none.</summary>
    internal async Task<(IReadOnlyList<SharedCode> Codes, IReadOnlyList<string> Problems)> WaitCodesAsync(CancellationToken token)
    {
        var answer = await ReceiveAsync(Nearby.CodesTimeout, token, $"{Target.Name} allowed the request but sent no pairing codes in time.");
        switch (Nearby.Text(answer, "type"))
        {
            case "codes": break;
            case "failed": throw new InvalidOperationException($"{Target.Name}: {Nearby.Message(answer, "it couldn't get pairing codes from its hosts.")}");
            default: throw new InvalidDataException("A message from the other computer was malformed.");
        }
        var plaintext = JoinKeys.Open(key, Nearby.Bytes(answer, "iv", 12), Base64UrlBytes(answer, "box"));
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var codes = new List<SharedCode>();
            if (root.TryGetProperty("codes", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray().Take(Nearby.MaximumHosts))
                {
                    var host = Nearby.Text(item, "host");
                    var address = Nearby.Text(item, "address");
                    var code = Nearby.Text(item, "code");
                    if (host is null || !Nearby.IdentifierPattern().IsMatch(host) || address is null || address.Length > 64 || code is null || code.Length > 16)
                        throw new InvalidDataException("A message from the other computer was malformed.");
                    codes.Add(new(host, address, code));
                }
            var problems = new List<string>();
            if (root.TryGetProperty("problems", out var notes) && notes.ValueKind == JsonValueKind.Array)
                foreach (var note in notes.EnumerateArray().Take(Nearby.MaximumHosts))
                    if (note.ValueKind == JsonValueKind.String && note.GetString() is { Length: > 0 } text)
                        problems.Add(new string(text.Where(c => !char.IsControl(c)).Take(400).ToArray()));
            return (codes, problems);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal Task DoneAsync(IReadOnlyList<string> paired, CancellationToken token) =>
        channel.SendAsync(new { type = "done", paired }, token);

    private async Task<JsonElement> ReceiveAsync(TimeSpan timeout, CancellationToken token, string late)
    {
        try { return await channel.ReceiveAsync(timeout, token); }
        catch (TimeoutException) { throw new InvalidOperationException(late); }
        catch (Exception error) when (error is EndOfStreamException or IOException or SocketException)
        {
            throw new InvalidOperationException($"{Target.Name} stopped answering (Martlet there closed, or the request was withdrawn).");
        }
    }

    private static byte[] Base64UrlBytes(JsonElement element, string name)
    {
        if (Nearby.Text(element, name) is not { Length: > 0 and < Nearby.MaximumLine } text || !Base64Url.IsValid(text))
            throw new InvalidDataException("A message from the other computer was malformed.");
        return Base64Url.DecodeFromChars(text);
    }

    public void Dispose() => client.Dispose();
}

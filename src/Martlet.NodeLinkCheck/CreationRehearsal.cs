using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses Martlet's creations end to end on this PC with the production code (docs/CREATIONS.md): two real gateways
/// (Kestrel, pinned TLS, signed requests) on 127.0.0.1 with an in-memory creations.json and pieces, and three simulated
/// desktops that keep their creations in a temporary folder with the production store (CreationStore) and sync engine
/// (CreationSync) over the desktop's paired client (HostCreationPeer). The creations are the FIXTURE - NOT AI test-tone kind
/// (FixtureCreations): generated tones kept as FLAC. Nothing leaves loopback; the folder is deleted afterwards and nothing
/// touches the credential vault.
/// </summary>
internal static class CreationRehearsal
{
    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "martlet-creation-rehearsal-" + Guid.NewGuid().ToString("N"));
        await using var h1 = await CreationHost.StartAsync("lab-host-1");
        await using var h2 = await CreationHost.StartAsync("lab-host-2");
        var a = new LabDesktop("lab-desktop-a", Path.Combine(root, "a"));
        var b = new LabDesktop("lab-desktop-b", Path.Combine(root, "b"));
        var c = new LabDesktop("lab-desktop-c", Path.Combine(root, "c"));
        var registry = new CreationRegistry();
        registry.Register(FixtureCreations.Kind);
        Creation? short1 = null, long1 = null;
        CreationLibrary? beforeRemoval = null;

        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add((name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add((name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        try
        {
            await Run("Desktops A, B and C pair with lab-host-1 and lab-host-2 (signed, pinned connections)", async () =>
            {
                foreach (var desktop in new[] { a, b, c })
                foreach (var host in new[] { h1, h2 })
                    await desktop.PairAsync(host);
                return (true, "each desktop paired with both hosts");
            });
            await Run("FLAC keeps audio losslessly at a fraction of WAV's size (a 60 s, 48 kHz stereo music-like FIXTURE signal)", () =>
            {
                var pcm = MusicLike(60, 2);
                var flac = FlacCodec.Encode(pcm, 48_000, 2);
                var back = FlacCodec.Decode(flac);
                var vocals = MusicLike(60, 1, silentHalf: true);
                var vocalsFlac = FlacCodec.Encode(vocals, 48_000, 1);
                var same = back.Pcm16.AsSpan().SequenceEqual(pcm) && FlacCodec.Decode(vocalsFlac).Pcm16.AsSpan().SequenceEqual(vocals);
                return Task.FromResult((same && flac.Length < pcm.Length,
                    $"stereo: WAV {Mb(pcm.Length + 44)}, FLAC {Mb(flac.Length)} ({100.0 * flac.Length / pcm.Length:0}%); mono with a silent half: " +
                    $"WAV {Mb(vocals.Length + 44)}, FLAC {Mb(vocalsFlac.Length)} ({100.0 * vocalsFlac.Length / vocals.Length:0}%); decoded identical: {same}"));
            });
            await Run("A makes two test tones: a short one and a 40 s noisy one whose audio travels in two 3 MiB pieces", async () =>
            {
                short1 = await CreationStore.AddAsync(a.DataDirectory, FixtureCreations.Draft(a.Author, "Short tone", 1.5, 440), registry,
                    DateTimeOffset.UtcNow, token);
                long1 = await CreationStore.AddAsync(a.DataDirectory, FixtureCreations.Draft(a.Author, "Long noisy tone", 40, 330, noise: 9_000), registry,
                    DateTimeOffset.UtcNow.AddSeconds(1), token);
                var audio = long1.Asset("audio")!;
                return (CreationStore.IsComplete(a.DataDirectory, short1) && CreationStore.IsComplete(a.DataDirectory, long1) && audio.Chunks.Count >= 2,
                    $"A holds 2 creations; the long tone's audio: {Mb(audio.Bytes)} in {audio.Chunks.Count} pieces; short: {Mb(short1.Bytes)}");
            });
            await Run("A syncs with lab-host-1 only: the host keeps the list and every piece, each piece one signed request", async () =>
            {
                var result = await a.SyncAsync([h1], token);
                var expected = CreationStore.View(a.DataDirectory).LiveChunks();
                return (result.Sent == expected.Count && h1.SavedChunks.Count == expected.Count && h1.SavedLibrary is not null &&
                        result.Hosts.Single().Complete.Count == 2,
                    $"sent {result.Sent} pieces; lab-host-1 saved creations.json and {h1.SavedChunks.Count} of {expected.Count} pieces; complete there: {result.Hosts.Single().Complete.Count}");
            });
            await Run("Nothing changed: A's next sync reads only the host's digests (no list, no pieces)", async () =>
            {
                var counting = new Counting(a.Peer(h1));
                var result = await a.SyncAsync([counting], token);
                return (counting.Digests == 1 && counting.Reads == 0 && counting.Merges == 0 && result.Sent == 0 &&
                        result.Hosts.Single().State == CreationSyncState.Shared,
                    $"digest reads: {counting.Digests}; full reads of the list: {counting.Reads}; merges: {counting.Merges}; pieces sent: {result.Sent}");
            });
            await Run("Desktop B, new and empty, takes both from lab-host-1 and passes them on to lab-host-2, which A never reached", async () =>
            {
                var result = await b.SyncAsync([h1, h2], token);
                var same = await SameAssetsAsync(a, b, short1!, token) && await SameAssetsAsync(a, b, long1!, token);
                var expected = CreationStore.View(b.DataDirectory).LiveChunks();
                return (result.Local.Count == 2 && result.Waiting == 0 && same && h2.SavedChunks.Count == expected.Count,
                    $"B complete: {result.Local.Count}, waiting {result.Waiting}; assets identical to A's: {same}; lab-host-2 now holds {h2.SavedChunks.Count} of {expected.Count} pieces");
            });
            await Run("An interrupted copy continues where it stopped: C loses lab-host-2 after 2 pieces, then fetches only the rest", async () =>
            {
                var total = CreationStore.View(b.DataDirectory).LiveChunks().Count;
                var one = await c.SyncAsync([new Flaky(c.Peer(h2), 2)], token);
                var fetchedBefore = h2.ChunkReads;
                var two = await c.SyncAsync([h2], token);
                var second = h2.ChunkReads - fetchedBefore;
                return (one.Waiting > 0 && two.Waiting == 0 && two.Local.Count == 2 && second == total - 2,
                    $"first pass: {one.Local.Count} complete, {one.Waiting} waiting; second pass fetched {second} of {total} pieces");
            });
            await Run("Martlet on C performs a creation A made: the kind's handler reads and decodes C's own copy", async () =>
            {
                var creation = CreationStore.View(c.DataDirectory).Resolve(long1!.Key)!;
                var handler = registry.HandlerFor(FixtureCreations.KindName);
                using var attached = registry.Handle(FixtureCreations.KindName, FixtureCreations.Handler);
                var result = await registry.HandlerFor(FixtureCreations.KindName)!.PerformAsync(new(creation,
                    JsonSerializer.SerializeToElement(new { start_ms = 1_500 }), CreationStore.Assets(c.DataDirectory, creation)), token);
                return (handler is null && !result.IsError && result.Text.Contains("from 1500 ms", StringComparison.Ordinal) &&
                        result.Text.Contains("1920000 frames", StringComparison.Ordinal),
                    $"no handler before it was attached: {handler is null}; {result.Text}");
            });
            await Run("A creation of a kind this Martlet doesn't know (from a newer Martlet) passes through hosts and desktops untouched", async () =>
            {
                var bytes = RandomNumberGenerator.GetBytes(10_000);
                var entry = new Creation
                {
                    Id = CreationLibrary.NewId(), Kind = "hologram", KindVersion = 3, Title = "A future kind", CreatedBy = a.Author,
                    CreatedAt = DateTimeOffset.UtcNow, Assets = [CreationLibrary.Asset("scene", "application/octet-stream", bytes)],
                    Revision = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = a.DeviceId
                };
                var library = CreationStore.View(a.DataDirectory).Add(entry, DateTimeOffset.UtcNow);
                await File.WriteAllBytesAsync(CreationStore.AssetPath(a.DataDirectory, entry.Assets![0].Sha256), bytes, token);
                CreationStore.Commit(a.DataDirectory, library);
                await a.SyncAsync([h1], token);
                var result = await b.SyncAsync([h1, h2], token);
                var kept = result.Library.Find(entry.Id) is { Removed: false } && result.Local.Contains(entry.Id) && registry.Find("hologram") is null;
                return (kept, $"B keeps it and its asset without knowing the kind: {kept}");
            });
            await Run("B renames the long tone: A and C see the new title after their next sync", async () =>
            {
                await CreationStore.RenameAsync(b.DataDirectory, long1!.Id, "Long tone, renamed", b.DeviceId, DateTimeOffset.UtcNow, token);
                await b.SyncAsync([h1, h2], token);
                var onA = (await a.SyncAsync([h1], token)).Library.Find(long1.Id)?.Title;
                var onC = (await c.SyncAsync([h2], token)).Library.Find(long1.Id)?.Title;
                return (onA == "Long tone, renamed" && onC == onA, $"A: \"{onA}\"; C: \"{onC}\"");
            });
            await Run("C deletes the long tone: the tombstone reaches both hosts and A and B, and everyone deletes its pieces and files", async () =>
            {
                beforeRemoval = CreationStore.View(a.DataDirectory);
                await CreationStore.RemoveAsync(c.DataDirectory, long1!.Id, c.DeviceId, DateTimeOffset.UtcNow, token);
                await c.SyncAsync([h2], token);
                await b.SyncAsync([h1, h2], token);
                var resultA = await a.SyncAsync([h1], token);
                var pieces = long1.Assets!.SelectMany(x => x.Chunks).ToArray();
                var hostsClear = !pieces.Any(h1.SavedChunks.ContainsKey) && !pieces.Any(h2.SavedChunks.ContainsKey);
                var filesClear = new[] { a, b, c }.All(d => long1.Assets!.All(x => !File.Exists(CreationStore.AssetPath(d.DataDirectory, x.Sha256))));
                return (resultA.Library.Find(long1.Id)?.Removed == true && hostsClear && filesClear,
                    $"A sees it deleted: {resultA.Library.Find(long1.Id)?.Removed == true}; hosts' pieces gone: {hostsClear}; desktops' files gone: {filesClear}");
            });
            await Run("An older copy (from before the deletion) can't bring it back", async () =>
            {
                using var peer = a.Peer(h1);
                var merged = await peer.MergeAsync(beforeRemoval!, token);
                return (merged.Library.Find(long1!.Id)?.Removed == true, $"after merging the stale copy it is {(merged.Library.Find(long1.Id)?.Removed == true ? "still deleted" : "back")}");
            });
            await Run("lab-host-1 restarts from its saved copy and still serves every piece", async () =>
            {
                using (var before = a.Peer(h1)) _ = await before.ReadAsync(token);
                var count = h1.SavedChunks.Count;
                await h1.RestartAsync();
                using var peer = a.Peer(h1);
                var after = await peer.ReadAsync(token);
                var piece = await peer.ReadChunkAsync(short1!.Assets![0].Chunks[0], token);
                return (after.Present.Count == count && after.Library.Find(short1.Id) is { Removed: false } && piece is not null,
                    $"pieces saved {count}, served after restart {after.Present.Count}; a piece readable: {piece is not null}");
            });
            await Run("Pieces are checked: a wrong SHA-256, a piece no creation has and an oversized body are refused (request.invalid)", async () =>
            {
                var live = CreationStore.View(a.DataDirectory).Find(short1!.Id)!;
                var other = RandomNumberGenerator.GetBytes(1_000);
                var wrong = await Code(() => a.SendRawAsync(h1, live.Assets![0].Chunks[0], other, token));
                var unknown = await Code(() => a.SendRawAsync(h1, Convert.ToHexStringLower(SHA256.HashData(other)), other, token));
                var big = RandomNumberGenerator.GetBytes(CreationLibrary.ChunkBytes + 1);
                var oversized = await Code(() => a.SendRawAsync(h1, Convert.ToHexStringLower(SHA256.HashData(big)), big, token));
                return (wrong == "request.invalid" && unknown == "request.invalid" && oversized == "request.invalid",
                    $"wrong SHA-256: {wrong}; not in the list: {unknown}; larger than a piece: {oversized}");
            });
            await Run("Only paired devices may read creations: an unsigned request is refused and shows no title", async () =>
            {
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(h1.Origin + "/martlet/v1/creations", token);
                var body = await response.Content.ReadAsStringAsync(token);
                return (response.StatusCode != HttpStatusCode.OK && !body.Contains("Short tone", StringComparison.Ordinal),
                    $"unsigned request: HTTP {(int)response.StatusCode}, no title in the answer");
            });
            await Run("A kind's rules hold: an unregistered kind, a missing part and a part too large are refused", async () =>
            {
                var unknown = await RefusedAsync(() => CreationStore.AddAsync(a.DataDirectory,
                    FixtureCreations.Draft(a.Author, "x", 1) with { Kind = "song" }, registry, DateTimeOffset.UtcNow, token));
                var missing = await RefusedAsync(() => CreationStore.AddAsync(a.DataDirectory,
                    FixtureCreations.Draft(a.Author, "x", 1) with { Assets = [] }, registry, DateTimeOffset.UtcNow, token));
                var huge = await RefusedAsync(() => CreationStore.AddAsync(a.DataDirectory, FixtureCreations.Draft(a.Author, "x", 1) with
                {
                    Assets = [new("audio", FlacCodec.MediaType, new byte[17 * 1024 * 1024])]
                }, registry, DateTimeOffset.UtcNow, token));
                return (unknown && missing && huge, $"unregistered kind refused: {unknown}; missing audio refused: {missing}; 17 MB audio refused: {huge}");
            });
            await Run("The sync records each host's state for the Creations page and creations_status (IDs only)", async () =>
            {
                var result = await b.SyncAsync([h1, h2, new Unreachable("lab-host-off")], token);
                var saved = CreationSyncState.Load(b.DataDirectory);
                var text = await File.ReadAllTextAsync(Path.Combine(b.DataDirectory, CreationSyncState.FileName), token);
                return (saved is { Hosts.Count: 3 } && saved.Hosts.Count(h => h.State == CreationSyncState.Shared) == 2 &&
                        saved.Hosts.Single(h => h.HostId == "lab-host-off").State == CreationSyncState.Unreachable &&
                        !text.Contains("Short tone", StringComparison.Ordinal),
                    $"{result.Describe()} Saved: {string.Join(", ", saved!.Hosts.Select(h => $"{h.HostId} {h.State} ({h.Complete.Count} complete)"))}");
            });
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, signed requests) with in-memory creations.json and pieces, and three " +
                "simulated desktops using the production CreationStore and CreationSync over the desktop's paired client (HostCreationPeer) " +
                "in a temporary folder, with the FIXTURE - NOT AI test-tone kind (generated tones as FLAC; nothing played). Not covered: the " +
                "desktop window and its 30-second sync, the Linux host's files, a real song and a real LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.00} MiB";

    // Several sines, a slow swell and a little hiss: a stand-in for music that FLAC can predict about as well as real music.
    private static byte[] MusicLike(int seconds, int channels, bool silentHalf = false)
    {
        var frames = seconds * 48_000;
        var random = new Random(7);
        var pcm = new byte[frames * 2 * channels];
        for (var i = 0; i < frames; i++)
        for (var c = 0; c < channels; c++)
        {
            var value = silentHalf && i % 96_000 >= 48_000 ? 0
                : 6_000 * Math.Sin(i * 0.0261 * (c + 1)) + 3_000 * Math.Sin(i * 0.0587) + 2_000 * Math.Sin(i * 0.0071) * Math.Sin(i * 0.000_3) +
                  random.Next(-120, 120);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + c) * 2), (short)Math.Clamp(value, short.MinValue, short.MaxValue));
        }
        return pcm;
    }

    private static async Task<bool> SameAssetsAsync(LabDesktop left, LabDesktop right, Creation creation, CancellationToken token)
    {
        foreach (var asset in creation.Assets!)
        {
            var one = await CreationStore.ReadAssetAsync(left.DataDirectory, creation, asset.Name, token);
            var two = await CreationStore.ReadAssetAsync(right.DataDirectory, creation, asset.Name, token);
            if (one is null || two is null || !one.AsSpan().SequenceEqual(two)) return false;
        }
        return true;
    }

    private static async Task<string?> Code(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Audio2FaceHostException error) { return error.Code; }
    }

    private static async Task<bool> RefusedAsync(Func<Task> call)
    {
        try { await call(); return false; }
        catch (ContractException) { return true; }
    }

    /// <summary>A host that hands out <paramref name="pieces"/> pieces and then drops off.</summary>
    private sealed class Flaky(HostCreationPeer inner, int pieces) : ICreationHost, IDisposable
    {
        private int served;
        public string HostId => inner.HostId;
        public Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token) => inner.ReadDigestAsync(token);
        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token) =>
            inner.MergeAsync(library, token);
        public Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) =>
            ++served > pieces ? throw new CreationHostException("lab-host-2 dropped off.") : inner.ReadChunkAsync(sha256, token);
        public Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token) =>
            throw new CreationHostException("lab-host-2 dropped off.");
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Counts what the sync asks a host.</summary>
    private sealed class Counting(HostCreationPeer inner) : ICreationHost, IDisposable
    {
        internal int Digests, Reads, Merges;
        public string HostId => inner.HostId;

        public Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token)
        {
            Digests++;
            return inner.ReadDigestAsync(token);
        }

        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token)
        {
            Reads++;
            return inner.ReadAsync(token);
        }

        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token)
        {
            Merges++;
            return inner.MergeAsync(library, token);
        }

        public Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) => inner.ReadChunkAsync(sha256, token);
        public Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token) =>
            inner.SendChunkAsync(sha256, data, token);
        public void Dispose() => inner.Dispose();
    }

    private sealed class Unreachable(string hostId) : ICreationHost
    {
        public string HostId => hostId;
        public Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token) => throw new CreationHostException("off");
        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token) => throw new CreationHostException("off");
        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token) =>
            throw new CreationHostException("off");
        public Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) => throw new CreationHostException("off");
        public Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token) =>
            throw new CreationHostException("off");
    }

    /// <summary>A simulated desktop: its pairings (secrets in memory), its data folder and the production sync engine.</summary>
    private sealed class LabDesktop(string deviceId, string dataDirectory)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly CreationSync sync = new(dataDirectory);
        internal string DeviceId => deviceId;
        internal string DataDirectory => dataDirectory;
        internal CreationAuthor Author => new() { Device = deviceId, Computer = deviceId.ToUpperInvariant(), Persona = "Fixture" };

        internal async Task PairAsync(CreationHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = deviceId, DisplayName = deviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, deviceId,
                card.PairingId, card.Token.Reveal());
        }

        internal HostCreationPeer Peer(CreationHost host) => new(host.HostId, () => Connect(host));

        /// <summary>One pass of the desktop's sync with <paramref name="hosts"/> (lab hosts, or stand-ins).</summary>
        internal async Task<CreationSyncResult> SyncAsync(IReadOnlyList<object> hosts, CancellationToken token)
        {
            var peers = hosts.Select(h => h switch
            {
                CreationHost lab => Peer(lab),
                ICreationHost other => other,
                _ => throw new ArgumentException("Not a host.")
            }).ToArray();
            try { return await sync.RunAsync(peers, token); }
            finally { foreach (var peer in peers.OfType<IDisposable>()) peer.Dispose(); }
        }

        internal async Task SendRawAsync(CreationHost host, string sha256, byte[] data, CancellationToken token)
        {
            using var connection = Connect(host);
            await connection.SendCreationChunkAsync(sha256, data, token);
        }

        // A restarted lab host has a new port and forgets volatile pairings; the desktop pairs again, as it would after a reset.
        private Audio2FaceHostConnection Connect(CreationHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            if (pairing.Origin != host.Origin)
            {
                PairAsync(host).GetAwaiter().GetResult();
                (pairing, secret) = pairings[host.HostId];
            }
            return new Audio2FaceHostConnection(pairing, secret);
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with an in-memory copy of creations.json and the pieces that survives a restart,
    /// counting how often desktops read the list and pieces.</summary>
    private sealed class CreationHost : IAsyncDisposable, IGatewayCreationStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private int chunkReads;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal byte[]? SavedLibrary { get; private set; }
        internal Dictionary<string, byte[]> SavedChunks { get; } = new(StringComparer.Ordinal);
        internal int ChunkReads => Volatile.Read(ref chunkReads);

        internal static async Task<CreationHost> StartAsync(string hostId)
        {
            var host = new CreationHost { HostId = hostId, certificate = Certificate() };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                await host.ListenAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        private async Task ListenAsync()
        {
            Origin = $"https://127.0.0.1:{FreePort()}";
            var origin = new GatewayOrigin(Origin);
            Server = new GatewayServer(Identity, origin, [], this);
            Server.AttachCreationStorage(this);
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task RestartAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
            await ListenAsync();
        }

        public byte[]? LoadLibrary() => SavedLibrary;
        public void SaveLibrary(byte[] bytes) => SavedLibrary = bytes;
        public bool HasChunk(string sha256) => SavedChunks.ContainsKey(sha256);

        public byte[]? LoadChunk(string sha256)
        {
            Interlocked.Increment(ref chunkReads);
            return SavedChunks.TryGetValue(sha256, out var bytes) ? (byte[])bytes.Clone() : null;
        }

        public void SaveChunk(string sha256, byte[] bytes) => SavedChunks[sha256] = (byte[])bytes.Clone();
        public void RemoveChunk(string sha256) => SavedChunks.Remove(sha256);

        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
            certificate?.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet creation rehearsal (fixture)", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}

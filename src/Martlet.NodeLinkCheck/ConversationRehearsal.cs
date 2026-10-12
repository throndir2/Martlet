using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Conversation;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses one record of conversations on every computer (docs/MEMORY.md) end to end on this PC with the production code: two
/// real gateways (Kestrel, pinned TLS) on 127.0.0.1 with the household's account directory and in-memory conversation storage,
/// and simulated desktops, each with a real record of conversations (Martlet.Conversation.ConversationHistory) in a temporary
/// folder, the desktop's paired client (HostConversationPeer) and the real record sync (ConversationRecordSync), wired the way
/// the desktop wires them. Conversations are synthetic; nothing leaves loopback and the folder is deleted afterwards.
/// </summary>
internal static class ConversationRehearsal
{
    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "martlet-conversation-rehearsal-" + Guid.NewGuid().ToString("N"));
        // The household: Sam (the owner) is signed in on A, B and C; Alex on D.
        var samAccount = Account.Create("Sam", AccountRoles.Owner, Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7"));
        var alexAccount = Account.Create("Alex", AccountRoles.Member, Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0"));
        foreach (var device in new[] { "lab-desktop-a", "lab-desktop-b", "lab-desktop-c" })
            samAccount = samAccount.WithDevice(AccountDevice.For(device, AccountLoginKey.ForWindows(device, "S-1-5-21-1-2-3-1001"), started));
        alexAccount = alexAccount.WithDevice(AccountDevice.For("lab-desktop-d", AccountLoginKey.ForWindows("lab-desktop-d", "S-1-5-21-1-2-3-1002"), started));
        using (var founder = NetworkKey.Create("lab-desktop-a"))
            LabHost.Accounts = AccountDirectory.Empty.Put(founder, samAccount, started).Put(founder, alexAccount, started).Write();
        await using var h1 = await LabHost.StartAsync("lab-record-1");
        await using var h2 = await LabHost.StartAsync("lab-record-2");
        LabHost[] hosts = [h1, h2];
        var sam = samAccount.Id;
        var a = new LabDesktop("lab-desktop-a", Path.Combine(root, "a"), sam);
        var b = new LabDesktop("lab-desktop-b", Path.Combine(root, "b"), sam);
        var c = new LabDesktop("lab-desktop-c", Path.Combine(root, "c"), sam);
        var d = new LabDesktop("lab-desktop-d", Path.Combine(root, "d"), sam);
        var zone = TimeZoneInfo.Utc;
        Guid trip = Guid.NewGuid(), joke = Guid.NewGuid();
        HistoryExchange? kyoto = null, stay = null, pin = null;

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
            await Run("Desktops A, B, C (Sam) and D (Alex) pair with lab-record-1 and lab-record-2 (signed, pinned connections)", async () =>
            {
                foreach (var desktop in new[] { a, b, c, d })
                foreach (var host in hosts)
                    await desktop.PairAsync(host);
                return (true, "each desktop paired with both hosts");
            });

            await Run("A records a conversation about a trip and syncs: both hosts keep the month for Sam's account", async () =>
            {
                kyoto = await a.History.AppendAsync(trip, HistoryInputKind.Spoken, "We should visit Kyoto in April.",
                    "The cherry blossoms will be out then.", "Sam");
                stay = await a.History.AppendAsync(trip, HistoryInputKind.Typed, "Where should we stay?", "A ryokan near Gion would be lovely.", "Sam");
                pin = await a.History.AppendAsync(joke, HistoryInputKind.Typed, "My PIN is 4321. Now tell me a joke.", "Why did the owl cross the road?", null);
                var result = await a.SyncAsync(hosts, token);
                var months = await Task.WhenAll(hosts.Select(h => a.MonthsAsync(h, token)));
                return (result.Recorded == 3 && result.Same == 2 && months.All(m => m.Count == 1),
                    $"recorded {result.Recorded}; the same on {result.Same} of 2 hosts; months on the hosts: {string.Join(", ", months.Select(m => m.Count))}");
            });

            await Run("B (now the companion) syncs, recalls the trip when asked (PastConversations) and search_conversations finds it", async () =>
            {
                await b.History.LoadAsync(token);
                var result = await b.SyncAsync(hosts, token);
                var now = DateTimeOffset.UtcNow;
                var recalled = PastConversations.Recall(b.History, "Do you remember what we said about Kyoto?", now, zone, Guid.NewGuid());
                var (request, _) = PastConversations.Parse("{\"query\":\"ryokan\"}", now, zone);
                var found = request is null ? [] : PastConversations.Find(b.History, request, Guid.NewGuid());
                return (result.Taken == 3 && recalled.Any(e => e.Id == kyoto!.Id) && found.Any(e => e.Id == stay!.Id),
                    $"took {result.Taken}; recall brings back the Kyoto exchange: {recalled.Any(e => e.Id == kyoto!.Id)}; " +
                    $"search finds the ryokan exchange: {found.Any(e => e.Id == stay!.Id)}");
            });

            await Run("B edits Martlet's reply; A takes the edit (marked edited)", async () =>
            {
                await b.History.ChangeAsync(stay!.Id, e => e with { Reply = "A ryokan near Gion, with a garden view.", Edited = DateTimeOffset.UtcNow }, token);
                await b.SyncAsync(hosts, token);
                var result = await a.SyncAsync(hosts, token);
                var edited = (await a.ReadAsync()).Exchange(stay.Id);
                return (result.Taken == 1 && edited is { Reply: "A ryokan near Gion, with a garden view.", Edited: not null },
                    $"A took {result.Taken}; reply edited there: {edited?.Reply == "A ryokan near Gion, with a garden view."}");
            });

            await Run("A deletes your message with the PIN, then the whole joke conversation: B deletes them, and they never come back", async () =>
            {
                await a.History.ChangeAsync(pin!.Id, e => e with { User = "", Edited = DateTimeOffset.UtcNow }, token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var blanked = (await b.ReadAsync()).Exchange(pin.Id)?.User == "";
                await a.History.DeleteAsync(joke, token);
                var gave = await a.SyncAsync(hosts, token);
                var took = await b.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                var aHas = (await a.ReadAsync()).Exchanges(joke).Count;
                var bHas = (await b.ReadAsync()).Exchanges(joke).Count;
                var hostCopy = await a.ReadMonthAsync(h1, SharedConversations.MonthOf(pin.At), token);
                var leaks = new[] { h1, h2 }.Count(h => h.Contains("4321"));
                return (blanked && gave.Recorded == 1 && took.Deleted == 1 && aHas == 0 && bHas == 0 && hostCopy.Find(pin.Id) is { Deleted: true } && leaks == 0,
                    $"message blanked on B: {blanked}; A recorded {gave.Recorded} deletion; B deleted {took.Deleted}; after more syncs A has " +
                    $"{aHas} and B {bHas}; the hosts keep a tombstone and no PIN: {leaks == 0}");
            });

            await Run("Offline on both: A and B edit the same reply (B a moment later); after syncing, every computer has B's edit", async () =>
            {
                await a.History.ChangeAsync(kyoto!.Id, e => e with { Reply = "Cherry blossoms, early April.", Edited = DateTimeOffset.UtcNow }, token);
                await Task.Delay(20, token);
                await b.History.ChangeAsync(kyoto.Id, e => e with { Reply = "Cherry blossoms peak in early April, book soon!", Edited = DateTimeOffset.UtcNow }, token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                var onA = (await a.ReadAsync()).Exchange(kyoto.Id)?.Reply;
                var onB = (await b.ReadAsync()).Exchange(kyoto.Id)?.Reply;
                return (onA == onB && onA == "Cherry blossoms peak in early April, book soon!", $"A: \"{onA}\"; B: \"{onB}\"");
            });

            await Run("lab-record-2 stops; A records an exchange; lab-record-2 restarts with its saved copies and gets the change on the next sync", async () =>
            {
                await h2.StopAsync();
                var later = await a.History.AppendAsync(trip, HistoryInputKind.Typed, "Add a day trip to Nara.", "Nara's deer park is a short train ride away.", "Sam");
                var whileDown = await a.SyncAsync(hosts, token);
                await h2.StartAsync();
                foreach (var desktop in new[] { a, b, c, d }) await desktop.PairAsync(h2);
                var before = (await a.ReadMonthAsync(h2, SharedConversations.MonthOf(later.At), token)).Find(later.Id) is not null;
                var after = await a.SyncAsync(hosts, token);
                var copy = await a.ReadMonthAsync(h2, SharedConversations.MonthOf(later.At), token);
                return (whileDown.Down == 1 && !before && after.Same == 2 && copy.Find(later.Id) is { Deleted: false } && copy.Find(kyoto!.Id) is not null,
                    $"while stopped: {whileDown.Down} not responding; restarted with its saved copies: {copy.Find(kyoto!.Id) is not null}; " +
                    $"had the new exchange before A synced: {before}; after A synced the same on {after.Same} of 2 hosts and it has it: " +
                    $"{copy.Find(later.Id) is { Deleted: false }}");
            });

            await Run("C, a new computer of Sam's, takes the whole record from the hosts", async () =>
            {
                var result = await c.SyncAsync(hosts, token);
                var record = await c.ReadAsync();
                var mine = await a.ReadAsync();
                var same = record.Exchanges(trip).Select(e => (e.Id, e.Reply)).SequenceEqual(mine.Exchanges(trip).Select(e => (e.Id, e.Reply)));
                return (result.Taken == 3 && same, $"took {result.Taken}; the trip conversation is the same as A's: {same}");
            });

            await Run("An exchange a newer Martlet changed passes through B untouched and B never writes its older line over it", async () =>
            {
                var month = SharedConversations.MonthOf(kyoto!.At);
                var copy = await b.ReadMonthAsync(h1, month, token);
                var line = System.Text.Json.Nodes.JsonNode.Parse(copy.Find(kyoto.Id)!.Exchange!)!.AsObject();
                line["v"] = 2;
                line["mood"] = "excited";
                var newer = copy.Find(kyoto.Id)! with { Revision = copy.Find(kyoto.Id)!.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "newer-pc", Exchange = line.ToJsonString() };
                await b.MergeMonthAsync(h1, SharedConversations.Of(month, [newer]), token);
                var first = await b.SyncAsync(hosts, token);
                var second = await b.SyncAsync(hosts, token);
                var kept = (await b.ReadMonthAsync(h2, month, token)).Find(kyoto.Id)!;
                return (first.Unreadable == 1 && second.Recorded == 0 && kept.Exchange!.Contains("excited", StringComparison.Ordinal) &&
                        (await b.ReadAsync()).Exchange(kyoto.Id) is not null,
                    $"B can't read it: {first.Unreadable}; recorded nothing over it: {second.Recorded == 0}; both hosts keep the newer version: " +
                    $"{kept.Exchange!.Contains("excited", StringComparison.Ordinal)}");
            });

            await Run("D, where only Alex is signed in, is refused Sam's record on both hosts (conversations.account_denied)", async () =>
            {
                var result = await d.SyncAsync(hosts, token);
                return (result.Denied == 2 && result.Taken == 0 && (await d.ReadAsync()).Stats.Exchanges == 0,
                    $"refused by {result.Denied} of 2 hosts; took {result.Taken}");
            });

            await Run("C deletes everything: A and B delete their whole record too", async () =>
            {
                await c.History.DeleteAllAsync(token);
                await c.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var aLeft = (await a.ReadAsync()).Stats.Exchanges;
                var bLeft = (await b.ReadAsync()).Stats.Exchanges;
                // The newer Martlet's version stays on B until a newer Martlet there deletes it or it is deleted too.
                return (aLeft == 0 && bLeft <= 1, $"A keeps {aLeft} exchanges; B keeps {bLeft}");
            });

            await Run("Sync state keeps no conversation text; only paired devices may read the hosts' copies", async () =>
            {
                var leaks = new[] { a, b, c, d }.Where(x => x.StateContains("Gion") || x.StateContains("4321")).Select(x => x.DeviceId).ToArray();
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(h1.Origin + "/martlet/v1/conversations/accounts/" + sam.ToString("N"), token);
                var body = await response.Content.ReadAsStringAsync(token);
                return (leaks.Length == 0 && response.StatusCode != HttpStatusCode.OK && !body.Contains("months", StringComparison.Ordinal),
                    $"conversation text in sync state: {(leaks.Length == 0 ? "none" : string.Join(", ", leaks))}; unsigned request: HTTP {(int)response.StatusCode}");
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
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, signed requests) with the household's account directory and " +
                "in-memory conversation storage, and simulated desktops using the desktop's paired client and the real record sync over real " +
                "records of conversations in a temporary folder, wired as the desktop wires them. Synthetic conversations. Not covered: the " +
                "desktop window and its 30-second sync, a reply recalling around a sync, the Linux host's files and two real computers on a LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    /// <summary>A simulated desktop: its record of conversations, the real record sync and its pairings, wired as the desktop
    /// wires them for the account signed in.</summary>
    private sealed class LabDesktop
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly Guid account;
        internal string DeviceId { get; }
        internal ConversationHistory History { get; }
        internal ConversationRecordSync Sync { get; }

        internal LabDesktop(string deviceId, string directory, Guid account)
        {
            DeviceId = deviceId;
            this.account = account;
            History = new ConversationHistory(Path.Combine(directory, ConversationHistory.DirectoryName));
            Sync = new ConversationRecordSync(History, deviceId);
        }

        internal async Task PairAsync(LabHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = DeviceId, DisplayName = DeviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, DeviceId,
                card.PairingId, card.Token.Reveal());
        }

        private Audio2FaceHostConnection Connect(LabHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            return new Audio2FaceHostConnection(pairing, secret);
        }

        /// <summary>What the desktop's record sync does: one peer per paired host for the account signed in, the real engine.</summary>
        internal async Task<ConversationSyncResult> SyncAsync(IEnumerable<LabHost> hosts, CancellationToken token)
        {
            var peers = hosts.Select(host => new HostConversationPeer(host.HostId, () => Connect(host), account)).ToArray();
            try { return await Sync.RunAsync(peers, DateTimeOffset.UtcNow, token); }
            finally { foreach (var peer in peers) peer.Dispose(); }
        }

        internal async Task<IReadOnlyDictionary<string, string>> MonthsAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadConversationMonthsAsync(account, token);
        }

        internal async Task<SharedConversations> ReadMonthAsync(LabHost host, string month, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadConversationMonthAsync(account, month, token);
        }

        internal async Task<SharedConversations> MergeMonthAsync(LabHost host, SharedConversations copy, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeConversationMonthAsync(account, copy, token);
        }

        /// <summary>The record as the desktop reads it again (a fresh load from the month files).</summary>
        internal async Task<ConversationHistory> ReadAsync()
        {
            var record = new ConversationHistory(History.Directory);
            await record.LoadAsync();
            return record;
        }

        internal bool StateContains(string text) => Directory.Exists(Sync.StateDirectory) &&
            Directory.EnumerateFiles(Sync.StateDirectory, "*", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path).Contains(text, StringComparison.Ordinal));
    }

    /// <summary>A real gateway on 127.0.0.1 with the household's account directory and conversation storage that survives a restart.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayConversationStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private readonly Dictionary<string, byte[]> saved = new(StringComparer.Ordinal);
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";

        /// <summary>The household's account directory every lab host starts with (accounts.json).</summary>
        internal static byte[]? Accounts { get; set; }

        internal static async Task<LabHost> StartAsync(string hostId)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate() };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                await host.StartAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        internal async Task StartAsync()
        {
            Origin = $"https://127.0.0.1:{FreePort()}";
            var origin = new GatewayOrigin(Origin);
            Server = new GatewayServer(Identity, origin, [], this);
            Server.AttachConversationStorage(this);
            if (Accounts is { } accounts) Server.AttachAccountStorage(new AccountFile(accounts));
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
        }

        internal bool Contains(string text) =>
            saved.Values.Any(bytes => System.Text.Encoding.UTF8.GetString(bytes).Contains(text, StringComparison.Ordinal));

        private sealed class AccountFile(byte[] bytes) : IGatewayAccountStorage
        {
            public byte[]? Load() => bytes;
            public void Save(byte[] value) { }
        }

        public IReadOnlyCollection<string> List() => saved.Keys.ToArray();
        public byte[]? Load(string key) => saved.TryGetValue(key, out var bytes) ? bytes : null;
        public void Save(string key, byte[] bytes) => saved[key] = (byte[])bytes.Clone();
        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
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
            var request = new CertificateRequest("CN=Martlet conversation rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

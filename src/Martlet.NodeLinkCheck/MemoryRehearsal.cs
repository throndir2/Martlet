using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Gateway;
using Martlet.Memory;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses one memory on every computer (docs/MEMORY.md) end to end on this PC with the production code: two real gateways
/// (Kestrel, pinned TLS) on 127.0.0.1 with an in-memory memories.json, and three simulated desktops, each with a real
/// Martlet.Memory store in a temporary folder, the desktop's paired client and the real sync engine
/// (Martlet.Core.Sync.MemorySyncNode), wired the way the desktop wires them. Facts are synthetic; nothing leaves loopback and the
/// folder is deleted afterwards.
/// </summary>
internal static class MemoryRehearsal
{
    private const string Cat = "The owner's cat is called Miso.";
    private const string CatOlder = "The owner's cat is called Miso and is three years old.";
    private const string Tea = "The owner drinks green tea in the morning.";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "martlet-memory-rehearsal-" + Guid.NewGuid().ToString("N"));
        // The household's account directory on both hosts: Sam, the owner from before accounts, is signed in on A, B and C; Alex
        // on D. E is a desktop on an older Martlet, with no account.
        var samAccount = Account.Create("Sam", AccountRoles.Owner, Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7"));
        var alexAccount = Account.Create("Alex", AccountRoles.Member, Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0"));
        foreach (var device in new[] { "lab-desktop-a", "lab-desktop-b", "lab-desktop-c" })
            samAccount = samAccount.WithDevice(AccountDevice.For(device, AccountLoginKey.ForWindows(device, "S-1-5-21-1-2-3-1001"), started));
        alexAccount = alexAccount.WithDevice(AccountDevice.For("lab-desktop-d", AccountLoginKey.ForWindows("lab-desktop-d", "S-1-5-21-1-2-3-1002"), started));
        using (var founder = NetworkKey.Create("lab-desktop-a"))
            LabHost.Accounts = AccountDirectory.Empty.Put(founder, samAccount, started).Put(founder, alexAccount, started).Write();
        await using var h1 = await LabHost.StartAsync("lab-memory-1");
        await using var h2 = await LabHost.StartAsync("lab-memory-2");
        LabHost[] hosts = [h1, h2];
        var a = new LabDesktop("lab-desktop-a", Path.Combine(root, "a"));
        var b = new LabDesktop("lab-desktop-b", Path.Combine(root, "b"));
        var c = new LabDesktop("lab-desktop-c", Path.Combine(root, "c"));
        Guid catId = default, teaId = default;

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
            await Run("Desktops A, B and C pair with lab-memory-1 and lab-memory-2 (signed, pinned connections)", async () =>
            {
                foreach (var desktop in new[] { a, b, c })
                foreach (var host in hosts)
                    await desktop.PairAsync(host);
                return (true, "each desktop paired with both hosts");
            });

            await Run("A remembers two facts (one the owner typed, one picked out of a conversation) and syncs: both hosts keep them", async () =>
            {
                catId = (await a.SaveAsync(Cat, typed: true)).Id;
                teaId = (await a.SaveAsync(Tea, typed: false)).Id;
                var result = await a.SyncAsync(hosts, token);
                var h1Copy = await a.ReadAsync(h1, token);
                var h2Copy = await a.ReadAsync(h2, token);
                return (result.Recorded == 2 && h1Copy.Live.Count() == 2 && h2Copy.Live.Count() == 2,
                    $"recorded {result.Recorded}; hosts keep {h1Copy.Live.Count()} and {h2Copy.Live.Count()} facts");
            });

            await Run("B (now the companion) syncs and recalls what A remembered: the same facts, IDs, revisions and provenance", async () =>
            {
                var result = await b.SyncAsync(hosts, token);
                var facts = await b.FactsAsync();
                var mine = await a.FactsAsync();
                var recalled = await b.RecallAsync("What is my cat called?");
                return (result.Taken == 2 && Same(facts, mine) && recalled.Any(f => f.Id == catId),
                    $"took {result.Taken}; same as A: {Same(facts, mine)}; recalling \"cat\" finds the cat fact: {recalled.Any(f => f.Id == catId)}");
            });

            await Run("B edits the cat fact; A takes the edit (revision 2, B's words, A's original creation kept)", async () =>
            {
                await b.EditAsync(catId, CatOlder);
                await b.SyncAsync(hosts, token);
                var result = await a.SyncAsync(hosts, token);
                var fact = (await a.FactsAsync()).Single(f => f.Id == catId);
                return (result.Taken == 1 && fact is { Revision: 2, Content: CatOlder } && fact.CreatedFrom.SourceKind == MemorySourceKind.UserEntry,
                    $"A took {result.Taken}; revision {fact.Revision}; content updated: {fact.Content == CatOlder}");
            });

            await Run("Whose memories: A remembers a fact that belongs to Sam's voice; B takes it as Sam's, makes it everyone's and A takes that; a fact with no voice is written exactly as before voices", async () =>
            {
                const string sam = "5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7";
                var id = (await a.SaveAsync("Sam is allergic to peanuts.", typed: false, voiceId: sam)).Id;
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var onB = (await b.FactsAsync()).Single(f => f.Id == id);
                await b.EditAsync(id, onB.Content, voiceId: "");
                await b.SyncAsync(hosts, token);
                var result = await a.SyncAsync(hosts, token);
                var onA = (await a.FactsAsync()).Single(f => f.Id == id);
                var plain = !MemoryFactJson.Write((await a.FactsAsync()).Single(f => f.Id == catId)).Contains("voice_id", StringComparison.Ordinal);
                return (onB.VoiceId == sam && result.Taken == 1 && onA is { Revision: 2, VoiceId: null } && plain,
                    $"B took it as Sam's: {onB.VoiceId == sam}; A took B's change: {result.Taken} (revision {onA.Revision}, everyone's: {onA.VoiceId is null}); " +
                    $"a fact with no voice has no voice_id field: {plain}");
            });

            await Run("A forgets the tea fact: every computer forgets it, and it never comes back from a computer that had it", async () =>
            {
                await a.DeleteAsync(teaId);
                var forget = await a.SyncAsync(hosts, token);
                var bResult = await b.SyncAsync(hosts, token);
                var again = await b.SyncAsync(hosts, token);
                var aAgain = await a.SyncAsync(hosts, token);
                var bHas = (await b.FactsAsync()).Any(f => f.Id == teaId);
                var aHas = (await a.FactsAsync()).Any(f => f.Id == teaId);
                var hostLive = (await a.ReadAsync(h1, token)).Find(teaId);
                return (forget.Recorded == 1 && bResult.Forgot == 1 && !bHas && !aHas && again.Taken == 0 && aAgain.Taken == 0 && hostLive is { Forgotten: true },
                    $"A recorded {forget.Recorded} deletion; B forgot {bResult.Forgot}; still gone on A and B after more syncs: {!aHas && !bHas}");
            });

            await Run("Offline on both: A and B edit different facts, then the same fact (B five milliseconds later); after syncing, both have both changes and B's later edit", async () =>
            {
                var aFact = (await a.SaveAsync("The owner's birthday is in May.", typed: true)).Id;
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                await a.EditAsync(aFact, "The owner's birthday is on May 4.");
                await b.SaveAsync("The owner plays the cello.", typed: true);
                await a.EditAsync(catId, "The owner's cat Miso likes boxes.");
                await Task.Delay(5, token);
                await b.EditAsync(catId, "The owner's cat Miso is orange.");
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                var af = await a.FactsAsync();
                var bf = await b.FactsAsync();
                var cat = af.Single(f => f.Id == catId).Content;
                return (Same(af, bf) && cat == "The owner's cat Miso is orange." && af.Any(f => f.Content == "The owner plays the cello.") &&
                        af.Any(f => f.Content == "The owner's birthday is on May 4."),
                    $"same on A and B: {Same(af, bf)}; cat fact: \"{cat}\"; {af.Count} facts");
            });

            await Run("A change a host missed reaches it later: lab-memory-1 is down while A remembers a fact, then restarts with its saved copy and gets it on A's next sync", async () =>
            {
                await h1.StopAsync();
                var id = (await a.SaveAsync("The owner's favourite colour is teal.", typed: true)).Id;
                await a.SyncAsync(hosts, token);
                var onH2 = (await a.ReadAsync(h2, token)).Find(id) is { Forgotten: false };
                await h1.StartAsync();
                foreach (var desktop in new[] { a, b, c }) await desktop.PairAsync(h1);
                var before = (await a.ReadAsync(h1, token)).Find(id) is not null;
                await a.SyncAsync(hosts, token);
                var after = (await a.ReadAsync(h1, token)).Find(id) is { Forgotten: false };
                return (onH2 && !before && after, $"lab-memory-2 had it: {onH2}; restarted lab-memory-1 before A synced: {before}, after: {after}");
            });

            await Run("New computer C (with one fact of its own) syncs: it takes everything, its own fact reaches A, and it records no deletions", async () =>
            {
                var own = (await c.SaveAsync("The owner works from home on Fridays.", typed: true)).Id;
                var result = await c.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                var af = await a.FactsAsync();
                var cf = await c.FactsAsync();
                return (Same(af, cf) && af.Any(f => f.Id == own) && result.Recorded == 1,
                    $"C took {result.Taken}, recorded {result.Recorded}; same as A: {Same(af, cf)}; C's fact on A: {af.Any(f => f.Id == own)}");
            });

            await Run("An expiring fact: A saves one that expires in two seconds; B takes it; once it expired every computer forgets it", async () =>
            {
                var id = (await a.SaveAsync("The owner's parcel arrives today.", typed: true, expiresIn: TimeSpan.FromSeconds(2))).Id;
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var taken = (await b.FactsAsync()).Any(f => f.Id == id);
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var left = (await b.FactsAsync()).Any(f => f.Id == id) || (await a.FactsAsync()).Any(f => f.Id == id);
                var host = (await a.ReadAsync(h2, token)).Find(id);
                return (taken && !left && host is { Forgotten: true }, $"B had it: {taken}; gone everywhere after it expired: {!left}; the hosts keep it as forgotten: {host?.Forgotten}");
            });

            await Run("A fact from a newer Martlet passes through hosts and this version's desktops untouched", async () =>
            {
                var id = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var newer = SharedMemories.Empty.With(new SharedMemory
                {
                    Id = id, Revision = 1, UpdatedAt = now, UpdatedBy = "lab-desktop-future",
                    Fact = "{\"schema_version\":2,\"id\":\"" + id + "\",\"content\":\"A future fact.\",\"mood\":\"curious\"}"
                });
                await b.MergeAsync(h1, newer, token);
                var result = await b.SyncAsync(hosts, token);
                var onH2 = (await b.ReadAsync(h2, token)).Find(id) is { Forgotten: false };
                var inStore = (await b.FactsAsync()).Any(f => f.Id == id);
                return (result.Unreadable == 1 && onH2 && !inStore, $"B can't read {result.Unreadable}; passed on to lab-memory-2: {onH2}; kept out of B's store: {!inStore}");
            });

            await Run("A new memory folder on B is a new store: B takes every fact again and forgets nothing anywhere", async () =>
            {
                var before = (await b.ReadAsync(h1, token)).Live.Count();
                b.UseMemoryFolder(Path.Combine(root, "b-moved"));
                var result = await b.SyncAsync(hosts, token);
                var after = (await b.ReadAsync(h1, token)).Live.Count();
                var same = Same(await b.FactsAsync(), await a.FactsAsync());
                return (result.Recorded == 0 && before == after && same, $"took {result.Taken}, recorded {result.Recorded}; facts on lab-memory-1 {before} -> {after}; same as A: {same}");
            });

            await Run("Together too many for one store (512): every computer forgets the oldest facts picked out of conversations, never one the owner typed", async () =>
            {
                await a.BulkAsync(300, "a");
                await c.BulkAsync(300, "c");
                var typedBefore = (await a.FactsAsync()).Count(f => f.LastModifiedBy.SourceKind != MemorySourceKind.Conversation);
                await a.SyncAsync(hosts, token);
                await c.SyncAsync(hosts, token);
                await a.SyncAsync(hosts, token);
                await b.SyncAsync(hosts, token);
                var af = await a.FactsAsync();
                var cf = await c.FactsAsync();
                var bf = await b.FactsAsync();
                var typedAfter = af.Count(f => f.LastModifiedBy.SourceKind != MemorySourceKind.Conversation);
                var oldestKept = af.Where(f => f.Content.StartsWith("Bulk", StringComparison.Ordinal)).Min(f => f.UpdatedAtUtc);
                return (af.Count <= MemorySyncNode.StoreCapacity && Same(af, cf) && Same(af, bf) && typedAfter >= typedBefore,
                    $"A holds {af.Count}, C {cf.Count}, B {bf.Count}; same everywhere: {Same(af, cf) && Same(af, bf)}; typed facts kept: {typedAfter} of {typedBefore}; oldest conversation fact kept from {oldestKept:u}");
            });

            await Run("Memory spaces: A puts a fact in Sam's account space on both hosts; B reads it there with the same digest; Alex's space is refused to B (Alex isn't signed in there); the household and the old document never see it; it survives a host restart; a bad space ID never leaves the desktop", async () =>
            {
                var sam = MemorySpaceId.Account(Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7"));
                var alex = MemorySpaceId.Account(Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0"));
                var id = Guid.NewGuid();
                var fact = SharedMemories.Empty.With(new SharedMemory
                {
                    Id = id, Revision = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = a.DeviceId,
                    Fact = SharedMemories.Canonical("{\"id\":\"" + id + "\",\"content\":\"Sam's sister is called Ines.\"}")
                });
                foreach (var host in hosts) await a.MergeSpaceAsync(host, sam, fact, token);
                var onB = await b.ReadSpaceAsync(h2, sam, token);
                var digest = await b.ReadSpaceDigestAsync(h1, sam, token);
                var apart = await Denied(() => b.ReadSpaceAsync(h1, alex, token)) &&
                    (await b.ReadSpaceAsync(h1, MemorySpaceId.Household, token)).Facts.Count == 0 &&
                    (await a.ReadAsync(h1, token)).Find(id) is null;
                await h1.StopAsync();
                await h1.StartAsync();
                foreach (var desktop in new[] { a, b, c }) await desktop.PairAsync(h1);
                var kept = (await c.ReadSpaceAsync(h1, sam, token)).Find(id) is { Forgotten: false };
                var refused = false;
                try { await a.ReadSpaceAsync(h1, "account-not-a-space", token); }
                catch (ArgumentException) { refused = true; }
                return (onB.Find(id) is { Forgotten: false } && digest == fact.Digest() && apart && kept && refused &&
                        h1.Server.MemorySpaces.SequenceEqual([sam]),
                    $"B reads it from lab-memory-2: {onB.Find(id) is { Forgotten: false }}; digest on lab-memory-1 matches: {digest == fact.Digest()}; " +
                    $"other spaces and the old document stay without it: {apart}; kept after lab-memory-1 restarted: {kept}; " +
                    $"bad space ID refused before sending: {refused}; lab-memory-1 keeps spaces [{string.Join(", ", h1.Server.MemorySpaces)}]");
            });

            var samSpace = samAccount.SpaceId;
            var alexPc = new LabDesktop("lab-desktop-d", Path.Combine(root, "d"));
            var olderPc = new LabDesktop("lab-desktop-e", Path.Combine(root, "e"));
            foreach (var desktop in new[] { alexPc, olderPc })
            foreach (var host in hosts)
                await desktop.PairAsync(host);

            await Run("Accounts: A's memories from before accounts move into Sam's space with the same store, so its next sync (space and old document together) records nothing new and gives both hosts' spaces every fact", async () =>
            {
                var before = await a.FactsAsync();
                var moved = a.MoveIntoSpace(samSpace);
                var result = await a.SyncSpaceAsync(hosts, samSpace, oldDocument: true, token);
                var inSpace = await a.FactsAsync(samSpace);
                // Every fact A keeps is live in both hosts' copies of Sam's space and in the old document (they also carry facts
                // this version can't read, which never enter a store).
                bool Holds(SharedMemories copy) => inSpace.All(f => copy.Find(f.Id) is { Forgotten: false });
                var onHosts = Holds(await a.ReadSpaceAsync(h1, samSpace, token)) && Holds(await a.ReadSpaceAsync(h2, samSpace, token));
                var old = Holds(await a.ReadAsync(h1, token)) && Holds(await a.ReadAsync(h2, token));
                var kept = before.Count(f => inSpace.Any(g => g.Id == f.Id));
                return (moved && result.Recorded == 0 && kept >= before.Count - result.Forgot && onHosts && old,
                    $"moved: {moved}; recorded as new: {result.Recorded}; took {result.Taken}, forgot {result.Forgot} (to make room); " +
                    $"Sam's space holds {inSpace.Count}, {kept} of the {before.Count} from before; both hosts' spaces hold them: {onHosts}; " +
                    $"the old document holds them: {old}");
            });

            await Run("Accounts: B syncs Sam's space and the household; D (Alex) shares the household but is refused Sam's space and the old document; E (an older Martlet, no account) keeps using the old document and A's next sync brings its fact into Sam's space", async () =>
            {
                b.MoveIntoSpace(samSpace);
                var wifi = (await a.SaveAsync("The household Wi-Fi network is called Nest.", typed: true, space: MemorySpaceId.Household)).Id;
                await a.SyncSpaceAsync(hosts, MemorySpaceId.Household, oldDocument: false, token);
                await b.SyncSpaceAsync(hosts, samSpace, oldDocument: true, token);
                await b.SyncSpaceAsync(hosts, MemorySpaceId.Household, oldDocument: false, token);
                var bHas = (await b.FactsAsync(samSpace)).Count == (await a.FactsAsync(samSpace)).Count &&
                    (await b.FactsAsync(MemorySpaceId.Household)).Any(f => f.Id == wifi);
                await alexPc.SyncSpaceAsync(hosts, MemorySpaceId.Household, oldDocument: false, token);
                var dHousehold = (await alexPc.FactsAsync(MemorySpaceId.Household)).Any(f => f.Id == wifi);
                var dRefused = await Denied(() => alexPc.ReadSpaceAsync(h1, samSpace, token)) && await Denied(() => alexPc.ReadAsync(h1, token));
                var older = (await olderPc.SaveAsync("The owner's bike is blue.", typed: true)).Id;
                await olderPc.SyncAsync(hosts, token);
                var eTook = (await olderPc.FactsAsync()).Count;
                await a.SyncSpaceAsync(hosts, samSpace, oldDocument: true, token);
                var aTook = (await a.FactsAsync(samSpace)).Any(f => f.Id == older);
                return (bHas && dHousehold && dRefused && eTook > 1 && aTook,
                    $"B has Sam's space and the household fact: {bHas}; D has the household fact: {dHousehold}; D refused Sam's space and " +
                    $"the old document: {dRefused}; E took {eTook} facts from the old document; A took E's fact into Sam's space: {aTook}");
            });

            await Run("Desktops keep no fact in Martlet's data folder (only IDs, revisions and digests); only paired devices may read the hosts' copy", async () =>
            {
                var leaks = new[] { a, b, c, alexPc, olderPc }.Where(x => x.DataFolderContains("Miso") || x.DataFolderContains("Nest")).Select(x => x.DeviceId).ToArray();
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(h1.Origin + "/martlet/v1/memories", token);
                var body = await response.Content.ReadAsStringAsync(token);
                var hostHas = h1.SavedText?.Contains("Miso", StringComparison.Ordinal) == true;
                return (leaks.Length == 0 && hostHas && response.StatusCode != HttpStatusCode.OK && !body.Contains("Miso", StringComparison.Ordinal),
                    $"facts in desktop data folders: {(leaks.Length == 0 ? "none" : string.Join(", ", leaks))}; host copy holds them: {hostHas}; " +
                    $"unsigned request: HTTP {(int)response.StatusCode}, no fact in the answer");
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
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, signed requests) with an in-memory memories.json and memory spaces, and three simulated " +
                "desktops using the desktop's paired client and the real memory sync engine over real Martlet.Memory stores in a temporary " +
                "folder, wired as the desktop wires them. Synthetic facts. Not covered: the desktop window and its 30-second sync, a " +
                "conversation's recall and remembering around a sync, the Linux host's file and two real computers on a LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    /// <summary>Whether the host refused the device the memory space (or the old document): <c>memories.space_denied</c>.</summary>
    private static async Task<bool> Denied(Func<Task<SharedMemories>> read)
    {
        try
        {
            await read();
            return false;
        }
        catch (Audio2FaceHostException error) when (error.Code == "memories.space_denied")
        {
            return true;
        }
    }

    private static bool Same(IReadOnlyList<MemoryFact> left, IReadOnlyList<MemoryFact> right) =>
        left.Count == right.Count && left.OrderBy(f => f.Id).Select(MemoryFactJson.Write)
            .SequenceEqual(right.OrderBy(f => f.Id).Select(MemoryFactJson.Write), StringComparer.Ordinal);

    /// <summary>A simulated desktop: Martlet's data folder (memory-sync.json), a real memory store, its pairings and the real
    /// sync engine, opened and wired the way the desktop's memory service and sync do.</summary>
    private sealed class LabDesktop
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly string dataDirectory;
        private string memoryDirectory;
        internal string DeviceId { get; }
        internal MemorySyncNode Node { get; }

        internal LabDesktop(string deviceId, string directory)
        {
            DeviceId = deviceId;
            dataDirectory = directory;
            memoryDirectory = Path.Combine(directory, "memory");
            Directory.CreateDirectory(directory);
            Node = new MemorySyncNode(directory, deviceId);
        }

        internal void UseMemoryFolder(string directory) => memoryDirectory = directory;

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

        internal async Task<SharedMemories> ReadAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadMemoriesAsync(token);
        }

        internal async Task<SharedMemories> MergeAsync(LabHost host, SharedMemories memories, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeMemoriesAsync(memories, token);
        }

        internal async Task<SharedMemories> ReadSpaceAsync(LabHost host, string space, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadMemorySpaceAsync(space, token);
        }

        internal async Task<string> ReadSpaceDigestAsync(LabHost host, string space, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadMemorySpaceDigestAsync(space, token);
        }

        internal async Task<SharedMemories> MergeSpaceAsync(LabHost host, string space, SharedMemories memories, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeMemorySpaceAsync(space, memories, token);
        }

        /// <param name="space">A memory space's store, where the desktop keeps it (MemorySpaceFolders); null: the store from
        /// before accounts.</param>
        private async Task<T> WithStoreAsync<T>(Func<MemoryStore, Task<T>> action, string? space = null)
        {
            var preview = MemoryStoreActivationPreview.Create(space is null ? memoryDirectory : SpaceStore(space));
            using var store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
            return await action(store);
        }

        private string SpaceStore(string space) => MemorySpaceFolders.Store(dataDirectory, space, MemorySettings.Create(),
            space.StartsWith(MemorySpaceId.AccountPrefix, StringComparison.Ordinal) ? Guid.ParseExact(space[MemorySpaceId.AccountPrefix.Length..], "N") : null);

        /// <summary>What the desktop does for the owner on its first start with accounts: the store from before accounts and
        /// memory-sync.json move into the owner's space (MemoryStore.MoveStore; the store ID stays).</summary>
        internal bool MoveIntoSpace(string space)
        {
            var moved = MemoryStore.MoveStore(memoryDirectory, SpaceStore(space));
            var state = Path.Combine(dataDirectory, MemorySyncState.FileName);
            var target = MemorySpaceFolders.Sync(dataDirectory, space);
            if (File.Exists(state))
            {
                Directory.CreateDirectory(target);
                File.Move(state, Path.Combine(target, MemorySyncState.FileName));
            }
            return moved;
        }

        /// <summary>What the desktop's memory sync does for one space: read every reachable host's copy of the space (and, for
        /// the owner's space, of the old single document), sync the space's store with its own sync state, give each the merged
        /// copy. A host that refuses the space is left out.</summary>
        internal async Task<MemorySyncResult> SyncSpaceAsync(IEnumerable<LabHost> hosts, string space, bool oldDocument, CancellationToken token)
        {
            var copies = new List<SharedMemories>();
            var reachable = new List<(LabHost Host, bool Old)>();
            foreach (var host in hosts.Where(h => h.Running))
            {
                try
                {
                    copies.Add(await ReadSpaceAsync(host, space, token));
                    reachable.Add((host, false));
                }
                catch (Exception error) when (error is HttpRequestException or Audio2FaceHostException or IOException or OperationCanceledException) { }
                if (!oldDocument) continue;
                try
                {
                    copies.Add(await ReadAsync(host, token));
                    reachable.Add((host, true));
                }
                catch (Exception error) when (error is HttpRequestException or Audio2FaceHostException or IOException or OperationCanceledException) { }
            }
            var node = new MemorySyncNode(MemorySpaceFolders.Sync(dataDirectory, space), DeviceId);
            var result = await WithStoreAsync(store => node.SyncAsync(copies, read => ReadLocalAsync(store, read),
                (changes, apply) => store.MergeAsync(new() { Facts = changes.Facts, Forget = changes.Forget }, apply),
                Describe, DateTimeOffset.UtcNow, token), space);
            foreach (var (host, old) in reachable)
                if (old) await MergeAsync(host, result.Document, token);
                else await MergeSpaceAsync(host, space, result.Document, token);
            return result;
        }

        private static async Task<LocalMemories> ReadLocalAsync(MemoryStore store, CancellationToken token)
        {
            var inspection = await store.InspectAsync(token);
            return new LocalMemories(inspection.StoreId,
                [.. inspection.Facts.Select(f => new LocalMemory(f.Id, f.Revision, f.UpdatedAtUtc, MemoryFactJson.Write(f)))]);
        }

        internal Task<MemoryFact> SaveAsync(string content, bool typed, TimeSpan? expiresIn = null, string? voiceId = null, string? space = null) => WithStoreAsync(async store =>
        {
            var now = DateTimeOffset.UtcNow;
            return (await store.SaveAsync(new()
            {
                Content = content,
                Provenance = typed ? MemoryProvenance.UserEntry(Guid.NewGuid(), now) : MemoryProvenance.Conversation(Guid.NewGuid(), now),
                Retention = expiresIn is { } after ? MemoryRetention.ExpiringAt(now + after) : MemoryRetention.UntilDeleted(),
                VoiceId = voiceId
            })).Fact;
        }, space);

        /// <summary>Edits a fact's words, keeping whose it is unless <paramref name="voiceId"/> gives another ("" for no one).</summary>
        internal Task<MemoryFact> EditAsync(Guid id, string content, string? voiceId = null) => WithStoreAsync(async store =>
        {
            var fact = (await store.InspectAsync()).Facts.Single(f => f.Id == id);
            return (await store.EditAsync(new()
            {
                Id = id, ExpectedRevision = fact.Revision, Content = content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), DateTimeOffset.UtcNow), Retention = fact.Retention,
                VoiceId = voiceId is null ? fact.VoiceId : voiceId.Length == 0 ? null : voiceId
            })).Fact;
        });

        internal Task<bool> DeleteAsync(Guid id) => WithStoreAsync(async store =>
        {
            var fact = (await store.InspectAsync()).Facts.Single(f => f.Id == id);
            await store.DeleteAsync(new() { Id = id, ExpectedRevision = fact.Revision, ConsentId = Guid.NewGuid() });
            return true;
        });

        internal Task<IReadOnlyList<MemoryFact>> FactsAsync(string? space = null) => WithStoreAsync(async store => (await store.InspectAsync()).Facts, space);

        internal Task<IReadOnlyList<MemoryFact>> RecallAsync(string query) => WithStoreAsync(async store =>
            (IReadOnlyList<MemoryFact>)(await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource(query)!)).Hits.Select(h => h.Fact).ToArray());

        /// <summary>Old conversation facts, a day apart, put into the store in one commit (synthetic history).</summary>
        internal Task<int> BulkAsync(int count, string tag) => WithStoreAsync(async store =>
        {
            var start = DateTimeOffset.UtcNow.AddDays(-count - 1);
            var facts = Enumerable.Range(0, count).Select(i =>
            {
                var at = start.AddDays(i).AddMinutes(tag == "a" ? 0 : 30);
                var provenance = MemoryProvenance.Conversation(Guid.NewGuid(), at);
                return MemoryFactJson.Write(new MemoryFact
                {
                    Id = Guid.NewGuid(), Revision = 1, Content = $"Bulk fact {tag}-{i} from an old conversation.", CreatedAtUtc = at, UpdatedAtUtc = at,
                    CreatedFrom = provenance, LastModifiedBy = provenance, Retention = MemoryRetention.UntilDeleted()
                });
            }).ToArray();
            return (await store.MergeAsync(new() { Facts = facts, Forget = [] })).Saved;
        });

        /// <summary>What the desktop's memory sync does: read every reachable host's copy, sync the store, give each the merged copy.</summary>
        internal async Task<MemorySyncResult> SyncAsync(IEnumerable<LabHost> hosts, CancellationToken token)
        {
            var copies = new List<SharedMemories>();
            var reachable = new List<LabHost>();
            foreach (var host in hosts.Where(h => h.Running))
            {
                try
                {
                    copies.Add(await ReadAsync(host, token));
                    reachable.Add(host);
                }
                catch (Exception error) when (error is HttpRequestException or Audio2FaceHostException or IOException or OperationCanceledException) { }
            }
            var result = await WithStoreAsync(store => Node.SyncAsync(copies,
                async read =>
                {
                    var inspection = await store.InspectAsync(read);
                    return new LocalMemories(inspection.StoreId,
                        [.. inspection.Facts.Select(f => new LocalMemory(f.Id, f.Revision, f.UpdatedAtUtc, MemoryFactJson.Write(f)))]);
                },
                (changes, apply) => store.MergeAsync(new() { Facts = changes.Facts, Forget = changes.Forget }, apply),
                Describe, DateTimeOffset.UtcNow, token));
            foreach (var host in reachable) await MergeAsync(host, result.Document, token);
            return result;
        }

        private static MemoryFactInfo? Describe(string json)
        {
            try
            {
                var fact = MemoryFactJson.Read(json);
                return new(fact.Retention.HasExpired(DateTimeOffset.UtcNow), fact.LastModifiedBy.SourceKind != MemorySourceKind.Conversation);
            }
            catch (MemoryException) { return null; }
        }

        internal bool DataFolderContains(string text) => Directory.EnumerateFiles(dataDirectory, "*", SearchOption.TopDirectoryOnly)
            .Any(path => File.ReadAllText(path).Contains(text, StringComparison.Ordinal));
    }

    /// <summary>A real gateway on 127.0.0.1 with an in-memory memories.json and memory spaces that survive a restart.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayMemoryStorage, IGatewayMemorySpaceStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal bool Running => listener is not null;
        private byte[]? saved;
        internal string? SavedText => saved is null ? null : Encoding.UTF8.GetString(saved);

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
            Server.AttachMemoryStorage(this);
            Server.AttachMemorySpaceStorage(this);
            if (Accounts is { } accounts) Server.AttachAccountStorage(new AccountFile(accounts));
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            listener = null;
        }

        /// <summary>The household's account directory every lab host starts with (accounts.json).</summary>
        internal static byte[]? Accounts { get; set; }

        private sealed class AccountFile(byte[] bytes) : IGatewayAccountStorage
        {
            public byte[]? Load() => bytes;
            public void Save(byte[] value) { }
        }

        public byte[]? Load() => saved;
        public void Save(byte[] bytes) => saved = (byte[])bytes.Clone();
        private readonly Dictionary<string, byte[]> savedSpaces = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> List() => savedSpaces.Keys.ToArray();
        public byte[]? Load(string space) => savedSpaces.TryGetValue(space, out var bytes) ? bytes : null;
        public void Save(string space, byte[] bytes) => savedSpaces[space] = (byte[])bytes.Clone();
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
            var request = new CertificateRequest("CN=Martlet memory rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

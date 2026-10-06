using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>While this PC asks to join a network: the host it asked, the network and the check number both screens show.</summary>
public sealed record NetworkJoinWait(string HostId, string NetworkId, string CheckNumber, DateTimeOffset Since);

/// <summary>
/// This desktop's place in the owner's Martlet network, kept in network.json in its data folder: the roster it accepted
/// (null outside a network), the join request it is waiting on, hosts the owner paired here on purpose (<see cref="Adopt"/>:
/// added back to the network even after a removal) and hosts the owner forgot here (<see cref="Ignored"/>: not paired again
/// by the network). Nonsecret.
/// </summary>
public sealed record NetworkLocalState
{
    public const string FileName = "network.json";
    private const int MaximumBytes = NetworkRoster.MaximumBytes + 8_192;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public NetworkRoster? Roster { get; init; }
    public NetworkJoinWait? Waiting { get; init; }
    public IReadOnlyList<string> Adopt { get; init; } = [];
    public IReadOnlyList<string> Ignored { get; init; } = [];
    /// <summary>The network this PC was last removed from (its old key can't rejoin; Martlet makes a new one).</summary>
    public string? RemovedFrom { get; init; }

    public static NetworkLocalState Empty { get; } = new();

    public NetworkLocalState WithAdopted(string hostId) => this with
    {
        Adopt = Adopt.Append(hostId).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
        Ignored = Ignored.Where(id => id != hostId).ToArray()
    };

    public NetworkLocalState WithIgnored(string hostId) => this with
    {
        Ignored = Ignored.Append(hostId).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
        Adopt = Adopt.Where(id => id != hostId).ToArray()
    };

    public byte[] Write()
    {
        var document = new Document
        {
            SchemaVersion = 1, Roster = Roster is null ? null : JsonSerializer.Deserialize<JsonElement>(Roster.Write()),
            Waiting = Waiting, Adopt = Adopt, Ignored = Ignored, RemovedFrom = RemovedFrom
        };
        return JsonSerializer.SerializeToUtf8Bytes(document, Json);
    }

    public static NetworkLocalState Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "network.json is empty or too large.", ErrorCode.PayloadTooLarge);
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "network.json is malformed.");
        }
        ContractRules.Require(document is { SchemaVersion: 1 }, "network.json was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        foreach (var id in (document!.Adopt ?? []).Concat(document.Ignored ?? [])) ContractRules.Identifier(id);
        if (document.Waiting is { } waiting) ContractRules.Identifier(waiting.HostId);
        return new()
        {
            Roster = document.Roster is { ValueKind: JsonValueKind.Object } roster ? NetworkRoster.Parse(JsonSerializer.SerializeToUtf8Bytes(roster)) : null,
            Waiting = document.Waiting, Adopt = document.Adopt ?? [], Ignored = document.Ignored ?? [], RemovedFrom = document.RemovedFrom
        };
    }

    private sealed record Document
    {
        public required int SchemaVersion { get; init; }
        public JsonElement? Roster { get; init; }
        public NetworkJoinWait? Waiting { get; init; }
        public IReadOnlyList<string>? Adopt { get; init; }
        public IReadOnlyList<string>? Ignored { get; init; }
        public string? RemovedFrom { get; init; }
    }
}

/// <summary>What one network sync found and decided. The caller keeps <see cref="Paired"/> (secrets go to the OS vault),
/// forgets <see cref="Forget"/> on this PC, saves <see cref="State"/> and, when <see cref="RetireKey"/> is set, replaces
/// this PC's network key before the next sync.</summary>
public sealed record NetworkSyncResult
{
    public required NetworkLocalState State { get; init; }
    public IReadOnlyList<(Audio2FaceHostPairing Pairing, string Secret)> Paired { get; init; } = [];
    public IReadOnlyList<string> Forget { get; init; } = [];
    public IReadOnlyList<HostJoinRequest> Joins { get; init; } = [];
    public IReadOnlyDictionary<string, HostNetworkView> Views { get; init; } = new Dictionary<string, HostNetworkView>();
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Events { get; init; } = [];
    public bool RetireKey { get; init; }
}

/// <summary>
/// Keeps this desktop in the owner's Martlet network, once per sync:
/// <list type="number">
/// <item>reads every paired host's network;</item>
/// <item>outside a network: becomes a member once a member allowed it, asks a bound host to join, or founds a network
/// when a paired host is in none;</item>
/// <item>as a member: merges every host's copy, adds paired hosts that are in no network (binding them), forgets hosts
/// removed from the network, pairs by itself with every member host it isn't paired with, lists pending join requests
/// and pushes the merged roster to every host whose copy differs.</item>
/// </list>
/// Nothing here shows UI; approvals and removals are explicit calls (<see cref="Approve"/>, <see cref="Remove"/>).
/// </summary>
public sealed class NetworkSyncEngine(INetworkSigner signer, string displayName, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly string name = NetworkRoster.CleanName(displayName, signer.DeviceId);

    public INetworkSigner Signer => signer;

    /// <summary>Reads every paired host's network (its roster, the computers paired with it and, for a member, requests to
    /// join) and changes nothing: no network is started or joined and no key is needed. For a PC that only watches, such
    /// as a host PC outside the network.</summary>
    public static async Task<NetworkSyncResult> ReadOnlyAsync(NetworkLocalState state, IReadOnlyList<Audio2FaceHostPairing> paired,
        Func<Audio2FaceHostPairing, Audio2FaceHostConnection> connect, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(paired);
        ArgumentNullException.ThrowIfNull(connect);
        HostRoutes.Update(state.Roster);
        var reads = await Task.WhenAll(paired.Select(p => ReadAsync(p, connect, token)));
        var views = new Dictionary<string, HostNetworkView>(StringComparer.Ordinal);
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (pairing, view, note, _) in reads)
        {
            if (view is not null) views[pairing.HostId] = view;
            if (note is not null) notes[pairing.HostId] = note;
        }
        return new() { State = state, Views = views, Notes = notes };
    }

    public async Task<NetworkSyncResult> SyncAsync(NetworkLocalState state, IReadOnlyList<Audio2FaceHostPairing> paired,
        Func<Audio2FaceHostPairing, Audio2FaceHostConnection> connect, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(paired);
        ArgumentNullException.ThrowIfNull(connect);
        HostRoutes.Update(state.Roster);
        var events = new List<string>();
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        var reads = await Task.WhenAll(paired.Select(p => ReadAsync(p, connect, token)));
        var views = new Dictionary<string, HostNetworkView>(StringComparer.Ordinal);
        foreach (var (pairing, view, note, isRevoked) in reads)
        {
            if (view is not null) views[pairing.HostId] = view;
            if (note is not null) notes[pairing.HostId] = note;
            if (isRevoked) revoked.Add(pairing.HostId);
        }
        var now = time.GetUtcNow();
        var roster = state.Roster;
        var waiting = state.Waiting;

        // A network only this PC ever wrote to, that no host took, gives way to the network a paired host is already in
        // (two desktops founding at once with the same new host).
        if (roster is not null && roster.Members.All(m => m.UpdatedBy == signer.DeviceId) &&
            roster.Revision > (now - TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds() &&
            !views.Values.Any(v => v.Bound && v.Roster!.NetworkId == roster.NetworkId) &&
            views.Values.Any(v => v.Bound && v.Roster!.NetworkId != roster.NetworkId))
        {
            events.Add("Another of your computers already added a host here to its Martlet network, so this PC asks to join that one instead.");
            roster = null;
        }

        if (roster is null)
        {
            if (views.Values.FirstOrDefault(v => v.Roster is { } r && r.Trusts(signer.DeviceId, signer.PublicKey)) is { } approved)
            {
                roster = NetworkRoster.Bootstrap(approved.Roster!).Roster;
                waiting = null;
                events.Add($"This PC joined your Martlet network (allowed on {roster.Desktop(signer.DeviceId)?.UpdatedBy ?? "another computer"}).");
            }
            else if (views.Values.Where(v => v.Bound).OrderBy(v => v.HostId == waiting?.HostId ? 0 : 1)
                .ThenBy(v => v.HostId, StringComparer.Ordinal).FirstOrDefault() is { } bound)
            {
                if (bound.Roster!.Desktop(signer.DeviceId) is { Removed: true } gone && gone.Key == signer.PublicKey)
                {
                    events.Add($"This PC was removed from that Martlet network, so Martlet makes it a new key to ask again.");
                    return new()
                    {
                        State = state with { Roster = null, Waiting = null, RemovedFrom = bound.Roster.NetworkId },
                        Views = views, Notes = notes, Events = events, RetireKey = true
                    };
                }
                var pairing = paired.First(p => p.HostId == bound.HostId);
                try
                {
                    using var connection = connect(pairing);
                    var (joinState, networkId, check) = await connection.RequestJoinAsync(name, signer.PublicKey, token);
                    if (joinState == "member") roster = NetworkRoster.Bootstrap(bound.Roster).Roster;
                    else
                    {
                        if (waiting is null || waiting.NetworkId != networkId || waiting.CheckNumber != check)
                            events.Add($"Asked to join your Martlet network through {bound.HostId}. Allow {signer.DeviceId} on one of your other computers " +
                                $"(Devices); check that it shows {check}.");
                        waiting = new(bound.HostId, networkId, check, waiting?.NetworkId == networkId ? waiting.Since : now);
                    }
                }
                catch (Exception error) when (IsHostFailure(error, token)) { notes[bound.HostId] = "Couldn't ask to join: " + error.Message; }
            }
            else if (views.Values.Any(v => v.Supported && v.State is "unbound" or "removed"))
            {
                roster = NetworkRoster.Found(signer, name, now);
                waiting = null;
                events.Add("Started your Martlet network with this PC. Hosts you pair here join it, and your other computers can ask to join.");
            }
            if (roster is null)
                return new() { State = state with { Waiting = waiting }, Views = views, Notes = notes, Events = events };
        }

        foreach (var view in views.Values.Where(v => v.Roster?.NetworkId == roster.NetworkId))
            roster = NetworkRoster.Accept(roster, view.Roster!).Roster;
        HostRoutes.Update(roster);
        NetworkSyncResult Leave(NetworkRoster from, string text)
        {
            events.Add(text);
            return new()
            {
                State = NetworkLocalState.Empty with { RemovedFrom = from.NetworkId },
                Forget = paired.Where(p => from.Host(p.HostId) is not null).Select(p => p.HostId).ToArray(),
                Views = views, Notes = notes, Events = events, RetireKey = true
            };
        }
        if (!roster.Trusts(signer.DeviceId, signer.PublicKey))
        {
            var by = roster.Desktop(signer.DeviceId)?.UpdatedBy;
            return Leave(roster, $"This PC was removed from your Martlet network{(by is null ? "" : $" on {by}")}, so it forgot the network's hosts.");
        }

        var adopt = state.Adopt.ToHashSet(StringComparer.Ordinal);
        var ignored = state.Ignored.ToHashSet(StringComparer.Ordinal);
        var forget = new List<string>();
        foreach (var pairing in paired)
        {
            var entry = roster.Host(pairing.HostId);
            // A host removed from the network is forgotten here even when it no longer answers this PC (it revoked the
            // network's desktops), unless the owner paired it here again on purpose.
            if (entry is { Removed: true } && !adopt.Contains(pairing.HostId))
            {
                forget.Add(pairing.HostId);
                events.Add($"{pairing.HostId} was removed from your Martlet network on {entry.UpdatedBy}, so this PC forgot it.");
                continue;
            }
            if (!views.TryGetValue(pairing.HostId, out var view) || !view.Supported) continue;
            if (view.Bound && view.Roster!.NetworkId != roster.NetworkId)
            {
                notes[pairing.HostId] = "It belongs to another Martlet network, so your other computers don't get it. Run martlet-host network-reset on it to bring it into yours.";
                continue;
            }
            if (entry is null or { Removed: true } || entry.Origin != pairing.Origin || entry.Spki != pairing.SpkiFingerprint)
            {
                roster = roster.AddHost(signer, pairing.HostId, pairing.HostId, pairing.Origin, pairing.SpkiFingerprint, now);
                if (entry?.Removed != false)
                    events.Add($"Added {pairing.HostId} to your Martlet network; your other computers pair with it by themselves.");
            }
            // Outside addresses the owner set on the host itself (martlet-host owner-exposure) join its entry when it has none
            // or they are newer than the entry.
            if (view.AdvertisedAddresses is { } advertised && view.AdvertisedAt is { } setAt && roster.Host(pairing.HostId) is { Removed: false } listed &&
                (setAt > listed.ChangedAt || listed.Addresses is null && advertised.Count > 0) && !advertised.SequenceEqual(listed.Addresses ?? []))
            {
                roster = roster.SetHostAddresses(signer, pairing.HostId, advertised, now);
                events.Add(advertised.Count == 0
                    ? $"{pairing.HostId} has no outside addresses any more (set on the host)."
                    : $"{pairing.HostId} can be reached from outside home at {string.Join(", ", advertised)} (set on the host).");
            }
            adopt.Remove(pairing.HostId);
            ignored.Remove(pairing.HostId);
        }

        var added = new List<(Audio2FaceHostPairing Pairing, string Secret)>();
        string? deniedBy = null;
        foreach (var host in roster.ActiveHosts)
        {
            if (ignored.Contains(host.Id) || forget.Contains(host.Id)) continue;
            if (paired.Any(p => p.HostId == host.Id) && !revoked.Contains(host.Id)) continue;
            try
            {
                var pairing = await HostNetworkPairing.PairAsMemberAsync(host, roster.NetworkId, signer, name, token, time);
                added.Add(pairing);
                notes.Remove(host.Id);
                events.Add($"Paired with {host.Id} through your Martlet network.");
            }
            catch (Audio2FaceHostException error) when (error.Code == "network.denied" && revoked.Contains(host.Id))
            {
                // It revoked this PC and no longer counts it as a member: this PC was removed from the network.
                deniedBy = host.Id;
            }
            catch (Exception error) when (IsHostFailure(error, token))
            {
                if (!revoked.Contains(host.Id)) notes[host.Id] = "Not paired yet: " + error.Message;
            }
        }
        if (deniedBy is not null)
            return Leave(roster, $"{deniedBy} revoked this PC and no longer counts it in your Martlet network: it was removed from the network, so it forgot the network's hosts.");

        var digest = roster.Digest();
        foreach (var pairing in paired.Where(p => !revoked.Contains(p.HostId)))
        {
            if (!views.TryGetValue(pairing.HostId, out var view) || !view.Supported || view.Roster?.Digest() == digest) continue;
            if (view.Bound && view.Roster!.NetworkId != roster.NetworkId) continue;
            try
            {
                using var connection = connect(pairing);
                var merged = await connection.MergeNetworkAsync(roster, token);
                views[pairing.HostId] = merged;
                if (merged.Roster?.NetworkId == roster.NetworkId) roster = NetworkRoster.Accept(roster, merged.Roster).Roster;
            }
            catch (Exception error) when (IsHostFailure(error, token)) { notes[pairing.HostId] = "Couldn't share the network: " + error.Message; }
        }
        foreach (var (pairing, secret) in added)
        {
            try
            {
                using var connection = new Audio2FaceHostConnection(pairing, secret);
                var merged = await connection.MergeNetworkAsync(roster, token);
                views[pairing.HostId] = merged;
                if (merged.Roster?.NetworkId == roster.NetworkId) roster = NetworkRoster.Accept(roster, merged.Roster).Roster;
            }
            catch (Exception error) when (IsHostFailure(error, token)) { }
        }

        var joins = views.Values.Where(v => v.Roster?.NetworkId == roster.NetworkId).SelectMany(v => v.Joins)
            .Where(j => !roster.Trusts(j.DeviceId, j.Key) && !(roster.Desktop(j.DeviceId) is { Removed: true } r && r.Key == j.Key))
            .GroupBy(j => j.DeviceId, StringComparer.Ordinal).Select(g => g.OrderByDescending(j => j.RequestedAt).First())
            .OrderBy(j => j.RequestedAt).ToArray();
        HostRoutes.Update(roster);
        return new()
        {
            State = state with
            {
                Roster = roster, Waiting = null, RemovedFrom = null,
                Adopt = adopt.Where(id => paired.Any(p => p.HostId == id)).ToArray(),
                Ignored = ignored.Where(id => roster.Host(id) is { Removed: false }).ToArray()
            },
            Paired = added, Forget = forget, Joins = joins, Views = views, Notes = notes, Events = events
        };
    }

    /// <summary>Lets <paramref name="join"/>'s desktop into the network (signed by this PC); the next sync shares it.</summary>
    public NetworkLocalState Approve(NetworkLocalState state, HostJoinRequest join)
    {
        var roster = state.Roster ?? throw new InvalidOperationException("This PC is not in a Martlet network.");
        return state with { Roster = roster.AddDesktop(signer, join.DeviceId, join.DisplayName, join.Key, time.GetUtcNow()) };
    }

    /// <summary>Lets in every desktop that asks to join through one of <paramref name="ownHosts"/>, this PC's own host
    /// services. Every pairing with such a host was approved on this PC (its pairing code, or its Allow in Add a computer, shows
    /// only here), so for a member PC that approval already covers the network and a second Allow would only repeat it. Returns
    /// the new state and the requests it let in; nothing changes outside a network.</summary>
    public (NetworkLocalState State, IReadOnlyList<HostJoinRequest> Approved) ApproveThrough(NetworkLocalState state,
        IReadOnlyList<HostJoinRequest> joins, IReadOnlyCollection<string> ownHosts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(joins);
        ArgumentNullException.ThrowIfNull(ownHosts);
        if (state.Roster is null || ownHosts.Count == 0) return (state, []);
        var approved = joins.Where(j => ownHosts.Contains(j.HostId, StringComparer.Ordinal) && j.DeviceId != signer.DeviceId)
            .DistinctBy(j => j.DeviceId, StringComparer.Ordinal).ToArray();
        foreach (var join in approved) state = Approve(state, join);
        return (state, approved);
    }

    /// <summary>Lets in every desktop whose join request a member host of the network attests was paired by signing in (the
    /// owner account, or an identity the owner allowed on that host). The owner set that sign-in up at home, so the sign-in is
    /// the owner's approval and a check number would only repeat it. Requests through a host that is not an active member of
    /// this PC's roster are left for an Allow. Returns the new state and the requests it let in.</summary>
    public (NetworkLocalState State, IReadOnlyList<HostJoinRequest> Approved) ApproveSignedIn(NetworkLocalState state,
        IReadOnlyList<HostJoinRequest> joins)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(joins);
        if (state.Roster is not { } roster) return (state, []);
        var approved = joins.Where(j => j.SignIn is not null && j.DeviceId != signer.DeviceId && roster.Host(j.HostId) is { Removed: false } &&
                !(roster.Desktop(j.DeviceId) is { Removed: true } removed && removed.Key == j.Key))
            .DistinctBy(j => j.DeviceId, StringComparer.Ordinal).ToArray();
        foreach (var join in approved) state = Approve(state, join);
        return (state, approved);
    }

    /// <summary>Removes a desktop or host from the network (signed by this PC); the next sync shares it, hosts revoke the
    /// removed desktop and a removed host stops trusting the network's desktops.</summary>
    public NetworkLocalState Remove(NetworkLocalState state, string kind, string id)
    {
        var roster = state.Roster ?? throw new InvalidOperationException("This PC is not in a Martlet network.");
        if (kind == NetworkKinds.Desktop && id == signer.DeviceId)
            throw new InvalidOperationException("Remove another computer; this PC can't remove itself.");
        var next = state with { Roster = roster.Remove(signer, kind, id, time.GetUtcNow()) };
        return kind == NetworkKinds.Host ? next with { Adopt = next.Adopt.Where(h => h != id).ToArray() } : next;
    }

    /// <summary>Sets a host's outside addresses (empty removes them), signed by this PC; the next sync shares them.</summary>
    public NetworkLocalState SetHostAddresses(NetworkLocalState state, string hostId, IEnumerable<string> addresses)
    {
        var roster = state.Roster ?? throw new InvalidOperationException("This PC is not in a Martlet network.");
        return state with { Roster = roster.SetHostAddresses(signer, hostId, addresses, time.GetUtcNow()) };
    }

    private static async Task<(Audio2FaceHostPairing Pairing, HostNetworkView? View, string? Note, bool Revoked)> ReadAsync(
        Audio2FaceHostPairing pairing, Func<Audio2FaceHostPairing, Audio2FaceHostConnection> connect, CancellationToken token)
    {
        try
        {
            using var connection = connect(pairing);
            return (pairing, await connection.ReadNetworkAsync(token), null, false);
        }
        catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
        {
            return (pairing, HostNetworkView.Unsupported(pairing.HostId),
                "It runs an older Martlet, so it can't share your network. Update it (Update host).", false);
        }
        catch (Audio2FaceHostException error) when (error.Code is "auth.revoked" or "auth.invalid")
        {
            return (pairing, null, "It no longer accepts this PC's pairing.", true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (pairing, null, "Didn't answer in time.", false); }
        catch (Exception error) when (IsHostFailure(error, token)) { return (pairing, null, error.Message, false); }
    }

    /// <summary>A failure of one host (unreachable, refused, timed out, invalid answer) rather than this sync being canceled.</summary>
    private static bool IsHostFailure(Exception error, CancellationToken token) => error is OperationCanceledException
        ? !token.IsCancellationRequested
        : error is Audio2FaceHostException or IOException or ContractException or InvalidOperationException or ArgumentException or
            JsonException or TimeoutException or HttpRequestException or UnauthorizedAccessException;
}

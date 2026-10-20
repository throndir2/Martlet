using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.HostClient;
using Martlet.Core.Network;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>A share link this PC made (docs/NETWORK.md, "Sharing with a link"): its ID, the friend's name, the hosts it covers
/// (null: all of your hosts, also those you add later) and when it was made. The secret stays in Windows Credential Manager.</summary>
internal sealed record MadeShareLink
{
    public required string Id { get; init; }
    public string? Label { get; init; }
    /// <summary>Your name as the friend sees it in the link.</summary>
    public string? From { get; init; }
    public List<string>? Hosts { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    [JsonIgnore] public bool AllHosts => Hosts is null;
    [JsonIgnore] public string Name => Label ?? "a friend";

    internal bool Covers(string hostId) => Hosts is null || Hosts.Contains(hostId, StringComparer.Ordinal);
}

/// <summary>A share link a friend sent and this PC accepted: who sent it, the name it carries, the owner's network and the hosts
/// this PC signed in to with it. The secret stays in Windows Credential Manager, so Martlet can sign in to hosts the link
/// covers later.</summary>
internal sealed record AcceptedShareLink
{
    public required string Id { get; init; }
    public string? From { get; init; }
    public string? Label { get; init; }
    public string? NetworkId { get; init; }
    public List<string> HostIds { get; init; } = [];
    /// <summary>Hosts of the link you forgot on this PC: Martlet doesn't sign in to them again.</summary>
    public List<string> Skipped { get; init; } = [];
    public DateTimeOffset AcceptedAt { get; init; }
    [JsonIgnore] public string Sender => From ?? "a friend";
}

/// <summary>share-links.json: the share links this PC made and the ones it accepted (never a secret), and the IDs of links that
/// were stopped (each host is told to remove them until none keeps them; they are never added again).</summary>
internal sealed record ShareLinksDocument
{
    public int Version { get; init; } = 1;
    public List<MadeShareLink> Made { get; init; } = [];
    public List<AcceptedShareLink> Accepted { get; init; } = [];
    public List<string> Stopped { get; init; } = [];
}

/// <summary>One change the owner's PC sends a host so it keeps the share links it should (<see cref="ShareLinks.Plan"/>).</summary>
internal sealed record ShareLinkChange(string HostId, MadeShareLink Link, bool Add);

/// <summary>
/// Share links (docs/NETWORK.md, "Sharing with a link"): one link per friend gives their Martlet some or all of your hosts'
/// engines. This PC keeps the links it made and accepted in share-links.json and their secrets in Windows Credential Manager.
/// Hosts keep only a link's verifier.
/// </summary>
internal static class ShareLinks
{
    internal const string FileName = "share-links.json";
    internal const string Provider = "link";
    internal const int MaximumMade = 32;
    internal const int MaximumAccepted = 32;
    internal const int MaximumStopped = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static ShareLinksDocument Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        try
        {
            if (!File.Exists(path)) return new();
            var document = JsonSerializer.Deserialize<ShareLinksDocument>(File.ReadAllBytes(path), Json) ?? new();
            return document with
            {
                Made = (document.Made ?? []).Where(l => ShareLink.IsToken(l.Id, 16)).Take(MaximumMade).ToList(),
                Accepted = (document.Accepted ?? []).Where(l => ShareLink.IsToken(l.Id, 16)).Take(MaximumAccepted).ToList(),
                Stopped = (document.Stopped ?? []).Where(id => ShareLink.IsToken(id, 16)).TakeLast(MaximumStopped).ToList()
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Share links: share-links.json is unreadable; Martlet starts with no share links. " + error.Message);
            return new();
        }
    }

    internal static void Save(string dataDirectory, ShareLinksDocument document)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = Path.Combine(dataDirectory, $"share-links.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void KeepSecret(string linkId, string secret)
    {
        var stored = new WindowsCredentialStore().WriteShareLinkSecret(linkId, secret);
        if (stored != Martlet.Core.Settings.CredentialError.None)
            throw new InvalidOperationException("Couldn't keep the share link's secret in Windows Credential Manager: " +
                Martlet.Core.Settings.CredentialMessages.Describe(stored));
    }

    internal static string? Secret(string linkId) =>
        new WindowsCredentialStore().ReadShareLinkSecret(linkId, out var secret) == Martlet.Core.Settings.CredentialError.None ? secret : null;

    internal static void ForgetSecret(string linkId) => new WindowsCredentialStore().DeleteShareLinkSecret(linkId);

    /// <summary>The settings change that keeps <paramref name="link"/> on a host (its verifier, never the secret).</summary>
    internal static System.Text.Json.Nodes.JsonObject AddChange(MadeShareLink link, string secret)
    {
        var change = new System.Text.Json.Nodes.JsonObject
        {
            ["action"] = "share-link", ["id"] = link.Id, ["label"] = link.Label, ["verifier"] = ShareLink.Verifier(link.Id, secret)
        };
        if (link.Hosts is { } hosts) change["hosts"] = new System.Text.Json.Nodes.JsonArray(hosts.Select(h => (System.Text.Json.Nodes.JsonNode?)h).ToArray());
        return change;
    }

    internal static System.Text.Json.Nodes.JsonObject RemoveChange(string linkId) => new() { ["action"] = "remove-share-link", ["id"] = linkId };

    /// <summary>What the owner's PC sends its hosts so each keeps exactly the links this PC made for it: a link that covers a
    /// host (all hosts, or that host named) is added where it is missing or its host list differs; a link this PC made that no
    /// longer covers a host is removed there, and so is a stopped link (<paramref name="stopped"/>) wherever a host still keeps
    /// it. A stopped link is never added again. Links made on another of your computers are left alone. Hosts older than share
    /// links (<see cref="HostSignInSettings.SupportsLinks"/> false) get nothing.</summary>
    internal static IReadOnlyList<ShareLinkChange> Plan(IReadOnlyList<MadeShareLink> made, IReadOnlyDictionary<string, HostSignInSettings> read,
        IReadOnlyCollection<string>? stopped = null)
    {
        var changes = new List<ShareLinkChange>();
        stopped ??= [];
        foreach (var (hostId, settings) in read.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (settings.Links is not { } kept) continue;
            foreach (var link in made.Where(l => !stopped.Contains(l.Id)))
            {
                var there = kept.FirstOrDefault(k => k.Id == link.Id);
                if (link.Covers(hostId))
                {
                    var same = there is not null && (there.Hosts is null ? link.Hosts is null
                        : link.Hosts is not null && there.Hosts.Order(StringComparer.Ordinal).SequenceEqual(link.Hosts.Order(StringComparer.Ordinal)));
                    if (!same || there!.Label != link.Label) changes.Add(new(hostId, link, true));
                }
                else if (there is not null) changes.Add(new(hostId, link, false));
            }
            foreach (var id in stopped.Where(id => kept.Any(k => k.Id == id)))
                changes.Add(new(hostId, new MadeShareLink { Id = id, Label = kept.First(k => k.Id == id).Label }, false));
        }
        return changes;
    }

    /// <summary>The links this PC made that a host says were stopped (on another of your computers, or here earlier): they
    /// become stopped here too, so this PC never adds them again.</summary>
    internal static IReadOnlyList<MadeShareLink> StoppedElsewhere(IReadOnlyList<MadeShareLink> made, IReadOnlyDictionary<string, HostSignInSettings> read) =>
        made.Where(l => read.Values.Any(s => s.RemovedLinks.Contains(l.Id, StringComparer.Ordinal))).ToArray();

    /// <summary>A host's invite as a share link carries it: its pin, home address and outside addresses (from the network
    /// roster, else what the host advertised, else what this PC kept with the pairing).</summary>
    internal static NetworkInvite Invite(PairedHost host, NetworkRoster? roster, IReadOnlyList<string>? advertised)
    {
        var member = roster?.Host(host.HostId) is { Removed: false } entry ? entry : null;
        var addresses = (member?.Addresses is { Count: > 0 } listed ? listed
                : advertised is { Count: > 0 } said ? advertised
                : host.OutsideAddresses ?? [])
            .Where(NetworkInvite.IsAddress).Take(NetworkInvite.MaximumAddresses).ToArray();
        return new()
        {
            HostId = host.HostId, SpkiFingerprint = host.Pairing.SpkiFingerprint, Origin = host.Pairing.Origin, Addresses = addresses,
            NetworkId = member is not null ? roster!.NetworkId : null, Label = ShareLink.Clean(member?.Name)
        };
    }

    /// <summary>The hosts in a link that someone outside your home can reach: those with an outside address.</summary>
    internal static IReadOnlyList<string> Unreachable(ShareLink link) =>
        link.Hosts.Where(h => h.Addresses.Count == 0).Select(h => h.HostId).ToArray();
}

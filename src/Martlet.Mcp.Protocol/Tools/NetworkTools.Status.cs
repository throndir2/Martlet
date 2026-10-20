using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>Shared "who does what" as the desktop keeps it in a data directory (the file names match Martlet.Desktop's
    /// ClusterSync): the sync choice, on unless cluster-sync.txt says "off", and cluster.json without host addresses.</summary>
    private static object ClusterStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "cluster-sync.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        object plan;
        var path = Path.Combine(directory, "cluster.json");
        if (!File.Exists(path)) plan = new { state = "none" };
        else
        {
            try
            {
                var copy = Martlet.Core.Cluster.ClusterPlan.Parse(File.ReadAllBytes(path));
                plan = new
                {
                    state = "loaded", revision = copy.Revision,
                    jobs = copy.Assignments.Select(a => new
                    {
                        job = a.Job, host = a.HostId, off = a.Off, failover = a.Failover, movedFrom = a.MovedFrom,
                        updatedBy = a.UpdatedBy, updatedAt = a.UpdatedAt
                    }).ToArray(),
                    hosts = copy.Nodes.Select(n => new { hostId = n.HostId, removed = n.Removed, roles = n.Roles.Select(r => r.Kind).ToArray() }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                plan = new { state = "unreadable" };
            }
        }
        return new { sync = choice switch { "off" => "off", null => "on (default)", _ => "on" }, plan };
    }

    /// <summary>The Martlet network as the desktop keeps it in a data directory (network.json and network\device_ecdsa, the
    /// names Martlet.Desktop's NetworkIdentity uses), this data folder's device ID (device.json) and the Windows login's kind.
    /// No keys, signatures, addresses, e-mail or names of the Windows login are returned.</summary>
    private static object NetworkStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var key = File.Exists(Path.Combine(directory, "network", "device_ecdsa"));
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        var pairedHosts = PairedHostsSummary(directory);
        var friends = FriendsSummary(directory);
        var shareLinks = ShareLinksSummary(directory);
        var routerPort = RouterPortSummary(directory);
        var householdSignIn = HouseholdSignInSummary(directory);
        var device = DeviceSummary(directory);
        var login = Martlet.Mcp.Shared.WindowsLogin.Current;
        var windowsLogin = new { kind = login.Kind, hasEmailHint = login.HasEmailHint, hasSid = login.Sid.Length > 0 };
        if (!File.Exists(path)) return new { state = "none", key, device, windowsLogin, pairedHosts, friends, shareLinks, routerPort, householdSignIn };
        Martlet.Avatar.Audio2Face.Remote.NetworkLocalState local;
        try { local = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", key, device, windowsLogin };
        }
        var roster = local.Roster;
        return new
        {
            state = roster is not null ? "member" : local.Waiting is not null ? "waiting" : "none",
            key,
            device,
            windowsLogin,
            networkId = roster?.NetworkId ?? local.Waiting?.NetworkId,
            revision = roster?.Revision,
            founder = roster?.Founder?.Id,
            waiting = local.Waiting is { } wait ? new { hostId = wait.HostId, checkNumber = wait.CheckNumber, since = wait.Since } : null,
            desktops = roster?.Members.Where(m => m.IsDesktop).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt }).ToArray(),
            hosts = roster?.Members.Where(m => m.IsHost).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt, outsideAddresses = m.Addresses?.Count ?? 0 }).ToArray(),
            adopt = local.Adopt,
            ignored = local.Ignored,
            removedFrom = local.RemovedFrom,
            pairedHosts,
            friends,
            shareLinks,
            routerPort,
            householdSignIn
        };
    }

    /// <summary>share-links.json in short (docs/NETWORK.md, "Sharing with a link"): the share links this PC made (the friend's
    /// name, whether it covers all hosts or which, when it was made) and the ones it accepted (who sent it, the hosts it signed in
    /// to and those you forgot), whether martlet:// links open Martlet for this Windows user, and whether another start left a
    /// clicked link for the running Martlet. Never a link ID or secret.</summary>
    private static object ShareLinksSummary(string directory)
    {
        string? protocol = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\martlet\shell\open\command");
                protocol = key?.GetValue(null) as string;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var pending = File.Exists(Path.Combine(directory, "pending-share-link.txt"))
            || (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "pending-share-link-*.link").Any());
        try
        {
            var path = Path.Combine(directory, "share-links.json");
            if (!File.Exists(path)) return new { made = Array.Empty<object>(), accepted = Array.Empty<object>(), protocolRegistered = protocol is not null, pendingHandOver = pending };
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static string[]? List(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null;
            return new
            {
                made = root.TryGetProperty("made", out var made) && made.ValueKind == JsonValueKind.Array
                    ? made.EnumerateArray().Select(l => (object)new { label = Text(l, "label"), allHosts = List(l, "hosts") is null, hosts = List(l, "hosts"), createdAt = Text(l, "created_at") }).ToArray()
                    : [],
                accepted = root.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.Array
                    ? accepted.EnumerateArray().Select(l => (object)new
                    {
                        from = Text(l, "from"), label = Text(l, "label"), hosts = List(l, "host_ids") ?? [], forgotten = List(l, "skipped") ?? [],
                        acceptedAt = Text(l, "accepted_at")
                    }).ToArray()
                    : [],
                protocolRegistered = protocol is not null,
                pendingHandOver = pending
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>router-port.json in short (docs/NETWORK.md, "Opening a port on your router"): the port Martlet opened on the router
    /// for this PC's host service (which host, the router's name, the external and internal port, when it was opened and last
    /// renewed, the last problem and whether this PC reached it from inside). Never an address. Null when none is open.</summary>
    private static object? RouterPortSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "router-port.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
            return new
            {
                hostId = Text(root, "host_id"), router = Text(root, "router_name"), externalPort = Number(root, "external_port"),
                internalPort = Number(root, "internal_port"), openedAt = Text(root, "opened_at"), renewedAt = Text(root, "renewed_at"),
                problem = Text(root, "problem"),
                answeredFromInside = root.TryGetProperty("answered_from_inside", out var answered) && answered.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? answered.GetBoolean() : (bool?)null
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>household-signin.json: the household's sign-in providers as the desktop last read them from its hosts (each
    /// provider's ID, kind, name, the hosts that have it, miss it, have other settings or lack its client secret, and whether that
    /// desktop keeps its secret to add it to new hosts) and the providers that desktop set up; never a secret. Null when the
    /// desktop hasn't read it.</summary>
    private static object? HouseholdSignInSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "household-signin.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>This data folder's device ID, read-only (Martlet.Core's DeviceIds): saved in device.json ("saved"), or the one the
    /// desktop will keep from its pairings ("pairings") or an older Martlet's desktop-&lt;pc name&gt; ("legacy"); null with "new"
    /// when the desktop picks a new desktop-&lt;pc name&gt;-&lt;6 of [a-z0-9]&gt; one on its next start.</summary>
    private static object DeviceSummary(string directory) => Martlet.Core.Network.DeviceIds.Peek(directory) is { } choice
        ? new { id = (string?)choice.Id, source = choice.Source.ToString().ToLowerInvariant(), computerName = Martlet.Core.Network.ThisComputer.Name }
        : new { id = (string?)null, source = "new", computerName = Martlet.Core.Network.ThisComputer.Name };

    /// <summary>hosts.json in short: each paired host's ID, the name its computer goes by (computerName: the name the host reported
    /// in host-hardware.json, computerNameFrom "report", else the one its host ID was made from, "host-id"), how many outside
    /// addresses are kept with its pairing and its access ("friend" for a host a friend shares with this PC: its engines only,
    /// never in this PC's network; "member" for your own).</summary>
    private static object? PairedHostsSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "hosts.json");
            if (!File.Exists(path)) return Array.Empty<object>();
            var hardware = new Martlet.Core.Installation.HostHardwareStore(directory).Load();
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.GetProperty("hosts").EnumerateArray().Select(h =>
            {
                var hostId = h.GetProperty("pairing").GetProperty("hostId").GetString() ?? "";
                var reported = hardware.FirstOrDefault(r => r.HostId == hostId)?.ComputerName;
                return new
                {
                    hostId,
                    computerName = Martlet.Core.Network.HostIds.ComputerName(hostId, reported),
                    computerNameFrom = reported is null ? "host-id" : "report",
                    outsideAddresses = h.TryGetProperty("outsideAddresses", out var outside) && outside.ValueKind == JsonValueKind.Array
                        ? outside.GetArrayLength() : 0,
                    access = IsSharedHost(h) ? "friend" : "member",
                    // Found at another home address than its paired one (its computer got a new address); the address isn't returned.
                    movedOnHomeNetwork = h.TryGetProperty("movedTo", out var moved) && moved.ValueKind == JsonValueKind.String
                };
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "unreadable";
        }
    }

    /// <summary>friends.json: what Share › Friends last read from each of your hosts (people's labels, providers, how many of
    /// their computers signed in, how many asked); null when the desktop hasn't read it.</summary>
    private static object? FriendsSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "friends.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }

    /// <summary>Probes each roster host's home and outside addresses (pinned TLS, GET /health/live) when contactHosts is true.</summary>
    private static async Task<object> OutsideReachabilityAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var contact = OptionalBool(arguments, "contactHosts") == true;
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        Martlet.Core.Network.NetworkRoster? roster = null;
        try { if (File.Exists(path)) roster = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)).Roster; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable" };
        }
        if (roster is null) return new { state = "none", hosts = Array.Empty<object>() };
        var hosts = new List<object>();
        foreach (var host in roster.ActiveHosts.Where(h => h.Origin is not null && h.Spki is not null))
        {
            var outside = host.Addresses ?? [];
            if (!contact)
            {
                hosts.Add(new { id = host.Id, outsideAddresses = outside.Count, checkedNow = false });
                continue;
            }
            var timeout = TimeSpan.FromSeconds(4);
            var probes = await Task.WhenAll(new string?[] { null }.Concat(outside).Select(address =>
                Martlet.Avatar.Audio2Face.Remote.HostRoutes.ProbeAsync(host.Origin!, host.Spki!, address, timeout, cancellation)));
            var home = probes[0];
            var outsideResults = probes.Skip(1).Select((p, i) => new { address = i + 1, reachable = p.Reachable, ms = p.Milliseconds, problem = p.Problem }).ToArray();
            var use = home.Reachable ? "home" : outsideResults.FirstOrDefault(o => o.reachable) is { } first ? $"outside {first.address}" : "none";
            hosts.Add(new
            {
                id = host.Id, outsideAddresses = outside.Count, checkedNow = true,
                home = new { reachable = home.Reachable, ms = home.Milliseconds, problem = home.Problem },
                outside = outsideResults, wouldUse = use
            });
        }
        return new { state = "member", contacted = contact, hosts };
    }

    /// <summary>"Let my other computers find this PC" as the desktop keeps it (the file names match Martlet.Desktop's Nearby and
    /// HostRegistry): the choice, on unless nearby.txt says "off", and the paired hosts this PC could share (those it runs, saved
    /// as ThisPcDocker, or reaches over SSH). Host IDs and how each is reached only: no addresses, SSH targets or keys.</summary>
    private static object NearbyStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "nearby.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        object hosts;
        var path = Path.Combine(directory, "hosts.json");
        if (!File.Exists(path)) hosts = new { state = "none", paired = 0, shareable = Array.Empty<object>() };
        else
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var list = document.RootElement.GetProperty("hosts").EnumerateArray().Select(host =>
                {
                    var method = host.TryGetProperty("method", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "OnHost";
                    var ssh = host.TryGetProperty("sshTarget", out var target) && target.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(target.GetString());
                    var id = host.GetProperty("pairing").GetProperty("hostId").GetString();
                    var shareable = method == "ThisPcDocker" || method is "SshDocker" or "SshNative" && ssh;
                    return (id, method, shareable);
                }).ToArray();
                hosts = new
                {
                    state = "loaded", paired = list.Length,
                    shareable = list.Where(h => h.shareable).Select(h => new { hostId = h.id, reach = h.method }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                InvalidOperationException)
            {
                hosts = new { state = "unreadable" };
            }
        }
        // How Add a computer looks (Martlet.Desktop's Nearby): it lists each computer as it answers, for this long, and a
        // computer offers at most as many hosts as Martlet can remember.
        return new
        {
            share = choice switch { "off" => "off", null => "on (default)", _ => "on" }, port = 9444, hosts,
            find = new { listensSeconds = 15, queriesSentAtSeconds = new[] { 0, 0.5, 1.5, 3.5, 7.5 }, hostsOfferedAtMost = 64 }
        };
    }

    /// <summary>hosts_find: one host find on this PC's home network (<see cref="Martlet.Core.Network.HostFind"/>), with each answer
    /// compared to the pairings in the given data folder's hosts.json (paired origin and where it moved to). No keys or secrets.</summary>
    private static async Task<object> HostsFindAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var seconds = arguments.TryGetProperty("seconds", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var s)
            ? Math.Clamp(s, 0.5, 5) : 2;
        var paired = new Dictionary<string, (string Address, string? MovedTo)>(StringComparer.Ordinal);
        string? pairings = null;
        if (arguments.TryGetProperty("dataDirectory", out var given) && given.ValueKind == JsonValueKind.String)
        {
            pairings = "none";
            try
            {
                var path = Path.Combine(DataDirectory(arguments), "hosts.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                    foreach (var host in document.RootElement.GetProperty("hosts").EnumerateArray())
                    {
                        var pairing = host.GetProperty("pairing");
                        var origin = new Uri(pairing.GetProperty("origin").GetString()!);
                        var moved = host.TryGetProperty("movedTo", out var to) && to.ValueKind == JsonValueKind.String ? to.GetString() : null;
                        paired[pairing.GetProperty("hostId").GetString()!] = (origin.Host, moved?[..moved.LastIndexOf(':')]);
                    }
                    pairings = "loaded";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                InvalidOperationException or UriFormatException or ArgumentException)
            {
                pairings = "unreadable";
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var seen = await Martlet.Core.Network.HostFind.FindAsync(null, TimeSpan.FromSeconds(seconds), cancellation);
        var answers = seen.Select(sighting =>
        {
            var address = sighting.Address.ToString();
            var where = !paired.TryGetValue(sighting.HostId, out var host) ? (pairings is null ? null : "not paired here")
                : address == host.Address ? "at its paired address"
                : address == host.MovedTo ? "at the address saved as where it moved to"
                : System.Net.IPAddress.IsLoopback(sighting.Address) ? "on this PC (answered over loopback)"
                : "somewhere new (Martlet checks its pinned key there before it uses it)";
            return new { hostId = sighting.HostId, address, where };
        }).ToArray();
        return new
        {
            port = Martlet.Core.Network.HostFind.Port, listenedSeconds = seconds, tookMs = clock.ElapsedMilliseconds,
            targets = Martlet.Core.Network.HostFind.BroadcastTargets().Select(t => t.ToString()).ToArray(),
            pairings, answers
        };
    }
}

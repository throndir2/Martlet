using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>A paired Martlet host and how this desktop reaches it to change its roles. Nonsecret: the device secret stays in
/// Windows Credential Manager.</summary>
internal sealed record PairedHost
{
    public required AvatarRemoteHost Pairing { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<HostSetupMethod>))]
    public HostSetupMethod Method { get; init; } = HostSetupMethod.Agent;
    /// <summary>user@computer for the SSH methods.</summary>
    public string? SshTarget { get; init; }
    /// <summary>The SSH host key Martlet pinned for it ("ssh-ed25519 SHA256:..."); runs refuse a different key.</summary>
    public string? SshHostKey { get; init; }
    /// <summary>The MAC address it reported for Wake-on-LAN (Prepare this computer), so Martlet can wake it.</summary>
    public string? WakeMac { get; init; }

    [JsonIgnore] public string HostId => Pairing.HostId;
    [JsonIgnore] public string Address => new Uri(Pairing.Origin).Host;

    internal HostSetupTarget Target(string version) => new(Method, SshTarget ?? "", Address, HostId, version);

    internal bool CanLaunch => Method switch
    {
        HostSetupMethod.ThisPcDocker or HostSetupMethod.Agent => true,
        HostSetupMethod.SshDocker or HostSetupMethod.SshNative => !string.IsNullOrWhiteSpace(SshTarget),
        _ => false
    };

    internal string Reach => Method switch
    {
        HostSetupMethod.ThisPcDocker => "This PC with Docker Desktop",
        HostSetupMethod.SshDocker => $"SSH to {SshTarget ?? "(not set)"} (Docker)",
        HostSetupMethod.SshNative => $"SSH to {SshTarget ?? "(not set)"} (Ubuntu)",
        _ => "Martlet on that computer (paired connection)"
    };
}

/// <summary>A role a Martlet host can run for this desktop, installed with the same martlet-host flow as every role.
/// <paramref name="RouteId"/> is the gateway route the host advertises once the role runs.</summary>
internal sealed record HostRoleInfo(string Kind, string Chip, string Name, string Needs, string RouteId, string Job, string Description)
{
    internal HostAction Add => HostAction.Add(Kind);
    internal HostAction Remove => HostAction.Remove(Kind);
}

/// <summary>The role catalog. A new role needs its deploy/host/roles files, a gateway relay worker and one entry here.</summary>
internal static class HostRoles
{
    internal const string Audio2Face = "audio2face";
    internal const string Ollama = "ollama";
    internal const string Stt = "stt";
    internal const string F5 = "f5";
    internal const string Xtts = "xtts";
    internal const string Chatterbox = "chatterbox";
    internal const string GptSovits = "gpt-sovits";
    internal const string Dia = "dia";
    internal const string Singing = "singing";

    /// <summary>The host role of the voice engine chosen for Speaking (<see cref="SpeakingEngineChoice"/>).</summary>
    internal static string Speaking => SpeakingEngineChoice.Current.HostRoleKind;

    /// <summary>Whether <paramref name="kind"/> is a voice engine's role (any of <see cref="SpeechEngines"/>).</summary>
    internal static bool Speaks(string kind) => SpeechEngines.ForRoleKind(kind) is not null;

    /// <summary>The voice engines a host offers (<paramref name="offers"/>, from <see cref="HostControl.CheckAsync"/>) other
    /// than <paramref name="keep"/>. Each keeps its model in the graphics card's memory, so a host runs one at a time
    /// (role.conf exclusive=voice) and switching engines there stops the others.</summary>
    internal static IReadOnlyList<HostRoleInfo> OtherVoiceEngines(IReadOnlyDictionary<string, string>? offers, string keep) =>
        offers is null ? [] : All.Where(r => r.Kind != keep && Speaks(r.Kind) && offers.ContainsKey(r.Kind)).ToList();

    /// <summary>Roles in words, a voice engine by its engine's name ("F5-TTS and XTTS-v2"); an unknown kind keeps its own name.</summary>
    internal static string Names(IEnumerable<string> kinds)
    {
        var names = kinds.Select(kind => SpeechEngines.ForRoleKind(kind)?.Name ?? All.FirstOrDefault(r => r.Kind == kind)?.Name ?? kind).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    internal static readonly IReadOnlyList<HostRoleInfo> All =
    [
        new(Audio2Face, "Lip-sync", "Lip-sync", "an NVIDIA GPU (RTX 20 series or newer) with at least 4 GB (no NVIDIA account or key)",
            Audio2FaceHostClient.RouteId, "lip-sync",
            "Moves the character's face with Martlet's voice. Generated voice audio goes to that host."),
        new(Ollama, "Thinks", "Thinking", "Docker; an NVIDIA GPU is recommended",
            HostRoute.OllamaChatRouteId, "thinking",
            "Runs the conversation model on that host. Your messages and recent conversation go there."),
        new(Stt, "Listens", "Listening", "Docker; an NVIDIA GPU is recommended",
            Audio2FaceHostConnection.TranscriptionRouteId, "listening",
            "Turns speech into text on that host. Your recorded speech goes there and is not stored."),
        new(Chatterbox, "Speaks", "Speaking (Chatterbox Turbo)", "an NVIDIA GPU with at least 6 GB",
            SpeechEngines.Chatterbox.RouteId, "speaking",
            "Speaks replies on that host with Chatterbox Turbo, which can laugh, sigh and change tone. Reply text and the " +
            "selected voice sample (longer than 5 seconds) go there. MIT-licensed model; replies carry an inaudible watermark."),
        new(F5, "Speaks", "Speaking (F5-TTS)", "an NVIDIA GPU with at least 6 GB",
            HostRoute.F5RouteId, "speaking",
            "Speaks replies on that host with F5-TTS. Reply text and the selected voice sample go there."),
        new(Xtts, "Speaks", "Speaking (XTTS-v2)", "an NVIDIA GPU with at least 4 GB",
            HostRoute.XttsRouteId, "speaking",
            "Speaks replies on that host with XTTS-v2, which starts speaking before a sentence is finished. Reply text and the " +
            "selected voice sample go there. Its model allows noncommercial use only."),
        new(GptSovits, "Speaks", "Speaking (GPT-SoVITS)", "an NVIDIA GPU with at least 4 GB",
            HostRoute.GptSovitsRouteId, "speaking",
            "Speaks replies on that host with GPT-SoVITS, good for anime-style voices; it starts each sentence as soon as it is " +
            "generated and needs a 3-10 second             voice sample. Reply text and the selected voice sample go there."),
        new(Dia, "Speaks", "Speaking (Dia)", "an NVIDIA GPU with at least 8 GB",
            HostRoute.DiaRouteId, "speaking",
            "Speaks replies on that host with Dia, which can laugh, sigh, cough and gasp when a reply asks for it (English only). " +
            "Reply text and the selected voice sample go there. Its model is Apache-2.0."),
        new(Singing, "Sings", "Singing", "an NVIDIA GPU with at least 6 GB",
            Audio2FaceHostConnection.SongRouteId, "singing",
            "Writes songs from lyrics and a style and sings them in a voice from your voice library (ACE-Step 1.5 and SoulX-Singer). " +
            "The lyrics, style and the voice's recording go there. Songs take about a minute; it frees the graphics card when idle.")
    ];

    internal static HostRoleInfo Get(string kind) => All.FirstOrDefault(r => r.Kind == kind) ??
        throw new InvalidOperationException($"Unknown host role '{kind}'.");

    internal static HostRoleInfo? ForRoute(string routeId) => All.FirstOrDefault(r => r.RouteId == routeId);
}

/// <summary>Every Martlet host this desktop is paired with, in hosts.json next to the other local preferences. Which host
/// handles lip-sync stays in the avatar profile (<see cref="AvatarProfile.RemoteHost"/>); an older single pairing saved
/// only there is listed here too.</summary>
internal static class HostRegistry
{
    internal const string FileName = "hosts.json";
    internal const int MaximumHosts = 16;
    private const int MaximumBytes = 65_536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Document
    {
        public required int Version { get; init; }
        public required IReadOnlyList<PairedHost> Hosts { get; init; }
    }

    /// <summary>Whether a host address is this PC: its LAN address (<paramref name="thisPcAddress"/>) or a loopback address.</summary>
    internal static bool IsThisPc(string address, string? thisPcAddress) =>
        address == thisPcAddress || address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(address, out var ip) && System.Net.IPAddress.IsLoopback(ip);

    /// <summary>The saved hosts plus the lip-sync host from the avatar profile when it predates this list. A host saved as
    /// "this PC, with Docker Desktop" that answers on another computer's address (a pairing code from that computer pasted
    /// while the Add a computer wizard still showed This PC) is that other computer, reached through Martlet there; so is a
    /// host saved by an older Martlet as "I run its commands on it myself".</summary>
    internal static IReadOnlyList<PairedHost> Load(string directory, AvatarRemoteHost? assigned = null, string? thisPcAddress = null)
    {
        var path = Path.Combine(directory, FileName);
        List<PairedHost> hosts;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException($"{FileName} is too large.");
            var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new InvalidDataException($"{FileName} is empty.");
            if (document.Version != 1) throw new InvalidDataException("Saved hosts were written by a newer Martlet.");
            hosts = [.. document.Hosts];
            foreach (var host in hosts) host.Pairing.Validate();
            if (hosts.Select(h => h.HostId).Distinct(StringComparer.Ordinal).Count() != hosts.Count)
                throw new InvalidDataException("A saved host is listed twice.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { hosts = []; }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException($"Saved hosts couldn't be read ({error.Message}). Pair again.", error);
        }
        for (var i = 0; i < hosts.Count; i++)
            if (hosts[i].Method == HostSetupMethod.OnHost ||
                thisPcAddress is not null && hosts[i].Method == HostSetupMethod.ThisPcDocker && !IsThisPc(hosts[i].Address, thisPcAddress))
                hosts[i] = hosts[i] with { Method = HostSetupMethod.Agent };
        if (assigned is not null && hosts.All(h => h.HostId != assigned.HostId))
            hosts.Add(new()
            {
                Pairing = assigned,
                Method = new Uri(assigned.Origin).Host == thisPcAddress ? HostSetupMethod.ThisPcDocker : HostSetupMethod.Agent
            });
        return hosts;
    }

    internal static void Save(string directory, IReadOnlyList<PairedHost> hosts)
    {
        if (hosts.Count > MaximumHosts) throw new InvalidOperationException($"Martlet can remember up to {MaximumHosts} hosts; forget one first.");
        foreach (var host in hosts) host.Pairing.Validate();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"hosts.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document { Version = 1, Hosts = hosts }, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static IReadOnlyList<PairedHost> Upsert(IReadOnlyList<PairedHost> hosts, PairedHost host)
    {
        var list = hosts.ToList();
        var index = list.FindIndex(h => h.HostId == host.HostId);
        if (index >= 0) list[index] = host;
        else list.Add(host);
        return list;
    }
}

/// <summary>Reads, pairs, forgets and re-reaches Martlet hosts, keeping hosts.json, the lip-sync assignment in the avatar
/// profile and the pairing secrets in step.</summary>
internal sealed class HostPairings(string dataDirectory, AvatarProfileStore profiles, ISetupService settings)
{
    internal string DataDirectory => dataDirectory;

    internal async Task<(AvatarProfile Profile, string? Revision)> LoadProfileAsync(CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token);
        if (loaded.Settings is null) throw new InvalidOperationException("Complete Setup first.");
        var id = loaded.Settings.Profile.Id;
        var saved = await profiles.LoadAsync(id, token);
        return (saved.Profile ?? AvatarProfile.BuiltIn(id), saved.Revision);
    }

    internal async Task<(IReadOnlyList<PairedHost> Hosts, AvatarProfile Profile)> LoadAsync(CancellationToken token)
    {
        var (profile, _) = await LoadProfileAsync(token);
        return (HostRegistry.Load(dataDirectory, profile.RemoteHost, HostSetupCommands.ThisPcAddress()), profile);
    }

    /// <summary>Saves a new pairing. Pairing hands the host no job: it stands by until you hand it one (handing it lip-sync
    /// checks it runs Audio2Face, or installs it in the same step). Re-pairing a host keeps its role. A pairing the owner made
    /// here (<paramref name="adopt"/>) is shared with the Martlet network on its next sync, even after a removal.</summary>
    internal async Task<(PairedHost Host, bool LipSync)> AddAsync(AvatarRemoteHost pairing, HostSetupMethod method, string? sshTarget,
        CancellationToken token, string? sshHostKey = null, bool adopt = true)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        var hosts = HostRegistry.Load(dataDirectory, profile.RemoteHost, HostSetupCommands.ThisPcAddress());
        var previous = hosts.FirstOrDefault(h => h.HostId == pairing.HostId);
        if (method == HostSetupMethod.OnHost) method = HostSetupMethod.Agent;
        var ssh = method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;
        var host = new PairedHost
        {
            Pairing = pairing, Method = method,
            SshTarget = ssh ? sshTarget : null,
            SshHostKey = ssh ? sshHostKey ?? (previous?.SshTarget == sshTarget ? previous?.SshHostKey : null) : null
        };
        // Re-pairing with a code keeps how Martlet already reaches it (for example over SSH).
        if (previous is not null && method == HostSetupMethod.Agent)
            host = host with { Method = previous.Method, SshTarget = previous.SshTarget, SshHostKey = previous.SshHostKey };
        if (previous is not null) host = host with { WakeMac = previous.WakeMac };
        HostRegistry.Save(dataDirectory, HostRegistry.Upsert(hosts, host));
        var lipSync = profile.RemoteHost?.HostId == pairing.HostId;
        if (lipSync) await profiles.SaveAsync(profile with { RemoteHost = pairing }, revision, token);
        var store = new WindowsCredentialStore();
        foreach (var old in new[] { previous?.Pairing, profile.RemoteHost?.HostId == pairing.HostId ? profile.RemoteHost : null })
            if (old is not null && old.CredentialId != pairing.CredentialId) store.DeleteAvatarHostSecret(old.HostId, old.CredentialId);
        if (adopt) NetworkIdentity.Adopt(dataDirectory, pairing.HostId);
        return (host, lipSync);
    }

    /// <summary>Forgets a host here (list, lip-sync assignment and secret). The host itself still lists this device until revoked there.</summary>
    internal async Task<PairedHost?> ForgetAsync(string hostId, CancellationToken token)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        var hosts = HostRegistry.Load(dataDirectory, profile.RemoteHost, HostSetupCommands.ThisPcAddress());
        var host = hosts.FirstOrDefault(h => h.HostId == hostId);
        if (host is null) return null;
        HostRegistry.Save(dataDirectory, hosts.Where(h => h.HostId != hostId).ToList());
        if (profile.RemoteHost?.HostId == hostId) await profiles.SaveAsync(profile with { RemoteHost = null }, revision, token);
        new WindowsCredentialStore().DeleteAvatarHostSecret(host.Pairing.HostId, host.Pairing.CredentialId);
        try { new HostHardwareStore(dataDirectory).Forget(hostId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return host;
    }

    /// <summary>Changes how Martlet reaches a host to install or remove its roles.</summary>
    internal async Task<PairedHost> SetReachAsync(string hostId, HostSetupMethod method, string? sshTarget, string version,
        CancellationToken token)
    {
        var (hosts, _) = await LoadAsync(token);
        var host = hosts.FirstOrDefault(h => h.HostId == hostId) ?? throw new InvalidOperationException("That host is no longer paired.");
        if (method == HostSetupMethod.OnHost) method = HostSetupMethod.Agent;
        var ssh = method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;
        var target = ssh ? sshTarget?.Trim() : null;
        var updated = host with { Method = method, SshTarget = target, SshHostKey = target == host.SshTarget ? host.SshHostKey : null };
        if (ssh) HostSetupCommands.Validate(updated.Target(version), HostAction.Status);
        HostRegistry.Save(dataDirectory, HostRegistry.Upsert(hosts, updated));
        return updated;
    }

    /// <summary>Forgets the SSH host key saved with every paired host on that computer (after a reinstall).</summary>
    internal async Task ClearSshHostKeyAsync(HostShellTarget target, CancellationToken token)
    {
        var (hosts, _) = await LoadAsync(token);
        var changed = hosts.Select(h => h.SshHostKey is not null && h.SshTarget is { } ssh &&
                TryParse(ssh)?.Machine == target.Machine ? h with { SshHostKey = null } : h).ToList();
        if (!changed.SequenceEqual(hosts)) HostRegistry.Save(dataDirectory, changed);

        static HostShellTarget? TryParse(string text)
        {
            try { return HostShellTarget.Parse(text); }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>Hands lip-sync to a paired host, to this PC's own Audio2Face service (null) or to nobody (voice loudness).</summary>
    internal async Task<(AvatarProfile Before, AvatarProfile After)> AssignLipSyncAsync(PairedHost? host, bool off, CancellationToken token)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        var next = off ? profile with { LipSync = AvatarLipSync.Loudness, RemoteHost = null }
            : host is not null ? profile with { LipSync = AvatarLipSync.Auto, RemoteHost = host.Pairing }
            : profile with { LipSync = profile.LipSync == AvatarLipSync.Audio2Face ? AvatarLipSync.Audio2Face : AvatarLipSync.Auto, RemoteHost = null };
        if (next != profile) await profiles.SaveAsync(next, revision, token);
        return (profile, next);
    }
}

/// <summary>Talks to a paired host over its pinned pairing, on an explicit action or while the owner keeps who does what in
/// sync (<see cref="ClusterSync"/>).</summary>
internal static class HostControl
{
    /// <summary>Which Martlet roles (and models) a reachable host runs, in words.</summary>
    internal static string Describe(IReadOnlyDictionary<string, string> offers) => offers.Count == 0
        ? "Connected. No host roles installed."
        : "Connected. Runs " + string.Join(", ", HostRoles.All.Where(r => offers.ContainsKey(r.Kind))
            .Select(r => r.Name)) + ".";

    /// <summary>Reads which roles a paired host currently offers this PC, and saves the hardware it reports.</summary>
    internal static async Task<HostCheck> CheckAsync(AvatarRemoteHost host, HostHardwareStore? hardware, CancellationToken token)
    {
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            return new(false, "Pair this host again.");
        Audio2FaceHostConnection? connection = null;
        try
        {
            read.Secret.Use(secret => connection = new Audio2FaceHostConnection(GatewayAvatarHostLink.Pairing(host), secret));
            var routes = await connection!.ReadRoutesAsync(token);
            var offers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var route in routes)
                if (HostRoles.ForRoute(route.RouteId) is { } role) offers[role.Kind] = route.ModelId;
            var text = Describe(offers);
            string? version = null;
            try
            {
                var (hardwareText, reported) = await HostsWindow.ReadHardwareAsync(connection, hardware, token);
                text += " " + hardwareText;
                version = reported;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception error) when (error is Audio2FaceHostException or IOException or JsonException or TimeoutException or
                HttpRequestException or InvalidOperationException) { }
            return new(true, text, offers, version, routes);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "Didn't respond in time."); }
        catch (Exception error) when (error is Audio2FaceHostException or IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException or HttpRequestException)
        {
            return new(false, error.Message);
        }
        finally { connection?.Dispose(); }
    }
}

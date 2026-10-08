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
    /// <summary>Where the host answers from outside home ("name:port"), kept with the pairing: from the invite a computer
    /// signed in with, then from the network roster on every sync. A computer that has never been on the home network
    /// reconnects with these (home address first, these after 1.5 seconds) whatever its network state.</summary>
    public IReadOnlyList<string>? OutsideAddresses { get; init; }
    /// <summary>"friend" for a host a friend shares with this PC (<see cref="HostSignInAccess.Friend"/>): this PC signed in there
    /// as a friend and may use only its engines (thinking, listening, speaking, lip-sync, reading). It is never part of this
    /// PC's Martlet network or its shared plan, and none of the syncs between your own computers talk to it. Null for your own
    /// hosts.</summary>
    public string? Access { get; init; }
    /// <summary>Who this PC signed in as on a shared host ("ana@example.net (google)"), for people to see.</summary>
    public string? SignedInAs { get; init; }

    [JsonIgnore] public string HostId => Pairing.HostId;
    [JsonIgnore] public string Address => new Uri(Pairing.Origin).Host;
    /// <summary>A host a friend shares with this PC: its engines only (see <see cref="Access"/>).</summary>
    [JsonIgnore] public bool Shared => Access == HostSignInAccess.Friend;

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
        HostSetupMethod.SshNative => $"SSH to {SshTarget ?? "(not set)"} (native)",
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
    internal const string DeepThinking = "deep-thinking";
    internal const string Stt = "stt";
    internal const string F5 = "f5";
    internal const string Xtts = "xtts";
    internal const string Chatterbox = "chatterbox";
    internal const string ChatterboxOriginal = "chatterbox-original";
    internal const string ChatterboxNano = "chatterbox-nano";
    internal const string GptSovits = "gpt-sovits";
    internal const string Dia = "dia";
    internal const string Singing = "singing";
    internal const string Pictures = "pictures";
    internal const string Ocr = "ocr";

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
        new(DeepThinking, "Thinking pool", "Thinking pool", "Docker; an NVIDIA GPU is recommended",
            HostRoute.DeepThinkingRouteId, "deep thinking",
            "Joins the Thinking pool: background jobs (thinking longer, research, summaries) run on that host with a model of its own, beside Thinking's, while the " +
            "conversation carries on. A job's text (for a think, the task and the conversation so far) goes there."),
        new(Stt, "Listens", "Listening", "Docker; an NVIDIA GPU is recommended",
            Audio2FaceHostConnection.TranscriptionRouteId, "listening",
            "Turns speech into text on that host. Your recorded speech goes there and is not stored."),
        new(Chatterbox, "Speaks", "Speaking (Chatterbox Turbo)", "an NVIDIA GPU with at least 6 GB",
            SpeechEngines.Chatterbox.RouteId, "speaking",
            "Speaks replies on that host with Chatterbox Turbo, which can laugh, sigh and whisper. Reply text and the " +
            "selected voice sample (longer than 5 seconds) go there. MIT-licensed model; replies carry an inaudible watermark."),
        new(ChatterboxOriginal, "Speaks", "Speaking (Chatterbox Original)", "an NVIDIA GPU with at least 6 GB",
            SpeechEngines.ChatterboxOriginal.RouteId, "speaking",
            "Speaks replies on that host with the original Chatterbox, which says each sentence calmly or expressively as the reply " +
            "asks. Reply text and the selected voice sample (longer than 5 seconds) go there. MIT-licensed model; replies carry an " +
            "inaudible watermark."),
        new(ChatterboxNano, "Speaks", "Speaking (Chatterbox Nano)", "Docker; it uses an NVIDIA GPU when there is one, otherwise the processor",
            SpeechEngines.ChatterboxNano.RouteId, "speaking",
            "Speaks replies on that host with Chatterbox Nano, a small Chatterbox that can laugh, sigh and whisper and runs on the " +
            "processor when there is no graphics card. Reply text and the selected voice sample (longer than 5 seconds) go there. " +
            "MIT-licensed model; replies carry an inaudible watermark."),
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
            "The lyrics, style and the voice's recording go there. Songs take a few minutes; it frees the graphics card when idle."),
        new(Pictures, "Draws", "Pictures", "an NVIDIA GPU with at least 8 GB",
            Audio2FaceHostConnection.PictureRouteId, "pictures",
            "Draws the picture descriptions Martlet writes on that host with ComfyUI and frees the graphics card when idle."),
        new(Ocr, "Reads", "Reading", "Docker; it runs on the processor (no graphics card needed)",
            Audio2FaceHostConnection.OcrRouteId, "reading",
            "Reads the text on your screen with RapidOCR on that host while Martlet watches it. Screenshots go there, are read in " +
            "memory and are not kept.")
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
    internal const int MaximumHosts = 64;
    private const int MaximumBytes = 262_144;
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
        // A host a friend shares never holds this PC's voices (speak there with the recording itself from the first sentence),
        // and its owner's home address never changes how this PC reaches its own host at the same address (or the other way).
        foreach (var host in hosts.Where(h => h.Shared))
        {
            Audio2FaceHostConnection.SendRecordingTo(host.HostId);
            HostRoutes.KeepApart(host.Pairing.Origin, host.Pairing.SpkiFingerprint);
        }
        // Saved outside addresses reach the host until the roster (which wins once this PC syncs it) says otherwise.
        foreach (var host in hosts.Where(h => h.OutsideAddresses is { Count: > 0 }))
            HostRoutes.Prime(host.Pairing.Origin, host.HostId, host.OutsideAddresses!, host.Pairing.SpkiFingerprint);
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

    /// <summary>The hosts with their outside addresses taken from <paramref name="roster"/> where it lists that host (with the
    /// same home origin); unchanged hosts are returned as they are, so the caller saves only when something changed. A host a
    /// friend shares is never in this PC's roster, so its invite's addresses stay.</summary>
    internal static IReadOnlyList<PairedHost> WithRosterAddresses(IReadOnlyList<PairedHost> hosts, Martlet.Core.Network.NetworkRoster? roster)
    {
        if (roster is null) return hosts;
        return hosts.Select(h => !h.Shared && roster.Host(h.HostId) is { Removed: false } entry && entry.Origin == h.Pairing.Origin &&
                !(entry.Addresses ?? []).SequenceEqual(h.OutsideAddresses ?? [])
            ? h with { OutsideAddresses = entry.Addresses is { Count: > 0 } a ? a.ToArray() : null }
            : h).ToList();
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
/// profile and the pairing secrets in step. Pairing needs no Setup: a new PC joins your hosts first and then uses them for
/// its jobs.</summary>
internal sealed class HostPairings(string dataDirectory, AvatarProfileStore profiles, ISetupService settings)
{
    internal string DataDirectory => dataDirectory;

    /// <summary>This PC's character profile, or null before anything was saved on this PC (no settings yet, so no profile and
    /// no lip-sync assignment). Throws only when the settings can't be read, as the profile they name is then unknown.</summary>
    internal async Task<(AvatarProfile? Profile, string? Revision)> LoadProfileAsync(CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        if (loaded.Settings is null) return (null, null);
        var id = loaded.Settings.Profile.Id;
        var saved = await profiles.LoadAsync(id, token);
        return (saved.Profile ?? AvatarProfile.BuiltIn(id), saved.Revision);
    }

    /// <summary>This PC's character profile to change. On a PC where nothing was saved yet it first saves new settings (no
    /// jobs chosen), which gives the profile its ID, so a change such as handing lip-sync to a host never waits for Setup.</summary>
    internal async Task<(AvatarProfile Profile, string? Revision)> EnsureProfileAsync(CancellationToken token)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        if (profile is not null) return (profile, revision);
        var saved = await settings.SaveAsync(SetupSettings.Begin(null), null, token);
        // Another change may have saved this PC's first settings a moment earlier; use those.
        if (!saved.Save.Saved && (await LoadProfileAsync(token)) is { Profile: { } existing } again) return (existing, again.Revision);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        return (AvatarProfile.BuiltIn(saved.Settings.Profile.Id), null);
    }

    internal async Task<(IReadOnlyList<PairedHost> Hosts, AvatarProfile? Profile)> LoadAsync(CancellationToken token)
    {
        var (profile, _) = await LoadProfileAsync(token);
        return (HostRegistry.Load(dataDirectory, profile?.RemoteHost, HostSetupCommands.ThisPcAddress()), profile);
    }

    /// <summary>Throws when this PC's settings can't be read, before a one-use pairing code is spent on a pairing that
    /// couldn't be kept. A PC with no settings yet pairs fine.</summary>
    internal Task CheckCanKeepAsync(CancellationToken token) => LoadProfileAsync(token);

    /// <summary>Saves a new pairing. Pairing hands the host no job: it stands by until you hand it one (handing it lip-sync
    /// checks it runs Audio2Face, or installs it in the same step). Re-pairing a host keeps its role. A pairing the owner made
    /// here (<paramref name="adopt"/>) is shared with the Martlet network on its next sync, even after a removal. A host a friend
    /// shares with this PC (<paramref name="access"/> "friend", from its sign-in) is kept apart: never adopted into this PC's
    /// network, never in its shared plan.</summary>
    internal async Task<(PairedHost Host, bool LipSync)> AddAsync(AvatarRemoteHost pairing, HostSetupMethod method, string? sshTarget,
        CancellationToken token, string? sshHostKey = null, bool adopt = true, IReadOnlyList<string>? outsideAddresses = null,
        string? access = null, string? signedInAs = null)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        var hosts = HostRegistry.Load(dataDirectory, profile?.RemoteHost, HostSetupCommands.ThisPcAddress());
        var previous = hosts.FirstOrDefault(h => h.HostId == pairing.HostId);
        if (method == HostSetupMethod.OnHost) method = HostSetupMethod.Agent;
        var shared = access == HostSignInAccess.Friend;
        // A friend's host and one of yours never replace each other under the same name. Only the same host (the same key)
        // changes between the two, when its owner switches this PC's sign-in.
        if (previous is not null && previous.Shared != shared && previous.Pairing.SpkiFingerprint != pairing.SpkiFingerprint)
            throw new InvalidOperationException(shared
                ? $"{pairing.HostId} is already one of your own hosts on this PC, so Martlet can't keep a friend's host with the same name here."
                : $"{pairing.HostId} is a host a friend shares with this PC, so Martlet can't pair your own host with the same name here. " +
                  "Forget the shared one under Devices › Hosts shared with this PC first.");
        var ssh = !shared && method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;
        var host = new PairedHost
        {
            Pairing = pairing, Method = shared ? HostSetupMethod.Agent : method,
            SshTarget = ssh ? sshTarget : null,
            SshHostKey = ssh ? sshHostKey ?? (previous?.SshTarget == sshTarget ? previous?.SshHostKey : null) : null,
            Access = shared ? HostSignInAccess.Friend : null, SignedInAs = shared ? signedInAs : null
        };
        // Re-pairing with a code keeps how Martlet already reaches it (for example over SSH); a friend's host is reached only
        // through its gateway.
        if (previous is not null && method == HostSetupMethod.Agent && !shared && !previous.Shared)
            host = host with { Method = previous.Method, SshTarget = previous.SshTarget, SshHostKey = previous.SshHostKey };
        if (previous is not null && !shared) host = host with { WakeMac = previous.WakeMac };
        host = host with
        {
            // The addresses of a friend's host never become your own host's (or the other way round) under the same name.
            OutsideAddresses = outsideAddresses is { Count: > 0 } ? outsideAddresses.ToArray()
                : previous is { } before && before.Shared == shared ? before.OutsideAddresses : null
        };
        if (shared)
        {
            Audio2FaceHostConnection.SendRecordingTo(pairing.HostId);
            HostRoutes.KeepApart(pairing.Origin, pairing.SpkiFingerprint);
        }
        if (host.OutsideAddresses is { Count: > 0 } outside) HostRoutes.Set(pairing.Origin, pairing.HostId, outside, pairing.SpkiFingerprint);
        HostRegistry.Save(dataDirectory, HostRegistry.Upsert(hosts, host));
        var lipSync = profile?.RemoteHost?.HostId == pairing.HostId;
        if (lipSync) await profiles.SaveAsync(profile! with { RemoteHost = pairing }, revision, token);
        var store = new WindowsCredentialStore();
        foreach (var old in new[] { previous?.Pairing, lipSync ? profile!.RemoteHost : null })
            if (old is not null && old.CredentialId != pairing.CredentialId) store.DeleteAvatarHostSecret(old.HostId, old.CredentialId);
        if (adopt && !shared) NetworkIdentity.Adopt(dataDirectory, pairing.HostId);
        return (host, lipSync);
    }

    /// <summary>Keeps every paired host's outside addresses in step with <paramref name="roster"/> (saved only when changed).
    /// Returns the hosts whose addresses changed.</summary>
    internal IReadOnlyList<string> KeepRosterAddresses(Martlet.Core.Network.NetworkRoster? roster)
    {
        var hosts = HostRegistry.Load(dataDirectory);
        var updated = HostRegistry.WithRosterAddresses(hosts, roster);
        var changed = updated.Where((h, i) => !ReferenceEquals(h, hosts[i])).Select(h => h.HostId).ToArray();
        if (changed.Length > 0) HostRegistry.Save(dataDirectory, updated);
        return changed;
    }

    /// <summary>Forgets a host here (list, lip-sync assignment and secret). The host itself still lists this device until revoked there.</summary>
    internal async Task<PairedHost?> ForgetAsync(string hostId, CancellationToken token)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        var hosts = HostRegistry.Load(dataDirectory, profile?.RemoteHost, HostSetupCommands.ThisPcAddress());
        var host = hosts.FirstOrDefault(h => h.HostId == hostId);
        if (host is null) return null;
        HostRegistry.Save(dataDirectory, hosts.Where(h => h.HostId != hostId).ToList());
        if (profile?.RemoteHost?.HostId == hostId) await profiles.SaveAsync(profile with { RemoteHost = null }, revision, token);
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
        var (profile, revision) = await EnsureProfileAsync(token);
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
    /// <summary>Which Martlet roles (and models) a reachable host runs, in words; a Deep thinking role that runs several thinks
    /// at once (<paramref name="routes"/>) says how many.</summary>
    internal static string Describe(IReadOnlyDictionary<string, string> offers, IReadOnlyList<HostRoute>? routes = null) => offers.Count == 0
        ? "Connected. No host roles installed."
        : "Connected. Runs " + string.Join(", ", HostRoles.All.Where(r => offers.ContainsKey(r.Kind))
            .Select(r => r.Kind == HostRoles.DeepThinking && new HostCheck(true, "", offers, Routes: routes).DeepThinkingSlots is > 1 and var slots
                ? $"{r.Name} ({slots} thinks at once)" : r.Name)) + ".";

    /// <summary>Reads which roles a paired host currently offers this PC, and saves the hardware it reports. A host a friend
    /// shares with this PC (<paramref name="shared"/>) says only which engines it offers: its hardware is its owner's.</summary>
    internal static async Task<HostCheck> CheckAsync(AvatarRemoteHost host, HostHardwareStore? hardware, CancellationToken token,
        bool shared = false)
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
            var text = Describe(offers, routes);
            string? version = null;
            if (shared) return new(true, text, offers, null, routes);
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
            return new(false, error.Message) { Code = (error as Audio2FaceHostException)?.Code };
        }
        finally { connection?.Dispose(); }
    }
}

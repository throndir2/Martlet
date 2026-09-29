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
    public HostSetupMethod Method { get; init; } = HostSetupMethod.OnHost;
    /// <summary>user@computer for the SSH methods.</summary>
    public string? SshTarget { get; init; }
    /// <summary>The SSH host key Martlet pinned for it ("ssh-ed25519 SHA256:..."); runs refuse a different key.</summary>
    public string? SshHostKey { get; init; }

    [JsonIgnore] public string HostId => Pairing.HostId;
    [JsonIgnore] public string Address => new Uri(Pairing.Origin).Host;

    internal HostSetupTarget Target(string version) => new(Method, SshTarget ?? "", Address, HostId, version);

    internal bool CanLaunch => Method switch
    {
        HostSetupMethod.ThisPcDocker => true,
        HostSetupMethod.SshDocker or HostSetupMethod.SshNative => !string.IsNullOrWhiteSpace(SshTarget),
        _ => false
    };

    internal string Reach => Method switch
    {
        HostSetupMethod.ThisPcDocker => "This PC, with Docker Desktop",
        HostSetupMethod.SshDocker => $"SSH to {SshTarget ?? "(not set)"}, Docker there",
        HostSetupMethod.SshNative => $"SSH to {SshTarget ?? "(not set)"}, native Ubuntu",
        _ => "Not set: you run its commands on that computer"
    };
}

/// <summary>A role a Martlet host can run for this desktop, installed with the same martlet-host flow as every role.</summary>
internal sealed record HostRoleInfo(string Kind, string Chip, string Name, string Needs, HostAction Add, HostAction Remove);

internal static class HostRoles
{
    internal const string Audio2Face = "audio2face";

    internal static readonly IReadOnlyList<HostRoleInfo> All =
    [
        new(Audio2Face, "Lip-sync", "Lip-sync (Audio2Face)", "an NVIDIA GPU with 4 GB+ and a free NVIDIA NGC API key",
            HostAction.AddAudio2Face, HostAction.RemoveAudio2Face)
    ];

    internal static HostRoleInfo Get(string kind) => All.FirstOrDefault(r => r.Kind == kind) ??
        throw new InvalidOperationException($"Unknown host role '{kind}'.");
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

    /// <summary>The saved hosts plus the lip-sync host from the avatar profile when it predates this list.</summary>
    internal static IReadOnlyList<PairedHost> Load(string directory, AvatarRemoteHost? assigned = null, string? thisPcAddress = null)
    {
        var path = Path.Combine(directory, FileName);
        List<PairedHost> hosts;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException($"{FileName} is too large.");
            var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new InvalidDataException($"{FileName} is empty.");
            if (document.Version != 1) throw new InvalidDataException($"{FileName} was written by a newer Martlet.");
            hosts = [.. document.Hosts];
            foreach (var host in hosts) host.Pairing.Validate();
            if (hosts.Select(h => h.HostId).Distinct(StringComparer.Ordinal).Count() != hosts.Count)
                throw new InvalidDataException($"{FileName} lists a host twice.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { hosts = []; }
        catch (Exception error) when (error is JsonException or ContractException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException($"{FileName} could not be read ({error.Message}). Fix or delete it, then pair again.", error);
        }
        if (assigned is not null && hosts.All(h => h.HostId != assigned.HostId))
            hosts.Add(new()
            {
                Pairing = assigned,
                Method = new Uri(assigned.Origin).Host == thisPcAddress ? HostSetupMethod.ThisPcDocker : HostSetupMethod.OnHost
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
        if (loaded.Settings is null) throw new InvalidOperationException("Complete Setup once so Martlet can save its hosts and who handles lip-sync.");
        var id = loaded.Settings.Profile.Id;
        var saved = await profiles.LoadAsync(id, token);
        return (saved.Profile ?? AvatarProfile.BuiltIn(id), saved.Revision);
    }

    internal async Task<(IReadOnlyList<PairedHost> Hosts, AvatarProfile Profile)> LoadAsync(CancellationToken token)
    {
        var (profile, _) = await LoadProfileAsync(token);
        return (HostRegistry.Load(dataDirectory, profile.RemoteHost, HostSetupCommands.ThisPcAddress()), profile);
    }

    /// <summary>Saves a new pairing. The first host paired takes over lip-sync unless lip-sync is turned off; later hosts
    /// stand by until you hand them a role on the Devices page. Re-pairing a host keeps its role.</summary>
    internal async Task<(PairedHost Host, bool LipSync)> AddAsync(AvatarRemoteHost pairing, HostSetupMethod method, string? sshTarget,
        CancellationToken token, string? sshHostKey = null)
    {
        var (profile, revision) = await LoadProfileAsync(token);
        var hosts = HostRegistry.Load(dataDirectory, profile.RemoteHost, HostSetupCommands.ThisPcAddress());
        var previous = hosts.FirstOrDefault(h => h.HostId == pairing.HostId);
        var ssh = method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;
        var host = new PairedHost
        {
            Pairing = pairing, Method = method,
            SshTarget = ssh ? sshTarget : null,
            SshHostKey = ssh ? sshHostKey ?? (previous?.SshTarget == sshTarget ? previous?.SshHostKey : null) : null
        };
        if (previous is not null && method == HostSetupMethod.OnHost)
            host = host with { Method = previous.Method, SshTarget = previous.SshTarget, SshHostKey = previous.SshHostKey };
        HostRegistry.Save(dataDirectory, HostRegistry.Upsert(hosts, host));
        var lipSync = profile.RemoteHost?.HostId == pairing.HostId ||
            profile.RemoteHost is null && profile.LipSync != AvatarLipSync.Loudness && hosts.All(h => h.HostId == pairing.HostId);
        if (lipSync) await profiles.SaveAsync(profile with { RemoteHost = pairing }, revision, token);
        var store = new WindowsCredentialStore();
        foreach (var old in new[] { previous?.Pairing, profile.RemoteHost?.HostId == pairing.HostId ? profile.RemoteHost : null })
            if (old is not null && old.CredentialId != pairing.CredentialId) store.DeleteAvatarHostSecret(old.HostId, old.CredentialId);
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

/// <summary>Talks to a paired host over its pinned pairing. Only ever runs on an explicit action.</summary>
internal static class HostControl
{
    /// <summary>Reads which roles a paired host currently offers this PC, and saves the hardware it reports.</summary>
    internal static async Task<HostCheck> CheckAsync(AvatarRemoteHost host, HostHardwareStore? hardware, CancellationToken token)
    {
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            return new(false, "This PC's pairing secret is missing; pair again.");
        Audio2FaceHostConnection? connection = null;
        try
        {
            read.Secret.Use(secret => connection = new Audio2FaceHostConnection(GatewayAvatarHostLink.Pairing(host), secret));
            var route = await connection!.ReadRouteAsync(token);
            var offers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (route is not null) offers[HostRoles.Audio2Face] = route.ModelId;
            var text = route is null ? "Reachable. Not running Audio2Face." : $"Reachable. Runs Audio2Face (model {route.ModelId}).";
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
            return new(true, text, offers, version);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "Did not answer in time."); }
        catch (Exception error) when (error is Audio2FaceHostException or IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException or HttpRequestException)
        {
            return new(false, error.Message);
        }
        finally { connection?.Dispose(); }
    }
}

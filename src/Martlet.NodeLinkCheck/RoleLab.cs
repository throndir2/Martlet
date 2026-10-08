using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;
using Martlet.Diagnostics;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// A live lab for switching your computers between companion and host PC through Martlet MCP (role_lab): a real gateway on
/// 127.0.0.1 (pinned TLS, signed requests, the network roster and the shared settings in memory), already paired with the
/// desktop that runs on <c>dataDirectory</c> under the device ID that desktop gives itself (hosts.json written there, the pairing
/// secret in the lab credential folder named by MARTLET_LAB_CREDENTIALS, never Windows Credential Manager), and a simulated
/// companion PC (lab-companion, LAB-COMPANION) paired with it. Once the desktop has bound the host to its network, the companion
/// asks to join it (the desktop's owner allows it), and every 2 seconds it syncs the shared settings with the real sync engine
/// as a desktop does: it says what it is (pc.lab-companion), follows an ask that it become a host or a companion PC
/// (role.lab-companion) and, when role-lab.ask appears in the data directory ("host" or "companion"), asks the desktop to switch
/// (role.&lt;desktop&gt;). The lab writes what it sees to role-lab.json there and stops when its standard input closes,
/// role-lab.stop appears there, or after 20 minutes.
/// </summary>
internal static class RoleLab
{
    internal const string StatusFile = "role-lab.json";
    internal const string StopFile = "role-lab.stop";
    internal const string AskFile = "role-lab.ask";
    internal const string CompanionDevice = "lab-companion";
    internal const string CompanionName = "LAB-COMPANION";
    private const string Companion = "companion", Host = "host";

    internal static async Task<int> RunAsync(string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !Directory.Exists(dataDirectory))
            return Fail("The data directory must be an existing absolute folder (a disposable one).");
        if (LabCredentialNative.FromEnvironment() is null)
            return Fail($"Set {LabCredentialNative.Variable} to an existing folder (Invoke-MartletMcp.ps1 -LabCredentials); the lab never writes Windows Credential Manager.");
        if (File.Exists(Path.Combine(dataDirectory, "hosts.json")))
            return Fail("hosts.json already exists there; use a fresh disposable data directory.");

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        _ = Task.Run(() =>
        {
            try { while (Console.In.ReadLine() is not null) { } }
            catch (IOException) { }
            lifetime.Cancel();
        });
        var token = lifetime.Token;
        var folder = Path.Combine(Path.GetTempPath(), "martlet-role-lab-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await SignInRehearsal.LabHost.StartAsync("lab-role-host");

            // The desktop on the data directory, under the device ID it names itself by (desktop-<computer>), so the entries it
            // keeps about itself (pc.<device>, role.<device>) are the ones the network knows it by.
            var desktopDevice = LocalLogs.ThisDeviceId();
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, pairingSecret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), desktopDevice,
                "LAB-DESKTOP", token);
            using (var lease = new SecretLease(pairingSecret))
            {
                var stored = new WindowsCredentialStore().WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
                if (stored != CredentialError.None) return Fail("Couldn't keep the lab pairing secret: " + stored);
            }
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hosts.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                hosts = new[]
                {
                    new
                    {
                        pairing = new
                        {
                            origin = pairing.Origin, hostId = pairing.HostId, spkiFingerprint = pairing.SpkiFingerprint, deviceId = pairing.DeviceId,
                            credentialId = pairing.CredentialId
                        },
                        method = "Agent"
                    }
                }
            }), token);

            // The other companion PC: its own network key and pairing, and a shared-settings copy in a temporary folder.
            using var companionKey = NetworkKey.Create(CompanionDevice);
            var network = new SignInRehearsal.LabDesktop(companionKey, CompanionName);
            var companionCard = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (companionPairing, companionSecret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, companionCard.Code.Reveal(),
                CompanionDevice, CompanionName, token);
            network.Keep(companionPairing, companionSecret);
            var computer = new LabComputer(Path.Combine(folder, "companion"), DateTimeOffset.UtcNow);

            Console.WriteLine(JsonSerializer.Serialize(new { ready = true, hostId = host.HostId, desktop = desktopDevice, companion = CompanionDevice }));
            var events = new List<string>();
            var everMember = false;
            while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, StopFile)))
            {
                // The companion joins only once the desktop has bound the host to its network (it would found its own otherwise).
                if (host.Server.NetworkState.State == "bound" && network.State.RemovedFrom is null)
                {
                    try
                    {
                        var result = await network.SyncAsync(token);
                        events.AddRange(result.Events.Select(e => "network: " + e));
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { events.Add("network sync failed: " + error.Message); }
                }
                var member = network.State.Roster?.Trusts(companionKey.DeviceId, companionKey.PublicKey) == true;
                everMember |= member;

                // MCP's ask: one word in a file, read once (a file still being written is read on the next round).
                var ask = Path.Combine(dataDirectory, AskFile);
                string? wanted = null;
                try
                {
                    if (File.Exists(ask))
                    {
                        wanted = (await File.ReadAllTextAsync(ask, token)).Trim();
                        File.Delete(ask);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { wanted = null; }
                if (wanted is Host or Companion)
                {
                    try
                    {
                        computer.Node.Put(Key(SharedSettings.RolePrefix, desktopDevice), JsonSerializer.Serialize(wanted), DateTimeOffset.UtcNow);
                        events.Add($"{CompanionDevice} asked {desktopDevice} to become a {wanted} PC");
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { events.Add("couldn't ask: " + error.Message); }
                }
                else if (wanted is not null) events.Add($"ignored an ask for \"{wanted}\" (host or companion)");

                SharedSettings? copy = null;
                try
                {
                    using var connection = new Audio2FaceHostConnection(companionPairing, companionSecret);
                    var result = await computer.Node.SyncAsync([await connection.ReadSettingsAsync(token)], true, DateTimeOffset.UtcNow, token);
                    copy = await connection.MergeSettingsAsync(result.Document, token);
                    events.AddRange(result.Applied.Select(a => $"{CompanionDevice} took {a.Key} from {a.By}"));
                    events.AddRange(result.Recorded.Select(key => $"{CompanionDevice} recorded {key}: {result.Document.Find(key)?.Value}"));
                    events.AddRange(result.Waiting.Select(w => $"{CompanionDevice} waits on {w.Key}: {w.Value}"));
                }
                catch (Exception error) when (error is not OperationCanceledException) { events.Add("settings sync failed: " + error.Message); }

                var status = new
                {
                    ready = true, hostId = host.HostId, origin = host.Origin, network = host.Server.NetworkState.State,
                    networkId = host.Server.NetworkState.NetworkId, desktop = desktopDevice, companion = CompanionDevice,
                    companionRole = computer.Role, companionMember = member, companionWasMember = everMember,
                    companionWaiting = network.State.Waiting?.CheckNumber,
                    // What the host's copy says about each computer: what it says it is, and the newest ask that it switch.
                    desktopSays = copy?.Find(Key(SharedSettings.DevicePrefix, desktopDevice))?.Value,
                    desktopAsk = Ask(copy, desktopDevice),
                    companionSays = copy?.Find(Key(SharedSettings.DevicePrefix, CompanionDevice))?.Value,
                    companionAsk = Ask(copy, CompanionDevice),
                    events = events.TakeLast(16).ToArray(), at = DateTimeOffset.UtcNow
                };
                // Written whole and swapped in, so MCP never reads half of it; while MCP reads it, the next round writes it.
                var path = Path.Combine(dataDirectory, StatusFile);
                try
                {
                    await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(status), CancellationToken.None);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) { break; }
            }
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    private static object? Ask(SharedSettings? copy, string device) =>
        copy?.Find(Key(SharedSettings.RolePrefix, device)) is { } entry ? new { value = entry.Value, by = entry.UpdatedBy, at = entry.UpdatedAt } : null;

    /// <summary>A computer's entry name as the desktop writes it (SharedPc): lowercase letters, digits, dots and hyphens.</summary>
    private static string Key(string prefix, string device)
    {
        var key = prefix + new string(device.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' ? c : '-').ToArray());
        return key.Length > 64 ? key[..64] : key;
    }

    private static int Fail(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ready = false, error = message }));
        return 1;
    }

    /// <summary>The simulated companion PC's side of the shared settings: what it says it is (pc.lab-companion, as the desktop
    /// writes its own) and whether it is a companion or a host PC (role.lab-companion), which it follows when another computer
    /// writes it, as the desktop does.</summary>
    private sealed class LabComputer
    {
        private DateTimeOffset chosenAt;

        internal LabComputer(string directory, DateTimeOffset now)
        {
            Directory.CreateDirectory(directory);
            chosenAt = now;
            Node = new SharedSettingsNode(directory, CompanionDevice,
            [
                new DelegateSection(SharedSettings.DevicePrefix + CompanionDevice, "This PC's role", _ =>
                    Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(new { role = Role }), null, false, DateTimeOffset.UtcNow)),
                    (_, _) => Task.FromResult(SharedApply.Done)),
                new DelegateSection(SharedSettings.RolePrefix + CompanionDevice, "Companion or host PC", _ =>
                    Task.FromResult<SharedLocal?>(new(JsonSerializer.Serialize(Role), null, false, chosenAt)),
                    (setting, _) =>
                    {
                        string? wanted;
                        try { wanted = JsonSerializer.Deserialize<string>(setting.Value); }
                        catch (JsonException) { wanted = null; }
                        if (wanted is not (Host or Companion)) return Task.FromResult(SharedApply.Waiting("It was chosen on a newer Martlet."));
                        Role = wanted;
                        chosenAt = DateTimeOffset.UtcNow;
                        return Task.FromResult(SharedApply.Done);
                    })
            ]);
        }

        internal SharedSettingsNode Node { get; }
        internal string Role { get; private set; } = Companion;
    }
}

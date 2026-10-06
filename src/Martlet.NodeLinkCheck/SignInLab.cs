using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// A live sign-in lab for driving the desktop's Sign-in from outside window through Martlet MCP (signin_lab): a real
/// gateway on 127.0.0.1 with an owner account, an OpenID Connect provider (an issuer in this process) and an allowed
/// identity, already paired with the desktop that runs on <c>dataDirectory</c> (hosts.json written there, the pairing secret
/// in the lab credential folder named by MARTLET_LAB_CREDENTIALS, never Windows Credential Manager). A simulated laptop signs
/// in with that identity and keeps syncing the network, so the desktop lets it in and, once the owner removes the identity
/// in the window, removes it from the network again. The lab writes what it sees to signin-lab.json in the data directory
/// and stops when its standard input closes, signin-lab.stop appears there, or after 20 minutes.
/// </summary>
internal static class SignInLab
{
    internal const string StatusFile = "signin-lab.json";
    internal const string StopFile = "signin-lab.stop";
    internal const string DesktopDevice = "lab-desktop";

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
        await using var host = await SignInRehearsal.LabHost.StartAsync("lab-signin-host");
        using var issuer = new SignInRehearsal.LabIssuer();
        host.Server.UseSignInProviderHandler(issuer);
        // An outside address set on the host itself: the desktop signs it into the roster and keeps it with its pairing.
        host.Server.Guard.Exposure = new() { OutsideAddresses = ["lab-home.example.net:9443"], OutsideAddressesSetAt = DateTimeOffset.UtcNow };

        using var adminKey = NetworkKey.Create("lab-admin");
        var admin = new SignInRehearsal.LabDesktop(adminKey, "LAB-ADMIN");
        await admin.PairByCodeAsync(host, token);
        var secret = Totp.NewSecret();
        await admin.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "owner", ["user"] = "owner", ["password"] = "lab owner passphrase", ["totp_secret"] = secret,
            ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow)
        }, token);
        await admin.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "provider", ["provider_config"] = new JsonObject
            {
                ["id"] = "authentik", ["kind"] = "oidc", ["name"] = "Authentik (lab)", ["issuer"] = SignInRehearsal.LabIssuer.Issuer,
                ["client_id"] = SignInRehearsal.LabIssuer.ClientId, ["client_secret"] = SignInRehearsal.LabIssuer.ClientSecret
            }
        }, token);
        await admin.ChangeAsync(host.HostId, new JsonObject
        {
            ["action"] = "allow", ["provider"] = "authentik", ["subject"] = "lab-user-42", ["label"] = "me@example.net"
        }, token);

        // The desktop on the data directory: paired by a code, its secret in the lab folder, the host in its hosts.json.
        var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
        var (pairing, pairingSecret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), DesktopDevice, "LAB-DESKTOP", token);
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

        // The laptop away from home signs in with the allowed identity.
        using var laptopKey = NetworkKey.Create("lab-laptop");
        var laptop = new SignInRehearsal.LabDesktop(laptopKey, "LAB-LAPTOP");
        var invite = new NetworkInvite { HostId = host.HostId, SpkiFingerprint = host.Fingerprint, Origin = host.Origin, Label = "Lab home" };
        var (laptopPairing, laptopSecret, who) = await HostSignInClient.SignInInBrowserAsync(invite, host.Origin, "authentik", laptopKey.DeviceId,
            "LAB-LAPTOP", issuer.Browse, TimeSpan.FromSeconds(30), token);
        laptop.Keep(laptopPairing, laptopSecret);

        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, hostId = host.HostId, laptop = laptopKey.DeviceId, signedInAs = who.ToString() }));
        var events = new List<string>();
        var everMember = false;
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, StopFile)))
        {
            // The laptop syncs only once the desktop has bound the host to its network (it would found its own otherwise).
            if (host.Server.NetworkState.State == "bound" && laptop.State.RemovedFrom is null)
            {
                try
                {
                    var result = await laptop.SyncAsync(token);
                    events.AddRange(result.Events.Select(e => "laptop: " + e));
                }
                catch (Exception error) when (error is not OperationCanceledException) { events.Add("laptop sync failed: " + error.Message); }
            }
            var member = laptop.State.Roster?.Trusts(laptopKey.DeviceId, laptopKey.PublicKey) == true;
            everMember |= member;
            var canUse = await laptop.CanUseAsync(host.HostId, token);
            var status = new
            {
                ready = true, hostId = host.HostId, origin = host.Origin, network = host.Server.NetworkState.State,
                networkId = host.Server.NetworkState.NetworkId, laptop = laptopKey.DeviceId, signedInAs = who.ToString(),
                laptopMember = member, laptopWasMember = everMember, laptopRemoved = laptop.State.RemovedFrom is not null,
                laptopCanUseHost = canUse, waiting = laptop.State.Waiting?.CheckNumber,
                events = events.TakeLast(12).ToArray(), at = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, StatusFile), JsonSerializer.Serialize(status), CancellationToken.None);
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ready = false, error = message }));
        return 1;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// The sign-in lab's account mode (signin_lab mode "account"; docs/MCP.md): sign-in providers for the whole household and, with
/// them, account logins. Two real gateways on 127.0.0.1 (<see cref="HostA"/> and <see cref="HostB"/>), both routed to the lab's
/// OpenID Connect issuer, are paired with the desktop that runs on <c>dataDirectory</c> (hosts.json written there, the pairing
/// secrets in the lab credential folder named by MARTLET_LAB_CREDENTIALS). Before the desktop binds them to its network, the lab's
/// own admin desktop sets the household provider <c>authentik</c> up on host A only, as a public client (no client secret). The
/// desktop then adds it to host B by itself when it reads its hosts' sign-in settings (Devices › Friends' read, or the Sign-in
/// from outside window), and Save provider in that window saves it on both hosts (with a typed client secret, on both). The lab
/// writes what each host has (provider IDs and whether each keeps a client secret; never the secret) to signin-lab.json and stops
/// when its standard input closes, signin-lab.stop appears in the data directory, or after 20 minutes.
/// </summary>
internal static class SignInAccountLab
{
    internal const string HostA = "lab-host-a";
    internal const string HostB = "lab-host-b";
    internal const string Provider = "authentik";

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
        using var issuer = new SignInRehearsal.LabIssuer();
        await using var a = await SignInRehearsal.LabHost.StartAsync(HostA);
        await using var b = await SignInRehearsal.LabHost.StartAsync(HostB);
        LabHosts hosts = [a, b];
        foreach (var host in hosts) host.Server.UseSignInProviderHandler(issuer);

        // The lab's admin desktop pairs with both hosts while they are in no network and sets the household provider up on host A
        // only, as a public client: the desktop adds it to host B by itself.
        using var adminKey = NetworkKey.Create("lab-admin");
        var admin = new SignInRehearsal.LabDesktop(adminKey, "LAB-ADMIN");
        foreach (var host in hosts) await admin.PairByCodeAsync(host, token);
        await admin.ChangeAsync(HostA, new HouseholdProvider(Provider, "oidc", "Authentik (lab)", SignInRehearsal.LabIssuer.Issuer,
            SignInRehearsal.LabIssuer.ClientId, null, null).Change(null), token);

        // The desktop on the data directory: paired with both hosts by a code, its secrets in the lab folder, both in its hosts.json.
        var entries = new List<object>();
        foreach (var host in hosts)
        {
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), SignInLab.DesktopDevice, "LAB-DESKTOP", token);
            using (var lease = new SecretLease(secret))
            {
                var stored = new WindowsCredentialStore().WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
                if (stored != CredentialError.None) return Fail("Couldn't keep the lab pairing secret: " + stored);
            }
            entries.Add(new
            {
                pairing = new
                {
                    origin = pairing.Origin, hostId = pairing.HostId, spkiFingerprint = pairing.SpkiFingerprint, deviceId = pairing.DeviceId,
                    credentialId = pairing.CredentialId
                },
                method = "Agent"
            });
        }
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hosts.json"), JsonSerializer.Serialize(new { version = 1, hosts = entries }), token);

        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, mode = "account", hosts = hosts.Select(h => h.HostId).ToArray(), provider = Provider,
            providerOnHosts = hosts.OnHosts(Provider) }));
        var events = new List<string>();
        string[] seen = [];
        while (!token.IsCancellationRequested && !File.Exists(Path.Combine(dataDirectory, SignInLab.StopFile)))
        {
            var on = hosts.OnHosts(Provider);
            if (!on.SequenceEqual(seen))
            {
                events.Add($"{Provider} is on {(on.Length == 0 ? "no host" : string.Join(" and ", on))}");
                seen = on;
            }
            var status = new
            {
                ready = true, mode = "account", hosts = hosts.Select(h => h.HostId).ToArray(), provider = Provider,
                network = hosts.ToDictionary(h => h.HostId, h => h.Server.NetworkState.State),
                networkId = a.Server.NetworkState.NetworkId,
                providers = hosts.ToDictionary(h => h.HostId, h => Providers(h.SignInBytes)),
                providerOnHosts = on,
                clientSecretOnHosts = hosts.Where(h => Providers(h.SignInBytes).Any(p => p.Id == Provider && p.HasClientSecret)).Select(h => h.HostId).ToArray(),
                events = events.TakeLast(12).ToArray(), at = DateTimeOffset.UtcNow
            };
            var path = Path.Combine(dataDirectory, SignInLab.StatusFile);
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

    private sealed class LabHosts : List<SignInRehearsal.LabHost>
    {
        internal string[] OnHosts(string provider) => this.Where(h => Providers(h.SignInBytes).Any(p => p.Id == provider)).Select(h => h.HostId).ToArray();
    }

    internal sealed record LabProvider(string Id, string Kind, string Name, bool HasClientSecret);

    /// <summary>The providers in a host's signin.json: IDs, kinds, names and whether a client secret is kept (never the secret).</summary>
    internal static LabProvider[] Providers(byte[]? signIn)
    {
        if (signIn is null) return [];
        try
        {
            var root = JsonNode.Parse(signIn);
            return root?["providers"]?.AsArray().Select(p => new LabProvider((string?)p?["id"] ?? "", (string?)p?["kind"] ?? "", (string?)p?["name"] ?? "",
                p?["client_secret"] is not null)).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static int Fail(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ready = false, error = message }));
        return 1;
    }
}

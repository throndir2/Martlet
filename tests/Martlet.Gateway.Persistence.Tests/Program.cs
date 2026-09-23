using System.Security.Cryptography;
using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--clean-probe")
        {
            using var restored = new NativeAuthority(args[1], TimeProvider.System);
            Assert.Single(restored.Credentials.ListRegistrations());
            restored.Clean();
            return 0;
        }
        if (args.Length != 4 || args[0] != "--crash-probe" ||
            !Enum.TryParse<StoreStep>(args[2], out var step))
            return 2;
        var clock = new Clock();
        var armed = false;
        using var host = new NativeAuthority(args[1], clock, fault: observed =>
        {
            if (armed && observed == step) Environment.Exit(71);
        });
        armed = true;
        if (args[3] == "revoke")
            host.Credentials.RevokeDevice("fixture-device");
        else if (args[3] == "rotate")
            host.Credentials.Rotate(Assert.Single(host.Credentials.ListRegistrations()).CredentialId, TimeSpan.FromMinutes(1));
        else if (args[3] == "issue")
            host.Issue();
        else if (args[3] == "nonce")
        {
            var record = Assert.Single(host.Storage.Initial.Credentials);
            var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
            var canonical = GatewayRequestSigner.Canonical(host.Identity.HostId, record.CredentialId,
                "GET", "/martlet/v1/version", "voice", clock.GetUtcNow().ToUnixTimeSeconds(), nonce, SHA256.HashData([]));
            host.Credentials.Authenticate(new()
            {
                CredentialId = record.CredentialId, Nonce = nonce, Timestamp = clock.GetUtcNow(),
                Role = GatewayRole.Voice, CanonicalBytes = canonical,
                Signature = HMACSHA256.HashData(record.SigningKey, canonical)
            });
        }
        host.Clean();
        return 3;
    }
}

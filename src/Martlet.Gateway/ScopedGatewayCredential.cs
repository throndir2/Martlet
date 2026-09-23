using System.Security.Cryptography;
using Martlet.Core.Settings;

namespace Martlet.Gateway;

// Client-held secret only. This type cannot register, import or grant server authority.
public sealed class ScopedGatewayCredential : IDisposable
{
    private readonly SecretLease secret;
    private readonly object gate = new();
    public GatewayOrigin Origin { get; }
    public GatewayHostIdentity Identity { get; }
    public string CredentialId { get; }
    public string DeviceId { get; }
    public GatewayRole Role => GatewayRole.Voice;
    public GatewayCredentialLifetime Lifetime => new PairedDeviceLifetime();

    private ScopedGatewayCredential(GatewayOrigin origin, GatewayHostIdentity identity,
        string credentialId, string deviceId, ReadOnlySpan<char> value)
    {
        Origin = origin;
        Identity = identity;
        CredentialId = credentialId;
        DeviceId = deviceId;
        secret = new(value);
    }

    public static ScopedGatewayCredential Restore(GatewayOrigin origin, GatewayHostIdentity identity,
        GatewayProtocolVersion protocol, string credentialId, string deviceId, GatewayRole role,
        GatewayCredentialLifetime lifetime, ReadOnlySpan<char> secret)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(identity);
        GatewayClientJson.Protocol(protocol);
        identity.Validate();
        GatewayRules.Require(role == GatewayRole.Voice && lifetime is PairedDeviceLifetime, "auth.invalid");
        GatewayRules.Identifier(deviceId);
        GatewayRules.Require(Base64Url.TryDecode(credentialId, 16, out _), "auth.invalid");
        var valid = Base64Url.TryDecode(new string(secret), 32, out var bytes);
        try { GatewayRules.Require(valid, "auth.invalid"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return new(origin, identity, credentialId, deviceId, secret);
    }

    public void UseSecret(SecretAction action)
    {
        lock (gate) secret.Use(action);
    }

    internal GatewayRequestSigner CreateSigner(TimeProvider? clock = null)
    {
        GatewayRequestSigner? signer = null;
        UseSecret(value => signer = new(Identity, CredentialId, value, clock));
        return signer!;
    }

    public void Dispose()
    {
        lock (gate) secret.Dispose();
    }
    public override string ToString() => nameof(ScopedGatewayCredential);
}

using System.Text.Json.Serialization;

namespace Martlet.Gateway;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PairedDeviceLifetime), "paired")]
[JsonDerivedType(typeof(RetiringCredentialLifetime), "retiring")]
public abstract record GatewayCredentialLifetime
{
    private protected GatewayCredentialLifetime() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PairedDeviceLifetime : GatewayCredentialLifetime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetiringCredentialLifetime : GatewayCredentialLifetime
{
    public required DateTimeOffset ExpiresAt { get; init; }
}

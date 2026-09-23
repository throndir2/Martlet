using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Gateway.Trust;

[JsonConverter(typeof(GatewaySecretJsonConverter))]
public sealed class GatewaySecret : IDisposable
{
    public const int ByteLength = 32;
    private readonly object gate = new();
    private byte[]? bytes;

    private GatewaySecret(byte[] bytes) => this.bytes = bytes;

    public static GatewaySecret Import(ReadOnlySpan<byte> source)
    {
        if (source.Length != ByteLength)
            throw new ArgumentException("A gateway secret must contain exactly 32 bytes.", nameof(source));
        return new(source.ToArray());
    }

    public void CopyTo(Span<byte> destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(bytes is null, this);
            if (destination.Length != ByteLength)
                throw new ArgumentException("The destination must contain exactly 32 bytes.", nameof(destination));
            bytes.CopyTo(destination);
        }
    }

    internal static GatewaySecret Generate() => new(RandomNumberGenerator.GetBytes(ByteLength));

    internal byte[] Verifier()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(bytes is null, this);
            return SHA256.HashData(bytes);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
            bytes = null;
        }
    }

    public override string ToString() => "[gateway secret redacted]";
}

public sealed class GatewaySecretJsonConverter : JsonConverter<GatewaySecret>
{
    public override GatewaySecret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Gateway secrets require explicit bounded transport handling.");

    public override void Write(Utf8JsonWriter writer, GatewaySecret value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Gateway secrets cannot be serialized.");
}

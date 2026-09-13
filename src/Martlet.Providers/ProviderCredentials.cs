using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Martlet.Providers;

public interface IProviderCredentialSource
{
    // Expected OS-store failures must be translated to CredentialUnavailableException.
    ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken);
}

public sealed class CredentialUnavailableException() : Exception("The bound provider credential is unavailable.");

public sealed class BoundProviderCredential : IDisposable
{
    private string? secret;
    [JsonIgnore]
    public ProviderCredentialBinding Binding { get; }

    public BoundProviderCredential(ProviderCredentialBinding binding, string secret)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096 ||
            secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new CredentialUnavailableException();
        Binding = binding;
        this.secret = secret;
    }

    internal AuthenticationHeaderValue CreateAuthorization() =>
        new("Bearer", secret ?? throw new CredentialUnavailableException());

    public void Dispose() => secret = null;
    public override string ToString() => "[provider credential redacted]";
}

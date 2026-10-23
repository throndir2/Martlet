using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using Martlet.Core.Hardware;

namespace Martlet.HostClient;

/// <summary>What a host answered at <see cref="HardwareProof.Path"/>: the TLS key it showed on this connection, its proof,
/// the nonce this PC asked with, or why it couldn't be read.</summary>
public sealed record HostProofAnswer(string? Spki, HardwareProofDocument? Proof, string Nonce, string? Problem);

/// <summary>Asks a host to prove which computer it is (docs/NETWORK.md, "When a host is reinstalled"). The TLS key isn't
/// pinned here: a reinstalled host has a new one. The proof binds the key the host showed on this very connection, so a
/// proof from another computer or for another key never counts.</summary>
public static class HostComputerProof
{
    /// <summary>Asks <paramref name="origin"/> (dialing <paramref name="address"/>, "ip:port", when not null) for a proof.</summary>
    public static async Task<HostProofAnswer> AskAsync(string origin, string? address, TimeSpan timeout, CancellationToken token = default)
    {
        var nonce = HardwareProof.NewNonce();
        string? presented = null;
        var home = new Uri(origin);
        var endpoint = address is null ? new DnsEndPoint(home.Host.Trim('[', ']'), home.Port) : HostRoutes.Parse(address);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (_, cancel) => await HostRoutes.DialAsync(endpoint, cancel).ConfigureAwait(false),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    HostTransport.ValidateCertificate(certificate, chain, errors, key => { presented = key; return true; }, TimeProvider.System),
            },
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancel.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(new Uri(home, HardwareProof.Target(nonce)), HttpCompletionOption.ResponseHeadersRead,
                cancel.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(presented, null, nonce, "The host runs an older Martlet that can't prove which computer it is. Update it.");
            if (!response.IsSuccessStatusCode) return new(presented, null, nonce, $"The host answered HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > 16_384) return new(presented, null, nonce, "The host's answer is too long.");
            var proof = await response.Content.ReadFromJsonAsync<HardwareProofDocument>(HardwareProof.Json, cancel.Token).ConfigureAwait(false);
            return proof is null ? new(presented, null, nonce, "The host's answer is empty.") : new(presented, proof, nonce, null);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            if (token.IsCancellationRequested) throw;
            return new(presented, null, nonce, HostRoutes.Describe(error.InnerException ?? error));
        }
    }

    /// <summary>Checks an answer against the keys this PC saved for <paramref name="hostId"/>.</summary>
    public static HardwareCheck Check(HostProofAnswer answer, string hostId, IReadOnlyList<HardwareKey>? saved) =>
        answer is { Proof: { } proof, Spki: { } spki }
            ? HardwareProof.Check(proof, hostId, spki, answer.Nonce, saved)
            : HardwareCheck.Invalid;

    /// <summary>The keys an answer proves for <paramref name="hostId"/> (to save while this PC still trusts the host's TLS key).</summary>
    public static IReadOnlyList<HardwareKey> Keys(HostProofAnswer answer, string hostId) =>
        answer is { Proof: { } proof, Spki: { } spki } ? HardwareProof.Verified(proof, hostId, spki, answer.Nonce) : [];
}

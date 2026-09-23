using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Martlet.Gateway;

public sealed class GatewayClientException : Exception
{
    public GatewayFailure Failure { get; }

    internal GatewayClientException(string code) : base(GatewayFailures.Get(code).Summary) =>
        Failure = GatewayFailures.Get(code);
}

public sealed class PinnedGatewayClient : IDisposable
{
    private readonly GatewayOrigin origin;
    private readonly HttpClient client;
    private int disposed;

    private PinnedGatewayClient(GatewayOrigin origin, HttpMessageHandler handler)
    {
        this.origin = origin;
        client = new(handler, disposeHandler: true)
        {
            BaseAddress = new(origin.CanonicalOrigin + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public static PinnedGatewayClient Create(
        GatewayOrigin origin,
        GatewayHostIdentity identity,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        var effectiveClock = clock ?? TimeProvider.System;
        var effectiveCrypto = crypto ?? new SystemGatewayCrypto();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            Credentials = null,
            DefaultProxyCredentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.CertificateChainPolicy = new()
        {
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ValidateCertificate(certificate, chain, errors, identity, effectiveClock, effectiveCrypto);
        return new(origin, handler);
    }

    internal static PinnedGatewayClient CreateForFixture(
        GatewayOrigin origin,
        HttpMessageHandler handler) => new(origin, handler);

    public async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        GatewayRules.Require(request.RequestUri is { IsAbsoluteUri: true } uri &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.Equals(uri.Host, origin.Uri.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == origin.Port &&
            string.IsNullOrEmpty(uri.Fragment), "binding.unsafe");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayClientException("gateway.deadline");
        }
        catch (HttpRequestException)
        {
            throw new GatewayClientException("gateway.connection_failed");
        }
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            response.Dispose();
            throw new GatewayClientException("gateway.redirect_rejected");
        }
        return response;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            client.Dispose();
    }

    internal static bool ValidateCertificate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        GatewayHostIdentity identity,
        TimeProvider clock,
        IGatewayCrypto crypto)
    {
        if (certificate is null ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;
        X509Certificate2? owned = null;
        var certificate2 = certificate as X509Certificate2;
        if (certificate2 is null)
        {
            owned = new(certificate);
            certificate2 = owned;
        }
        try
        {
            var now = clock.GetUtcNow();
            if (now < certificate2.NotBefore.ToUniversalTime() ||
                now > certificate2.NotAfter.ToUniversalTime() ||
                !string.Equals(identity.SpkiFingerprint,
                    crypto.SpkiFingerprint(certificate2), StringComparison.Ordinal))
                return false;
            if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                return errors == SslPolicyErrors.None;
            if (chain is null)
                return false;
            const X509ChainStatusFlags allowed =
                X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
            return chain.ChainStatus.Length > 0 &&
                chain.ChainStatus.All(status => (status.Status & ~allowed) == 0);
        }
        finally
        {
            owned?.Dispose();
        }
    }
}

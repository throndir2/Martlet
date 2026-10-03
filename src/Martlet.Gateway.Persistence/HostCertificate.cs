using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

// Certificate mechanics adapted from PR #37, 18daa4acfa90d7c7dff52861c93800a2f63b0823.
internal static class HostCertificate
{
    internal static X509Certificate2 Create(DateTimeOffset now, ECDsa? retainedKey = null,
        IPAddress? privateAddress = null)
    {
        using var generated = retainedKey is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var key = retainedKey ?? generated!;
        var request = new CertificateRequest("CN=Martlet local gateway", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var sans = new SubjectAlternativeNameBuilder();
        sans.AddIpAddress(IPAddress.Loopback);
        sans.AddIpAddress(IPAddress.IPv6Loopback);
        if (privateAddress is not null)
        {
            _ = GatewayHostBinding.ExactPrivateAddress(new GatewayOrigin(
                privateAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"https://[{privateAddress}]:9443" : $"https://{privateAddress}:9443"));
            sans.AddIpAddress(privateAddress);
        }
        request.CertificateExtensions.Add(sans.Build());
        // Valid from a little earlier, so time held through a small clock step back (and a desktop slightly behind) accepts it.
        var notBefore = now - GatewayCredentialStore.MaximumClockStepBack;
        return request.CreateSelfSigned(notBefore, notBefore.AddDays(90));
    }

    internal static X509Certificate2 Load(byte[] bytes)
    {
        X509Certificate2? certificate = null;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(bytes, null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            using var privateKey = certificate.GetECDsaPrivateKey();
            using var publicKey = certificate.GetECDsaPublicKey();
            if (privateKey is null || publicKey is null || privateKey.KeySize != 256 ||
                privateKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                certificate.Subject != "CN=Martlet local gateway" ||
                !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) ||
                !privateKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()) ||
                certificate.NotAfter.ToUniversalTime() <= certificate.NotBefore.ToUniversalTime() ||
                certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime() > TimeSpan.FromDays(90))
                throw Error(GatewayPersistenceFailure.InvalidState);
            if (certificate.Extensions.Count != 4 ||
                certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is not { CertificateAuthority: false } ||
                certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault()?.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
                certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault()?.EnhancedKeyUsages is not { Count: 1 } usages ||
                usages[0].Value != "1.3.6.1.5.5.7.3.1" ||
                certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault() is not { } san ||
                !ValidAddresses(san.EnumerateIPAddresses().ToArray()) ||
                san.EnumerateDnsNames().Any())
                throw Error(GatewayPersistenceFailure.InvalidState);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = certificate.NotBefore.AddSeconds(1);
            if (!chain.Build(certificate))
                throw Error(GatewayPersistenceFailure.InvalidState);
            var challenge = RandomNumberGenerator.GetBytes(32);
            if (!publicKey.VerifyData(challenge, privateKey.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
                throw Error(GatewayPersistenceFailure.InvalidState);
            return certificate;
        }
        catch (CryptographicException)
        {
            certificate?.Dispose();
            throw Error(GatewayPersistenceFailure.InvalidState);
        }
        catch
        {
            certificate?.Dispose();
            throw;
        }
    }

    internal static IPAddress? PrivateAddress(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateIPAddresses().SingleOrDefault(address => !IPAddress.IsLoopback(address));

    private static bool ValidAddresses(IPAddress[] addresses)
    {
        if (addresses.Length is not (2 or 3) || addresses.Distinct().Count() != addresses.Length ||
            !addresses.Contains(IPAddress.Loopback) || !addresses.Contains(IPAddress.IPv6Loopback))
            return false;
        var extra = addresses.SingleOrDefault(address => !IPAddress.IsLoopback(address));
        if (addresses.Length == 2) return extra is null;
        if (extra is null) return false;
        try
        {
            _ = GatewayHostBinding.ExactPrivateAddress(new GatewayOrigin(
                extra.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"https://[{extra}]:9443" : $"https://{extra}:9443"));
            return true;
        }
        catch (GatewayProtocolException) { return false; }
    }
}

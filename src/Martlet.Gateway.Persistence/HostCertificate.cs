using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Gateway.Trust;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal static class HostCertificate
{
    internal static X509Certificate2 Create(Guid hostId, DateTimeOffset now, ECDsa? retainedKey = null)
    {
        using var generated = retainedKey is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var key = retainedKey ?? generated!;
        var request = new CertificateRequest($"CN=Martlet-{hostId:D}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var sans = new SubjectAlternativeNameBuilder();
        sans.AddIpAddress(IPAddress.Loopback);
        sans.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sans.Build());
        return request.CreateSelfSigned(now, now.AddDays(90));
    }

    internal static X509Certificate2 Load(byte[] bytes, Guid hostId)
    {
        X509Certificate2? certificate = null;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(bytes, null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            using var privateKey = certificate.GetECDsaPrivateKey();
            using var publicKey = certificate.GetECDsaPublicKey();
            if (privateKey is null || publicKey is null || privateKey.KeySize != 256 ||
                privateKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                certificate.Subject != $"CN=Martlet-{hostId:D}" ||
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
                !san.EnumerateIPAddresses().OrderBy(address => address.ToString()).SequenceEqual(
                    new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }.OrderBy(address => address.ToString())) ||
                san.EnumerateDnsNames().Any())
                throw Error(GatewayPersistenceFailure.InvalidState);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = certificate.NotBefore.AddMinutes(1);
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

    internal static GatewayHostIdentity Identity(Guid hostId, X509Certificate2 certificate)
    {
        using var key = certificate.GetECDsaPublicKey() ?? throw Error(GatewayPersistenceFailure.InvalidState);
        return new(hostId, Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())));
    }
}

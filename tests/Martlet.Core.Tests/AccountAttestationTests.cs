using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

public sealed class AccountAttestationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sam = new("5a6e0000-0000-4000-8000-00000000005a");
    private static readonly AccountAttestationLogin Password = new() { Kind = "martlet", Provider = "martlet", Subject = "sam" };

    private static X509Certificate2 HostCertificate(bool ecdsa)
    {
        using var ec = ecdsa ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        using var rsa = ecdsa ? null : RSA.Create(2048);
        var request = ec is not null
            ? new CertificateRequest("CN=Martlet local gateway", ec, HashAlgorithmName.SHA256)
            : new CertificateRequest("CN=Martlet local gateway", rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(90));
    }

    private static string Pin(X509Certificate2 certificate)
    {
        using var ec = certificate.GetECDsaPublicKey();
        using var rsa = ec is null ? certificate.GetRSAPublicKey() : null;
        var spki = ec?.ExportSubjectPublicKeyInfo() ?? rsa!.ExportSubjectPublicKeyInfo();
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(spki));
    }

    private static (NetworkRoster Roster, NetworkKey Founder) Household(string hostPin)
    {
        var founder = NetworkKey.Create("desktop-home");
        var roster = NetworkRoster.Found(founder, "HOME", Now).AddHost(founder, "home-host", "Home host", "https://192.168.1.20:9443", hostPin, Now);
        return (roster, founder);
    }

    [Theory]
    [InlineData(true, AccountAttestation.EcdsaP256)]
    [InlineData(false, AccountAttestation.RsaPss)]
    public void A_host_attestation_verifies_against_the_roster_pin_of_its_tls_key(bool ecdsa, string algorithm)
    {
        using var certificate = HostCertificate(ecdsa);
        var (roster, founder) = Household(Pin(certificate));
        using (founder)
        {
            var attestation = AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-laptop-ab12cd", Password,
                Now.AddMilliseconds(700), AccountAttestation.DefaultLifetime, certificate);
            Assert.Equal(algorithm, attestation.Algorithm);
            Assert.Equal(Now, attestation.IssuedAt);
            Assert.Equal(Now + AccountAttestation.DefaultLifetime, attestation.ExpiresAt);
            Assert.Equal(Pin(certificate), AccountAttestation.KeyFingerprint(attestation.HostKey));
            Assert.Equal(AccountAttestationCheck.Valid, attestation.Check(roster, Now.AddMinutes(1)));
            // A checker a little behind the host's clock still accepts it; past its expiry nobody does.
            Assert.Equal(AccountAttestationCheck.Valid, attestation.Check(roster, Now.AddMinutes(-4)));
            Assert.Equal(AccountAttestationCheck.NotYetValid, attestation.Check(roster, Now.AddMinutes(-6)));
            Assert.Equal(AccountAttestationCheck.Expired, attestation.Check(roster, attestation.ExpiresAt));

            // Every signed field counts.
            Assert.Equal(AccountAttestationCheck.BadSignature, (attestation with { AccountId = Guid.NewGuid() }).Check(roster, Now));
            Assert.Equal(AccountAttestationCheck.BadSignature, (attestation with { DeviceId = "desktop-other-zz9999" }).Check(roster, Now));
            Assert.Equal(AccountAttestationCheck.BadSignature, (attestation with { Login = Password with { Subject = "alex" } }).Check(roster, Now));
            Assert.Equal(AccountAttestationCheck.BadSignature, (attestation with { ExpiresAt = attestation.ExpiresAt.AddDays(1) }).Check(roster, Now));

            // The host must be an active host of the same network, pinned to this key.
            using var stranger = NetworkKey.Create("desktop-elsewhere");
            Assert.Equal(AccountAttestationCheck.OtherNetwork, attestation.Check(NetworkRoster.Found(stranger, "ELSEWHERE", Now), Now));
            Assert.Equal(AccountAttestationCheck.UnknownHost, attestation.Check(roster.Remove(founder, NetworkKinds.Host, "home-host", Now.AddSeconds(1)), Now));
            var moved = roster.AddHost(founder, "home-host", "Home host", "https://192.168.1.20:9443", "sha256:" + new string('0', 64), Now.AddSeconds(1));
            Assert.Equal(AccountAttestationCheck.KeyNotPinned, attestation.Check(moved, Now));
            using var other = HostCertificate(ecdsa);
            var forged = AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-laptop-ab12cd", Password, Now, AccountAttestation.DefaultLifetime, other);
            Assert.Equal(AccountAttestationCheck.KeyNotPinned, forged.Check(roster, Now));
            Assert.Equal(AccountAttestationCheck.BadSignature, (forged with { HostKey = attestation.HostKey }).Check(roster, Now));
        }
    }

    [Fact]
    public void The_text_form_is_one_short_ascii_line_that_round_trips()
    {
        using var certificate = HostCertificate(ecdsa: false);
        var (roster, founder) = Household(Pin(certificate));
        using (founder)
        {
            var login = new AccountAttestationLogin { Kind = "oidc", Provider = "authentik", Subject = "user-é-42" };
            var attestation = AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-laptop-ab12cd", login, Now,
                AccountAttestation.MaximumLifetime, certificate);
            var text = attestation.ToText();
            Assert.True(text.Length <= AccountAttestation.MaximumTextLength);
            Assert.All(text, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
            Assert.All(attestation.Write(), b => Assert.True(b is >= 0x20 and < 0x7f));
            var read = AccountAttestation.FromText(text);
            Assert.Equal(attestation, read with { Login = attestation.Login });
            Assert.Equal(login, read.Login);
            Assert.Equal(AccountAttestationCheck.Valid, read.Check(roster, Now.AddDays(29)));
            Assert.Equal(AccountAttestationCheck.Valid, AccountAttestation.Parse(attestation.Write()).Check(roster, Now));
            Assert.Throws<FormatException>(() => AccountAttestation.FromText(text + "!"));
            Assert.Throws<FormatException>(() => AccountAttestation.FromText(""));
        }
    }

    [Fact]
    public void Malformed_attestations_are_refused()
    {
        using var certificate = HostCertificate(ecdsa: true);
        var (roster, founder) = Household(Pin(certificate));
        using (founder)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-a", Password, Now,
                AccountAttestation.MaximumLifetime + TimeSpan.FromSeconds(1), certificate));
            Assert.Throws<ArgumentException>(() => AccountAttestation.Issue(roster.NetworkId, "home-host", Guid.Empty, "desktop-a", Password, Now,
                AccountAttestation.DefaultLifetime, certificate));
            Assert.Throws<ArgumentException>(() => AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-a",
                Password with { Subject = "Sam" }, Now, AccountAttestation.DefaultLifetime, certificate));
            Assert.Throws<ArgumentException>(() => AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-a",
                Password with { Kind = "windows" }, Now, AccountAttestation.DefaultLifetime, certificate));
            var good = AccountAttestation.Issue(roster.NetworkId, "home-host", Sam, "desktop-a", Password, Now, AccountAttestation.DefaultLifetime, certificate);
            Assert.Equal(AccountAttestationCheck.Malformed, (good with { Algorithm = "none" }).Check(roster, Now));
            Assert.Equal(AccountAttestationCheck.Malformed, (good with { ExpiresAt = good.IssuedAt }).Check(roster, Now));
            Assert.Equal(AccountAttestationCheck.BadSignature, (good with { Signature = "" }).Check(roster, Now));
            Assert.Throws<FormatException>(() => AccountAttestation.Parse("{\"schema_version\":2}"u8));
            Assert.Throws<FormatException>(() => AccountAttestation.Parse("not json"u8));
        }
    }
}

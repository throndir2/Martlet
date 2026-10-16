using System.IO;
using System.Security.Cryptography;
using System.Text;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class OwnerSshTests
{
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("fixture host key blob");
    private static readonly string Line = Convert.ToBase64String(Key);

    [Fact]
    public void Known_hosts_trust_the_same_key_for_the_same_computer_and_port_only()
    {
        var known = $"# comment\n192.168.50.9,nahida ssh-ed25519 {Line}\n[192.168.50.9]:9222 ecdsa-sha2-nistp256 {Line}\n" +
            $"@revoked [revoked.lan]:22 ssh-ed25519 {Line}\nother.lan ssh-ed25519 {Convert.ToBase64String(new byte[] { 1, 2, 3 })}\n";
        Assert.True(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@192.168.50.9"), Key));
        Assert.True(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@NAHIDA"), Key));
        Assert.True(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@192.168.50.9:9222"), Key));
        Assert.False(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@192.168.50.9:2222"), Key));
        Assert.False(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@other.lan"), Key));
        Assert.False(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@revoked.lan"), Key));
        Assert.False(OwnerSsh.Trusts(known, HostShellTarget.Parse("throndir@192.168.50.9"), [9, 9, 9]));
    }

    [Fact]
    public void Hashed_known_hosts_names_are_matched()
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        var hash = HMACSHA1.HashData(salt, Encoding.ASCII.GetBytes("[nahida.lan]:9222"));
        var known = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-ed25519 {Line}\n";
        Assert.True(OwnerSsh.Trusts(known, HostShellTarget.Parse("me@nahida.lan:9222"), Key));
        Assert.False(OwnerSsh.Trusts(known, HostShellTarget.Parse("me@nahida.lan"), Key));
    }

    [Fact]
    public void Only_keys_without_a_passphrase_are_used()
    {
        var folder = Directory.CreateTempSubdirectory("martlet-owner-ssh-").FullName;
        try
        {
            using (var open = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                File.WriteAllText(Path.Combine(folder, "id_ecdsa"), open.ExportECPrivateKeyPem());
            using (var locked = RSA.Create(2048))
                File.WriteAllText(Path.Combine(folder, "id_rsa"), locked.ExportEncryptedPkcs8PrivateKeyPem("passphrase",
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10000)));
            File.WriteAllText(Path.Combine(folder, "id_ed25519"), "not a key");
            var keys = OwnerSsh.Keys(folder);
            try { Assert.Equal(["id_ecdsa"], keys.Select(k => k.Name)); }
            finally { foreach (var (_, key) in keys) key.Dispose(); }
        }
        finally { Directory.Delete(folder, true); }
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AccountLinksTests : IDisposable
{
    private const string SidA = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string SidB = "S-1-5-21-1004336348-1177238915-682003330-1002";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 20, 0, 0, TimeSpan.Zero);
    private readonly NetworkKey a = NetworkKey.Create("desktop-a");
    private readonly NetworkKey b = NetworkKey.Create("desktop-b");
    private readonly X509Certificate2 host;
    private readonly NetworkRoster roster;

    public AccountLinksTests()
    {
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            host = new CertificateRequest("CN=Martlet local gateway", ec, HashAlgorithmName.SHA256).CreateSelfSigned(Now.AddDays(-2), Now.AddDays(90));
        using var key = host.GetECDsaPublicKey()!;
        roster = NetworkRoster.Found(a, "A", Now.AddDays(-1))
            .AddDesktop(a, b.DeviceId, "B", b.PublicKey, Now.AddDays(-1))
            .AddHost(a, "home-host", "Home host", "https://192.168.1.20:9443",
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), Now.AddDays(-1));
    }

    public void Dispose()
    {
        a.Dispose();
        b.Dispose();
        host.Dispose();
    }

    private AccountAttestation Attest(Guid account, string device, string user = "sam") => AccountAttestation.Issue(roster.NetworkId, "home-host",
        account, device, AccountLoginKey.ForPassword(user), Now, AccountAttestation.DefaultLifetime, host);

    // Sam's account, made on desktop-a with its Windows login.
    private AccountDirectory WithSam(out Account sam)
    {
        var directory = AccountDirectory.Empty.Put(a, Account.Create("Sam", AccountRoles.Member)
            .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows("desktop-a", SidA), null, Now))
            .WithDevice(AccountDevice.For("desktop-a", AccountLoginKey.ForWindows("desktop-a", SidA), Now)), Now);
        sam = directory.Accounts[0];
        return directory;
    }

    private int Rejected(AccountDirectory before, AccountDirectory after) => AccountDirectory.Accept(before, after, roster).Rejected;

    [Fact]
    public void Signing_in_as_someone_else_binds_this_pc_with_the_attestation()
    {
        var directory = WithSam(out var sam);
        var signedIn = AccountLinks.SignedIn(sam, "desktop-b", Attest(sam.Id, "desktop-b"), Now.AddMinutes(1));
        var device = signedIn.Device("desktop-b")!;
        Assert.Equal(AccountLoginKey.ForPassword("sam"), device.Login);
        Assert.NotNull(device.Attestation);
        Assert.NotNull(signedIn.Login(AccountLoginKey.ForPassword("sam")));
        Assert.Equal(0, Rejected(directory, directory.Put(b, signedIn, Now.AddMinutes(1))));
        Assert.Throws<InvalidOperationException>(() => AccountLinks.SignedIn(sam, "desktop-b", Attest(sam.Id, "desktop-c"), Now));
        Assert.Throws<InvalidOperationException>(() => AccountLinks.SignedIn(sam, "desktop-b", Attest(Guid.NewGuid(), "desktop-b"), Now));
    }

    [Fact]
    public void Linking_this_windows_login_keeps_the_proof_this_pc_has()
    {
        var directory = WithSam(out var sam);
        Assert.Throws<InvalidOperationException>(() => AccountLinks.WithWindowsLogin(sam, "desktop-b", SidB, "Sam on B", Now));
        var signedIn = directory.Put(b, AccountLinks.SignedIn(sam, "desktop-b", Attest(sam.Id, "desktop-b"), Now.AddMinutes(1)), Now.AddMinutes(1));
        // An hour later the attestation has expired, but the binding keeps the time it was made.
        var linked = AccountLinks.WithWindowsLogin(signedIn.Find(sam.Id)!, "desktop-b", SidB, "Sam on B", Now.AddHours(1));
        Assert.Equal(AccountLoginKey.ForWindows("desktop-b", SidB), linked.Device("desktop-b")!.Login);
        Assert.NotNull(linked.Login(AccountLoginKey.ForWindows("desktop-b", SidB)));
        Assert.Equal(0, Rejected(signedIn, signedIn.Put(b, linked, Now.AddHours(1))));
        Assert.Equal(0, Rejected(directory, signedIn.Put(b, linked, Now.AddHours(1))));
        // The PC that made the account links its own Windows login with no attestation.
        Assert.Null(AccountLinks.WithWindowsLogin(sam, "desktop-a", SidA, null, Now).Device("desktop-a")!.Attestation);
    }

    [Fact]
    public void Unlinking_the_only_login_is_refused_and_otherwise_signs_this_pc_out()
    {
        WithSam(out var sam);
        Assert.Throws<InvalidOperationException>(() => AccountLinks.WithoutWindowsLogin(sam, "desktop-a", SidA));
        var withPassword = sam.WithLogin(AccountLogin.For(AccountLoginKey.ForPassword("sam"), null, Now));
        var unlinked = AccountLinks.WithoutWindowsLogin(withPassword, "desktop-a", SidA);
        Assert.Null(unlinked.Login(AccountLoginKey.ForWindows("desktop-a", SidA)));
        Assert.Null(unlinked.Device("desktop-a"));
        Assert.Same(sam, AccountLinks.WithoutWindowsLogin(sam, "desktop-b", SidB));
    }

    [Fact]
    public void Merging_moves_logins_bindings_and_voices_and_a_fresh_pc_accepts_it()
    {
        var directory = WithSam(out var sam);
        var laptop = Account.Create("Sam (laptop)", AccountRoles.Admin)
            .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows("desktop-b", SidB), null, Now))
            .WithDevice(AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-b", SidB), Now))
            .WithVoice(new string('a', 32)).WithEmailHint(roster.NetworkId, "sam@example.net");
        directory = directory.Put(b, laptop, Now);
        laptop = directory.Find(laptop.Id)!;

        var merged = AccountLinks.Merge(directory, a, sam.Id, laptop.Id, Now.AddMinutes(1));
        var kept = merged.Find(sam.Id)!;
        Assert.Equal(sam.Id, merged.Find(laptop.Id)!.MergedInto);
        Assert.True(merged.Find(laptop.Id)!.Removed);
        Assert.NotNull(kept.Device("desktop-b"));
        Assert.NotNull(kept.Login(AccountLoginKey.ForWindows("desktop-b", SidB)));
        Assert.Equal([new string('a', 32)], kept.Voices);
        Assert.True(kept.HasEmail(roster.NetworkId, "SAM@example.net"));
        Assert.Equal(AccountRoles.Admin, kept.Role);
        Assert.Equal(0, Rejected(directory, merged));
        Assert.Equal(0, Rejected(AccountDirectory.Empty, merged));
    }

    [Fact]
    public void Only_a_pc_bound_to_the_kept_account_merges_and_never_the_owners_account_away()
    {
        var directory = WithSam(out var sam);
        var other = Account.Create("Alex", AccountRoles.Member) with { CreatedBy = "desktop-b" };
        directory = directory.Put(b, other, Now);
        Assert.Throws<InvalidOperationException>(() => AccountLinks.Merge(directory, b, sam.Id, other.Id, Now));
        Assert.Throws<InvalidOperationException>(() => AccountLinks.Merge(directory, a, sam.Id, sam.Id, Now));
        var owner = OwnerAccount.Create(roster, "Owner");
        directory = directory.Put(a, owner, Now);
        Assert.Throws<InvalidOperationException>(() => AccountLinks.Merge(directory, a, sam.Id, owner.Id, Now));
    }
}

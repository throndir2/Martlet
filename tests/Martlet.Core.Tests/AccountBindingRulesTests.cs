using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

public sealed class AccountBindingRulesTests : IDisposable
{
    private const string SidA = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string SidB = "S-1-5-21-1004336348-1177238915-682003330-1002";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 20, 0, 0, TimeSpan.Zero);
    private static readonly AccountLoginKey Password = AccountLoginKey.ForPassword("sam");
    private readonly NetworkKey a = NetworkKey.Create("desktop-a");
    private readonly NetworkKey b = NetworkKey.Create("desktop-b");
    private readonly X509Certificate2 host;
    private readonly X509Certificate2 stranger;
    private readonly NetworkRoster roster;

    public AccountBindingRulesTests()
    {
        host = Certificate();
        stranger = Certificate();
        roster = NetworkRoster.Found(a, "A", Now.AddDays(-1))
            .AddDesktop(a, b.DeviceId, "B", b.PublicKey, Now.AddDays(-1))
            .AddHost(a, "home-host", "Home host", "https://192.168.1.20:9443", Pin(host), Now.AddDays(-1));
    }

    public void Dispose()
    {
        a.Dispose();
        b.Dispose();
        host.Dispose();
        stranger.Dispose();
    }

    private static X509Certificate2 Certificate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=Martlet local gateway", ec, HashAlgorithmName.SHA256).CreateSelfSigned(Now.AddDays(-2), Now.AddDays(90));
    }

    private static string Pin(X509Certificate2 certificate)
    {
        using var ec = certificate.GetECDsaPublicKey()!;
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()));
    }

    private string Attest(Guid account, string device, X509Certificate2? by = null, DateTimeOffset? at = null) =>
        AccountAttestation.Issue(roster.NetworkId, "home-host", account, device, Password, at ?? Now, AccountAttestation.DefaultLifetime, by ?? host).ToText();

    // Sam's account, made on desktop-a.
    private Account Sam(AccountDirectory? into = null) => (into ?? AccountDirectory.Empty).Put(a, Account.Create("Sam", AccountRoles.Member)
        .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows("desktop-a", SidA), null, Now))
        .WithLogin(AccountLogin.For(AccountLoginKey.ForPassword("sam"), null, Now))
        .WithDevice(AccountDevice.For("desktop-a", AccountLoginKey.ForWindows("desktop-a", SidA), Now)), Now).Accounts[0];

    private static AccountDevice OnB(string? attestation, DateTimeOffset? at = null) =>
        AccountDevice.For("desktop-b", AccountLoginKey.ForPassword("sam"), at ?? Now.AddMinutes(1), attestation);

    private string? Refusal(Account? accepted, Account incoming, IReadOnlyCollection<Account>? merged = null) =>
        AccountDirectory.Refusal(accepted, incoming, roster, merged);

    [Fact]
    public void The_device_that_made_the_account_needs_no_attestation()
    {
        var sam = Sam();
        Assert.Null(Refusal(null, sam));
        Assert.Equal("creator", AccountBindingRules.Proof(null, sam, sam.Devices[0], roster));
    }

    [Fact]
    public void Another_device_binds_only_with_a_host_attestation_for_that_device_and_account()
    {
        var sam = Sam();
        Account Bound(AccountDevice device) => AccountDirectory.Empty.Put(b, sam.WithDevice(device), Now.AddMinutes(1)).Accounts[0];

        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, Bound(OnB(null))));
        var good = Bound(OnB(Attest(sam.Id, "desktop-b")));
        Assert.Null(Refusal(sam, good));
        Assert.Equal("attestation", AccountBindingRules.Proof(sam, good, good.Device("desktop-b")!, roster));
        // Another device's attestation, another account's, a key the roster doesn't pin, or one used after it expired.
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, Bound(OnB(Attest(sam.Id, "desktop-c")))));
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, Bound(OnB(Attest(Guid.NewGuid(), "desktop-b")))));
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, Bound(OnB(Attest(sam.Id, "desktop-b", stranger)))));
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, Bound(OnB(Attest(sam.Id, "desktop-b"), Now.AddHours(1)))));
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, Bound(OnB("not-an-attestation"))));
        // A fresh computer that never saw Sam checks every binding the same way.
        Assert.Null(Refusal(null, good));
        Assert.Equal(AccountBindingRules.Refused, Refusal(null, Bound(OnB(null))));
    }

    [Fact]
    public void A_binding_already_accepted_stays_valid_after_its_host_is_removed()
    {
        var sam = Sam();
        var bound = AccountDirectory.Empty.Put(b, sam.WithDevice(OnB(Attest(sam.Id, "desktop-b"))), Now.AddMinutes(1)).Accounts[0];
        var renamed = AccountDirectory.Empty.Put(a, bound with { Name = "Sam B." }, Now.AddMinutes(5)).Accounts[0];
        var withoutHost = roster.Remove(a, "host", "home-host", Now.AddMinutes(2));
        Assert.Null(AccountDirectory.Refusal(bound, renamed, withoutHost));
        Assert.Equal(AccountBindingRules.Refused, AccountDirectory.Refusal(null, renamed, withoutHost));
    }

    [Fact]
    public void A_merged_accounts_bindings_move_into_the_account_it_was_merged_into()
    {
        var sam = Sam();
        var old = Account.Create("Sam (laptop)", AccountRoles.Member) with { CreatedBy = "desktop-b" };
        var directory = AccountDirectory.Empty.Put(a, sam, Now).Put(b, old, Now);
        // desktop-a made Sam's account, so it may merge the laptop account in: desktop-b's binding moves with the laptop
        // account's attestation, and desktop-b is the laptop account's creator.
        var byA = directory.Remove(a, old.Id, Now.AddMinutes(1), mergedInto: sam.Id);
        var mergedByA = AccountBindingRules.MergedInto(sam.Id, byA);
        var moved = byA.Put(a, sam.WithDevice(OnB(Attest(old.Id, "desktop-b"))), Now.AddMinutes(1)).Find(sam.Id)!;
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, moved));
        Assert.Null(Refusal(sam, moved, mergedByA));
        Assert.Equal("merged", AccountBindingRules.Proof(sam, moved, moved.Device("desktop-b")!, roster, mergedByA));
        var created = byA.Put(a, sam.WithDevice(OnB(null)), Now.AddMinutes(1)).Find(sam.Id)!;
        Assert.Null(Refusal(sam, created, mergedByA));

        // desktop-b has no proof for Sam's account: merging its own account into Sam's carries nothing in.
        var byB = directory.Remove(b, old.Id, Now.AddMinutes(1), mergedInto: sam.Id);
        var takeover = byB.Put(b, sam.WithDevice(OnB(null)), Now.AddMinutes(1)).Find(sam.Id)!;
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, takeover, AccountBindingRules.MergedInto(sam.Id, byB)));
        var attested = byB.Put(b, sam.WithDevice(OnB(Attest(old.Id, "desktop-b"))), Now.AddMinutes(1)).Find(sam.Id)!;
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, attested, AccountBindingRules.MergedInto(sam.Id, byB)));
    }

    [Fact]
    public void Accept_finds_the_merge_in_the_same_batch()
    {
        var sam = Sam();
        var old = Account.Create("Sam (laptop)", AccountRoles.Member) with { CreatedBy = "desktop-b" };
        var directory = AccountDirectory.Empty.Put(a, sam, Now).Put(b, old, Now);
        var merged = directory.Remove(a, old.Id, Now.AddMinutes(1), mergedInto: sam.Id)
            .Put(a, sam.WithDevice(OnB(Attest(old.Id, "desktop-b"))), Now.AddMinutes(1));
        var result = AccountDirectory.Accept(directory, merged, roster);
        Assert.Equal(0, result.Rejected);
        Assert.NotNull(result.Directory.Find(sam.Id)!.Device("desktop-b"));
        Assert.Equal(sam.Id, result.Directory.Find(old.Id)!.MergedInto);
    }

    [Fact]
    public void Member_desktops_bind_themselves_to_the_owner_account_on_migration_but_not_to_another_account()
    {
        var owner = OwnerAccount.Create(roster, "Owner");
        var directory = AccountDirectory.Empty.Put(a, owner.WithDevice(AccountDevice.For("desktop-a", AccountLoginKey.ForWindows("desktop-a", SidA), Now)), Now);
        var accepted = directory.Accounts[0];
        var self = AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-b", SidB), Now.AddMinutes(1));
        var migrated = directory.Put(b, accepted.WithDevice(self), Now.AddMinutes(1)).Find(owner.Id)!;
        Assert.Null(Refusal(accepted, migrated));
        Assert.Equal("migration", AccountBindingRules.Proof(accepted, migrated, migrated.Device("desktop-b")!, roster));
        // With another device's Windows login, it is no migration.
        var foreign = AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-a", SidA), Now.AddMinutes(1));
        Assert.Equal(AccountBindingRules.Refused, Refusal(accepted, directory.Put(b, accepted.WithDevice(foreign), Now.AddMinutes(1)).Find(owner.Id)!));
        // A member desktop binding itself to someone else's account is refused.
        var sam = Sam();
        Assert.Equal(AccountBindingRules.Refused, Refusal(sam, AccountDirectory.Empty.Put(b, sam.WithDevice(self), Now.AddMinutes(1)).Accounts[0]));
    }

    [Fact]
    public void Migration_bindings_stay_valid_after_another_desktop_writes_the_owner_account()
    {
        using var c = NetworkKey.Create("desktop-c");
        var three = roster.AddDesktop(a, c.DeviceId, "C", c.PublicKey, Now.AddDays(-1));
        AccountDevice Self(string device, string sid) => AccountDevice.For(device, AccountLoginKey.ForWindows(device, sid), Now);
        var owner = OwnerAccount.Create(three, "Owner");
        // desktop-a made the owner account, desktop-b bound itself, then desktop-c bound itself and wrote the entry last.
        var directory = AccountDirectory.Empty.Put(a, owner.WithDevice(Self("desktop-a", SidA)), Now);
        directory = directory.Put(b, directory.Find(owner.Id)!.WithDevice(Self("desktop-b", SidB)), Now.AddMinutes(1));
        directory = directory.Put(c, directory.Find(owner.Id)!.WithDevice(Self("desktop-c", SidA)), Now.AddMinutes(2));
        var latest = directory.Find(owner.Id)!;

        // A computer that sees the entry for the first time (a new desktop or host) accepts every binding.
        Assert.Null(AccountDirectory.Refusal(null, latest, three));
        Assert.Equal("migration", AccountBindingRules.Proof(null, latest, latest.Device("desktop-b")!, three));
        Assert.Equal(0, AccountDirectory.Accept(AccountDirectory.Empty, directory, three).Rejected);

        // A desktop removed from the network loses its binding.
        var withoutB = three.Remove(a, "desktop", "desktop-b", Now.AddMinutes(3));
        Assert.Equal(AccountBindingRules.Refused, AccountDirectory.Refusal(null, latest, withoutB));
    }

    [Fact]
    public void The_founders_owner_account_is_the_household_owner_but_other_owner_accounts_are_not()
    {
        var self = AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-b", SidB), Now.AddMinutes(1));
        // An owner account that desktop-a (the founder) made after accounts came: a random ID, not OwnerAccount.IdFor.
        var founders = AccountDirectory.Empty.Put(a, Account.Create("Owner", AccountRoles.Owner), Now).Accounts[0];
        Assert.True(AccountBindingRules.IsHouseholdOwner(founders, roster));
        var joined = AccountDirectory.Empty.Put(b, founders.WithDevice(self), Now.AddMinutes(1)).Accounts[0];
        Assert.Null(Refusal(null, joined));
        Assert.Equal("migration", AccountBindingRules.Proof(null, joined, self, roster));

        // An owner account another desktop made, or a member account the founder made, gives no migration.
        var others = AccountDirectory.Empty.Put(b, Account.Create("Other", AccountRoles.Owner), Now).Accounts[0];
        Assert.False(AccountBindingRules.IsHouseholdOwner(others, roster));
        var member = Sam();
        Assert.False(AccountBindingRules.IsHouseholdOwner(member, roster));
        Assert.Equal(AccountBindingRules.Refused, Refusal(null, AccountDirectory.Empty.Put(b, member.WithDevice(self), Now.AddMinutes(1)).Accounts[0]));
    }

    [Fact]
    public void A_removed_account_has_no_bindings_to_check()
    {
        var sam = Sam();
        Assert.Null(Refusal(sam, AccountDirectory.Empty.Put(a, sam, Now).Remove(b, sam.Id, Now.AddMinutes(1)).Accounts[0]));
    }

    [Fact]
    public void Same_name_means_the_same_person_after_case_spaces_and_unicode_form()
    {
        Assert.True(SamePerson.SameName("John Mathieu Soreño", " john mathieu SOREÑO "));
        Assert.True(SamePerson.SameName("Soreño", "Soren\u0303o"));
        Assert.False(SamePerson.SameName("Sam", "Sam B."));
        Assert.False(SamePerson.SameName("Me", "me"));
        Assert.False(SamePerson.SameName(null, ""));
    }

    [Fact]
    public void A_desktop_binds_itself_to_an_account_with_the_name_of_one_it_made_and_keeps_it_after_the_merge()
    {
        var sam = Sam();
        var self = AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-b", SidB), Now.AddMinutes(1));
        var own = Account.Create("sam", AccountRoles.Member).WithLogin(AccountLogin.For(AccountLoginKey.ForWindows("desktop-b", SidB), null, Now));
        var directory = AccountDirectory.Empty.Put(a, sam, Now).Put(b, own, Now);
        own = directory.Find(own.Id)!;
        var bound = directory.Put(b, sam.WithDevice(self), Now.AddMinutes(1));
        var entry = bound.Find(sam.Id)!;

        Assert.Equal(SamePerson.Proof, AccountBindingRules.Proof(sam, entry, self, roster, AccountBindingRules.Related(entry, bound)));
        Assert.Equal(0, AccountDirectory.Accept(AccountDirectory.Empty, bound, roster).Rejected);
        // After the fold only the tombstone (its name kept) is left, and a host that sees the entry first still takes it.
        var folded = bound.Remove(b, own.Id, Now.AddMinutes(2), mergedInto: sam.Id);
        Assert.Equal(0, AccountDirectory.Accept(AccountDirectory.Empty, folded, roster).Rejected);

        // Another name, another device's Windows login, or a desktop removed from the network: refused.
        var other = AccountDirectory.Empty.Put(a, sam, Now).Put(b, own with { Name = "Robin" }, Now);
        Assert.Equal(1, AccountDirectory.Accept(AccountDirectory.Empty, other.Put(b, sam.WithDevice(self), Now.AddMinutes(1)), roster).Rejected);
        var foreign = AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-a", SidA), Now.AddMinutes(1));
        Assert.Equal(1, AccountDirectory.Accept(AccountDirectory.Empty, directory.Put(b, sam.WithDevice(foreign), Now.AddMinutes(1)), roster).Rejected);
        var withoutB = roster.Remove(a, "desktop", "desktop-b", Now.AddMinutes(3));
        Assert.Equal(AccountBindingRules.Refused, AccountBindingRules.Refusal(null, entry, withoutB, AccountBindingRules.Related(entry, bound)));
    }

    [Fact]
    public void Every_desktop_picks_the_same_keeper_the_household_owner_then_the_higher_role_then_the_first_windows_login()
    {
        Account Made(NetworkKey by, string role, DateTimeOffset at, string sid) => Account.Create("Sam", role)
            .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows(by.DeviceId, sid), null, at));
        var older = Made(b, AccountRoles.Member, Now.AddDays(-5), SidB);
        var newer = Made(a, AccountRoles.Member, Now, SidA);
        var directory = AccountDirectory.Empty.Put(b, older, Now).Put(a, newer, Now);
        Assert.Equal(older.Id, SamePerson.Keeper(directory, "SAM", roster)!.Id);

        var admin = Made(a, AccountRoles.Admin, Now.AddDays(1), SidA);
        Assert.Equal(admin.Id, SamePerson.Keeper(directory.Put(a, admin, Now), "Sam", roster)!.Id);

        var owner = OwnerAccount.Create(roster, "Sam").WithLogin(AccountLogin.For(AccountLoginKey.ForWindows("desktop-a", SidA), null, Now.AddDays(2)));
        Assert.Equal(owner.Id, SamePerson.Keeper(directory.Put(a, admin, Now).Put(a, owner, Now), "Sam", roster)!.Id);

        // An account without a Windows login is no Windows account of that name.
        var password = Account.Create("Sam", AccountRoles.Owner).WithLogin(AccountLogin.For(Password, "sam", Now.AddDays(-9)));
        Assert.Equal(older.Id, SamePerson.Keeper(directory.Put(b, password, Now), "Sam", roster)!.Id);
    }
}

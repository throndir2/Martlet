using System.Text;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

public sealed class AccountDirectoryTests : IDisposable
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly NetworkKey a = NetworkKey.Create("desktop-a");
    private readonly NetworkKey b = NetworkKey.Create("desktop-b");
    private readonly NetworkKey outsider = NetworkKey.Create("desktop-x");
    private readonly NetworkRoster roster;

    public AccountDirectoryTests() =>
        roster = NetworkRoster.Found(a, "A", Start).AddDesktop(a, b.DeviceId, "B", b.PublicKey, Start);

    public void Dispose()
    {
        a.Dispose();
        b.Dispose();
        outsider.Dispose();
    }

    private static Account Owner(string name = "Sam") => Account.Create(name, AccountRoles.Owner)
        .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows("desktop-a", Sid), "sam@example.net", Start))
        .WithDevice(AccountDevice.For("desktop-a", AccountLoginKey.ForWindows("desktop-a", Sid), Start));

    [Fact]
    public void An_entry_is_signed_by_its_writer_and_survives_a_round_trip()
    {
        var owner = Owner();
        var directory = AccountDirectory.Empty.Put(a, owner, Start);
        var entry = Assert.Single(directory.Accounts);
        Assert.Equal("desktop-a", entry.UpdatedBy);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), entry.Revision);
        Assert.True(AccountDirectory.Verify(entry, roster));

        var bytes = directory.Write();
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"accounts\":[", text, StringComparison.Ordinal);
        Assert.Contains("\"signed_in_at\"", text, StringComparison.Ordinal);
        Assert.Contains("\"memories_about_me\":false", text, StringComparison.Ordinal);
        var parsed = AccountDirectory.Parse(bytes);
        Assert.Equal(directory.Digest(), parsed.Digest());
        var read = Assert.Single(parsed.Accounts);
        Assert.True(AccountDirectory.Verify(read, roster));
        Assert.Equal(owner.Id, read.Id);
        Assert.Equal("account-" + owner.Id.ToString("N"), read.SpaceId);
        Assert.Equal(owner.Id, parsed.FindByLogin(AccountLoginKey.ForWindows("desktop-a", Sid))!.Id);
        Assert.Equal(owner.Id, Assert.Single(parsed.SignedInOn("desktop-a")).Id);

        // Any change after signing breaks the signature.
        Assert.False(AccountDirectory.Verify(read with { Name = "Mallory" }, roster));
        Assert.False(AccountDirectory.Verify(read with { Role = AccountRoles.Member }, roster));
        Assert.False(AccountDirectory.Verify(read with { Devices = [] }, roster));
        Assert.False(AccountDirectory.Verify(read with { UpdatedBy = "desktop-b" }, roster));
    }

    [Fact]
    public void Merge_is_commutative_associative_and_idempotent()
    {
        var sam = Owner();
        var alex = Account.Create("Alex", AccountRoles.Member);
        var kim = Account.Create("Kim", AccountRoles.Member);
        var one = AccountDirectory.Empty.Put(a, sam, Start).Put(a, alex, Start.AddSeconds(1));
        var two = one.Put(b, one.Find(sam.Id)! with { Name = "Samantha" }, Start.AddSeconds(5)).Put(b, kim, Start.AddSeconds(6));
        var three = one.Put(a, one.Find(alex.Id)! with { Role = AccountRoles.Admin }, Start.AddSeconds(3)).Remove(a, sam.Id, Start.AddSeconds(4));
        var four = AccountDirectory.Empty.Put(b, alex, Start.AddSeconds(2));
        AccountDirectory[] copies = [AccountDirectory.Empty, one, two, three, four];

        foreach (var x in copies)
        {
            Assert.Equal(x.Digest(), AccountDirectory.Merge(x, x).Digest());
            foreach (var y in copies)
            {
                Assert.Equal(AccountDirectory.Merge(x, y).Digest(), AccountDirectory.Merge(y, x).Digest());
                foreach (var z in copies)
                    Assert.Equal(AccountDirectory.Merge(AccountDirectory.Merge(x, y), z).Digest(),
                        AccountDirectory.Merge(x, AccountDirectory.Merge(y, z)).Digest());
            }
        }

        var all = copies.Aggregate(AccountDirectory.Merge);
        Assert.True(all.Find(sam.Id)!.Removed); // removed at 4 s, although Samantha was written later
        Assert.Equal(AccountRoles.Admin, all.Find(alex.Id)!.Role);
        Assert.Equal("Kim", all.Find(kim.Id)!.Name);
        Assert.Equal(2, all.Live.Count());
    }

    [Fact]
    public void A_removed_account_never_comes_back_and_keeps_no_logins()
    {
        var sam = Owner();
        var live = AccountDirectory.Empty.Put(a, sam, Start);
        var removed = live.Remove(b, sam.Id, Start.AddMinutes(1));
        var tombstone = removed.Find(sam.Id)!;
        Assert.True(tombstone.Removed);
        Assert.Equal("Sam", tombstone.Name);
        Assert.Empty(tombstone.Logins);
        Assert.Empty(tombstone.Devices);
        Assert.Null(removed.FindByLogin(AccountLoginKey.ForWindows("desktop-a", Sid)));
        Assert.Empty(removed.SignedInOn("desktop-a"));

        // An offline computer edits the account later: the removal still wins everywhere.
        var later = live.Put(a, live.Find(sam.Id)! with { Name = "Sam B" }, Start.AddHours(1));
        Assert.True(AccountDirectory.Merge(later, removed).Find(sam.Id)!.Removed);
        Assert.True(AccountDirectory.Merge(removed, later).Find(sam.Id)!.Removed);
        Assert.Throws<InvalidOperationException>(() => removed.Put(a, sam, Start.AddHours(2)));
        Assert.Same(removed, removed.Remove(a, sam.Id, Start.AddHours(2)));
    }

    [Fact]
    public void Revisions_move_forward_even_when_a_clock_is_behind()
    {
        var sam = Owner();
        var first = AccountDirectory.Empty.Put(a, sam, Start.AddHours(1));
        var behind = first.Put(b, first.Find(sam.Id)! with { Name = "Sammy" }, Start);
        Assert.True(behind.Find(sam.Id)!.Revision > first.Find(sam.Id)!.Revision);
        Assert.Equal("Sammy", AccountDirectory.Merge(first, behind).Find(sam.Id)!.Name);
    }

    [Fact]
    public void Only_entries_signed_by_an_active_member_desktop_are_accepted()
    {
        var sam = Owner();
        var fromA = AccountDirectory.Empty.Put(a, sam, Start);
        var accepted = AccountDirectory.Accept(AccountDirectory.Empty, fromA, roster);
        Assert.Equal(0, accepted.Rejected);
        Assert.Equal(fromA.Digest(), accepted.Directory.Digest());

        // Already known entries need no check; no roster refuses everything new.
        Assert.Equal(0, AccountDirectory.Accept(accepted.Directory, fromA, null).Rejected);
        Assert.Equal(1, AccountDirectory.Accept(AccountDirectory.Empty, fromA, null).Rejected);

        // A computer outside the network, a forged name and a removed member are refused; the copy keeps what it had.
        var intruder = AccountDirectory.Empty.Put(outsider, Account.Create("Eve", AccountRoles.Owner), Start);
        var forged = fromA with { Accounts = [fromA.Accounts[0] with { Name = "Eve", Revision = fromA.Accounts[0].Revision + 1 }] };
        var refused = AccountDirectory.Accept(accepted.Directory, AccountDirectory.Merge(intruder, forged), roster);
        Assert.Equal(2, refused.Rejected);
        Assert.Equal(accepted.Directory.Digest(), refused.Directory.Digest());

        var fromB = accepted.Directory.Put(b, accepted.Directory.Find(sam.Id)! with { Name = "Sam B" }, Start.AddMinutes(1));
        var withoutB = roster.Remove(a, NetworkKinds.Desktop, b.DeviceId, Start.AddMinutes(2));
        Assert.Equal(1, AccountDirectory.Accept(accepted.Directory, fromB, withoutB).Rejected);
        Assert.Equal("Sam B", AccountDirectory.Accept(accepted.Directory, fromB, roster).Directory.Find(sam.Id)!.Name);
    }

    [Fact]
    public void A_login_two_accounts_list_belongs_to_the_one_that_added_it_first()
    {
        var key = AccountLoginKey.ForPassword(" Sam@Example.net ");
        Assert.Equal("sam@example.net", key.Subject);
        var first = Account.Create("Sam", AccountRoles.Member).WithLogin(AccountLogin.For(key, "Sam", Start));
        var second = Account.Create("Sam too", AccountRoles.Member).WithLogin(AccountLogin.For(key, "Sam", Start.AddMinutes(1)));
        var directory = AccountDirectory.Merge(AccountDirectory.Empty.Put(b, second, Start), AccountDirectory.Empty.Put(a, first, Start));
        Assert.Equal(first.Id, directory.FindByLogin(key)!.Id);

        // Unlinking a login drops the device bindings that signed in with it.
        var bound = first.WithDevice(AccountDevice.For("desktop-b", key, Start, "opaque.attestation-text"));
        Assert.Single(bound.Devices);
        Assert.Empty(bound.WithoutLogin(key).Devices);
        Assert.Equal(Start, bound.WithLogin(AccountLogin.For(key, "New label", Start.AddDays(1))).Login(key)!.AddedAt);
    }

    [Theory]
    [InlineData("windows", "desktop-a", "not-a-sid")]
    [InlineData("martlet", "martlet", "Upper")]
    [InlineData("martlet", "other", "sam")]
    [InlineData("oidc", "bad provider", "subject")]
    [InlineData("oidc", "google", "")]
    [InlineData("password", "martlet", "sam")]
    public void Invalid_logins_are_refused(string kind, string provider, string subject)
    {
        var account = Account.Create("Sam", AccountRoles.Member)
            .WithLogin(new AccountLogin { Kind = kind, Provider = provider, Subject = subject, AddedAt = Start });
        Assert.Throws<ContractException>(() => AccountDirectory.Empty.Put(a, account, Start));
    }

    [Fact]
    public void Invalid_entries_and_documents_are_refused()
    {
        Assert.Throws<ContractException>(() => AccountDirectory.Empty.Put(a, Account.Create("  ", AccountRoles.Member), Start));
        Assert.Throws<ContractException>(() => AccountDirectory.Empty.Put(a, Account.Create("Sam", "guest"), Start));
        Assert.Throws<ContractException>(() => AccountDirectory.Empty.Put(a, Account.Create("Sam", AccountRoles.Member).WithVoice("V3"), Start));
        Assert.Throws<ContractException>(() => AccountDirectory.Empty.Put(a, Account.Create("Sam", AccountRoles.Member) with
        {
            Sharing = new() { Characters = "everyone" }
        }, Start));
        Assert.Throws<ContractException>(() => AccountDirectory.Empty.Put(a, Owner().WithDevice(
            AccountDevice.For("desktop-b", AccountLoginKey.ForWindows("desktop-a", Sid), Start, "line\nbreak")), Start));
        Assert.Equal(new string('x', 64), Account.Create(new string('x', 80), AccountRoles.Member).Name);

        var good = Encoding.UTF8.GetString(AccountDirectory.Empty.Put(a, Owner(), Start).Write());
        var newer = Assert.Throws<ContractException>(() => AccountDirectory.Parse(Encoding.UTF8.GetBytes(good.Replace("\"schema_version\":1", "\"schema_version\":2"))));
        Assert.Equal(ErrorCode.UnsupportedVersion, newer.Code);
        Assert.Throws<ContractException>(() => AccountDirectory.Parse(Encoding.UTF8.GetBytes(good.Replace("\"removed\":false", "\"removed\":false,\"password\":\"x\""))));
        Assert.Throws<ContractException>(() => AccountDirectory.Parse(Encoding.UTF8.GetBytes(good.Replace("\"signature\":\"", "\"signature\":\"x"))));
        Assert.Throws<ContractException>(() => AccountDirectory.Parse("{}"u8));
        Assert.Throws<ContractException>(() => AccountDirectory.Parse([]));
    }

    [Fact]
    public void Lists_are_written_in_one_order_whatever_order_they_were_given_in()
    {
        var id = Guid.NewGuid();
        var windows = AccountLogin.For(AccountLoginKey.ForWindows("desktop-a", Sid), null, Start);
        var password = AccountLogin.For(AccountLoginKey.ForPassword("sam"), null, Start);
        var one = Account.Create("Sam", AccountRoles.Member, id).WithLogin(windows).WithLogin(password)
            .WithVoice("ffffffffffffffffffffffffffffffff").WithVoice("00000000000000000000000000000000");
        var signed = AccountDirectory.Empty.Put(a, one, Start).Accounts[0];
        var shuffled = signed with { Logins = signed.Logins.Reverse().ToArray(), Voices = signed.Voices.Reverse().ToArray() };
        Assert.True(AccountDirectory.Verify(shuffled, roster));
        var x = AccountDirectory.Empty with { Accounts = [signed] };
        var y = AccountDirectory.Empty with { Accounts = [shuffled] };
        Assert.Equal(x.Digest(), y.Digest());
        Assert.Equal("martlet", AccountDirectory.Parse(y.Write()).Accounts[0].Logins[0].Kind);
    }

    [Theory]
    [InlineData("net-example", "8a58da66-fddc-5c5b-9282-fb119c84915f")]
    [InlineData("0f3a9c", "b9681809-5be4-5629-a701-10c7c878e749")]
    public void The_owner_account_ID_is_the_same_UUID_version_5_everywhere(string networkId, string expected) =>
        Assert.Equal(Guid.Parse(expected), OwnerAccount.IdFor(networkId));

    [Fact]
    public void The_migrated_owner_is_created_by_the_founder_whoever_writes_it()
    {
        var owner = OwnerAccount.Create(roster, "Owner");
        Assert.Equal(OwnerAccount.IdFor(roster.NetworkId), owner.Id);
        var byA = AccountDirectory.Empty.Put(a, owner, Start).Find(owner.Id)!;
        var byB = AccountDirectory.Empty.Put(b, owner, Start).Find(owner.Id)!;
        Assert.Equal("desktop-a", byA.CreatedBy);
        Assert.Equal("desktop-a", byB.CreatedBy);
        Assert.Equal("desktop-b", byB.UpdatedBy);
    }

    [Fact]
    public void The_creator_never_changes_and_a_change_of_it_is_refused()
    {
        var sam = Account.Create("Sam", AccountRoles.Member);
        var created = AccountDirectory.Empty.Put(b, sam, Start);
        Assert.Equal("desktop-b", created.Find(sam.Id)!.CreatedBy);

        // A later writer keeps the creator, even when it asks for another one.
        var edited = created.Put(a, created.Find(sam.Id)! with { Name = "Sam B", CreatedBy = "desktop-a" }, Start.AddMinutes(1));
        Assert.Equal("desktop-b", edited.Find(sam.Id)!.CreatedBy);
        Assert.Equal(0, AccountDirectory.Accept(created, edited, roster).Rejected);

        // An entry another desktop signed for the same account with itself as the creator is refused.
        var takeover = AccountDirectory.Empty.Put(a, sam with { Name = "Mine now" }, Start.AddMinutes(2));
        Assert.Equal("desktop-a", takeover.Find(sam.Id)!.CreatedBy);
        Assert.Equal("account.creator", AccountDirectory.Refusal(created.Find(sam.Id), takeover.Find(sam.Id)!, roster));
        var refused = AccountDirectory.Accept(created, takeover, roster);
        Assert.Equal(1, refused.Rejected);
        Assert.Equal("Sam", refused.Directory.Find(sam.Id)!.Name);

        Assert.Null(AccountDirectory.Refusal(null, takeover.Find(sam.Id)!, roster));
        Assert.Equal("account.no_network", AccountDirectory.Refusal(null, takeover.Find(sam.Id)!, null));
        var intruder = AccountDirectory.Empty.Put(outsider, sam, Start).Find(sam.Id)!;
        Assert.Equal("account.signer", AccountDirectory.Refusal(null, intruder, roster));
    }

    [Fact]
    public void Several_people_can_share_one_Windows_login()
    {
        var windows = AccountLoginKey.ForWindows("desktop-a", Sid);
        var sam = Account.Create("Sam", AccountRoles.Owner).WithLogin(AccountLogin.For(windows, null, Start));
        var alex = Account.Create("Alex", AccountRoles.Member).WithLogin(AccountLogin.For(windows, null, Start.AddMinutes(5)));
        var directory = AccountDirectory.Empty.Put(a, sam, Start).Put(a, alex, Start.AddMinutes(5));
        Assert.Equal(new[] { sam.Id, alex.Id }, directory.AccountsWith(windows).Select(x => x.Id));
        Assert.Equal(sam.Id, directory.FindByLogin(windows)!.Id);
    }

    [Fact]
    public void A_merged_account_names_the_account_it_went_into_and_e_mail_hints_are_hashes()
    {
        var network = roster.NetworkId;
        var hint = Account.EmailHintFor(network, " Sam@Example.net ");
        Assert.Equal(64, hint.Length);
        Assert.Equal(hint, Account.EmailHintFor(network, "sam@example.net"));
        Assert.NotEqual(hint, Account.EmailHintFor("other-network", "sam@example.net"));

        var sam = Account.Create("Sam", AccountRoles.Owner).WithEmailHint(network, "sam@example.net");
        var old = Account.Create("Sam (laptop)", AccountRoles.Member).WithEmailHint(network, "sam@example.net");
        var directory = AccountDirectory.Empty.Put(a, sam, Start).Put(a, old, Start);
        Assert.DoesNotContain("example.net", Encoding.UTF8.GetString(directory.Write()), StringComparison.Ordinal);
        Assert.True(directory.Find(sam.Id)!.HasEmail(network, "SAM@example.net"));

        var merged = directory.Remove(a, old.Id, Start.AddMinutes(1), mergedInto: sam.Id);
        var tombstone = AccountDirectory.Parse(merged.Write()).Find(old.Id)!;
        Assert.True(tombstone.Removed);
        Assert.Equal(sam.Id, tombstone.MergedInto);
        Assert.Empty(tombstone.EmailHints);
        Assert.True(AccountDirectory.Verify(tombstone, roster));
        Assert.False(AccountDirectory.Verify(tombstone with { MergedInto = Guid.NewGuid() }, roster));
        Assert.Throws<ContractException>(() => directory.Put(a, sam with { MergedInto = old.Id }, Start.AddMinutes(2)));
        Assert.Throws<ContractException>(() => directory.Put(a, sam with { EmailHints = ["sam@example.net"] }, Start.AddMinutes(2)));
    }
}

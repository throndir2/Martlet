using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop.Tests;

public sealed class AccountSecurityServiceTests : IDisposable
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string Device = "desktop-desk-a1b2c3";
    private readonly string data = Path.Combine(Path.GetTempPath(), "Martlet.Security." + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 host;
    private readonly NetworkRoster roster;
    private readonly NetworkKey key;
    private readonly AccountLoginKey windows = AccountLoginKey.ForWindows(Device, Sid);

    public AccountSecurityServiceTests()
    {
        Directory.CreateDirectory(data);
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            host = new CertificateRequest("CN=Martlet local gateway", ec, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90));
        using var pin = host.GetECDsaPublicKey()!;
        key = NetworkIdentity.LoadOrCreate(data, Device);
        roster = NetworkRoster.Found(key, "DESK", DateTimeOffset.UtcNow.AddDays(-1)).AddHost(key, "home-host", "Home host", "https://192.168.1.20:9443",
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(pin.ExportSubjectPublicKeyInfo())), DateTimeOffset.UtcNow.AddDays(-1));
        File.WriteAllBytes(Path.Combine(data, NetworkLocalState.FileName), new NetworkLocalState { Roster = roster }.Write());
    }

    public void Dispose()
    {
        key.Dispose();
        host.Dispose();
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    }

    private AccountAttestation Attest(Guid account) => AccountAttestation.Issue(roster.NetworkId, "home-host", account, Device,
        AccountLoginKey.ForPassword("alex"), DateTimeOffset.UtcNow, AccountAttestation.DefaultLifetime, host);

    // Sam: the household's owner account on this Windows login (made here). Alex: another member's account, made elsewhere.
    private (AccountSecurityService Service, Account Sam, Account Alex) Household()
    {
        var sam = OwnerAccount.Create(roster, "Sam").WithLogin(AccountLogin.For(windows, null, DateTimeOffset.UtcNow))
            .WithDevice(AccountDevice.For(Device, windows, DateTimeOffset.UtcNow));
        var alex = Account.Create("Alex", AccountRoles.Member).WithLogin(AccountLogin.For(AccountLoginKey.ForPassword("alex"), null, DateTimeOffset.UtcNow))
            with { CreatedBy = "desktop-elsewhere-x1y2z3" };
        var directory = AccountDirectory.Empty.Put(key, sam, DateTimeOffset.UtcNow).Put(key, alex, DateTimeOffset.UtcNow);
        AccountSession.SaveDirectory(data, directory);
        var session = AccountSession.Open(data, Sid, () => "Sam", Device, DateTimeOffset.UtcNow);
        return (new AccountSecurityService(session, data), directory.Find(sam.Id)!, directory.Find(alex.Id)!);
    }

    [Fact]
    public void The_windows_login_unlocks_its_own_account_until_it_asks_for_a_pin()
    {
        var (service, sam, _) = Household();
        Assert.Equal(sam.Id, service.Session.AccountId);
        Assert.True(service.UnlocksWithWindows(sam.Id));
        Assert.False(service.NeedsUnlock(sam.Id));
        service.Locks.SetPin(sam.Id, "2468", null);
        service.Locks.Change(sam.Id, l => l with { AskOnThisPc = true });
        Assert.True(service.NeedsUnlock(sam.Id));
    }

    [Fact]
    public void An_account_signed_in_with_a_prove_sign_in_needs_an_unlock_until_this_windows_login_is_linked()
    {
        var (service, _, alex) = Household();
        service.Session.SignIn(Attest(alex.Id), roster, DateTimeOffset.UtcNow);
        Assert.False(service.UnlocksWithWindows(alex.Id));
        Assert.True(service.NeedsUnlock(alex.Id));

        // The directory takes this PC's binding only with the attestation.
        Assert.Throws<InvalidOperationException>(() => service.ChangeDirectory((d, k, at) =>
            d.Put(k, d.Find(alex.Id)!.WithDevice(AccountDevice.For(Device, AccountLoginKey.ForPassword("alex"), at)), at)));
        var now = DateTimeOffset.UtcNow;
        service.ChangeDirectory((d, k, at) => d.Put(k, AccountLinks.SignedIn(d.Find(alex.Id)!, Device, Attest(alex.Id), now), at));
        Assert.NotNull(service.Session.Directory.Find(alex.Id)!.Device(Device)!.Attestation);

        service.Session.UseAtStart(alex.Id);
        service.LinkWindowsLogin();
        Assert.True(service.UnlocksWithWindows(alex.Id));
        Assert.Equal(windows, service.Session.State.ProofFor(alex.Id)!.Login);
        Assert.Equal(windows, service.Session.Directory.Find(alex.Id)!.Device(Device)!.Login);
    }

    [Fact]
    public void Signing_out_removes_the_binding_the_session_entry_and_the_unlock_file()
    {
        var (service, sam, alex) = Household();
        var now = DateTimeOffset.UtcNow;
        service.Session.SignIn(Attest(alex.Id), roster, now);
        service.ChangeDirectory((d, k, at) => d.Put(k, AccountLinks.SignedIn(d.Find(alex.Id)!, Device, Attest(alex.Id), now), at));
        service.Locks.RememberPassword(alex.Id, "correct horse battery", null);
        Assert.Throws<InvalidOperationException>(() => service.SignOut(sam.Id));

        service.SignOut(alex.Id);
        Assert.DoesNotContain(alex.Id, service.Session.State.SignedIn);
        Assert.Null(service.Session.Directory.Find(alex.Id)!.Device(Device));
        Assert.Empty(service.Locks.Load(alex.Id).Methods);
    }

    [Fact]
    public void On_exit_an_account_not_remembered_signs_out_and_encrypted_files_are_encrypted()
    {
        var (service, sam, alex) = Household();
        service.Session.SignIn(Attest(alex.Id), roster, DateTimeOffset.UtcNow);
        service.Locks.Change(alex.Id, l => l with { Remember = false });
        service.Session.UseAtStart(alex.Id);
        service.Locks.SetPin(sam.Id, "2468", null);
        var (_, fileKey) = service.Locks.TurnOnEncryption(sam.Id, "2468", null);
        service.SetFileKey(sam.Id, fileKey);
        File.WriteAllText(Path.Combine(AccountSession.FolderFor(data, sam.Id), "settings.json"), "{}");

        service.OnExit();
        Assert.Equal(sam.Id, service.Session.AccountId);
        Assert.DoesNotContain(alex.Id, service.Session.State.SignedIn);
        Assert.Equal((1, 0), AccountVault.Count(AccountSession.FolderFor(data, sam.Id)));
        Assert.Null(service.FileKey(sam.Id));
        // Without encryption on, nothing is encrypted.
        service.SetFileKey(alex.Id, AccountVault.NewKey());
        Assert.Null(service.Seal(alex.Id));
    }

    [Fact]
    public void Continue_as_replaces_the_new_account_first_start_made_while_it_is_unwritten()
    {
        var alexAttestation = default(AccountAttestation);
        var (service, _, alex) = Household();
        // A second Windows login on this PC: first start makes it a new pending account.
        const string other = "S-1-5-21-1004336348-1177238915-682003330-1002";
        var session = AccountSession.Open(Path.Combine(data, "second"), other, () => "Alex", Device, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.Combine(data, "second"));
        var made = session.AccountId;
        Assert.True(session.Current.Pending);
        alexAttestation = Attest(alex.Id);
        session.SignIn(alexAttestation, roster, DateTimeOffset.UtcNow, bindWith: AccountLoginKey.ForWindows(Device, other));
        session.UseAtStart(alex.Id, replaceNew: true);
        Assert.Equal(alex.Id, session.AccountId);
        Assert.DoesNotContain(made, session.State.SignedIn);
        Assert.False(Directory.Exists(AccountSession.FolderFor(Path.Combine(data, "second"), made)));
        Assert.Equal(AccountLoginKey.ForWindows(Device, other), session.State.ProofFor(alex.Id)!.Login);
        _ = service;
    }
}

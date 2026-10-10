using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Accounts;
using Martlet.Core.Network;

namespace Martlet.Desktop.Tests;

public sealed class AccountSessionTests : IDisposable
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string Device = "desktop-desk-a1b2c3";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string data = Path.Combine(Path.GetTempPath(), "Martlet.Accounts." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    }

    private AccountSession Open(string? name = "Sam Doe", string sid = Sid) =>
        AccountSession.Open(data, sid, () => name ?? throw new InvalidOperationException("The display name is read only at first start."), Device, Now);

    [Fact]
    public void FirstStartMakesAPendingOwnerNamedAfterTheWindowsLoginWithItsFolder()
    {
        var session = Open();

        Assert.Equal("Sam Doe", session.Current.Name);
        Assert.Equal(AccountRoles.Owner, session.Current.Role);
        Assert.True(session.Current.Pending);
        Assert.Equal(session.AccountId, session.OwnerId);
        Assert.Equal(Path.Combine(data, "accounts", session.AccountId.ToString("N")), session.AccountFolder);
        Assert.Equal(data, session.HouseholdFolder);
        Assert.True(Directory.Exists(session.AccountFolder));
        Assert.Equal("SD", session.Current.Initials);

        var again = Open(name: null);
        Assert.Equal(session.AccountId, again.AccountId);
    }

    [Fact]
    public void AnInstallInANetworkFromBeforeAccountsSignsInAsTheHouseholdsOwnerAccount()
    {
        using var key = NetworkKey.Create(Device);
        var roster = NetworkRoster.Found(key, "DESK", Now);
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, NetworkLocalState.FileName), new NetworkLocalState { Roster = roster }.Write());

        var session = Open();

        Assert.Equal(OwnerAccount.IdFor(roster.NetworkId), session.AccountId);
        Assert.Equal(AccountRoles.Owner, session.Current.Role);
    }

    [Fact]
    public void ALostSessionSignsInAgainToTheAccountsBoundToThisWindowsLogin()
    {
        using var key = NetworkKey.Create(Device);
        var login = AccountLoginKey.ForWindows(Device, Sid);
        var alex = Account.Create("Alex", AccountRoles.Member).WithLogin(AccountLogin.For(login, null, Now)).WithDevice(AccountDevice.For(Device, login, Now));
        var other = Account.Create("Robin", AccountRoles.Owner);
        Directory.CreateDirectory(data);
        AccountSession.SaveDirectory(data, AccountDirectory.Empty.Put(key, alex, Now).Put(key, other, Now));

        var session = Open(name: null);

        Assert.Equal(alex.Id, session.AccountId);
        Assert.False(session.Current.Pending);
        Assert.Equal(other.Id, session.OwnerId);
    }

    [Fact]
    public void AnotherWindowsLoginOnTheSameDataFolderGetsItsOwnAccount()
    {
        var first = Open();
        var second = Open(name: "Robin", sid: "S-1-5-21-1004336348-1177238915-682003330-1002");

        Assert.NotEqual(first.AccountId, second.AccountId);
        Assert.Equal("Robin", second.Current.Name);
    }

    [Fact]
    public async Task AddPersonSignsInAMemberAndASwitchRunsTheStepsBeforeItSavesAndTellsListeners()
    {
        var session = Open();
        var owner = session.AccountId;
        var changes = new List<AccountChange>();
        session.AddChangeStep((change, _) => { changes.Add(change); return Task.CompletedTask; });
        await session.StartAsync(CancellationToken.None);
        await session.StartAsync(CancellationToken.None);
        var raised = 0;
        session.AccountChanged += () => raised++;

        Assert.Throws<ArgumentException>(() => session.AddPerson("  ", Now));
        Assert.Throws<ArgumentException>(() => session.AddPerson("sam doe", Now));
        var alex = session.AddPerson("Alex", Now);
        await session.SwitchToAsync(alex, CancellationToken.None);

        Assert.Equal(2, changes.Count);
        Assert.Equal(new AccountChange(null, owner, null, AccountSession.FolderFor(data, owner), data, true), changes[0]);
        Assert.Equal(new AccountChange(owner, alex, AccountSession.FolderFor(data, owner), AccountSession.FolderFor(data, alex), data, true), changes[1]);
        Assert.Equal(1, raised);
        Assert.Equal(alex, session.AccountId);
        Assert.Equal(AccountRoles.Member, session.Current.Role);
        Assert.Equal(new[] { alex, owner }, session.SignedIn.Select(a => a.Id));
        Assert.Equal(alex, Open(name: null).AccountId);
    }

    [Fact]
    public async Task AFailingStepStopsTheSwitchOnTheOldAccount()
    {
        var session = Open();
        var owner = session.AccountId;
        var alex = session.AddPerson("Alex", Now);
        session.AddChangeStep((_, _) => throw new IOException("disk full"));
        var raised = false;
        session.AccountChanged += () => raised = true;

        await Assert.ThrowsAsync<IOException>(() => session.SwitchToAsync(alex, CancellationToken.None));

        Assert.Equal(owner, session.AccountId);
        Assert.False(raised);
        Assert.False(session.Switching);
        Assert.Equal(owner, Open(name: null).AccountId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SwitchToAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void ReconcileWritesPendingAccountsWithThisLoginAndHostsAcceptThem()
    {
        using var key = NetworkKey.Create(Device);
        var roster = NetworkRoster.Found(key, "DESK", Now);
        var session = Open();
        var alex = session.AddPerson("Alex", Now);

        var (directory, written) = AccountSession.Reconcile(AccountDirectory.Empty, session.State, roster, key, Device, Now);

        Assert.Equal(new[] { session.AccountId, alex }.Order(), written.Order());
        var login = AccountLoginKey.ForWindows(Device, Sid);
        foreach (var id in written)
        {
            var entry = directory.Find(id)!;
            Assert.NotNull(entry.Login(login));
            Assert.Equal(login, entry.Device(Device)!.Login);
        }
        Assert.Equal(AccountRoles.Owner, directory.Find(session.AccountId)!.Role);
        Assert.Equal(AccountRoles.Member, directory.Find(alex)!.Role);
        Assert.Equal(0, AccountDirectory.Accept(AccountDirectory.Empty, directory, roster).Rejected);

        session.Follow(directory, written);
        Assert.Empty(session.State.Pending);
        Assert.False(session.Current.Pending);
        Assert.Equal(directory.Digest(), AccountSession.LoadDirectory(data).Digest());
        var (same, none) = AccountSession.Reconcile(directory, session.State, roster, key, Device, Now.AddMinutes(1));
        Assert.Empty(none);
        Assert.Equal(directory.Digest(), same.Digest());
    }

    [Fact]
    public void AHouseholdKeepsOneOwnerAndThisPcsLoginComesBackAfterAConcurrentWriteDroppedIt()
    {
        using var key = NetworkKey.Create(Device);
        var roster = NetworkRoster.Found(key, "DESK", Now);
        var household = AccountDirectory.Empty.Put(key, Account.Create("Robin", AccountRoles.Owner), Now);
        var session = Open();

        var (directory, _) = AccountSession.Reconcile(household, session.State, roster, key, Device, Now);
        Assert.Equal(AccountRoles.Member, directory.Find(session.AccountId)!.Role);

        var dropped = directory.Put(key, directory.Find(session.AccountId)! with { Devices = [] }, Now.AddMinutes(1));
        var (healed, written) = AccountSession.Reconcile(dropped, session.State, roster, key, Device, Now.AddMinutes(2));
        Assert.Equal(new[] { session.AccountId }, written);
        Assert.NotNull(healed.Find(session.AccountId)!.Device(Device));
    }

    [Fact]
    public void TheMigratedOwnerIsWrittenWithTheFoundersNameAsItsCreatorSoEveryDesktopWritesTheSameEntry()
    {
        using var founder = NetworkKey.Create("desktop-founder");
        using var key = NetworkKey.Create(Device);
        var roster = NetworkRoster.Found(founder, "FOUNDER", Now);
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, NetworkLocalState.FileName), new NetworkLocalState { Roster = roster }.Write());
        var session = Open();

        var (directory, _) = AccountSession.Reconcile(AccountDirectory.Empty, session.State, roster, key, Device, Now);

        var owner = directory.Find(OwnerAccount.IdFor(roster.NetworkId))!;
        Assert.Equal("desktop-founder", owner.CreatedBy);
        Assert.Equal(AccountRoles.Owner, owner.Role);
    }
}

using Martlet.Core.Accounts;
using Martlet.Core.Contracts;

namespace Martlet.Core.Tests;

public sealed class AccountSessionStateTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASessionRoundTripsAndKeepsItsPendingAccounts()
    {
        var sam = Guid.NewGuid();
        var alex = Guid.NewGuid();
        var state = AccountSessionState.For(Sid, sam, new() { Id = sam, Name = "Sam", Role = AccountRoles.Owner, CreatedAt = Now })
            .Add(alex, new() { Id = alex, Name = "Alex", Role = AccountRoles.Member, CreatedAt = Now });

        var read = AccountSessionState.Parse(state.Write());

        Assert.Equal(sam, read.Current);
        Assert.Equal(new[] { sam, alex }, read.SignedIn);
        Assert.Equal("Alex", read.PendingFor(alex)!.Name);
        Assert.Equal(AccountRoles.Owner, read.PendingFor(sam)!.Role);
    }

    [Fact]
    public void UseMovesTheAccountFirstAndWrittenDropsOnlyThoseAccountsPendingEntries()
    {
        var sam = Guid.NewGuid();
        var alex = Guid.NewGuid();
        var state = AccountSessionState.For(Sid, sam, new() { Id = sam, Name = "Sam", Role = AccountRoles.Owner, CreatedAt = Now })
            .Add(alex, new() { Id = alex, Name = "Alex", Role = AccountRoles.Member, CreatedAt = Now });

        var used = state.Use(alex);
        Assert.Equal(alex, used.Current);
        Assert.Equal(new[] { alex, sam }, used.SignedIn);

        var written = used.Written([sam]);
        Assert.Null(written.PendingFor(sam));
        Assert.NotNull(written.PendingFor(alex));
        Assert.Same(written, written.Written([Guid.NewGuid()]));
    }

    [Fact]
    public void ASessionIsRefusedWhenItsAccountInUseIsNotSignedInOrItsWindowsLoginIsInvalid()
    {
        var sam = Guid.NewGuid();
        var state = AccountSessionState.For(Sid, sam);

        Assert.Throws<ContractException>(() => (state with { Current = Guid.NewGuid() }).Write());
        Assert.Throws<ContractException>(() => (state with { WindowsSid = "not-a-sid" }).Write());
        Assert.Throws<ContractException>(() => (state with
        {
            Pending = [new() { Id = Guid.NewGuid(), Name = "Nobody", Role = AccountRoles.Member, CreatedAt = Now }]
        }).Write());
        Assert.Throws<ContractException>(() => AccountSessionState.Parse("{\"schema_version\":2}"u8));
    }

    [Fact]
    public void SaveAndLoadUseAccountsSessionJsonInTheDataFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.AccountSession." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(AccountSessionState.Load(directory));
            var sam = Guid.NewGuid();
            AccountSessionState.For(Sid, sam).Save(directory);

            Assert.True(File.Exists(Path.Combine(directory, "accounts", "session.json")));
            Assert.Equal(sam, AccountSessionState.Load(directory)!.Current);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "accounts"), "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

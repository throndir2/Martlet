using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class GatewaySignInRolesTests : IDisposable
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 20, 0, 0, TimeSpan.Zero);
    private readonly NetworkKey admin = NetworkKey.Create("desktop-admin");
    private readonly NetworkKey member = NetworkKey.Create("desktop-member");

    public void Dispose()
    {
        admin.Dispose();
        member.Dispose();
    }

    private static Account Person(string name, string role, string device) => Account.Create(name, role)
        .WithLogin(AccountLogin.For(AccountLoginKey.ForWindows(device, Sid), null, Now))
        .WithDevice(AccountDevice.For(device, AccountLoginKey.ForWindows(device, Sid), Now));

    private static GatewaySignInChange Change(string action, Guid? account = null) => new() { Action = action, AccountId = account };

    [Fact]
    public void Before_the_household_has_accounts_every_member_desktop_may_change_sign_in()
    {
        Assert.True(GatewaySignInRoles.Allowed(AccountDirectory.Empty, "desktop-member", Change("allow")));
        Assert.True(GatewaySignInRoles.Allowed(AccountDirectory.Empty, "desktop-member", Change("owner")));
    }

    [Fact]
    public void Admins_change_everything_and_members_only_their_own_password_login()
    {
        var alex = Person("Alex", AccountRoles.Member, "desktop-member");
        var directory = AccountDirectory.Empty
            .Put(admin, Person("Sam", AccountRoles.Admin, "desktop-admin"), Now)
            .Put(member, alex, Now);
        Assert.True(GatewaySignInRoles.Allowed(directory, "desktop-admin", Change("allow")));
        Assert.True(GatewaySignInRoles.Allowed(directory, "desktop-admin", Change("account", alex.Id)));
        Assert.True(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("account", alex.Id)));
        Assert.True(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("remove-account-authenticator", alex.Id)));
        Assert.True(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("recovery-codes", alex.Id)));
        Assert.False(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("recovery-codes")));
        Assert.False(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("account", Guid.NewGuid())));
        Assert.False(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("allow")));
        Assert.False(GatewaySignInRoles.Allowed(directory, "desktop-member", Change("owner")));
        Assert.False(GatewaySignInRoles.Allowed(directory, "desktop-other", Change("account", alex.Id)));
    }
}

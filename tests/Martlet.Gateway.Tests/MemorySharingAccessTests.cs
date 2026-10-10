using Martlet.Core.Accounts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Sharing;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Host access follows sharing (docs/ACCOUNTS.md, "Sharing"): a character shared together is for every household device,
/// a character that remembers on its own but is private again only for its owner's devices, and giving facts is for every member
/// device.</summary>
public sealed class MemorySharingAccessTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private static Account Person(string name, string role, params string[] devices)
    {
        var account = Account.Create(name, role);
        foreach (var device in devices)
            account = account.WithDevice(AccountDevice.For(device, AccountLoginKey.ForWindows(device, Sid), DateTimeOffset.UtcNow));
        return account;
    }

    private static GatewayPrincipal Device(string id, GatewayAccess access = GatewayAccess.Full) => new()
    {
        HostId = "fixture-host", CredentialId = "credential-" + id, DeviceId = id, Role = GatewayRole.Voice,
        CredentialLifetime = new PairedDeviceLifetime(), Access = access
    };

    [Fact]
    public void Character_spaces_follow_how_their_owner_shares_them_and_giving_is_for_every_member()
    {
        var sam = Person("Sam", AccountRoles.Owner, "sams-pc");
        var alex = Person("Alex", AccountRoles.Member, "alexs-pc");
        var directory = new AccountDirectory { SchemaVersion = AccountDirectory.SchemaVersion1, Accounts = [sam, alex] };
        var companion = CompanionSettings.Create();
        companion = companion.AddCharacter("Aria", companion.ActivePersonaId, null, null, out var aria);
        var together = HouseholdSharing.Empty(sam.Id).WithMode(aria.Id, CharacterShareMode.Together, companion, LorebookLibrary.Create());
        var privateAgain = together.WithMode(aria.Id, null, companion, LorebookLibrary.Create());
        Assert.True(privateAgain.RemembersOnItsOwn(aria.Id));
        var space = MemorySpaceId.Character(aria.Id);

        bool May(string device, string where, GatewayMemorySpaceUse use, HouseholdSharing? sams, GatewayAccess access = GatewayAccess.Full) =>
            GatewayMemorySpaceRules.MayUse(directory, Device(device, access), where, use,
                sams is null ? null : new Dictionary<Guid, HouseholdSharing> { [sam.Id] = sams });

        foreach (var use in new[] { GatewayMemorySpaceUse.Read, GatewayMemorySpaceUse.Write })
        {
            Assert.True(May("alexs-pc", space, use, together));
            Assert.True(May("sams-pc", space, use, together));
            Assert.False(May("alexs-pc", space, use, privateAgain));
            Assert.True(May("sams-pc", space, use, privateAgain));
            // A character space nobody's sharing entry names: any device where someone of the household signed in, as before.
            Assert.True(May("alexs-pc", space, use, null));
            Assert.False(May("new-pc", space, use, together));
        }
        // Giving facts never reads the space: every member device may give to anyone's space, friends never.
        Assert.True(May("alexs-pc", sam.SpaceId, GatewayMemorySpaceUse.Give, null));
        Assert.True(May("new-pc", sam.SpaceId, GatewayMemorySpaceUse.Give, null));
        Assert.True(May("alexs-pc", space, GatewayMemorySpaceUse.Give, privateAgain));
        Assert.False(May("alexs-pc", sam.SpaceId, GatewayMemorySpaceUse.Read, null));
        Assert.False(May("alexs-pc", sam.SpaceId, GatewayMemorySpaceUse.Give, null, GatewayAccess.Friend));
    }
}

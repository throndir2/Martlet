using System.Net;
using System.Text.Json;
using Martlet.Core.Accounts;
using Martlet.Core.Network;
using Martlet.Core.Sync;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>The account directory decides which paired device may use which memory space (docs/ACCOUNTS.md, "Memory spaces";
/// GatewayMemorySpaceAccess.cs): an account's space only where that account is signed in, the household for every member, and the
/// old single document for the owner's devices and devices with no account yet. Friends never.</summary>
public sealed class MemorySpaceAccessTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string Spaces = "/martlet/v1/memories/spaces/";
    private static readonly string Character = MemorySpaceId.Character(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    private sealed class AccountStorage(byte[] initial) : IGatewayAccountStorage
    {
        public byte[]? Load() => initial;
        public void Save(byte[] bytes) { }
    }

    private static Account Person(string name, string role, Guid? id = null, params string[] devices)
    {
        var account = Account.Create(name, role, id);
        foreach (var device in devices)
            account = account.WithDevice(AccountDevice.For(device, AccountLoginKey.ForWindows(device, Sid), DateTimeOffset.UtcNow));
        return account;
    }

    private static GatewayPrincipal Device(string id, GatewayAccess access = GatewayAccess.Full) => new()
    {
        HostId = "fixture-host", CredentialId = "credential-" + id, DeviceId = id, Role = GatewayRole.Voice,
        CredentialLifetime = new PairedDeviceLifetime(), Access = access
    };

    private static SharedMemories OneFact(string content)
    {
        var id = Guid.NewGuid();
        return SharedMemories.Empty.With(new SharedMemory
        {
            Id = id, Revision = 1, UpdatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), UpdatedBy = "sams-pc",
            Fact = SharedMemories.Canonical(JsonSerializer.Serialize(new { id, content }))
        });
    }

    [Fact]
    public void Account_spaces_follow_device_bindings_and_the_household_is_for_every_member()
    {
        var sam = Person("Sam", AccountRoles.Owner, null, "sams-pc", "shared-pc");
        var alex = Person("Alex", AccountRoles.Member, null, "alexs-pc", "shared-pc");
        var directory = new AccountDirectory { SchemaVersion = AccountDirectory.SchemaVersion1, Accounts = [sam, alex] };
        foreach (var use in new[] { GatewayMemorySpaceUse.Read, GatewayMemorySpaceUse.Write })
        {
            bool May(string device, string space, GatewayAccess access = GatewayAccess.Full) =>
                GatewayMemorySpaceRules.MayUse(directory, Device(device, access), space, use);
            Assert.True(May("sams-pc", sam.SpaceId));
            Assert.False(May("alexs-pc", sam.SpaceId));
            Assert.True(May("shared-pc", sam.SpaceId));
            Assert.True(May("shared-pc", alex.SpaceId));
            Assert.False(May("new-pc", sam.SpaceId));
            Assert.False(May("sams-pc", MemorySpaceId.Account(Guid.NewGuid())));
            Assert.True(May("new-pc", MemorySpaceId.Household));
            // Until sharing records whose a character is, a device where someone of the household signed in may use its space.
            Assert.True(May("alexs-pc", Character));
            Assert.False(May("new-pc", Character));
            Assert.False(May("sams-pc", sam.SpaceId, GatewayAccess.Friend));
            Assert.False(May("sams-pc", MemorySpaceId.Household, GatewayAccess.Friend));
        }
        var removed = directory with { Accounts = [sam with { Removed = true }, alex] };
        Assert.False(GatewayMemorySpaceRules.MayUse(removed, Device("sams-pc"), sam.SpaceId, GatewayMemorySpaceUse.Read));
    }

    [Fact]
    public void The_old_document_is_for_the_owners_devices_and_devices_with_no_account_yet()
    {
        using var founder = NetworkKey.Create("sams-pc");
        var roster = NetworkRoster.Found(founder, "Home", DateTimeOffset.UtcNow);
        // The owner from before accounts has the ID every desktop computes from the network (role kept as member here, so the
        // ID alone decides), and Alex is someone else.
        var owner = Person("Sam", AccountRoles.Member, OwnerAccount.IdFor(roster.NetworkId), "sams-pc", "shared-pc");
        var alex = Person("Alex", AccountRoles.Member, null, "alexs-pc", "shared-pc");
        var directory = new AccountDirectory { SchemaVersion = AccountDirectory.SchemaVersion1, Accounts = [owner, alex] };

        Assert.True(GatewayMemorySpaceRules.MayUseOldDocument(directory, roster, Device("sams-pc")));
        Assert.True(GatewayMemorySpaceRules.MayUseOldDocument(directory, roster, Device("shared-pc")));
        Assert.True(GatewayMemorySpaceRules.MayUseOldDocument(directory, roster, Device("older-pc")));
        Assert.False(GatewayMemorySpaceRules.MayUseOldDocument(directory, roster, Device("alexs-pc")));
        Assert.False(GatewayMemorySpaceRules.MayUseOldDocument(directory, roster, Device("sams-pc", GatewayAccess.Friend)));
        // Without a network on this host, the directory's owner role decides.
        Assert.False(GatewayMemorySpaceRules.MayUseOldDocument(directory, null, Device("sams-pc")));
        var roles = directory with { Accounts = [owner with { Role = AccountRoles.Owner }, alex] };
        Assert.True(GatewayMemorySpaceRules.MayUseOldDocument(roles, null, Device("sams-pc")));
    }

    [Fact]
    public async Task A_host_lets_only_the_devices_of_an_account_use_its_space_and_the_old_document()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var key = NetworkKey.Create("sams-pc");
        var now = host.Clock.GetUtcNow();
        var sam = Person("Sam", AccountRoles.Owner, null, "sams-pc");
        var alex = Person("Alex", AccountRoles.Member, null, "alexs-pc");
        host.Server.AttachAccountStorage(new AccountStorage(AccountDirectory.Empty.Put(key, sam, now).Put(key, alex, now).Write()));
        var sams = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "sams-pc"), host.Clock);
        var alexs = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "alexs-pc"), host.Clock);
        var older = new GatewayRequestSigner(host.Identity, await host.PairAsync(deviceId: "older-pc"), host.Clock);

        using (var mine = await host.Client.SendAsync(host.SignedPost(Spaces + sam.SpaceId, GatewayRole.Voice, sams, OneFact("Sam's cat is Miso.").Write())))
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        foreach (var request in new[]
        {
            host.SignedGet(Spaces + sam.SpaceId, GatewayRole.Voice, alexs),
            host.SignedGet(Spaces + sam.SpaceId + "/digest", GatewayRole.Voice, older),
            host.SignedPost(Spaces + sam.SpaceId, GatewayRole.Voice, alexs, OneFact("Alex wrote here.").Write()),
            host.SignedGet("/martlet/v1/memories", GatewayRole.Voice, alexs),
            host.SignedGet("/martlet/v1/memories/digest", GatewayRole.Voice, alexs)
        })
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("memories.space_denied", await GatewayTestHost.FailureCode(response));
            }
        }
        foreach (var (device, path) in new[]
        {
            (alexs, Spaces + alex.SpaceId), (alexs, Spaces + MemorySpaceId.Household), (older, Spaces + MemorySpaceId.Household),
            (sams, "/martlet/v1/memories"), (older, "/martlet/v1/memories")
        })
        {
            using var response = await host.Client.SendAsync(host.SignedGet(path, GatewayRole.Voice, device));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}

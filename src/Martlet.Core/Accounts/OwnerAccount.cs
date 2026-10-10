using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Network;

namespace Martlet.Core.Accounts;

/// <summary>The owner account of a household that existed before accounts (docs/ACCOUNTS.md, "Migration"). Every desktop and
/// host computes the same owner account ID and creator from the network alone, so they agree without talking to each other.</summary>
public static class OwnerAccount
{
    /// <summary>The fixed Martlet namespace of <see cref="IdFor"/>.</summary>
    public static readonly Guid Namespace = new("e44b5fc7-4473-46d2-9844-25457de2bfa8");

    /// <summary>The owner account ID of the household <paramref name="networkId"/>: a UUID version 5 (RFC 9562) of
    /// <see cref="Namespace"/> and the name "martlet-household-owner\n&lt;network ID&gt;".</summary>
    public static Guid IdFor(string networkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(networkId);
        Span<byte> space = stackalloc byte[16];
        Namespace.TryWriteBytes(space, bigEndian: true, out _);
        var name = Encoding.UTF8.GetBytes("martlet-household-owner\n" + networkId);
        var input = new byte[16 + name.Length];
        space.CopyTo(input);
        name.CopyTo(input, 16);
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        var bytes = hash[..16];
        bytes[6] = (byte)(bytes[6] & 0x0F | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The owner account a desktop writes when it migrates <paramref name="roster"/>'s household: the ID from
    /// <see cref="IdFor"/> and, as its creator, the device that founded the network, so every desktop that migrates writes the
    /// same creator.</summary>
    public static Account Create(NetworkRoster roster, string name)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return Account.Create(name, AccountRoles.Owner, IdFor(roster.NetworkId)) with { CreatedBy = roster.Founder?.Id ?? "" };
    }
}

using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

public sealed class NetworkAddressTests
{
    private const string Spki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("home.example.net:9443", "home.example.net:9443")]
    [InlineData(" https://Home.Example.NET:9443/ ", "home.example.net:9443")]
    [InlineData("gpu-box.tailnet.ts.net:9443", "gpu-box.tailnet.ts.net:9443")]
    [InlineData("100.101.102.103:9443", "100.101.102.103:9443")]
    [InlineData("[fd7a:115c:a1e0::1]:9443", "[fd7a:115c:a1e0::1]:9443")]
    [InlineData("home.example.net", null)]
    [InlineData("home.example.net:0", null)]
    [InlineData("home.example.net:70000", null)]
    [InlineData("fd7a::1:9443", null)]
    [InlineData("-bad.example.net:9443", null)]
    [InlineData("user@home.example.net:9443", null)]
    [InlineData("home.example.net:9443/path", null)]
    [InlineData("1.2.3:9443", null)]
    public void Outside_addresses_are_normalized_or_refused(string text, string? expected) =>
        Assert.Equal(expected, NetworkRoster.NormalizeAddress(text));

    [Fact]
    public void Host_outside_addresses_are_signed_kept_on_address_changes_and_accepted_by_other_members()
    {
        using var founder = NetworkKey.Create("desktop-a");
        var roster = NetworkRoster.Found(founder, "A", Now)
            .AddHost(founder, "gpu-box", "GPU box", "https://192.168.1.20:9443", Spki, Now);
        var withoutAddresses = roster.Write();
        Assert.DoesNotContain("\"addresses\"", System.Text.Encoding.UTF8.GetString(withoutAddresses));

        var set = roster.SetHostAddresses(founder, "gpu-box", ["Home.Example.net:9443", "100.101.102.103:9443"], Now.AddSeconds(1));
        Assert.Equal(["home.example.net:9443", "100.101.102.103:9443"], set.Host("gpu-box")!.Addresses);
        var parsed = NetworkRoster.Parse(set.Write());
        Assert.Equal(set.Host("gpu-box")!.Addresses, parsed.Host("gpu-box")!.Addresses);
        // A member that knew the old roster accepts the signed change.
        var accepted = NetworkRoster.Accept(roster, parsed);
        Assert.Equal(0, accepted.Rejected);
        Assert.Equal(2, accepted.Roster.Host("gpu-box")!.Addresses!.Count);

        // The host moving to another home address keeps its outside addresses.
        var moved = set.AddHost(founder, "gpu-box", "GPU box", "https://192.168.1.21:9443", Spki, Now.AddSeconds(2));
        Assert.Equal(set.Host("gpu-box")!.Addresses, moved.Host("gpu-box")!.Addresses);

        // Tampering with the addresses breaks the signature, so the entry is refused.
        var forged = parsed with
        {
            Members = parsed.Members.Select(m => m.IsHost ? m with { Addresses = ["evil.example.net:443"] } : m).ToArray()
        };
        var merge = NetworkRoster.Accept(roster, forged);
        Assert.Equal(1, merge.Rejected);
        Assert.Null(merge.Roster.Host("gpu-box")!.Addresses);

        var cleared = set.SetHostAddresses(founder, "gpu-box", [], Now.AddSeconds(3));
        Assert.Null(cleared.Host("gpu-box")!.Addresses);
        Assert.Throws<ContractException>(() => set.SetHostAddresses(founder, "gpu-box", ["not an address"], Now.AddSeconds(4)));
        Assert.Throws<ContractException>(() => set.SetHostAddresses(founder, "gpu-box",
            ["a.example:1", "b.example:1", "c.example:1", "d.example:1", "e.example:1"], Now.AddSeconds(4)));
    }
}

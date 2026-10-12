using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class LocalHostServiceHolderTests
{
    [Theory]
    [InlineData("192.168.1.20:9443 |", "192.168.1.20:9443", false)]
    [InlineData("192.168.1.20:9443 |<no value>", "192.168.1.20:9443", false)]
    [InlineData(":9443 |<no value>", null, true)]
    [InlineData("192.168.1.20:9443 |192.168.1.20:9443", "192.168.1.20:9443", false)]
    [InlineData(":9443 |192.168.1.20:9443", "192.168.1.20:9443", true)]
    [InlineData("0.0.0.0:9443 |192.168.1.20:9443\n", "192.168.1.20:9443", true)]
    [InlineData(":9443 |", null, true)]
    [InlineData("|", null, false)]
    [InlineData("", null, false)]
    public void The_holders_home_address_is_read_from_its_binding_or_its_label(string output, string? home, bool everywhere)
    {
        var (endpoint, wide) = LocalHostService.ParseHolder(output);
        Assert.Equal(home, endpoint?.ToString());
        Assert.Equal(everywhere, wide);
    }
}

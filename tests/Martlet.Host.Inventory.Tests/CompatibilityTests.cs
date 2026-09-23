using System.Security.Cryptography;
using System.Text;

namespace Martlet.Host.Inventory.Tests;

public sealed class CompatibilityTests
{
    [Theory]
    [InlineData("inventory", 0, "ffd614da37275aaf31b7020138a85785e31a70ee5f2613d395987d8bad15daff")]
    [InlineData("prerequisites", 2, "7092253570ab0ce372b394f2df7a216d3b291b5daccb08d91f547a6aef776cbe")]
    [InlineData("missing-tools", 1, "919a72765929364afe6079eb7277a54bff810fb7062d268d4eaf8fb6eb2a643e")]
    [InlineData("unsupported", 3, "1b349da5fec9fa4d38613c5f5b04531be691fb7dc3c849b5e95a35932cacebde")]
    public void Historical_serializer_output_is_preserved(string name, int exit, string sha256)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".v1.json"));
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var report = HostJson.Deserialize(bytes);
        Assert.Equal(exit, report.ExitCode);
        Assert.Equal(Provenance.AuthoredFixture, report.Provenance);
        Assert.False(report.DeploymentQualified);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(HostJson.Serialize(report)));
        Assert.Equal(HostJson.Serialize(ReportFixtures.Create(name)), HostJson.Serialize(report));
        Assert.Contains("UNAUTHENTICATED", ReportFormatter.Human(report));
    }
}

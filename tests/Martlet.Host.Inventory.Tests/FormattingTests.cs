using System.Text;

namespace Martlet.Host.Inventory.Tests;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(Provenance.AuthoredFixture)]
    [InlineData(Provenance.LiveLocal)]
    public void Imported_provenance_is_retained_but_never_authenticated(Provenance claim)
    {
        var original = ReportFixtures.Create(provenance: claim);
        var report = HostJson.Deserialize(Encoding.UTF8.GetBytes(HostJson.Serialize(original)));
        Assert.Equal(claim, report.Provenance);
        Assert.False(report.DeploymentQualified);
        Assert.Equal(original.ScopeNotice, report.ScopeNotice);
        var human = ReportFormatter.Human(report);
        Assert.StartsWith("UNAUTHENTICATED REPORT METADATA", human);
        Assert.Contains("not proof of collection", human);
        Assert.Contains("Reported scope notice: " + original.ScopeNotice, human);
        Assert.Contains("Claimed source:", human);
        Assert.Contains("actual daemon access UNKNOWN", human);
        Assert.Contains("exact qualified version UNKNOWN", human);
        Assert.Contains("NotRun [HOST_NOT_RUN]", human);
    }

    [Fact]
    public void Pure_assembly_has_no_product_or_execution_dependency()
    {
        var assembly = typeof(HostReport).Assembly;
        Assert.Null(assembly.EntryPoint);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Martlet.", StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.GetTypes(), t => t.Name is "HostEvaluator" or "DoctorCommand" or "LocalHostSource" or "HostSnapshot");
        Assert.Throws<InvalidDataException>(() => EvidenceSources.Description((ProbeId)999));
        Assert.Throws<InvalidDataException>(() => RemedyCatalog.For((ProbeId)999, FindingCode.HOST_NOT_RUN));
        Assert.Throws<InvalidDataException>(() => RemedyCatalog.Get((FindingCode)999));
    }
}

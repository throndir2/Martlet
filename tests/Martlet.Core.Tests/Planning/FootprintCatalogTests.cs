using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests.Planning;

public sealed class FootprintCatalogTests
{
    private static IReadOnlyList<ComponentOption> Options => FootprintCatalog.Default.Options;

    [Fact]
    public void EveryComponentHasALocalOrHostedOption()
    {
        foreach (var component in Enum.GetValues<PlanComponent>())
            Assert.NotEmpty(FootprintCatalog.Default.For(component));
    }

    [Fact]
    public void BoundsAreNonNegativeAndPeakCoversSteady()
    {
        foreach (var option in Options)
        {
            var (steady, peak) = (option.Steady, option.Peak);
            Assert.True(new[] { steady.VramGb, steady.RamGb, steady.CpuThreads, steady.DiskGb, peak.VramGb, peak.RamGb, peak.CpuThreads,
                peak.DiskGb, option.ContextGb, option.IdleCpuThreads, option.MinGpuGb }.All(v => v >= 0 && double.IsFinite(v)), option.Id);
            Assert.True(peak.VramGb >= steady.VramGb && peak.RamGb >= steady.RamGb && peak.CpuThreads >= steady.CpuThreads &&
                peak.DiskGb >= steady.DiskGb, option.Id);
            Assert.True(option.IdleCpuThreads <= steady.CpuThreads || steady.CpuThreads == 0, option.Id);
        }
    }

    [Fact]
    public void LocalOptionsSayWhereTheirNumbersComeFromAndHostedOnesUseNothing()
    {
        foreach (var option in Options)
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Source), option.Id);
            if (!option.IsLocal) Assert.Equal(ResourceUse.Zero, option.Reserve);
            else Assert.True(option.Peak.DiskGb > 0 || option.Id is "loudness-lipsync" or "windows-speech" or "macos-speech", option.Id);
        }
    }

    [Fact]
    public void GraphicsCardOptionsNeedACardAndProcessorOptionsUseNoGraphicsMemory()
    {
        foreach (var option in Options.Where(o => o.IsLocal))
        {
            if (option.Gpu == GpuRequirement.None) Assert.Equal(0, option.GpuGb);
            else Assert.True(option.Peak.VramGb > 0, option.Id);
            if (option.Gpu == GpuRequirement.Nvidia) Assert.True(option.MinGpuGb > 0, option.Id);
        }
    }

    [Fact]
    public void EveryVoiceEngineAndParakeetModelHasAnOption()
    {
        foreach (var engine in SpeechEngines.All)
            Assert.Contains(Options, o => o.Component == PlanComponent.Voice && o.HostRoleKind == engine.HostRoleKind && o.ModelId == engine.DefaultModel);
        foreach (var model in new[] { "parakeet-tdt-110m-en", "parakeet-tdt-0.6b-v2-int8", "parakeet-tdt-0.6b-v3" })
            Assert.Contains(Options, o => o.Component == PlanComponent.Listening && o.ModelId == model);
    }

    [Fact]
    public void ParakeetDiskMatchesThePinnedDownloads()
    {
        // ParakeetModels' pinned file sizes: 477,410,591, 661,190,513 and 670,478,772 bytes.
        Assert.Equal(0.48, FootprintCatalog.Default.Find("parakeet-tdt-110m-en-cpu")!.Peak.DiskGb, 2);
        Assert.Equal(0.66, FootprintCatalog.Default.Find("parakeet-tdt-0.6b-v2-cpu")!.Peak.DiskGb, 2);
        Assert.Equal(0.67, FootprintCatalog.Default.Find("parakeet-tdt-0.6b-v3-cpu")!.Peak.DiskGb, 2);
    }
}

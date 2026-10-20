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
            else Assert.True(option.Peak.DiskGb > 0 || option.UsesThinking || option.Id is "loudness-lipsync" or "windows-speech" or "reading:windows-ocr",
                option.Id);
        }
    }

    [Fact]
    public void AnOptionThatUsesThinkingIsAnImageOrAudioModelAndTakesNothing()
    {
        var itself = Options.Where(o => o.UsesThinking).ToList();
        Assert.Equal(["hearing:thinking", "vision:thinking"], itself.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.All(itself, o => Assert.Equal(ResourceUse.Zero, o.Reserve));
        Assert.All(itself, o => Assert.Equal(GpuRequirement.None, o.Gpu));
        Assert.StartsWith("Runs with Thinking", FootprintCatalog.Default.Find("vision:thinking")!.WhereItRuns, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHostRoleThatDoesAPartHasAnOption()
    {
        // The audit in docs/RECOMMENDED_SETUPS.md#every-way-to-extend-martlet: every host role in deploy/host/roles takes compute,
        // so the planner knows its footprint.
        var roles = Directory.GetDirectories(Path.Combine(RepositoryRoot(), "deploy", "host", "roles")).Select(Path.GetFileName).ToList();
        Assert.NotEmpty(roles);
        // A Thinking pool model on an extra graphics card (deep-thinking-2...) has the deep-thinking role's footprint. The
        // deep-thinking-app role runs nothing of Martlet's: its option is the model the owner's own app serves, made when planning.
        foreach (var role in roles.Where(r => r != NetworkRecommender.AppDeepThinkingRole)
            .Select(r => Martlet.Core.Settings.SelfHostSetup.DeepThinkingCardOfRole(r) is > 1 ? "deep-thinking" : r))
            Assert.True(Options.Any(o => o.IsLocal && o.HostRoleKind == role), $"No catalog option runs the {role} host role.");
        Assert.All(Options.Where(o => o.HostRoleKind == "ocr"), o => Assert.Equal(PlanComponent.Reading, o.Component));
        Assert.Equal(["ppocrv5-mobile", "ppocrv5-server", "rapidocr-ppocrv4"], Options.Where(o => o.HostRoleKind == "ocr").Select(o => o.ModelId).Order());
        Assert.Equal(PlanComponent.SmartHome, Options.Single(o => o.HostRoleKind == "home-assistant").Component);
        Assert.All(Options.Where(o => o.HostRoleKind == "motion"), o => Assert.Equal(PlanComponent.Motion, o.Component));
        Assert.Equal(["hy-motion-1.0", "hy-motion-1.0-lite"], Options.Where(o => o.HostRoleKind == "motion").Select(o => o.ModelId).Order());
        Assert.All(Options.Where(o => o.HostRoleKind == "voice-recognition"), o => Assert.Equal(PlanComponent.People, o.Component));
        Assert.Equal([GpuRequirement.None, GpuRequirement.Nvidia], Options.Where(o => o.HostRoleKind == "voice-recognition").Select(o => o.Gpu));
    }

    [Fact]
    public void MotionNeedsTheCardTencentNamesAndPeopleRunsInMartletOnTheProcessor()
    {
        // deploy/host/roles/motion/role.conf: min_vram 23000 MiB for Lite and 26000 MiB for the standard model.
        Assert.Equal(23000 / 1024d, FootprintCatalog.Default.Find("motion:hy-motion-1.0-lite")!.MinGpuGb, 1);
        Assert.Equal(26000 / 1024d, FootprintCatalog.Default.Find("motion:hy-motion-1.0")!.MinGpuGb, 1);
        // A 24 GB card has room for Lite once the planner keeps its headroom.
        Assert.True(PlacementEngine.GpuCapacityGb(new MachineGpu("RTX 4090", GpuVendor.Nvidia, 24)) >=
            FootprintCatalog.Default.Find("motion:hy-motion-1.0-lite")!.GpuGb);
        var inApp = Assert.Single(FootprintCatalog.Default.For(PlanComponent.People), o => o.RunsInApp);
        Assert.Equal("people:wespeaker", inApp.Id);
        Assert.Equal(GpuRequirement.None, inApp.Gpu);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "deploy", "host", "roles"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root (deploy/host/roles) was not found above the test's folder.");
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

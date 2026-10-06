using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class SetupAdvisorTests
{
    private static AdvisorRole Role(SetupAdvice advice, string prefix) => advice.Roles.Single(r => r.Role.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void BalancedGamingPcKeepsGpuFreeAndOffloadsTheLlm()
    {
        var advice = SetupAdvisor.Recommend(new() { ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true, Character = true });

        Assert.Equal("Online", Role(advice, "Thinking").Where);
        Assert.Equal(AdvisorAvailability.Available, Role(advice, "Thinking").Availability);
        Assert.NotNull(Role(advice, "Thinking").HowTo);
        Assert.Equal("This PC (CPU)", Role(advice, "Speech-to-text").Where);
        Assert.Equal("Online", Role(advice, "Voice").Where);
        Assert.Equal("Loudness lip-sync", Role(advice, "Lip-sync").Choice);
        Assert.DoesNotContain(AdvisorNextStep.Hosts, advice.NextSteps);
    }

    [Fact]
    public void FastestSinglePcUsesASmallModelAndKeepsAClonedVoiceOnTheSameGpu()
    {
        var advice = SetupAdvisor.Recommend(new() { Goal = AdvisorGoal.Fastest, ThisPcGpu = AdvisorGpu.Nvidia24, Character = true });

        Assert.Equal("This PC (GPU)", Role(advice, "Thinking").Where);
        Assert.Equal(AdvisorAvailability.Available, Role(advice, "Thinking").Availability);
        Assert.Contains("small", Role(advice, "Thinking").Choice, StringComparison.Ordinal);
        Assert.Contains("smarter but slower", Role(advice, "Thinking").Why, StringComparison.Ordinal);
        Assert.Equal("This PC (GPU)", Role(advice, "Voice").Where);
        Assert.Contains(Settings.SpeechEngines.Default.Name, Role(advice, "Voice").Choice, StringComparison.Ordinal);
        // Chatterbox Turbo runs today: the plan says how to set it up, never "Planned".
        Assert.Equal(AdvisorAvailability.Available, Role(advice, "Voice").Availability);
        Assert.Contains("Voice engine", Role(advice, "Voice").HowTo, StringComparison.Ordinal);
        Assert.Equal("Parakeet speech recognition", Role(advice, "Speech-to-text").Choice);
        Assert.Equal(AdvisorAvailability.Available, Role(advice, "Speech-to-text").Availability);
    }

    [Fact]
    public void FastestNeverSuggestsAPlannedRecognizerOrALargerModel()
    {
        foreach (var gpu in new[] { AdvisorGpu.Nvidia8, AdvisorGpu.Nvidia12, AdvisorGpu.Nvidia16, AdvisorGpu.Nvidia24, AdvisorGpu.Nvidia32Plus })
        {
            var advice = SetupAdvisor.Recommend(new() { Goal = AdvisorGoal.Fastest, ThisPcGpu = gpu, VoiceInput = true });
            Assert.Contains("small", Role(advice, "Thinking").Choice, StringComparison.Ordinal);
            Assert.Equal("Parakeet speech recognition", Role(advice, "Speech-to-text").Choice);
            Assert.DoesNotContain(advice.Roles, r => r.Choice.Contains("Windows speech recognition", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FastestWithTwoHostsSplitsLlmAndSpeech()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Fastest, ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true, Character = true,
            OtherComputers = [new(AdvisorGpu.Nvidia24), new(AdvisorGpu.Nvidia24)]
        });

        Assert.Equal("Computer 2 (GPU)", Role(advice, "Thinking").Where);
        Assert.Equal("Computer 3 (GPU)", Role(advice, "Voice").Where);
        Assert.Equal("Computer 3 (GPU)", Role(advice, "Lip-sync").Where);
        Assert.Contains(AdvisorNextStep.Hosts, advice.NextSteps);
    }

    [Fact]
    public void PrivateGamingPcWarnsAboutSharingTheGpu()
    {
        var advice = SetupAdvisor.Recommend(new() { Goal = AdvisorGoal.Private, ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true });

        Assert.Equal("This PC (GPU)", Role(advice, "Thinking").Where);
        Assert.Contains(advice.Notes, n => n.Contains("slow games", StringComparison.Ordinal));
        Assert.DoesNotContain(advice.Roles, r => r.Where == "Online");
    }

    [Fact]
    public void UnlimitedBudgetGivesEachRoleItsOwnComputerAndLeavesSpares()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Private, ThisPcGpu = AdvisorGpu.Nvidia32Plus, GamesOnThisPc = true, Character = true,
            CustomVoice = true, OtherComputers = Enumerable.Repeat(new AdvisorComputer(AdvisorGpu.Nvidia32Plus), 5).ToArray()
        });

        Assert.Equal("Computer 2 (GPU)", Role(advice, "Thinking").Where);
        Assert.Equal("Computer 3 (GPU)", Role(advice, "Speech-to-text").Where);
        Assert.Equal("Computer 3 (GPU)", Role(advice, "Voice").Where);
        Assert.Equal("Computer 4 (GPU)", Role(advice, "Lip-sync").Where);
        Assert.Equal(6, advice.Machines.Count);
        Assert.StartsWith("Spare", advice.Machines[4].Runs[0], StringComparison.Ordinal);
        Assert.Contains(AdvisorNextStep.VoiceLibrary, advice.NextSteps);
    }

    [Fact]
    public void CustomVoiceWithoutAnyGpuExplainsWhy()
    {
        var advice = SetupAdvisor.Recommend(new() { CustomVoice = true });

        Assert.Equal("Online", Role(advice, "Voice").Where);
        Assert.Contains(advice.Notes, n => n.StartsWith("A custom voice needs an NVIDIA GPU", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedComputersGetRolesByTheirOwnGpu()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Private, ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true, Character = true,
            OtherComputers = [new(AdvisorGpu.Nvidia8, "old-laptop"), new(AdvisorGpu.Nvidia24, "gpu-box"), new(AdvisorGpu.None, "nas")]
        });

        Assert.Equal("gpu-box (GPU)", Role(advice, "Thinking").Where);
        Assert.Contains("large", Role(advice, "Thinking").Choice, StringComparison.Ordinal);
        Assert.Equal("old-laptop (GPU)", Role(advice, "Voice").Where);
        Assert.Equal("NVIDIA, 24 GB", advice.Machines.Single(m => m.Name == "gpu-box").Hardware);
        Assert.StartsWith("Not needed", advice.Machines.Single(m => m.Name == "nas").Runs[0], StringComparison.Ordinal);
        Assert.Equal("Private and offline: 3 computers", advice.Title);
    }

    [Fact]
    public void AmdComputerTakesTheLlmSoNvidiaStaysFreeForVoice()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Fastest, OtherComputers = [new(AdvisorGpu.Nvidia16), new(AdvisorGpu.OtherVendor)]
        });

        Assert.Equal("Computer 3 (GPU)", Role(advice, "Thinking").Where);
        Assert.Equal("Computer 2 (GPU)", Role(advice, "Voice").Where);
    }

    [Fact]
    public void UnsureGpuIsPlannedAsSmallNvidiaAndExplained()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Private, OtherComputers = [new(AdvisorGpu.Unknown)]
        });

        Assert.Equal("Computer 2 (GPU)", Role(advice, "Thinking").Where);
        Assert.Contains(advice.Notes, n => n.Contains("not sure which GPU is in Computer 2", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectedHardwareIsShownForThisPc()
    {
        var advice = SetupAdvisor.Recommend(new() { ThisPcGpu = AdvisorGpu.Nvidia24, ThisPcDetected = "NVIDIA GeForce RTX 4090 (24 GB)" });

        Assert.Equal("NVIDIA GeForce RTX 4090 (24 GB)", advice.Machines[0].Hardware);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", 23.99, AdvisorGpu.Nvidia24)]
    [InlineData("nvidia", 12.0, AdvisorGpu.Nvidia12)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", 11.0, AdvisorGpu.Nvidia8)]
    [InlineData("NVIDIA GeForce GTX 1650", 4.0, AdvisorGpu.Nvidia4)]
    [InlineData("NVIDIA GeForce RTX 5090", 31.5, AdvisorGpu.Nvidia32Plus)]
    [InlineData("nvidia", null, AdvisorGpu.Unknown)]
    [InlineData("AMD Radeon RX 7900 XTX", 24.0, AdvisorGpu.OtherVendor)]
    [InlineData("Intel(R) UHD Graphics", 0.1, AdvisorGpu.None)]
    [InlineData("AMD Radeon(TM) Graphics", 0.5, AdvisorGpu.None)]
    [InlineData(null, null, AdvisorGpu.None)]
    public void ClassifyMapsDetectedCards(string? name, double? memoryGb, AdvisorGpu expected) =>
        Assert.Equal(expected, SetupAdvisor.Classify(name, memoryGb));

    [Fact]
    public void HostHardwareStoreRoundTripsReports()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-hw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HostHardwareStore(directory);
            var report = new HostHardware("gpu-box", "https://192.168.1.20:9443", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "native",
                "Ubuntu 24.04.1 LTS", "6.8.0", "AMD Ryzen 9", 32, 62.7, "Docker 27.3.1", "yes",
                [new HostGpu("NVIDIA GeForce RTX 4090", "nvidia", 24564, "560.35.03")]);
            store.Save(report);
            store.Save(report with { OperatingSystem = "Ubuntu 24.04.2 LTS" });

            var loaded = Assert.Single(store.Load());
            Assert.Equal("Ubuntu 24.04.2 LTS", loaded.OperatingSystem);
            Assert.Equal(AdvisorGpu.Nvidia24, loaded.AdvisorGpu);
            Assert.Contains(loaded.Capabilities(), c => c.Contains("Audio2Face", StringComparison.Ordinal));
            store.Forget("gpu-box");
            Assert.Empty(store.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

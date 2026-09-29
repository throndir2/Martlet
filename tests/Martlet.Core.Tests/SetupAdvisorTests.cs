using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class SetupAdvisorTests
{
    private static AdvisorRole Role(SetupAdvice advice, string prefix) => advice.Roles.Single(r => r.Role.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void BalancedGamingPcKeepsGpuFreeAndOffloadsTheLlm()
    {
        var advice = SetupAdvisor.Recommend(new() { ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true, Character = true });

        Assert.Equal("Online", Role(advice, "Conversation").Where);
        Assert.Equal(AdvisorAvailability.Available, Role(advice, "Conversation").Availability);
        Assert.NotNull(Role(advice, "Conversation").HowTo);
        Assert.Equal("This PC (CPU)", Role(advice, "Speech-to-text").Where);
        Assert.Equal("Online", Role(advice, "Voice").Where);
        Assert.Equal("Loudness lip-sync", Role(advice, "Lip-sync").Choice);
        Assert.DoesNotContain(AdvisorNextStep.Hosts, advice.NextSteps);
    }

    [Fact]
    public void FastestSinglePcGivesTheWholeGpuToTheLlm()
    {
        var advice = SetupAdvisor.Recommend(new() { Goal = AdvisorGoal.Fastest, ThisPcGpu = AdvisorGpu.Nvidia24, Character = true });

        Assert.Equal("This PC (GPU)", Role(advice, "Conversation").Where);
        Assert.Equal(AdvisorAvailability.Available, Role(advice, "Conversation").Availability);
        Assert.Contains("12-14B Q4", Role(advice, "Conversation").Choice, StringComparison.Ordinal);
        Assert.Equal("Windows installed voices", Role(advice, "Voice").Choice);
        Assert.Equal("Loudness lip-sync", Role(advice, "Lip-sync").Choice);
    }

    [Fact]
    public void FastestWithTwoHostsSplitsLlmAndSpeech()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Fastest, ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true, Character = true,
            ExtraMachines = 2, ExtraMachineGpu = AdvisorGpu.Nvidia24
        });

        Assert.Equal("Computer 2 (GPU)", Role(advice, "Conversation").Where);
        Assert.Equal("Computer 3 (GPU)", Role(advice, "Voice").Where);
        Assert.Equal("Computer 3 (GPU)", Role(advice, "Lip-sync").Where);
        Assert.Contains(AdvisorNextStep.Hosts, advice.NextSteps);
    }

    [Fact]
    public void PrivateGamingPcWarnsAboutSharingTheGpu()
    {
        var advice = SetupAdvisor.Recommend(new() { Goal = AdvisorGoal.Private, ThisPcGpu = AdvisorGpu.Nvidia16, GamesOnThisPc = true });

        Assert.Equal("This PC (GPU)", Role(advice, "Conversation").Where);
        Assert.Contains(advice.Notes, n => n.Contains("frame rates", StringComparison.Ordinal));
        Assert.DoesNotContain(advice.Roles, r => r.Where == "Online");
    }

    [Fact]
    public void UnlimitedBudgetGivesEachRoleItsOwnComputerAndLeavesSpares()
    {
        var advice = SetupAdvisor.Recommend(new()
        {
            Goal = AdvisorGoal.Private, ThisPcGpu = AdvisorGpu.Nvidia32Plus, GamesOnThisPc = true, Character = true,
            CustomVoice = true, ExtraMachines = 5, ExtraMachineGpu = AdvisorGpu.Nvidia32Plus
        });

        Assert.Equal("Computer 2 (GPU)", Role(advice, "Conversation").Where);
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
}

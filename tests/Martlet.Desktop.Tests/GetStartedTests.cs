using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Home's Get started card: the main flow in three steps, each ticked from what Martlet knows, with stable button IDs.</summary>
public sealed class GetStartedTests
{
    private static readonly GetStartedActions Nothing = new(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });

    [Fact]
    public void ANewPcHasThreeStepsToDoInTheMainFlowsOrder()
    {
        var state = new GetStartedState(false, "Thinking: not set up.", false, "Martlet is Martlet now.", false, false);
        var steps = GetStarted.Steps(state, Nothing);
        Assert.Equal(new[] { GetStarted.Servers, GetStarted.Character, GetStarted.Talk }, steps.Select(s => s.Id));
        Assert.All(steps, step => Assert.False(step.Done));
        Assert.False(GetStarted.Done(state));
        Assert.Equal("Three steps and Martlet is ready to talk with you.", GetStarted.Summary(state));
        // Find a character needs Thinking, so it shows only once Thinking works.
        Assert.Equal(new[] { "Profiles", "Look", "Voice" }, steps[1].Commands.Select(c => c.Id));
        Assert.Equal(new[] { "RecommendedSetup", "AddComputer" }, steps[0].Commands.Select(c => c.Id));
        Assert.Equal(new[] { "Talk", "Listen" }, steps[2].Commands.Select(c => c.Id));
    }

    [Fact]
    public void EachStepTicksOnItsOwnAndTheCardHidesWhenAllAreDone()
    {
        var some = new GetStartedState(true, "Thinking: Ollama on this PC.", false, "Martlet is Martlet now.", false, true);
        var steps = GetStarted.Steps(some, Nothing);
        Assert.True(steps[0].Done);
        Assert.False(steps[1].Done);
        Assert.Contains("Find", steps[1].Commands.Select(c => c.Id));
        Assert.Equal("1 of 3 done.", GetStarted.Summary(some));
        Assert.True(GetStarted.Done(some with { CharacterChosen = true, Talked = true }));
    }
}

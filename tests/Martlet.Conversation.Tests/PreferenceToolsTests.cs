using Martlet.Core.Preferences;

namespace Martlet.Conversation.Tests;

/// <summary>manage_preferences (<see cref="PreferenceTools"/>): the reply's tool keeps the user's own rules for how Martlet behaves
/// in their one owner, always worded the same; and every check-in's facts carry the preferences that apply now.</summary>
public sealed class PreferenceToolsTests : IDisposable
{
    private const string Elden = "gaming/ELDEN RING";
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-preference-tools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void The_tool_is_always_worded_the_same()
    {
        Assert.Equal("manage_preferences", PreferenceTools.Definition.Name);
        Assert.Same(PreferenceTools.Definition, PreferenceTools.Definition);
        Assert.Contains("stop giving hints", PreferenceTools.Description);
        Assert.Contains("\"enum\":[\"list\",\"add\",\"update\",\"remove\"]", PreferenceTools.Definition.ParametersJson);
    }

    [Fact]
    public void Stop_giving_hints_is_saved_and_giving_hints_again_takes_it_away()
    {
        var owner = new UserPreferences(root);
        var add = PreferenceTools.Run(owner, """{"action":"add","text":"No hints unless I ask","where":"this game"}""", Elden);
        Assert.False(add.Result.IsError, add.Result.Output);
        Assert.True(add.Changed);
        Assert.Equal("added", add.Logged);
        var hint = Assert.Single(owner.All);
        Assert.Equal(Elden, hint.Activity);
        Assert.Equal(UserPreferences.Said, hint.Source);
        Assert.Contains(hint.Id, add.Result.Output);
        Assert.Contains("No hints unless I ask (while playing ELDEN RING)", owner.Note(Elden, null));

        var list = PreferenceTools.Run(owner, """{"action":"list"}""", null);
        Assert.Contains("\"applies_now\":false", list.Result.Output);
        Assert.False(list.Changed);

        var update = PreferenceTools.Run(owner, $$"""{"action":"update","id":"{{hint.Id}}","where":"games"}""", Elden);
        Assert.True(update.Changed, update.Result.Output);
        Assert.Equal("gaming", owner.Find(hint.Id)!.Activity);

        var remove = PreferenceTools.Run(owner, $$"""{"action":"remove","id":"{{hint.Id}}"}""", Elden);
        Assert.True(remove.Changed);
        Assert.Empty(owner.All);
        Assert.Null(owner.Note(Elden, null));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"text":"x"}""")]
    [InlineData("""{"action":"add"}""")]
    [InlineData("""{"action":"add","text":"x","where":"this game"}""")]
    [InlineData("""{"action":"update","id":"p123456"}""")]
    [InlineData("""{"action":"remove","id":"p123456"}""")]
    [InlineData("""{"action":"forget"}""")]
    public void Bad_calls_are_refused_and_change_nothing(string arguments)
    {
        var owner = new UserPreferences(root);
        var outcome = PreferenceTools.Run(owner, arguments, null);
        Assert.True(outcome.Result.IsError);
        Assert.False(outcome.Changed);
        Assert.Equal("refused", outcome.Logged);
        Assert.Empty(owner.All);
    }

    [Fact]
    public void Every_check_in_reads_the_preferences_that_apply_now()
    {
        var state = new CheckInState { Now = DateTimeOffset.UnixEpoch, Preferences = "- No hints unless I ask (while playing ELDEN RING)" };
        var facts = CheckInPrompts.Facts(CheckInFacts.None, state);
        Assert.Contains("standing preferences", facts);
        Assert.Contains("No hints unless I ask", facts);
        Assert.Equal("", CheckInPrompts.Facts(CheckInFacts.None, state with { Preferences = null }));
    }
}

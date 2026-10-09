using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

/// <summary>The Songs, pictures and creations check-in tool set: the reply tools it takes over, its tools with the reply's own
/// arguments, and the short line a reply gets in their place.</summary>
public sealed class CreationsCheckInTests
{
    [Fact]
    public void TheSetTakesOverTheFourStartingToolsWithTheReplysArguments()
    {
        var set = CreationsCheckIn.Set;
        Assert.Same(set, CheckInToolSets.Find(CreationsCheckIn.SetId));
        Assert.True(CheckInToolSets.IsId(set.Id));
        Assert.Equal([SongTools.SingName, SongTools.PlayName, PictureTools.DrawName, CreationTools.PerformName], set.Replaces);
        // stop_singing (an instant control) and list_creations (the reply needs its answer) stay on the reply.
        Assert.DoesNotContain(SongTools.StopName, set.Replaces);
        Assert.DoesNotContain(CreationTools.ListName, set.Replaces);
        Assert.All(set.Replaces, name => Assert.Contains(set.Tools, tool => tool.Name == name));
        Assert.Equal(SongTools.SingParametersJson, set.Tools.Single(t => t.Name == SongTools.SingName).ParametersJson);
        Assert.Equal(SongTools.PlayParametersJson, set.Tools.Single(t => t.Name == SongTools.PlayName).ParametersJson);
        Assert.Equal(PictureTools.DrawParametersJson, set.Tools.Single(t => t.Name == PictureTools.DrawName).ParametersJson);
        Assert.Equal(CreationTools.ListParametersJson, set.Tools.Single(t => t.Name == CreationTools.ListName).ParametersJson);
        Assert.Equal(CreationTools.PerformParametersJson, set.Tools.Single(t => t.Name == CreationTools.PerformName).ParametersJson);
    }

    [Fact]
    public void TheReplyGetsAShortStableLineForWhatIsHandedOff()
    {
        Assert.Null(CreationsCheckIn.ReplyGuidance(new HashSet<string>(), singing: true));
        var all = new HashSet<string>(CreationsCheckIn.Replaced);
        var line = CreationsCheckIn.ReplyGuidance(all, singing: true)!;
        Assert.Equal(line, CreationsCheckIn.ReplyGuidance(new HashSet<string>(CreationsCheckIn.Replaced.Reverse()), singing: true));
        Assert.Contains("make a song, sing a finished song once they say yes, draw a picture or perform or show something you made", line,
            StringComparison.Ordinal);
        Assert.Contains("right after your reply", line, StringComparison.Ordinal);
        Assert.Contains("stop_singing", line, StringComparison.Ordinal);
        Assert.Contains($"[{StayQuiet.Marker}]", line, StringComparison.Ordinal);
        var drawOnly = CreationsCheckIn.ReplyGuidance(new HashSet<string> { PictureTools.DrawName }, singing: false)!;
        Assert.StartsWith("When the user asks you to draw a picture, say", drawOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("stop_singing", drawOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyARefusedOrUnavailableStartIsForTheUser()
    {
        Assert.True(CreationsCheckIn.TellUser(new(PictureTools.Unavailable("pictures aren't set up"), true)));
        Assert.True(CreationsCheckIn.TellUser(new(SongTools.Unavailable("singing isn't set up."), true)));
        Assert.False(CreationsCheckIn.TellUser(new(PictureTools.Parse("{}").Problem!, true)));
        Assert.False(CreationsCheckIn.TellUser(new(PictureTools.Unavailable("x"), false)));
    }
}

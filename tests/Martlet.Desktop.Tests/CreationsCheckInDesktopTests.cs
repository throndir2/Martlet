using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The Songs, pictures and creations check-in tool set through the real controller: each tool does the reply's own
/// work with its gates, a start that can't happen is brought up by Martlet on its own, and the first line of every answer says
/// only the tool and what came of it.</summary>
public sealed class CreationsCheckInDesktopTests
{
    private static readonly CheckInToolContext Context = new("actions", "After each exchange", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ACheckInRunsTheReplyToolsWithTheirGatesAndBringsUpWhatCantStart()
    {
        await using var fixture = await LiveFixture.Create(data: true);
        Assert.True(fixture.Controller.RunsCreationsCheckIn);

        // Pictures aren't set up here: nothing is drawn, and Martlet tells the user why on its own.
        var draw = await fixture.Controller.CreationsCheckInAsync(
            new TextToolCall("c1", PictureTools.DrawName, """{"description":"a watercolour fox","title":"Fox"}"""), Context, default);
        Assert.True(draw.IsError);
        Assert.StartsWith("draw_picture: not done; Martlet tells the user why on its own.\n", draw.Output, StringComparison.Ordinal);
        Assert.Contains("pictures aren't set up", draw.Output, StringComparison.Ordinal);
        await LiveConversationTests.Until(() => fixture.Controller.Jobs.HasNews);
        Assert.Contains(fixture.Controller.Jobs.Active.Concat(fixture.Controller.Jobs.Recent), job => job.Kind == CheckIns.SayKind);

        // Singing isn't set up either.
        var sing = await fixture.Controller.CreationsCheckInAsync(new TextToolCall("c2", SongTools.SingName, """{"about":"the cat"}"""), Context, default);
        Assert.True(sing.IsError);
        Assert.Contains("singing isn't set up", sing.Output, StringComparison.Ordinal);

        // Bad arguments are the check-in model's to correct: nothing is brought up.
        var bad = await fixture.Controller.CreationsCheckInAsync(new TextToolCall("c3", PictureTools.DrawName, "{}"), Context, default);
        Assert.True(bad.IsError);
        Assert.Equal($"{PictureTools.DrawName}: not done.", bad.Output.Split('\n')[0]);

        // list_creations keeps the reply's gate: no kind of creation is registered in this test process.
        var list = await fixture.Controller.CreationsCheckInAsync(new TextToolCall("c4", CreationTools.ListName, "{}"), Context, default);
        Assert.Equal(fixture.Controller.Creations.Kinds.Count == 0, list.IsError);
        Assert.StartsWith($"{CreationTools.ListName}: ", list.Output.Split('\n')[0], StringComparison.Ordinal);

        var unknown = await fixture.Controller.CreationsCheckInAsync(new TextToolCall("c5", "stop_singing", "{}"), Context, default);
        Assert.True(unknown.IsError);
    }

    [Fact]
    public async Task APcWithoutADataFolderDoesntRunTheSet()
    {
        await using var fixture = await LiveFixture.Create();
        Assert.False(fixture.Controller.RunsCreationsCheckIn);
        var result = await fixture.Controller.CreationsCheckInAsync(new TextToolCall("c1", CreationTools.ListName, "{}"), Context, default);
        Assert.True(result.IsError);
    }
}

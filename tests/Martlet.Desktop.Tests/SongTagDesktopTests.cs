using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>Singing without tools: a reply on a route that carries no tools is offered the song tags.</summary>
public sealed class SongTagDesktopTests
{
    [Fact]
    public void ControlTagsAddTheSongTagsOnlyForAReplyThatSingsWithTags()
    {
        Assert.Null(LiveConversationConfiguration.ControlTags(false, false, null));
        Assert.Equal(SongTags.All, LiveConversationConfiguration.ControlTags(false, false, null, sings: true));
        var all = LiveConversationConfiguration.ControlTags(true, false, null, sings: true)!;
        Assert.Equal(ChattinessTags.All.Concat(SongTags.All), all);
        Assert.DoesNotContain(all, tag => SongTags.All.Contains(tag) && ChattinessTags.All.Contains(tag));
    }

    [Fact]
    public async Task AReplyDoesntSingWithTagsWhenSingingIsntSetUp()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var configuration = LiveConversationConfiguration.From(loaded)!;
        Assert.False(fixture.Controller.SingsWithTags(configuration));
    }
}

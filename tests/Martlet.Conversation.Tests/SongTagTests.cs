using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class SongTagTests
{
    [Fact]
    public void SongTagCallsStandForTheSongTools()
    {
        var calls = SongTags.Calls(["[chatty]", "[sing: a song about the rain]", "[play_song: 3fa2c19b0d71 from chorus]", "[STOP_SINGING]"]);

        Assert.Equal([SongTools.SingName, SongTools.PlayName, SongTools.StopName], calls.Select(call => call.Name));
        Assert.Equal("a song about the rain", JsonDocument.Parse(calls[0].ArgumentsJson).RootElement.GetProperty("about").GetString());
        var play = JsonDocument.Parse(calls[1].ArgumentsJson).RootElement;
        Assert.Equal("3fa2c19b0d71", play.GetProperty("song_id").GetString());
        Assert.Equal("chorus", play.GetProperty("from").GetString());
        Assert.Equal("{}", calls[2].ArgumentsJson);
    }

    [Fact]
    public void SongTagsWithoutWordsAndOtherTagsAreNotCalls()
    {
        Assert.Empty(SongTags.Calls(["[sing:]", "[sing: ]", "[play_song:]", "[seen: a cat]", "[quiet]"]));
        Assert.Null(SongTags.Kept(["[seen: a cat]", "[sing:]"]));
    }

    [Theory]
    [InlineData("3fa2c19b0d71", "3fa2c19b0d71", null)]
    [InlineData("3fa2c19b0d71 from chorus", "3fa2c19b0d71", "chorus")]
    [InlineData("3fa2c19b0d71, 1:05", "3fa2c19b0d71", "1:05")]
    [InlineData("song id: \"3fa2c19b0d71\" from the second verse", "3fa2c19b0d71", "the second verse")]
    [InlineData("song_id 3fa2c19b0d71", "3fa2c19b0d71", null)]
    public void PlayTagWordsGiveTheSongAndWhereToStart(string words, string id, string? from)
    {
        Assert.Equal((id, from), SongTags.SongAndFrom(words));
    }

    [Fact]
    public void PlayTagWithoutFromHasNoFromArgument()
    {
        var call = Assert.Single(SongTags.Calls(["[play_song: 3fa2c19b0d71]"]));
        Assert.False(JsonDocument.Parse(call.ArgumentsJson).RootElement.TryGetProperty("from", out _));
    }

    [Fact]
    public void KeptHasOnlyTheSongTagsInOrder()
    {
        Assert.Equal("[sing: rain] [stop_singing]", SongTags.Kept(["[chatty]", "[sing: rain]", "[stop_singing]"]));
    }

    [Fact]
    public void InstructionsTellTheTagsAndAreGoneWhenEmptied()
    {
        var text = SongTags.Instructions(null);
        Assert.NotNull(text);
        Assert.Contains("[sing:", text);
        Assert.Contains("[play_song:", text);
        Assert.Contains("[stop_singing]", text);
        Assert.Contains(StayQuiet.Marker, text);
        Assert.DoesNotContain("{silent}", text);

        var emptied = SkillSettings.Edited(SkillCatalog.SongTags, "");
        Assert.Null(SongTags.Instructions(emptied));
    }

    [Fact]
    public void AllOffersTheThreeSongTags()
    {
        Assert.Equal(3, SongTags.All.Count);
        Assert.All(SongTags.All, tag => Assert.StartsWith("[", tag));
    }
}

using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

// Companion › Replies and People: their page models decide without WPF.
public sealed class RepliesAndPeoplePageModelTests
{
    private static Dictionary<GenerationSetting, string> Texts(params (GenerationSetting Setting, string Text)[] typed)
    {
        var texts = RepliesPageModel.ReplySettings.ToDictionary(s => s.Setting, _ => "");
        foreach (var (setting, text) in typed) texts[setting] = text;
        return texts;
    }

    [Fact]
    public void Replies_reads_the_typed_settings_and_the_choices()
    {
        var (generation, problem) = RepliesPageModel.Read(
            Texts((GenerationSetting.MaxReplyTokens, "512"), (GenerationSetting.Temperature, "0.7"), (GenerationSetting.ContextTokens, "8192")),
            thinking: 1, shortFirst: 1, adult: 0, personal: 2);

        Assert.Null(problem);
        Assert.Equal(512, generation!.MaxReplyTokens);
        Assert.Equal(0.7, generation.Temperature);
        Assert.Equal(8192, generation.ContextTokens);
        Assert.Null(generation.TopP);
        Assert.True(generation.Reasoning);
        Assert.False(generation.ShortFirstSentence);
        Assert.Null(generation.AdultContent);
        Assert.Equal(PersonalContent.Everything, generation.PersonalContent);
    }

    [Fact]
    public void Replies_says_which_field_is_wrong_and_saves_nothing()
    {
        var (generation, problem) = RepliesPageModel.Read(Texts((GenerationSetting.TopK, "4.5")), 0, 0, 0, 0);

        Assert.Null(generation);
        Assert.Equal("Reply settings not saved yet: Top K: enter a whole number in the shown range, or leave it blank.", problem);
    }

    [Fact]
    public void Replies_blank_fields_and_default_choices_save_nothing_but_the_defaults()
    {
        var (generation, problem) = RepliesPageModel.Read(Texts(), 0, 0, 0, 0);

        Assert.Null(problem);
        Assert.Equal("Replies are brief; maximum length is 1024 tokens; Thinking steps are off; spoken replies start with a short " +
            "first sentence; other settings use the model default.", RepliesPageModel.DescribeGeneration(generation));
    }

    [Fact]
    public void People_says_when_a_voice_was_heard()
    {
        Assert.Equal("just now", PeoplePageModel.Ago(DateTimeOffset.UtcNow));
        Assert.Equal("5 min ago", PeoplePageModel.Ago(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5.5)));
        Assert.Equal("3 h ago", PeoplePageModel.Ago(DateTimeOffset.UtcNow - TimeSpan.FromHours(3.2)));
    }
}

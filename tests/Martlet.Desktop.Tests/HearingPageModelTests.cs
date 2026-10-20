using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Hearing's page model (Pages\Hearing), without WPF.</summary>
public sealed class HearingPageModelTests
{
    [Fact]
    public void TurnHearingOffAndOnSavesTheChoice()
    {
        var services = new FakeServices { Talk = new TalkPreferences(HearVoice: true) };
        var model = new HearingPageModel(services);
        Assert.True(model.On);
        Assert.Equal("On, but Thinking isn't set up yet.", model.NowText);
        Assert.Equal("Turn hearing off", model.ToggleText);

        model.Toggle();
        Assert.False(services.Talk.HearVoice);
        Assert.Equal("Hearing is off, so Thinking gets only the transcript of what you say.", services.Status);

        model = new HearingPageModel(services);
        Assert.False(model.On);
        Assert.Equal("Off: Martlet gets only the words you say, not how you say them.", model.NowText);
        Assert.False(model.AdviceWarns);
        Assert.Equal("Turn hearing on", model.ToggleText);

        model.Toggle();
        Assert.Null(services.Talk.HearVoice);
        Assert.Equal("Your recording would leave this PC, so hearing waits for your tick under Hear how you say it.", services.Status);
        Assert.Null(new HearingPageModel(services).ToggleText);
    }

    private sealed class FakeServices : IHearingPageServices
    {
        internal string? Status;

        public TalkPreferences Talk { get; set; } = new();
        public AppSettings? Settings => null;
        public ModelAbilities SavedModelAbilities() => new();
        public SenseRoute AudioRoute(SetupRoute? thinking, ModelAbilities abilities) => new(SenseKind.Audio, SensePath.None, null, "");
        public void ShowStatus(string text) => Status = text;
        public void SaveTalk(TalkPreferences next, bool render = false) => Talk = next;
    }
}
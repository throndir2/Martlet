using Martlet.Avatar.Hosting;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Eyes' page model (Pages\Eyes), without WPF.</summary>
public sealed class EyesPageModelTests
{
    [Fact]
    public void EyesSayWhereTheCharacterLooksAndWhetherTheFaceCanBeMeasured()
    {
        var eyes = new FakeEyes();
        var model = new EyesPageModel(eyes);
        Assert.Equal(MainWindow.EyesNow(null, null, eyes.Talk.GazeFree, null, measured: false, showing: false), model.NowText);
        Assert.Null(model.NowProblem);
        Assert.EndsWith("not decided yet, so it follows your mouse.", model.PersonalityNote, StringComparison.Ordinal);
        Assert.Equal("Reading the character...", EyesPageModel.Status(eyes));
        Assert.Equal("Measure the face", model.MeasureLabel);
        Assert.False(model.CanForget);
        // No conversation and no character read yet: Measure the face waits.
        Assert.False(model.CanMeasure(canSee: true));

        eyes.PersonalityGaze = GazeMode.Ahead;
        Assert.EndsWith($"now {CharacterGaze.Label(GazeMode.Ahead)}.", model.PersonalityNote, StringComparison.Ordinal);

        // Shown with only estimated eyes and no model that can see: Now says why emotes may not fit.
        eyes.Showing = true;
        eyes.EyesFrom = RendererEyesFrom.Estimate;
        Assert.StartsWith("The eyes are only estimated", new EyesPageModel(eyes).NowProblem, StringComparison.Ordinal);

        eyes.Measuring = true;
        var busy = new EyesPageModel(eyes);
        Assert.True(busy.Busy);
        Assert.Equal("Measuring...", busy.MeasureLabel);
        Assert.False(busy.CanMeasure(canSee: true));

        Assert.All(Enum.GetValues<GazeMode>(), mode => Assert.EndsWith(".", EyesPageModel.ModeNote(mode), StringComparison.Ordinal));
    }

    private sealed class FakeEyes : IEyesPageServices
    {
        public TalkPreferences Talk { get; set; } = new();
        public void SaveTalk(TalkPreferences next) => Talk = next;
        public GazeMode? PersonalityGaze { get; set; }
        public string Looking => "Looking at your mouse.";
        public string? LastLook => null;
        public bool Showing { get; set; }
        public string? EyesFrom { get; set; }
        public CharacterEyeService Eyes { get; } = new(null);
        public bool Measuring { get; set; }
        public bool CanAsk { get; set; }
        public CharacterActionCatalog? Catalog => null;
        public (bool CanSee, string Line, string? Off) Sight() => (false, "No model can see.", "Off.");
        public void FollowActions() { }
        public void Measure() { }
        public void Forget() { }
    }
}

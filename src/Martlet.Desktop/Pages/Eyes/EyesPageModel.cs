using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Eyes' decisions, without WPF: the Now line and its problem, what each gaze choice says, the face
/// measurement's status, and what Measure the face and Forget the measurement say and allow. <see cref="EyesPage"/> makes it again
/// each time it draws.</summary>
internal sealed class EyesPageModel(IEyesPageServices services)
{
    /// <summary>Whether the face is being measured, read once when the page draws.</summary>
    internal bool Busy { get; } = services.Measuring || services.Eyes.Busy;

    /// <summary>What the Now line says.</summary>
    internal string NowText
    {
        get
        {
            var prefs = services.Talk;
            var showing = services.Showing;
            return MainWindow.EyesNow(prefs.GazeUsual, services.PersonalityGaze, prefs.GazeFree, showing ? services.EyesFrom : null,
                services.Eyes.Current is not null, showing);
        }
    }

    /// <summary>Why emotes over the eyes may not fit, or null.</summary>
    internal string? NowProblem =>
        (services.Showing ? services.EyesFrom : null) == RendererEyesFrom.Estimate && services.Eyes.Current is null && !services.Sight().CanSee
            ? "The eyes are only estimated and no model that can see pictures is set up to measure them, so emotes drawn over the eyes may not fit."
            : null;

    /// <summary>What "As the personality decides" says, with the gaze the personality decided.</summary>
    internal string PersonalityNote => "Your Thinking model picks it from the personality, with the touch temperament (on the Touch page): " +
        (services.PersonalityGaze is { } decided ? $"now {CharacterGaze.Label(decided)}." : "not decided yet, so it follows your mouse.");

    /// <summary>What the choice of <paramref name="mode"/> does.</summary>
    internal static string ModeNote(GazeMode mode) => mode switch
    {
        GazeMode.Near => "It looks at your mouse pointer only while it is near the character, and otherwise straight ahead.",
        GazeMode.Ahead => "It looks straight ahead and doesn't follow your mouse.",
        GazeMode.Window => "It watches what you do in the window you're using: where you move the mouse or type in it, " +
            "and its middle until you do.",
        _ => "Its head and eyes follow your mouse pointer everywhere."
    };

    /// <summary>Where the eyes come from and how measuring went (CharacterEyesStatus).</summary>
    internal static string Status(IEyesPageServices services) => services.Eyes.ModelId is null ? "Reading the character..."
        : CharacterEyes.Status(services.EyesFrom, services.Eyes.Current, services.Showing, DateTimeOffset.Now);

    internal string MeasureLabel => Busy ? "Measuring..." : services.Eyes.Current is null ? "Measure the face" : "Measure the face again";

    /// <summary>Whether Measure the face may run: nothing measures now, the character is read and a model can see.</summary>
    internal bool CanMeasure(bool canSee) => !Busy && services.CanAsk && services.Catalog is not null && canSee;

    internal bool CanForget => !Busy && services.Eyes.Current is not null;
}

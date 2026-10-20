using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Emotes and motions, the UI only: Now, then every emote (expression) and motion of the character this PC
/// shows, plus Martlet's own nod and shake, with the tag replies write for each ({blush}), when to use it, the voice cue that also
/// sets it off ([laugh] with Chatterbox Turbo, (laughs) with Dia) and whether it is used (EmotesPage.Actions.cs), Make a motion for
/// a VRM (EmotesPage.Motions.cs), then the owner's combos of them (EmotesPage.Combos.cs and EmotesPage.ComboRow.cs). The Thinking
/// model names them once for each new model (Name them with Thinking asks again); edits save as you type, per model. What the page
/// needs from the app is <see cref="IEmotesPageServices"/>; its words are in <see cref="EmotesPageModel"/>. MainWindow adds the long
/// list a batch at a time on a page just opened (<paramref name="addRow"/>) and calls <see cref="ShowPlayed"/> when the character
/// plays something.</summary>
internal sealed partial class EmotesPage(IEmotesPageServices services, Action<Panel, UIElement> addRow)
{
    private TextBlock? characterActionsLast;
    private TextBlock? characterActionsHeld;
    private readonly List<(string Id, Button Button)> characterActionTries = [];

    /// <summary>The model whose emotes the page drew last (MainWindow draws the page again at once for another model).</summary>
    internal string? RenderedModel { get; private set; }

    /// <summary>Companion › Emotes and motions: Now, then the emotes, motions and combos.</summary>
    internal void Render(Panel page)
    {
        var actions = CharacterActionsCard();
        page.Children.Add(NowCard());
        page.Children.Add(actions);
    }

    private Border NowCard()
    {
        var actions = services.Actions;
        if (actions.Problem is { } problem)
            return PageNowCard("Martlet couldn't read this character's emotes and motions.", null, "EmotesNow", problem);
        if (actions.Current is not { } catalog) return PageNowCard("Reading the character's emotes and motions...", null, "EmotesNow");
        return PageNowCard(EmotesPageModel.Now(catalog), null, "EmotesNow",
            services.AvatarShowing ? null : "The character is hidden, so replies don't play them until it shows.");
    }

    /// <summary>The lingering emotes the character shows now, in words.</summary>
    private string HeldText() => EmotesPageModel.HeldText(services.Held.Current, DateTimeOffset.Now);

    /// <summary>After the character played something: what it played last, the lingering emotes, and Try or Turn off on each row
    /// and combo.</summary>
    internal void ShowPlayed()
    {
        if (characterActionsLast is not null) characterActionsLast.Text = services.LastAction ?? "";
        if (characterActionsHeld is not null) characterActionsHeld.Text = HeldText();
        foreach (var (id, button) in characterActionTries)
        {
            var label = services.Held.Holds(id) ? "Turn off" : "Try";
            button.Content = label;
            AutomationProperties.SetName(button, label);
        }
        RefreshComboTries();
    }

    /// <summary>Try: plays an emote or motion once, or turns a lingering one on (Turn off turns it off again), as a reply's
    /// {tag} and {/tag} would.</summary>
    private async Task TryCharacterActionAsync(CharacterActionSource source)
    {
        try
        {
            if (services.Held.Holds(source.Id)) await services.StopActionAsync(source, "a try");
            else await services.PlayActionAsync(source, "a try", hold: services.ShownCatalog?.Lingers(source) == true);
        }
        catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, services.Lifetime))
        {
            if (characterActionsLast is not null) characterActionsLast.Text = $"The character couldn't play \"{source.Name}\" right now.";
        }
    }

    private async Task ResetCharacterActionsAsync()
    {
        var why = await services.Actions.ResetAsync(services.Lifetime);
        services.ForgetEdits();
        if (why is not null && characterActionsLast is not null) characterActionsLast.Text = "Couldn't reset: " + why;
        else if (services.Open) services.Redraw();
    }
}

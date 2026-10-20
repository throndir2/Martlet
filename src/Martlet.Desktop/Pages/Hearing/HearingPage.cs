using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › Hearing (an optional extra, docs/SENSE_MODELS.md), the UI only: whether a model hears how you say things,
/// not only your words. In the standard order: Now (what hears your voice now, any problem, the audio model's lines, and Turn
/// hearing off or on), then the audio models' list (empty: Thinking's own model takes the recordings), then Hear how you say it
/// (the consent check box, the voice path and Test hearing). Off is Let ... hear my voice turned off (talk-preferences.json), so
/// Thinking gets only the transcript. Automation IDs: <c>HearingNow</c>, <c>TalkHearVoiceStatus</c>, the AudioModel lines,
/// <c>HearingToggle</c> (Turn hearing off while a model hears you, Turn hearing on after you turned it off) and the list's
/// <c>Pool-hearing-...</c>.
/// <para>What each line says and what Turn hearing off or on saves is in <see cref="HearingPageModel"/>; what the page needs from
/// the app is <see cref="IHearingPageServices"/>. The audio models' list (<paramref name="audioModelList"/>) and its Now lines
/// (<paramref name="audioModelLines"/>) are shared with Companion › Vision and built on the shared pool list, so MainWindow still
/// makes them (MainWindow.SensePools.cs and MainWindow.SenseModels.cs); Hear how you say it (<paramref name="hearVoiceCard"/>) is
/// part of how you talk, in MainWindow.Talk.cs.</para></summary>
internal sealed class HearingPage(IHearingPageServices services, Func<UIElement> audioModelList, Func<IEnumerable<UIElement>> audioModelLines,
    Func<UIElement> hearVoiceCard)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new HearingPageModel(services);

        var now = new TextBlock { Text = model.NowText, FontSize = 15, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(now, "HearingNow");
        var status = model.AdviceWarns ? Warning(model.Advice) : Note(model.Advice, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkHearVoiceStatus");
        var toggle = model.ToggleText is { } text ? PageButton(text, model.Toggle, primary: true, id: "HearingToggle") : null;
        page.Children.Add(Card([Heading("Now"), now, status, .. audioModelLines(), .. toggle is null ? Array.Empty<UIElement>() : [Row(toggle)]]));

        page.Children.Add(audioModelList());

        page.Children.Add(hearVoiceCard());
    }
}

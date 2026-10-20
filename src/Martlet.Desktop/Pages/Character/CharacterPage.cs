using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › Character, the UI only: Now, the character model card (show, hide, place, lock and click-through), Zoom
/// while the character shows, then Your characters, VRoid Hub and the link to Profiles. What the page needs from the app is
/// <see cref="ICharacterPageServices"/>; its decisions are in <see cref="CharacterPageModel"/>. The window keeps the two lines it
/// updates later and gives them as builders: where the character is remembered (<paramref name="placementLine"/>) and the zoom
/// view (<paramref name="viewLine"/>, null while the character is hidden). The last three cards are the window's own builders
/// (<paramref name="modelsCard"/>, <paramref name="vroidHubCard"/> and <paramref name="profilesLinkCard"/>).</summary>
internal sealed class CharacterPage(ICharacterPageServices services, Func<TextBlock> placementLine, Func<bool, TextBlock?> viewLine,
    Func<UIElement> modelsCard, Func<UIElement> vroidHubCard, Func<UIElement> profilesLinkCard)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new CharacterPageModel(services);
        var showing = model.Showing;
        page.Children.Add(PageNowCard(model.NowText, null, "SetupCharacterNow", services.CleanupProblem));

        var resetPosition = model.ShowsResetPosition
            ? PageButton("Reset position", services.ResetPosition, id: "SetupCharacterResetPosition") : null;
        var modelCard = Card(Heading("Character model"),
            HelpTip.Explain("Choose a character model, then adjust its size, position and motion. Choices save on their own and a showing " +
                "character switches right away.", new Thickness(0, 0, 0, 8), "SetupCharacterModelAbout", "the character model",
                wiki: "The-Character#show-and-add-characters"),
            Row(PageButton(model.ToggleLabel, services.ToggleCharacter, primary: !showing, id: "SetupCharacterToggle"),
                PageButton("Choose and customize", services.OpenCharacter, id: "OpenAvatar"),
                resetPosition,
                showing ? PageButton("Reset zoom", services.ResetZoom, id: "SetupCharacterResetZoom") : null,
                model.ShowsLock ? PageButton(model.LockLabel, () => services.SetLock(!services.Locked), id: "SetupCharacterLock") : null,
                model.ShowsClickThrough ? PageButton(model.ClickThroughLabel,
                    () => services.SetClickThrough(!services.ClickThrough), id: "SetupCharacterClickThrough") : null));
        if (modelCard.Child is Panel modelPanel)
        {
            modelPanel.Children.Add(placementLine());
            var clickThroughNote = HelpTip.Explain(model.ClickThroughNote,
                new Thickness(0, 4, 0, 0), "SetupCharacterClickThroughNote", "click-through", wiki: "The-Character#move-zoom-and-lock");
            AutomationProperties.SetAutomationId(clickThroughNote, "SetupCharacterClickThroughNote");
            modelPanel.Children.Add(clickThroughNote);
        }
        // What the showing model drives: textures (and any downscaling), blinking, mouth, motions and physics.
        if (model.ModelLine is { } line && modelCard.Child is Panel modelStack)
        {
            var modelNote = Note(line, new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(modelNote, "SetupCharacterModel");
            modelStack.Children.Add(modelNote);
        }
        page.Children.Add(modelCard);
        if (viewLine(showing) is { } view)
        {
            // Wheel zoom happens on the overlay itself; Martlet reads the overlay's view whenever this page renders.
            page.Children.Add(Card(Heading("Zoom"),
                HelpTip.Explain("Use the mouse wheel over the character to zoom. Ctrl+drag or middle-drag pans when zoomed in.",
                    new Thickness(0, 0, 0, 0), "SetupCharacterZoom", "zooming", wiki: "The-Character#move-zoom-and-lock"),
                Row(PageButton("Zoom in", () => services.Zoom("in"), id: "SetupCharacterZoomIn"),
                    PageButton("Zoom out", () => services.Zoom("out"), id: "SetupCharacterZoomOut")),
                view));
            services.Zoom("status");
        }
        page.Children.Add(modelsCard());
        page.Children.Add(vroidHubCard());
        page.Children.Add(profilesLinkCard());
    }
}

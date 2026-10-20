using System.Windows;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Touch, the UI only: Now, the Touch zones card (<see cref="TouchZonesPage"/>), the active persona's touch
/// temperament (TouchPage.Temperament.cs) and the changes the character made (TouchPage.ReactionChanges.cs). What the page needs from
/// the app is <see cref="ITouchPageServices"/>; its words are in <see cref="TouchPageModel"/>. MainWindow makes the Touch zones card
/// (<paramref name="zonesCard"/>: it places a model's first zones before it draws it) and adds the long temperament card a batch at a
/// time on a page just opened (<paramref name="addRow"/>).</summary>
internal sealed partial class TouchPage(ITouchPageServices services, Func<Border> zonesCard, Action<Panel, UIElement> addRow)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>: Now first, then the zones, the temperament (which joins with the
    /// zones' last rows on a page just opened) and the changes.</summary>
    internal void Render(Panel page)
    {
        services.FollowActions();
        page.Children.Add(zonesCard());
        page.Children.Insert(0, NowCard());
        addRow(page, TemperamentCard());
        page.Children.Add(ReactionChangesCard());
    }

    private Border NowCard() => services.Catalog is null
        ? PageNowCard("Reading the character...", null, "TouchNow")
        : PageNowCard(MainWindow.TouchNow(services.Zones.Current, services.Talk.TouchInterrupts), null, "TouchNow",
            services.AvatarShowing ? null : "The character is hidden, so touches do nothing until it shows.");
}

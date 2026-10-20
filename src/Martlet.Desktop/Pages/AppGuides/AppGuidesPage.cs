using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Companion › App guides (docs/APP_GUIDES.md), the UI only: the On switch (on by default) with what leaves this PC, Read up
/// when I start a game or app (on by default), the list of apps with their guides (Read up now or Read again, Read more, Allow
/// again, Delete; AppGuidesPage.Apps.cs) and Add an app (AppGuidesPage.Add.cs). What each line says is in
/// <see cref="AppGuidesPageModel"/>; what the page needs from the app is <see cref="IAppGuidesPageServices"/>. MainWindow keeps one
/// page for the window's life: it keeps the Add an app boxes and the status lines it updates while Martlet reads up.</summary>
internal sealed partial class AppGuidesPage(IAppGuidesPageServices services)
{
    private TextBlock? nowText, frontText;
    private readonly Dictionary<string, TextBlock> rows = new(StringComparer.Ordinal);

    /// <summary>The library revision the page last drew its list for: other changes (reading-up progress) only update its status lines.</summary>
    internal int Drawn { get; private set; } = -1;

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        rows.Clear();
        nowText = frontText = null;
        if (services.Guides is not { } guides)
        {
            page.Children.Add(PageNowCard("App guides are unavailable until Martlet's data folder can be used.", null, "AppGuidesNow"));
            return;
        }
        if (!guides.Loaded)
        {
            page.Children.Add(PageNowCard("Reading your app guides...", null, "AppGuidesNow"));
            return;
        }
        Drawn = guides.Revision;
        var model = new AppGuidesPageModel(guides);
        var library = guides.Library;
        var now = PageNowCard(model.NowText, null, "AppGuidesNow", model.LoadProblemText);
        nowText = FindById<TextBlock>(now, "AppGuidesNow");
        page.Children.Add(now);

        var on = new CheckBox { Content = "Read up on games and apps", IsChecked = library.On };
        AutomationProperties.SetAutomationId(on, "AppGuidesOn");
        on.Checked += (_, _) => SetOnAsync(true).Forget();
        on.Unchecked += (_, _) => SetOnAsync(false).Forget();
        var disclosure = HelpTip.Explain("For a game without a guide, Martlet sends its name to DuckDuckGo and reads up to 200 pages of " +
            "its wiki, and the wiki sites see this PC's internet address. A Thinking pool helper may then choose up to 300 more pages " +
            "from the game's name, the linked titles and what you do now, and the guides stay on this PC. To notice a game, Martlet " +
            "looks only at the name of the program in front and whether it fills the screen.",
            new Thickness(24, 2, 0, 8), "AppGuidesAbout", "what App guides send", wiki: "App-Guides#what-is-sent-where");
        AutomationProperties.SetAutomationId(disclosure, "AppGuidesDisclosure");
        var ask = new CheckBox { Content = "Read up when I start a game or app", IsChecked = library.ReadUpWhenStarted, IsEnabled = library.On };
        AutomationProperties.SetAutomationId(ask, "AppGuidesAsk");
        ask.Checked += (_, _) => SetAskAsync(true).Forget();
        ask.Unchecked += (_, _) => SetAskAsync(false).Forget();
        var askNote = HelpTip.Explain("When a game, or an app on your list, without a guide comes to the front, Martlet reads up on it " +
            "on its own and tells you, in character. If you tell it you'd rather it didn't, it stops and doesn't read up on that app on " +
            "its own again.", new Thickness(24, 2, 0, 4), "AppGuidesReadUp", "reading up on its own",
            wiki: "App-Guides#reading-up-when-you-start-a-game");
        var front = Note(model.FrontText, new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(front, "AppGuidesFront");
        AutomationProperties.SetLiveSetting(front, AutomationLiveSetting.Polite);
        frontText = front;
        page.Children.Add(Card(Heading("App guides"), on, disclosure, ask, askNote, front));

        page.Children.Add(AppsCard(guides, model));

        page.Children.Add(AddCard(guides));
    }

    // Only the status lines change while Martlet reads up (the boxes keep their focus).
    internal void RenderStatus()
    {
        if (services.Guides is not { } guides) return;
        var model = new AppGuidesPageModel(guides);
        if (nowText is not null) nowText.Text = model.NowText;
        if (frontText is not null) frontText.Text = model.FrontText;
        foreach (var entry in guides.Library.Apps)
            if (rows.TryGetValue(entry.Key, out var line)) line.Text = model.Status(entry);
    }

    private async Task SetOnAsync(bool on)
    {
        if (services.Guides is not { } guides || guides.On == on) return;
        try
        {
            await guides.SetOnAsync(on, services.Token);
            ErrorLog.Info($"App guides: you turned them {(on ? "on" : "off")}.");
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            services.Say($"Couldn't save the App guides choice: {why}.");
        }
    }

    private async Task SetAskAsync(bool ask)
    {
        if (services.Guides is not { } guides || guides.Library.ReadUpWhenStarted == ask) return;
        try { await guides.SetReadUpWhenStartedAsync(ask, services.Token); }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            services.Say($"Couldn't save the App guides choice: {why}.");
        }
    }

    // The first element under root with this automation ID.
    private static T? FindById<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && AutomationProperties.GetAutomationId(root) == id) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindById<T>(child, id) is { } found) return found;
        return null;
    }
}

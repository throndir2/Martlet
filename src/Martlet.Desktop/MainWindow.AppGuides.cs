using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Companion › App guides (docs/APP_GUIDES.md): the On switch (off by default) with what leaves this PC, Ask when I start a
/// game or app, the list of apps with their guides (Read up now or Read again, Ask again, Delete) and Add an app. The guide library
/// lives in &lt;data folder&gt;\guides; the watch of the program in front runs only while App guides are on, on this companion PC.</summary>
public partial class MainWindow
{
    private AppGuideService? appGuides;
    private AppGuideWatch? appGuideWatch;
    // The library revision the page last drew its list for: other changes (reading-up progress) only update its status lines.
    private int appGuidesDrawn = -1;
    private TextBlock? appGuidesNowText, appGuidesFrontText;
    private readonly Dictionary<string, TextBlock> appGuideRows = new(StringComparer.Ordinal);
    // What the Add an app boxes hold, kept while the page draws again.
    private string appGuideAddName = "", appGuideAddPrograms = "", appGuideAddSites = "";
    private string? appGuidesResult;

    private void InitializeAppGuides()
    {
        if (store is null) return;
        var web = new WebAccess();
        appGuides = new AppGuideService(new FileAppGuideStore(Path.Combine(store.DataDirectory, "guides")), new WebGuideBuilder(web, web), web);
        if (conversation is not null) conversation.Guides = appGuides;
        appGuideWatch = new AppGuideWatch(appGuides);
        appGuideWatch.OfferDue += offer => Dispatcher.BeginInvoke(() => OfferAppGuide(offer));
        appGuides.Changed += () => Dispatcher.BeginInvoke(AppGuidesChanged);
        LoadAppGuidesAsync().Forget();
    }

    private async Task LoadAppGuidesAsync()
    {
        if (appGuides is null) return;
        try
        {
            await appGuides.LoadAsync(lifetime.Token);
            var library = appGuides.Library;
            ErrorLog.Info($"App guides: {(library.On ? "on" : "off")}, {library.Apps.Count(a => a.BuiltAt is not null)} guides, " +
                $"{library.Apps.Count} apps on the list." + (appGuides.LoadProblem is { } problem ? $" The library couldn't be read: {problem}." : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorLog.Warn("App guides: couldn't read the guide library.", error);
        }
    }

    private void StopAppGuides()
    {
        appGuideWatch?.Dispose();
        appGuides?.Dispose();
    }

    /// <summary>The library, the app in front or reading up changed (on the UI thread).</summary>
    private void AppGuidesChanged()
    {
        if (closing || appGuides is null) return;
        if (appGuideWatch is not null) appGuideWatch.On = appGuides.On && Role == DeviceRole.Companion;
        if (openTab != CompanionTab.AppGuides) return;
        if (appGuides.Revision != appGuidesDrawn) RenderTab();
        else RenderAppGuidesStatus();
    }

    /// <summary>The watch saw a game (or an app on the list) without a guide in front: Martlet offers to read up on it, in the
    /// conversation that runs (never one started for it).</summary>
    private void OfferAppGuide(AppGuideOfferCandidate offer)
    {
        if (closing || appGuides is null || appGuides.Offer() is not { } still || still.Key != offer.Key) return;
        if (openConversation?.OfferGuide(offer) is not { } job) return;
        appGuides.Offered(offer.Key, () => job.Delivery == BackgroundDeliveryState.Dropped);
    }

    private void RenderAppGuidesTab(Panel page)
    {
        appGuideRows.Clear();
        appGuidesNowText = appGuidesFrontText = null;
        if (appGuides is not { } guides)
        {
            page.Children.Add(PageNowCard("App guides are unavailable until Martlet's data folder can be used.", null, "AppGuidesNow"));
            return;
        }
        if (!guides.Loaded)
        {
            page.Children.Add(PageNowCard("Reading your app guides...", null, "AppGuidesNow"));
            return;
        }
        appGuidesDrawn = guides.Revision;
        var library = guides.Library;
        var now = PageNowCard(AppGuidesNow(guides), null, "AppGuidesNow",
            guides.LoadProblem is { } unread ? $"Martlet couldn't read your guide library ({unread}). It wasn't changed. Turning App guides on starts a new library and keeps a copy of the old file, unless a newer Martlet wrote it." : null);
        appGuidesNowText = FindById<TextBlock>(now, "AppGuidesNow");
        page.Children.Add(now);

        var on = new CheckBox { Content = "Read up on games and apps", IsChecked = library.On };
        AutomationProperties.SetAutomationId(on, "AppGuidesOn");
        on.Checked += (_, _) => SetAppGuidesOnAsync(true).Forget();
        on.Unchecked += (_, _) => SetAppGuidesOnAsync(false).Forget();
        var disclosure = Note("When Martlet reads up on an app, it sends the app's name to DuckDuckGo to find its wiki, and the wiki " +
            "sites see the requests and this PC's internet address. The guides stay on this PC. To notice a game, Martlet looks only " +
            "at the name of the program in front and whether it fills the screen, never at window titles or the screen.",
            new Thickness(24, 2, 0, 8));
        AutomationProperties.SetAutomationId(disclosure, "AppGuidesDisclosure");
        var ask = new CheckBox { Content = "Ask when I start a game or app", IsChecked = library.AskWhenStarted, IsEnabled = library.On };
        AutomationProperties.SetAutomationId(ask, "AppGuidesAsk");
        ask.Checked += (_, _) => SetAppGuidesAskAsync(true).Forget();
        ask.Unchecked += (_, _) => SetAppGuidesAskAsync(false).Forget();
        var askNote = Note("When a game, or an app on your list, without a guide comes to the front, Martlet asks once, in character, " +
            "whether it should read up on it. If you say no, it doesn't ask about that app again.", new Thickness(24, 2, 0, 4));
        var front = Note(AppGuidesFront(guides), new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(front, "AppGuidesFront");
        AutomationProperties.SetLiveSetting(front, AutomationLiveSetting.Polite);
        appGuidesFrontText = front;
        page.Children.Add(Card(Heading("App guides"), on, disclosure, ask, askNote, front));

        var list = new List<UIElement>
        {
            Heading("Your apps"),
            Note("While an app is in front, or you name it, the best matching parts of its guide go with what you ask. You can also " +
                "say \"read up on Stardew Valley\".", new Thickness(0, 0, 0, 8))
        };
        if (library.Apps.Count == 0)
        {
            var empty = Note("No apps yet. Add one below, or ask Martlet to read up on one.", new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(empty, "AppGuidesEmpty");
            list.Add(empty);
        }
        foreach (var entry in library.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            list.Add(AppGuideRow(guides, entry, library.On));
        if (appGuidesResult is { } result)
        {
            var said = Note(result, new Thickness(0, 8, 0, 0));
            AutomationProperties.SetAutomationId(said, "AppGuidesResult");
            AutomationProperties.SetLiveSetting(said, AutomationLiveSetting.Polite);
            list.Add(said);
        }
        page.Children.Add(Card([.. list]));

        page.Children.Add(AddAppGuideCard(guides));
    }

    private UIElement AppGuideRow(AppGuideService guides, AppGuideEntry entry, bool on)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
        var reading = guides.Building?.Key == entry.Key;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var read = PageButton(entry.BuiltAt is null ? "Read up now" : "Read again", () => ReadUpFromPageAsync(entry).Forget(),
            id: "AppGuideRead-" + entry.Key);
        read.IsEnabled = on && guides.Building is null;
        if (!on) read.ToolTip = "Turn App guides on first.";
        buttons.Children.Add(read);
        if (entry.Declined)
        {
            var again = PageButton("Ask again", () => AppGuideAskAgainAsync(entry).Forget(), id: "AppGuideAskAgain-" + entry.Key);
            again.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(again);
        }
        var delete = PageButton("Delete", () => DeleteAppGuideAsync(entry).Forget(), id: "AppGuideDelete-" + entry.Key);
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.IsEnabled = !reading;
        buttons.Children.Add(delete);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { Text = entry.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(name, "AppGuideName-" + entry.Key);
        text.Children.Add(name);
        var status = Note(AppGuideStatus(guides, entry), new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(status, "AppGuideStatus-" + entry.Key);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        appGuideRows[entry.Key] = status;
        text.Children.Add(status);
        row.Children.Add(text);
        return row;
    }

    private Border AddAppGuideCard(AppGuideService guides)
    {
        var name = new TextBox { Text = appGuideAddName, MinWidth = 260 };
        AutomationProperties.SetAutomationId(name, "AppGuideAddName");
        AutomationProperties.SetName(name, "App name");
        name.TextChanged += (_, _) => appGuideAddName = name.Text;
        var programs = new TextBox { Text = appGuideAddPrograms, MinWidth = 260 };
        AutomationProperties.SetAutomationId(programs, "AppGuideAddPrograms");
        AutomationProperties.SetName(programs, "Program names (optional)");
        programs.TextChanged += (_, _) => appGuideAddPrograms = programs.Text;
        var sites = new TextBox { Text = appGuideAddSites, MinWidth = 260 };
        AutomationProperties.SetAutomationId(sites, "AppGuideAddSites");
        AutomationProperties.SetName(sites, "Wiki addresses (optional)");
        sites.TextChanged += (_, _) => appGuideAddSites = sites.Text;
        var on = guides.On;
        var add = PageButton("Add", () => AddAppGuideAsync(read: false).Forget(), id: "AppGuideAdd");
        var addRead = PageButton("Add and read up", () => AddAppGuideAsync(read: true).Forget(), primary: true, id: "AppGuideAddRead");
        addRead.IsEnabled = on && guides.Building is null;
        return Card(Heading("Add an app"),
            Note("Add a game or app Martlet should know: its name, the program names Windows shows for it (if they differ, " +
                "separated by commas) and the wiki or help pages to read (optional; Martlet finds its wiki itself otherwise).",
                new Thickness(0, 0, 0, 4)),
            Labeled("Name", WithHint(name, "Stardew Valley")),
            Labeled("Program names", WithHint(programs, "Optional, such as StardewValley")),
            Labeled("Wiki pages", WithHint(sites, "Optional, such as stardewvalleywiki.com")),
            Row(addRead, add));
    }

    /// <summary>The Now card's line: off, or how many guides and what is being read.</summary>
    private static string AppGuidesNow(AppGuideService guides)
    {
        var library = guides.Library;
        if (!library.On) return "Off. Martlet doesn't read up on games and apps, or notice which one you play.";
        var built = library.Apps.Where(a => a.BuiltAt is not null).ToArray();
        var text = built.Length == 0 ? "On. No guides yet."
            : $"On. {built.Length} guide{(built.Length == 1 ? "" : "s")}, {built.Sum(a => a.Pages)} pages, {Size(built.Sum(a => a.Bytes))}.";
        if (guides.Building is { } reading) text += $" Reading up on {reading.Name}: {reading.Progress ?? "starting"}.";
        return text;
    }

    /// <summary>What is in front, as App guides sees it.</summary>
    private static string AppGuidesFront(AppGuideService guides)
    {
        if (!guides.On) return "Martlet doesn't look at which app is in front while App guides are off.";
        if (guides.Front is not { } front) return "In front: nothing Martlet has noticed yet.";
        var name = front.Entry?.Name ?? (front.Program.Length > 0 ? front.Program : front.File);
        var what = front.Game ? "a game" : front.Entry is not null ? "on your list" : "not a game";
        var guide = front.Entry is { BuiltAt: not null } entry
            ? guides.IsReady(entry.Key) ? "its guide is ready" : "its guide is loading"
            : "no guide";
        return $"In front: {name} ({what}{(front.FullScreen ? ", full screen" : "")}), {guide}.";
    }

    /// <summary>A row's line: its guide (pages, size, when, where from), a problem, being read now, or declined.</summary>
    private static string AppGuideStatus(AppGuideService guides, AppGuideEntry entry)
    {
        string text;
        if (guides.Building is { } reading && reading.Key == entry.Key) text = $"Reading up now: {reading.Progress ?? "starting"}.";
        else if (entry.BuiltAt is { } at)
            text = $"{entry.Pages} page{(entry.Pages == 1 ? "" : "s")}, {Size(entry.Bytes)}, read {at.ToLocalTime():d MMM yyyy, HH:mm}." +
                (entry.Problem is { } failed ? $" The last try didn't work: {failed}." : "");
        else text = entry.Problem is { } problem ? $"No guide yet. The last try didn't work: {problem}." : "No guide yet.";
        if (guides.GuideProblem(entry.Key) is { } unread) text += $" Its guide can't be used: {unread}. Read it again to make a new one.";
        if (entry.Sites.Count > 0)
            text += " Reads from " + string.Join(", ", entry.Sites.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var url) ? url.Host : s)) + ".";
        if (entry.Programs.Count > 0) text += " Programs: " + string.Join(", ", entry.Programs) + ".";
        if (entry.Declined) text += " You said no when Martlet offered, so it doesn't ask about it.";
        return text;
    }

    private static string Size(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{Math.Max(1, bytes / 1_000)} KB";

    // Only the status lines change while Martlet reads up (the boxes keep their focus).
    private void RenderAppGuidesStatus()
    {
        if (appGuides is not { } guides) return;
        if (appGuidesNowText is not null) appGuidesNowText.Text = AppGuidesNow(guides);
        if (appGuidesFrontText is not null) appGuidesFrontText.Text = AppGuidesFront(guides);
        foreach (var entry in guides.Library.Apps)
            if (appGuideRows.TryGetValue(entry.Key, out var line)) line.Text = AppGuideStatus(guides, entry);
    }

    private async Task SetAppGuidesOnAsync(bool on)
    {
        if (appGuides is null || appGuides.On == on) return;
        try
        {
            await appGuides.SetOnAsync(on, lifetime.Token);
            ErrorLog.Info($"App guides: you turned them {(on ? "on" : "off")}.");
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ActionText.Text = $"Couldn't save the App guides choice: {why}.";
        }
    }

    private async Task SetAppGuidesAskAsync(bool ask)
    {
        if (appGuides is null || appGuides.Library.AskWhenStarted == ask) return;
        try { await appGuides.SetAskAsync(ask, lifetime.Token); }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ActionText.Text = $"Couldn't save the App guides choice: {why}.";
        }
    }

    private async Task AddAppGuideAsync(bool read)
    {
        if (appGuides is null) return;
        var name = AppGuideTools.CleanName(appGuideAddName);
        if (name.Length == 0)
        {
            appGuidesResult = "Type the app's name first.";
            RenderTab();
            return;
        }
        var programs = appGuideAddPrograms.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var given = appGuideAddSites.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sites = given.Select(AppGuideTools.Site).OfType<string>().ToArray();
        try
        {
            var entry = await appGuides.AddAsync(name, programs, sites, lifetime.Token);
            appGuideAddName = appGuideAddPrograms = appGuideAddSites = "";
            appGuidesResult = $"Added {entry.Name}." + (sites.Length < given.Length ? " Some addresses weren't web links, so they were left out." : "");
            ErrorLog.Info($"App guides: you added an app ({entry.Programs.Count} program names, {entry.Sites.Count} start pages).");
            if (read) await ReadUpFromPageAsync(entry);
            else if (openTab == CompanionTab.AppGuides) RenderTab();
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ErrorLog.Warn($"App guides: couldn't add an app ({why}).");
            appGuidesResult = $"Couldn't add it: {why}.";
            if (openTab == CompanionTab.AppGuides) RenderTab();
        }
    }

    /// <summary>Read up now (or Read again): in the conversation that runs, as its background job, else here on its own.</summary>
    private async Task ReadUpFromPageAsync(AppGuideEntry entry)
    {
        if (appGuides is not { On: true } guides) return;
        if (guides.Building is { } busy)
        {
            appGuidesResult = $"Martlet is already reading up on {busy.Name}.";
            if (openTab == CompanionTab.AppGuides) RenderTab();
            return;
        }
        if (openConversation?.ReadUp(guides, entry.Name, entry.Sites) is { } start)
        {
            appGuidesResult = start.Job is { } job ? $"Reading up on {entry.Name} in the conversation ({job.Id})."
                : $"Couldn't start reading up: {start.Message ?? start.Refusal}";
            if (openTab == CompanionTab.AppGuides) RenderTab();
            return;
        }
        appGuidesResult = $"Reading up on {entry.Name}...";
        if (openTab == CompanionTab.AppGuides) RenderTab();
        AppGuideBuild built;
        try { built = await Task.Run(() => guides.BuildAsync(entry.Name, entry.Sites, null, lifetime.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ErrorLog.Warn($"App guides: couldn't keep a guide read from the App guides page ({why}).");
            appGuidesResult = $"Couldn't keep the guide about {entry.Name}: {why}.";
            if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
            return;
        }
        LiveConversationController.LogBuild("the App guides page", built);
        appGuidesResult = built.Built
            ? $"Read up on {built.Name}: {built.Pages} page{(built.Pages == 1 ? "" : "s")}, {built.Chunks} sections."
            : $"Couldn't read up on {built.Name}: {built.Problem}.";
        if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
    }

    private async Task DeleteAppGuideAsync(AppGuideEntry entry)
    {
        if (appGuides is null) return;
        try
        {
            appGuidesResult = await appGuides.DeleteAsync(entry.Key, lifetime.Token)
                ? $"Deleted {entry.Name} and its guide." : $"Martlet is reading up on {entry.Name}; delete it once that's done.";
            ErrorLog.Info("App guides: you deleted an app and its guide.");
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            appGuidesResult = $"Couldn't delete it: {why}.";
        }
        if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
    }

    private async Task AppGuideAskAgainAsync(AppGuideEntry entry)
    {
        if (appGuides is null) return;
        try
        {
            await appGuides.AskAgainAsync(entry.Key, lifetime.Token);
            appGuidesResult = $"Martlet may offer to read up on {entry.Name} again.";
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            appGuidesResult = $"Couldn't change it: {why}.";
        }
        if (!closing && openTab == CompanionTab.AppGuides) RenderTab();
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

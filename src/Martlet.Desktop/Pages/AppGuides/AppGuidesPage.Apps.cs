using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Companion › App guides: the Your apps card, one row for each app with its status line and its buttons (Read up now or
/// Read again, Read more, Stop reading, Allow again, Delete).</summary>
internal sealed partial class AppGuidesPage
{
    private Border AppsCard(AppGuideService guides, AppGuidesPageModel model)
    {
        var library = guides.Library;
        var list = new List<UIElement>
        {
            Heading("Your apps"),
            HelpTip.Explain("While an app is in front, or you name it, the best matching parts of its guide go with what you ask. " +
                "You can also say \"read up on Stardew Valley\".", new Thickness(0, 0, 0, 8), "AppGuidesUse", "how guides are used",
                wiki: "App-Guides#how-she-uses-a-guide")
        };
        if (library.Apps.Count == 0)
        {
            var empty = Note("No apps yet: start a game, add one below or ask Martlet to read up on one.", new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(empty, "AppGuidesEmpty");
            list.Add(empty);
        }
        foreach (var entry in library.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            list.Add(AppRow(guides, model, entry, library.On));
        if (services.Result is { } result)
        {
            var said = Note(result, new Thickness(0, 8, 0, 0));
            AutomationProperties.SetAutomationId(said, "AppGuidesResult");
            AutomationProperties.SetLiveSetting(said, AutomationLiveSetting.Polite);
            list.Add(said);
        }
        return Card([.. list]);
    }

    private DockPanel AppRow(AppGuideService guides, AppGuidesPageModel model, AppGuideEntry entry, bool on)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
        var reading = guides.Building?.Key == entry.Key;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (reading)
        {
            // A read stops by itself at its caps; Stop reading ends it sooner and keeps what it read.
            var stop = PageButton("Stop reading", () => StopReading(entry), id: "AppGuideStop-" + entry.Key);
            stop.ToolTip = "Stop reading up on it now and keep what Martlet read so far.";
            buttons.Children.Add(stop);
        }
        else
        {
            var read = PageButton(entry.BuiltAt is null ? "Read up now" : "Read again", () => services.ReadUp(entry).Forget(),
                id: "AppGuideRead-" + entry.Key);
            read.IsEnabled = on && guides.Building is null;
            read.ToolTip = !on ? "Turn App guides on first." : entry.BuiltAt is null ? null
                : "Read its first pages again for what changed (the pages Read more added stay in the guide).";
            buttons.Children.Add(read);
            if (entry.BuiltAt is not null)
            {
                // Read more: more pages of the same wiki join the guide; it never reads the pages the guide has again.
                var more = PageButton("Read more", () => services.ReadMore(entry), id: "AppGuideMore-" + entry.Key);
                more.Margin = new Thickness(8, 0, 0, 0);
                more.IsEnabled = on && guides.Building is null;
                more.ToolTip = on ? "Read more pages of its wiki and add them to the guide." : "Turn App guides on first.";
                buttons.Children.Add(more);
            }
        }
        if (entry.Declined)
        {
            var again = PageButton("Allow again", () => AskAgainAsync(entry).Forget(), id: "AppGuideAskAgain-" + entry.Key);
            again.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(again);
        }
        var delete = PageButton("Delete", () => DeleteAsync(entry).Forget(), id: "AppGuideDelete-" + entry.Key);
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.IsEnabled = !reading;
        buttons.Children.Add(delete);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { Text = entry.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(name, "AppGuideName-" + entry.Key);
        text.Children.Add(name);
        var status = Note(model.Status(entry), new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(status, "AppGuideStatus-" + entry.Key);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        rows[entry.Key] = status;
        text.Children.Add(status);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Stop reading: ends the read now; the read keeps what it read and says so when it ends.</summary>
    private void StopReading(AppGuideEntry entry)
    {
        if (services.Guides is not { } guides) return;
        var stopped = guides.StopReading();
        ErrorLog.Info($"App guides: you stopped reading up ({(stopped ? "stopping" : "nothing was being read")}).");
        services.Result = stopped ? $"Stopping, and Martlet keeps what it read about {entry.Name}." : "Martlet isn't reading up now.";
        services.RedrawIfOpen();
    }

    private async Task DeleteAsync(AppGuideEntry entry)
    {
        if (services.Guides is not { } guides) return;
        try
        {
            services.Result = await guides.DeleteAsync(entry.Key, services.Token)
                ? $"Deleted {entry.Name} and its guide." : $"Martlet is reading up on {entry.Name}; delete it once that's done.";
            ErrorLog.Info("App guides: you deleted an app and its guide.");
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            services.Result = $"Couldn't delete it: {why}.";
        }
        services.RedrawIfOpen();
    }

    private async Task AskAgainAsync(AppGuideEntry entry)
    {
        if (services.Guides is not { } guides) return;
        try
        {
            await guides.AskAgainAsync(entry.Key, services.Token);
            services.Result = $"Martlet may read up on {entry.Name} on its own again.";
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            services.Result = $"Couldn't change it: {why}.";
        }
        services.RedrawIfOpen();
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Companion › App guides: the Add an app card (the name, the program names and the wiki pages, then Add and read up or
/// Add).</summary>
internal sealed partial class AppGuidesPage
{
    // The Add an app boxes are made once and moved into each new drawing of the page. The page draws again while you type (reading
    // up starts, adds the app and ends; Home refreshes), and a new box would lose your caret, so you could no longer type or delete.
    private TextBox? addName, addPrograms, addSites;

    private Border AddCard(AppGuideService guides)
    {
        var on = guides.On;
        var add = PageButton("Add", () => AddAsync(read: false).Forget(), id: "AppGuideAdd");
        var addRead = PageButton("Add and read up", () => AddAsync(read: true).Forget(), primary: true, id: "AppGuideAddRead");
        addRead.IsEnabled = on && guides.Building is null;
        return Card(Heading("Add an app"),
            Note("Add a game or app Martlet should know: its name, the program names Windows shows for it (if they differ, " +
                "separated by commas) and the wiki or help pages to read (optional; Martlet finds its wiki itself otherwise).",
                new Thickness(0, 0, 0, 4)),
            Field("Name", AddBox(ref addName, "AppGuideAddName", "App name", "Stardew Valley")),
            Field("Program names", AddBox(ref addPrograms, "AppGuideAddPrograms", "Program names (optional)",
                "Optional, such as StardewValley")),
            Field("Wiki pages", AddBox(ref addSites, "AppGuideAddSites", "Wiki addresses (optional)",
                "Optional, such as stardewvalleywiki.com")),
            Row(addRead, add));

        // The labels line up with the card's heading, text and buttons, not indented like an option's details.
        static DockPanel Field(string label, UIElement box)
        {
            var row = MainWindow.Labeled(label, box);
            row.Margin = new Thickness(0, 6, 0, 0);
            return row;
        }
    }

    /// <summary>An Add an app box with its hint, made the first time and taken out of the page's last drawing after that, so it keeps
    /// its text, caret, selection and undo. The box you type in keeps the keyboard focus too: it is on the new page before WPF
    /// looks again at where the focus is.</summary>
    private static Grid AddBox(ref TextBox? kept, string id, string name, string hint)
    {
        if (kept is null)
        {
            kept = new TextBox { MinWidth = 260 };
            AutomationProperties.SetAutomationId(kept, id);
            AutomationProperties.SetName(kept, name);
            WithHint(kept, hint, id + "Hint");
        }
        var field = (Grid)kept.Parent;
        if (field.Parent is Panel drawn) drawn.Children.Remove(field);
        return field;
    }

    /// <summary>What an Add an app box holds now ("" before the page first draws).</summary>
    private static string AddText(TextBox? box) => box?.Text ?? "";

    private async Task AddAsync(bool read)
    {
        if (services.Guides is not { } guides) return;
        var (name, programs, given, sites) = AppGuidesPageModel.AddInput(AddText(addName), AddText(addPrograms), AddText(addSites));
        if (name.Length == 0)
        {
            services.Result = "Type the app's name first.";
            services.Redraw();
            return;
        }
        try
        {
            var entry = await guides.AddAsync(name, programs, sites, services.Token);
            foreach (var box in new[] { addName, addPrograms, addSites }) box?.Clear();
            services.Result = AppGuidesPageModel.Added(entry, sites.Length, given.Length);
            ErrorLog.Info($"App guides: you added an app ({entry.Programs.Count} program names, {entry.Sites.Count} start pages).");
            if (read) await services.ReadUp(entry);
            else services.RedrawIfOpen();
        }
        catch (Exception error) when (AppGuideService.Refusal(error) is { } why)
        {
            ErrorLog.Warn($"App guides: couldn't add an app ({why}).");
            services.Result = $"Couldn't add it: {why}.";
            services.RedrawIfOpen();
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Memory › Memory brief: whether Martlet writes and sends a short profile of you (on by default while
/// memory is on), what it says now (you can change it: your own text stays until you let Martlet write it again), and Write it
/// again. The brief is the same on all your computers (the memory-brief shared setting).</summary>
public partial class MainWindow
{
    private TextBlock? memoryBriefStatus;
    private TextBox? memoryBriefBox;
    // The text the box showed last, so a new brief replaces it only while you haven't changed it.
    private string memoryBriefShown = "";
    private bool memoryBriefWired;

    private Border MemoryBriefCard(MemoryBriefService brief, bool memoryOn)
    {
        if (!memoryBriefWired)
        {
            memoryBriefWired = true;
            brief.Changed += () => Dispatcher.InvokeAsync(() => ShowMemoryBrief(brief));
        }
        var saved = brief.Current;
        var status = Note(brief.Status(memoryOn), new Thickness(0, 2, 0, 8));
        AutomationProperties.SetAutomationId(status, "MemoryBriefStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        memoryBriefStatus = status;
        var on = new CheckBox
        {
            Content = "Send a short profile of me with the first message of each conversation", IsChecked = saved.On, IsEnabled = memoryOn
        };
        AutomationProperties.SetAutomationId(on, "MemoryBriefOn");
        var box = new TextBox
        {
            Text = saved.Text ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = MemoryBriefFile.MaximumCharacters,
            IsEnabled = memoryOn && saved.On, Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetAutomationId(box, "MemoryBriefText");
        AutomationProperties.SetName(box, "Memory brief");
        InputHint.SetText(box, "Alex is a vegetarian nurse in Leeds; Sam is Alex's sister, and they go hiking most weekends.");
        memoryBriefBox = box;
        memoryBriefShown = box.Text;
        Button? save = null, again = null;
        save = PageButton("Save my changes", () =>
        {
            var problem = brief.SaveOwn(box.Text);
            memoryBriefShown = brief.Current.Text ?? "";
            if (problem is null) box.Text = memoryBriefShown;
            ShowMemoryBrief(brief, problem);
        }, id: "MemoryBriefSave");
        again = PageButton("Write it again", () => WriteMemoryBriefAsync(brief, again!, save!).Forget(), id: "MemoryBriefWrite");
        save.IsEnabled = again.IsEnabled = memoryOn && saved.On;
        void Turn(bool value)
        {
            var problem = brief.SetOn(value);
            box.IsEnabled = save.IsEnabled = again.IsEnabled = memoryOn && brief.Current.On;
            ShowMemoryBrief(brief, problem);
        }
        on.Checked += (_, _) => Turn(true);
        on.Unchecked += (_, _) => Turn(false);
        return Card(Heading("Memory brief"), status, on,
            HelpTip.Explain("Martlet writes it in the background from what it remembers about you and the people you talk about. " +
                "It goes with the first message of each conversation, so Martlet knows the important things about you even when " +
                "nothing you said matches them. It is written again when your memories change, on a Thinking pool member (or the " +
                "Thinking model in the cloud, once you aren't talking), never while Martlet answers you. It is the same on all your " +
                "computers. Change how it is written in Companion › Prompts › Writing the memory brief.",
                new Thickness(24, 2, 0, 0), "MemoryBrief", "the memory brief", wiki: "Memory#memory-brief"),
            box,
            HelpTip.Explain("You can change it. Your own text stays as you wrote it until you choose Write it again; clear it and " +
                "save to let Martlet write it from your memories.", new Thickness(0, 4, 0, 0), "MemoryBriefEdit", "changing the brief",
                wiki: "Memory#memory-brief"),
            Row(save, again));
    }

    private async Task WriteMemoryBriefAsync(MemoryBriefService brief, Button again, Button save)
    {
        again.IsEnabled = save.IsEnabled = false;
        ShowMemoryBrief(brief);
        string outcome;
        try { outcome = await Task.Run(() => brief.WriteNowAsync(lifetime.Token)); }
        catch (OperationCanceledException) { return; }
        if (closing) return;
        if (outcome == "written" && memoryBriefBox is not null)
        {
            memoryBriefShown = brief.Current.Text ?? "";
            memoryBriefBox.Text = memoryBriefShown;
        }
        again.IsEnabled = save.IsEnabled = homeSettings?.Memory?.Enabled == true && brief.Current.On;
        ShowMemoryBrief(brief);
    }

    /// <summary>Follows the brief: its state line, and its text while you haven't changed the box.</summary>
    private void ShowMemoryBrief(MemoryBriefService brief, string? problem = null)
    {
        if (closing || memoryBriefStatus is null) return;
        var line = brief.Status(homeSettings?.Memory?.Enabled == true);
        memoryBriefStatus.Text = problem is null ? line : line.TrimEnd('.') + " — " + problem;
        if (memoryBriefBox is { IsKeyboardFocusWithin: false } box && box.Text == memoryBriefShown)
        {
            memoryBriefShown = brief.Current.Text ?? "";
            if (box.Text != memoryBriefShown) box.Text = memoryBriefShown;
        }
    }
}

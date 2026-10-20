using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Emotes and motions' Make a motion row (docs/AVATARS.md#motions-in-conversation): for a VRM while motions are
/// set up, what the body does, a name and Make; the motion is made on the motion computer and kept the same way body_motion does,
/// so it joins the list. The kept motions and the starter motions stay in MainWindow (MainWindow.CharacterMotions.cs).</summary>
internal sealed partial class EmotesPage
{
    private string? characterMotionStatus;

    /// <summary>Make a motion (Companion › Emotes and motions, a VRM while motions are set up): what the body does, a name, Make,
    /// and how it went; or, while motions aren't set up, a line that says what they need.</summary>
    private void AddMakeMotion(List<UIElement> stack, CharacterActionCatalog catalog)
    {
        if (catalog.Inventory.Renderer != AvatarRenderer.Vrm || services.Motions is not { } motions) return;
        var status = Note(characterMotionStatus ?? EmotesPageModel.MotionsStatus(motions), new Thickness(0, 8, 0, 4));
        AutomationProperties.SetAutomationId(status, "CharacterMotionStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        stack.Add(status);
        if (!motions.IsSetUp) return;
        var describe = Compact(new TextBox { MinWidth = 260, MaxLength = MotionTools.MaximumMotionCharacters });
        AutomationProperties.SetName(describe, "What the body does");
        AutomationProperties.SetAutomationId(describe, "CharacterMotionDescription");
        AutomationProperties.SetHelpText(describe, "Plain English body movement only, such as \"A person raises both fists above the head and jumps once.\"");
        var name = Compact(new TextBox { Width = 120, MaxLength = MotionTools.MaximumNameCharacters });
        AutomationProperties.SetName(name, "Motion name");
        AutomationProperties.SetAutomationId(name, "CharacterMotionName");
        InputHint.SetText(name, "cheer");
        var make = Compact(PageButton(motions.Busy ? "Making..." : "Make", () => MakeMotionFromPageAsync(describe.Text, name.Text, status).Forget(),
            id: "CharacterMotionMake"));
        make.IsEnabled = !motions.Busy;
        var fields = new FillWrapPanel { Margin = new Thickness(0, 4, 0, 0), FillMinimum = 300 };
        var what = new DockPanel { Margin = new Thickness(0, 0, 18, 6) };
        what.Children.Add(RowLabel("Make a motion", describe));
        what.Children.Add(WithHint(describe, "A person raises both fists above the head and jumps once."));
        fields.Children.Add(what);
        fields.Children.Add(RowGroup(RowLabel("Name", name), name, make));
        stack.Add(fields);
    }

    /// <summary>Make: makes the described motion on the motion computer and keeps it, the same way body_motion does; it joins the
    /// list, where Try plays it.</summary>
    private async Task MakeMotionFromPageAsync(string description, string name, TextBlock status)
    {
        if (services.Motions is not { } motions) return;
        var (arguments, problem) = MotionTools.Parse(JsonSerializer.Serialize(new { name, motion = description }));
        void Say(string text)
        {
            characterMotionStatus = text;
            status.Text = text;
        }
        if (arguments is null || arguments.Motion is null)
        {
            Say(problem is null ? "Write what the body does, then a name." : "Give the motion a name in English letters, and write what the body does.");
            return;
        }
        using var studio = motions.Studio();
        if (studio is null)
        {
            Say("Motions aren't set up.");
            return;
        }
        Say($"Making {arguments.Name}...");
        try
        {
            var (creation, made) = await Task.Run(() => motions.MakeAndKeepAsync(arguments, studio,
                step => status.Dispatcher.InvokeAsync(() => status.Text = step + "..."), services.Lifetime), services.Lifetime);
            await services.Actions.RefreshKeptMotionsAsync(services.Lifetime);
            Say($"Kept {arguments.Name} ({creation.Key}){(made.Fixture ? ", a FIXTURE test motion (NOT AI)" : "")}, made on {made.Where} in " +
                $"{made.Took.TotalSeconds:0.#} s; it's in the list below, and Try plays it.");
            ErrorLog.Info($"Motions: the owner made {arguments.Name} ({creation.Key}) on {made.Where}{(made.Fixture ? ", FIXTURE - NOT AI" : "")}.");
            if (services.Open && !services.Typing) services.Redraw();
        }
        catch (MotionStudioException error) { Say($"{arguments.Name} wasn't made: {error.Message}."); }
        catch (OperationCanceledException) { }
        finally { studio.FreeLater(); }
    }
}

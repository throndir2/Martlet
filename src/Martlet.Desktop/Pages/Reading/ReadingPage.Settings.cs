using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;

namespace Martlet.Desktop;

/// <summary>Companion › Reading: a member's Settings in the list (This PC's engine, and the Reading role's model, where it stands
/// and Set up or Switch there).</summary>
internal sealed partial class ReadingPage
{
    /// <summary>A member's settings: This PC's engine (Windows OCR or the Reading role), and for the Reading role its model, where
    /// it stands and Set up or Switch there.</summary>
    private StackPanel MemberSettings(ReadingPageModel model, PoolMember member)
    {
        var stack = new StackPanel();
        if (member.Kind == PoolMemberKind.ThisPc)
        {
            var engines = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var (engine, label) in new[] { (ReadingPool.WindowsOcr, "Windows OCR (recommended)"), (ReadingPool.Role, "Martlet's Reading role") })
            {
                var pill = new RadioButton { Content = label, GroupName = "ReadingEngine", IsChecked = ReadingPool.UsesWindows(member) == (engine == ReadingPool.WindowsOcr) };
                pill.SetResourceReference(FrameworkElement.StyleProperty, "FilterPill");
                AutomationProperties.SetName(pill, "This PC reads with " + label);
                AutomationProperties.SetAutomationId(pill, "ReadingEngine-" + engine);
                pill.Checked += (_, _) => services.SaveMember(member.WithSetting(PoolSettingKeys.Engine, engine),
                    engine == ReadingPool.WindowsOcr ? "This PC now reads with Windows OCR." : "This PC now reads with Martlet's Reading role.");
                engines.Children.Add(pill);
            }
            stack.Children.Add(engines);
            if (ReadingPool.UsesWindows(member))
            {
                var state = Note(model.WindowsStateText, new Thickness(0, 4, 0, 0));
                if (services.WindowsReads == false) state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                AutomationProperties.SetAutomationId(state, "ReadingWindowsState");
                stack.Children.Add(state);
                return stack;
            }
        }
        foreach (var element in RolePanel(model, member)) stack.Children.Add(element);
        return stack;
    }

    /// <summary>The Reading role's settings of a member: the model pills (choosing one only shows it), the model's note, where
    /// it stands on that computer, and Set up there (or Switch to the picked model).</summary>
    private List<UIElement> RolePanel(ReadingPageModel model, PoolMember member)
    {
        var role = model.Role(member);
        var (place, target, where, chosen) = (role.Place, role.Target, role.Where, role.Chosen);
        var stack = new List<UIElement>();

        // The model: PP-OCRv5 mobile (recommended) on the processor or an NVIDIA graphics card, PP-OCRv5 server (an NVIDIA
        // graphics card only) or RapidOCR.
        var models = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        foreach (var option in OptionalExtras.ReadingModels)
        {
            var label = ReadingPageModel.ModelLabel(option);
            var pill = new RadioButton { Content = label, GroupName = "ReadingModel-" + place, IsChecked = option == chosen, IsEnabled = services.Pending is null };
            pill.SetResourceReference(FrameworkElement.StyleProperty, "FilterPill");
            AutomationProperties.SetName(pill, $"{label} on {where}");
            AutomationProperties.SetAutomationId(pill, $"ReadingModel-{place}-{option.Key}");
            if (ReadingPageModel.ModelBlocked(role, option) is { } why)
            {
                pill.IsEnabled = false;
                pill.ToolTip = why;
                ToolTipService.SetShowOnDisabled(pill, true);
                AutomationProperties.SetHelpText(pill, why);
            }
            pill.Checked += (_, _) =>
            {
                if (services.PickedModel(place) == option.Key) return;
                services.PickModel(place, option.Key);
                services.Redraw();
            };
            models.Children.Add(pill);
        }
        stack.Add(models);
        var modelNote = HelpTip.Explain(ReadingPageModel.ModelNote(chosen), new Thickness(0, 0, 0, 0), "ReadingModels-" + place, "reading models",
            wiki: "Vision#set-up-the-reading-role");
        AutomationProperties.SetAutomationId(modelNote, "ReadingModelNote-" + place);
        stack.Add(modelNote);

        var stateLine = Note(role.State, new Thickness(0, 4, 0, 0));
        if (role.Warning) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(stateLine, "ReadingHostState-" + place);
        stack.Add(stateLine);
        var host = role.OnThisPc ? null : target;
        if (role.Ready)
        {
            if (role.Other && target is { Shared: false })
            {
                var change = PageButton(role.Pending ? "Switching..." : $"Switch to {chosen.Name}",
                    () => services.SetUp(host, chosen, changing: true), primary: true, id: "ReadingSwitch-" + place);
                change.IsEnabled = services.Pending is null;
                stack.Add(Row(change));
            }
            return stack;
        }
        var setUp = PageButton(role.Pending ? "Setting up..." : $"Set up {chosen.Name}", () => services.SetUp(host, chosen, changing: false),
            primary: true, id: "ReadingSetUp-" + place);
        setUp.IsEnabled = !role.Pending && role.Cannot is null && services.Pending is null;
        if (role.Cannot is { } cannot)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        stack.Add(Row(setUp));
        return stack;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Add a person (the account picker): a name for a new account on this Windows login, with no password. Add calls the
/// given function, which makes the account and returns its ID, or a message to show when the name can't be used.</summary>
internal sealed class AddPersonDialog : ThemedWindow
{
    private readonly Func<string, (Guid? Id, string? Problem)> add;
    private readonly TextBox name = new() { MaxLength = Martlet.Core.Accounts.Account.MaximumNameLength, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };

    internal AddPersonDialog(Func<string, (Guid? Id, string? Problem)> add)
    {
        this.add = add;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Add a person";
        Width = 520;
        MinWidth = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "AddPersonDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Add a person" };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Someone else who uses this Windows sign-in gets their own account: their own characters, personalities and " +
                   "memories. They switch to it from the account button. No password is needed."
        });
        var label = new Label { Content = "_Name", Target = name, Padding = new Thickness(0, 0, 0, 4) };
        root.Children.Add(label);
        AutomationProperties.SetName(name, "Name");
        AutomationProperties.SetAutomationId(name, "AddPersonName");
        name.TextChanged += (_, _) => problem.Visibility = Visibility.Collapsed;
        root.Children.Add(name);
        problem.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(problem, "AddPersonProblem");
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
        root.Children.Add(problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(cancel, "AddPersonCancel");
        var ok = new Button { Content = "_Add", IsDefault = true, MinWidth = 90 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "AddPersonAdd");
        ok.Click += (_, _) => Add();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => name.Focus();
    }

    /// <summary>The new account, once Add made it.</summary>
    internal Guid? Added { get; private set; }

    private void Add()
    {
        var (id, why) = add(name.Text);
        if (id is { } made)
        {
            Added = made;
            DialogResult = true;
            return;
        }
        problem.Text = why ?? "Martlet couldn't add this person.";
        AutomationProperties.SetName(problem, problem.Text);
        problem.Visibility = Visibility.Visible;
        name.Focus();
    }
}

using System.Windows;
using System.Windows.Automation;
using Martlet.Presentation;

namespace Martlet.Desktop;

public partial class ConfirmationDialog : ThemedWindow
{
    /// <summary><paramref name="yes"/> and <paramref name="no"/> relabel the buttons (No stays the default and Esc);
    /// <paramref name="questionId"/> gives the question an automation ID, for a question MCP may read.</summary>
    internal ConfirmationDialog(string title, string question, string? yes = null, string? no = null, string? questionId = null)
    {
        InitializeComponent();
        Title = title;
        QuestionText.Text = question;
        if (yes is not null) YesButton.Content = yes;
        if (no is not null) NoButton.Content = no;
        if (questionId is not null) AutomationProperties.SetAutomationId(QuestionText, questionId);
    }

    internal static bool Confirm(Window owner, string question, string title, string? yes = null, string? no = null,
        string? questionId = null) =>
        new ConfirmationDialog(title, question, yes, no, questionId) { Owner = owner }.ShowDialog() == true;

    /// <summary>The question with a choice under it (<paramref name="option"/>, ticked when <paramref name="optionOn"/>).
    /// Changing the choice closes the dialog without an answer: <c>Changed</c> is true and the caller asks again.</summary>
    internal static (bool Yes, bool Changed) Confirm(Window owner, string question, string title, string option, bool optionOn,
        string? yes = null, string? no = null, string? questionId = null)
    {
        var dialog = new ConfirmationDialog(title, question, yes, no, questionId) { Owner = owner };
        dialog.loading = true;
        dialog.OptionBox.Content = option;
        dialog.OptionBox.IsChecked = optionOn;
        dialog.OptionBox.Visibility = Visibility.Visible;
        dialog.loading = false;
        var yesAnswer = dialog.ShowDialog() == true;
        return (yesAnswer && !dialog.optionChanged, dialog.optionChanged);
    }

    private bool loading, optionChanged;

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        optionChanged = true;
        DialogResult = false;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Copy_Click(object sender, RoutedEventArgs e) =>
        CopyText.From(CopyButton, $"{CopyText.Product}: {Title}{Environment.NewLine}{Environment.NewLine}{QuestionText.Text}");
}

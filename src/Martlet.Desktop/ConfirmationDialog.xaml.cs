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

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Copy_Click(object sender, RoutedEventArgs e) =>
        CopyText.From(CopyButton, $"{CopyText.Product}: {Title}{Environment.NewLine}{Environment.NewLine}{QuestionText.Text}");
}

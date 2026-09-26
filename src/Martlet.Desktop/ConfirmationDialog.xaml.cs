using System.Windows;

namespace Martlet.Desktop;

public partial class ConfirmationDialog : ThemedWindow
{
    internal ConfirmationDialog(string title, string question)
    {
        InitializeComponent();
        Title = title;
        QuestionText.Text = question;
    }

    internal static bool Confirm(Window owner, string question, string title) =>
        new ConfirmationDialog(title, question) { Owner = owner }.ShowDialog() == true;

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

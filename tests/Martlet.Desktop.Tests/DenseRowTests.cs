using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The dense editor rows of Companion › Emotes and motions and Companion › Touch (touch zones, touch temperament):
/// compact controls that line up, text boxes with their padding once, the panel that lets a row's last box fill the rest of its
/// line, and the touch temperament's table, whose columns line up and wrap under the names on a narrow window.</summary>
public sealed class DenseRowTests
{
    [Fact]
    public Task InputsHaveTheirPaddingOnceAndCompactControlsShareOneHeight() => OnDispatcher(() =>
    {
        var input = new TextBox { Text = "Top of head" };
        var password = new PasswordBox { Password = "fixture" };
        var choice = new ComboBox { ItemsSource = new[] { "(default)" }, SelectedIndex = 0 };
        var compactInput = new TextBox { Text = "35, 4, 30, 6" };
        var compactChoice = new ComboBox { ItemsSource = new[] { "(nothing else)" }, SelectedIndex = 0 };
        var compactButton = new Button { Content = "Delete" };
        var panel = new StackPanel();
        foreach (var control in new Control[] { input, password, choice, compactInput, compactChoice, compactButton })
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(control);
        }
        var window = new ThemedWindow { Content = panel, Width = 400, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        compactInput.SetResourceReference(FrameworkElement.StyleProperty, "CompactTextBox");
        compactChoice.SetResourceReference(FrameworkElement.StyleProperty, "CompactComboBox");
        compactButton.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        try
        {
            window.Show();
            window.UpdateLayout();
            // Padding 10,8 once around a 14-point line: about 37 pixels, as tall as a choice (it was 53 with the padding twice).
            Assert.InRange(input.ActualHeight, 36, 40);
            Assert.InRange(password.ActualHeight, 36, 40);
            Assert.Equal(new Thickness(12, 8, 36, 8), choice.Padding);
            Assert.InRange(choice.ActualHeight, 36, 40);
            Assert.Equal(32, compactInput.ActualHeight);
            Assert.Equal(32, compactChoice.ActualHeight);
            Assert.Equal(32, compactButton.ActualHeight);
            Assert.Equal(VerticalAlignment.Center, compactInput.VerticalContentAlignment);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LastChildTakesTheRestOfItsLineOrALineOfItsOwn() => OnDispatcher(() =>
    {
        var first = new Border { Width = 100, Height = 32 };
        var label = new Border { Width = 100, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { MinWidth = 50, Height = 32 };
        var panel = new FillWrapPanel { FillMinimum = 200, FillIndent = 20, Children = { first, label, fill } };

        // Wide: all on one line, the last child to the end of it; the short label gets the line's height to centre in.
        Layout(panel, 500);
        Assert.Equal(new Rect(0, 0, 100, 32), LayoutInformation.GetLayoutSlot(first));
        Assert.Equal(new Rect(100, 0, 100, 32), LayoutInformation.GetLayoutSlot(label));
        Assert.Equal(new Rect(200, 0, 300, 32), LayoutInformation.GetLayoutSlot(fill));
        Assert.Equal(32, panel.DesiredSize.Height);

        // Less than FillMinimum left: the last child takes a line of its own, from FillIndent.
        Layout(panel, 350);
        Assert.Equal(new Rect(100, 0, 100, 32), LayoutInformation.GetLayoutSlot(label));
        Assert.Equal(new Rect(20, 32, 330, 32), LayoutInformation.GetLayoutSlot(fill));
        Assert.Equal(64, panel.DesiredSize.Height);

        // Narrow: the others wrap like a WrapPanel too, each line as tall as its tallest child.
        Layout(panel, 150);
        Assert.Equal(new Rect(0, 32, 100, 20), LayoutInformation.GetLayoutSlot(label));
        Assert.Equal(new Rect(20, 52, 130, 32), LayoutInformation.GetLayoutSlot(fill));
        Assert.Equal(84, panel.DesiredSize.Height);

        // A hidden last child adds no height.
        fill.Visibility = Visibility.Collapsed;
        Layout(panel, 350);
        Assert.Equal(32, panel.DesiredSize.Height);
    });

    [Fact]
    public Task TemperamentColumnsShareWidthsAndWrapUnderTheNames() => OnDispatcher(() =>
    {
        var table = new MainWindow.TemperamentTable();
        var rows = new[] { Cells(), Cells() };
        foreach (var row in rows) table.Add(new Border(), row);
        var window = new Window
        {
            Content = table.View, Width = 1200, Height = 400, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            // Wide: a line per row. The choices share what the names and numbers leave, up to 170, 200 and 200 pixels, and each
            // column lines up from row to row.
            Fit(1000);
            Assert.Equal(new double[] { 170, 200, 200, 60, 60 }, rows[0].Select(cell => cell.Width));
            Assert.Equal(new Rect(662, 0, 68, 38), LayoutInformation.GetLayoutSlot(rows[0][4]));
            for (var i = 0; i < 5; i++) Assert.Equal(LayoutInformation.GetLayoutSlot(rows[0][i]), LayoutInformation.GetLayoutSlot(rows[1][i]));

            // Narrow: the choices keep their least widths, and the last cell wraps onto a second line that starts under the cells,
            // to the right of the name, rather than being cut off.
            Fit(600);
            Assert.Equal(new double[] { 96, 104, 104, 60, 60 }, rows[1].Select(cell => cell.Width));
            Assert.Equal(new Rect(0, 38, 68, 38), LayoutInformation.GetLayoutSlot(rows[1][4]));
            Assert.Equal(138, LayoutInformation.GetLayoutSlot((FrameworkElement)rows[1][4].Parent).X);
        }
        finally { window.Close(); }

        void Fit(double width)
        {
            table.View.Width = width;
            window.UpdateLayout();
            window.UpdateLayout();
        }

        static FrameworkElement[] Cells() => [.. Enumerable.Range(0, 5).Select(_ => new Border { Height = 32 })];
    });

    private static void Layout(FillWrapPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
    }

    private static async Task OnDispatcher(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); finished.SetResult(); }
            catch (Exception error) { finished.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}

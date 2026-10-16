using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The example text in an empty box you type in (InputHint and the shared InputTemplate in Themes\Controls.xaml).</summary>
public sealed class InputHintTests
{
    [Fact]
    public Task AnEmptyBoxShowsItsExampleWhereTypedTextStartsUntilSomethingIsTyped() => OnDispatcher(() =>
    {
        var box = new TextBox { Width = 320 };
        SetHint(box, "https://llm.example.com/v1");
        var compact = new TextBox { Width = 120 };
        compact.SetResourceReference(FrameworkElement.StyleProperty, "CompactTextBox");
        SetHint(compact, "2.5");
        var notes = new TextBox { Width = 320, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        SetHint(notes, "A long example that is too wide for one line of this box, so it wraps onto the next line.");
        var window = new ThemedWindow
        {
            Content = new StackPanel { Children = { box, compact, notes } }, Width = 420, Height = 400, ShowActivated = false,
            ShowInTaskbar = false
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            foreach (var each in new[] { box, compact, notes })
            {
                var hint = Hint(each);
                Assert.Equal(HintText(each), hint.Text);
                Assert.True(Shown(each));
                Assert.Equal(Visibility.Visible, hint.Visibility);
                Assert.Equal(HintText(each), AutomationProperties.GetHelpText(each));

                // The example starts where typed text starts, so it doesn't jump at the first character.
                each.Text = "h";
                window.UpdateLayout();
                Assert.False(Shown(each));
                Assert.Equal(Visibility.Collapsed, hint.Visibility);
                var typed = each.GetRectFromCharacterIndex(0);
                hint.Visibility = Visibility.Visible;
                window.UpdateLayout();
                var shown = hint.TranslatePoint(new Point(hint.Padding.Left, hint.Padding.Top), each);
                Assert.InRange(shown.X - typed.Left, -1.5, 1.5);
                Assert.InRange(shown.Y - typed.Top, -2.5, 2.5);

                each.Text = "";
                window.UpdateLayout();
                Assert.True(Shown(each));
                Assert.Equal(Visibility.Visible, hint.Visibility);
            }
            Assert.Equal(TextWrapping.NoWrap, Hint(box).TextWrapping);
            Assert.Equal(TextWrapping.Wrap, Hint(notes).TextWrapping);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task APasswordBoxShowsItsExampleUntilAKeyIsPasted() => OnDispatcher(() =>
    {
        var key = new PasswordBox { Width = 320 };
        SetHint(key, "Paste your OpenRouter key (sk-or-v1-…)");
        var window = new ThemedWindow { Content = key, Width = 420, Height = 200, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var hint = Hint(key);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            key.Password = "secret";
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            key.Clear();
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, hint.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ABoxKeepsItsOwnHelpTextAndShowsNoEmptyExample() => OnDispatcher(() =>
    {
        var helped = new TextBox();
        AutomationProperties.SetHelpText(helped, "Placeholders put a fact where you want it.");
        SetHint(helped, "Good morning, {name}!");
        Assert.Equal("Placeholders put a fact where you want it.", AutomationProperties.GetHelpText(helped));
        Assert.True(Shown(helped));

        // A changed example replaces the help text it gave, and no example shows nothing.
        var plain = new TextBox();
        SetHint(plain, "Alex");
        SetHint(plain, "Sam");
        Assert.Equal("Sam", AutomationProperties.GetHelpText(plain));
        SetHint(plain, null);
        Assert.Equal("", AutomationProperties.GetHelpText(plain));
        Assert.False(Shown(plain));
        Assert.False(Shown(new TextBox()));
    });

    private static TextBlock Hint(Control box) => Assert.IsType<TextBlock>(box.Template.FindName("Hint", box));

    // The desktop's own InputHint: the renderer and the MCP server, which this project also references, compile it too.
    private static readonly Type InputHint = typeof(ThemedWindow).Assembly.GetType("Martlet.Presentation.InputHint", true)!;

    private static void SetHint(DependencyObject box, string? text) => InputHint.GetMethod("SetText")!.Invoke(null, [box, text]);
    private static string? HintText(DependencyObject box) => (string?)InputHint.GetMethod("GetText")!.Invoke(null, [box]);
    private static bool Shown(DependencyObject box) => (bool)InputHint.GetMethod("GetShown")!.Invoke(null, [box])!;

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

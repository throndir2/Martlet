using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AccessKeysTests
{
    [Fact]
    public void A_plain_letter_presses_no_button_while_Enter_and_Esc_still_press_the_default_and_cancel_buttons() => RunSta(() =>
    {
        AccessKeys.RequireAlt();
        var pressed = new List<string>();
        Button Make(string content, string name)
        {
            var button = new Button { Content = content };
            button.Click += (_, _) => pressed.Add(name);
            return button;
        }
        var listen = Make("Start _listening", "listen");
        var ok = Make("OK", "ok");
        ok.IsDefault = true;
        var cancel = Make("Cancel", "cancel");
        cancel.IsCancel = true;
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -400, Top = -400, Width = 300, Height = 200,
            Content = new StackPanel { Children = { listen, ok, cancel } }
        };
        window.Show();
        try
        {
            Settle();
            var scope = PresentationSource.FromVisual(window);
            Assert.NotNull(scope);
            Assert.True((Keyboard.Modifiers & ModifierKeys.Alt) == 0, "Alt must not be held while this test runs.");

            AccessKeyManager.ProcessKey(scope, "l", false);
            Assert.Empty(pressed);

            AccessKeyManager.ProcessKey(scope, "\r", false);
            AccessKeyManager.ProcessKey(scope, "\u001b", false);
            Assert.Equal(["ok", "cancel"], pressed);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void An_open_menu_still_takes_plain_letters() => RunSta(() =>
    {
        AccessKeys.RequireAlt();
        var pressed = new List<string>();
        var open = new MenuItem { Header = "_Open Martlet" };
        open.Click += (_, _) => pressed.Add("open");
        var target = new Border { Width = 100, Height = 100, Background = Brushes.Transparent };
        var menu = new ContextMenu { Opacity = 0, PlacementTarget = target, Items = { open } };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -400, Top = -400, Width = 300, Height = 200, Content = target
        };
        window.Show();
        try
        {
            menu.IsOpen = true;
            Settle();
            Assert.True((Keyboard.Modifiers & ModifierKeys.Alt) == 0, "Alt must not be held while this test runs.");
            AccessKeyManager.ProcessKey(menu, "o", false);
            // A menu item raises Click once the menu has closed, a moment later.
            Settle();
            Assert.Equal(["open"], pressed);
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
        }
    });

    private static void Settle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}

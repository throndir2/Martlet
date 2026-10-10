using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>The account button at the bottom of the navigation rail shows who uses Martlet now.</summary>
public sealed class AccountPickerTests
{
    [Fact]
    public Task The_account_button_shows_the_account_in_use_and_how_many_people_use_this_pc() => OnDispatcher(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-account-picker-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var session = AccountSession.Open(data, identity.User!.Value, () => "Sam Doe", NetworkIdentity.ThisDevice, DateTimeOffset.UtcNow);
            session.AddPerson("Alex", DateTimeOffset.UtcNow);
        }
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var main = new MainWindow(new SettingsStore(data), null) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var button = Assert.IsType<Button>(main.FindName("AccountButton"));
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal("AccountButton", AutomationProperties.GetAutomationId(button));
            Assert.Equal("Account: Sam Doe, Owner, 2 people on this PC", AutomationProperties.GetName(button));
            Assert.Equal("SD", Assert.IsType<TextBlock>(main.FindName("AccountInitials")).Text);
            Assert.Equal("Sam Doe", Assert.IsType<TextBlock>(main.FindName("AccountName")).Text);
            Assert.Equal("Accounts: not synced yet.", Assert.IsType<TextBlock>(main.FindName("AccountsSyncStatusText")).Text);
        }
        finally
        {
            main.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    });

    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}

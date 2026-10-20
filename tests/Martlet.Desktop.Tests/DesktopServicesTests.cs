using System.IO;
using System.Windows.Threading;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>The main window's services are built in one place (<see cref="DesktopServices"/>, #962) without a window, and the
/// window uses exactly the services it is given.</summary>
public sealed class DesktopServicesTests
{
    [Fact]
    public void The_services_are_built_for_a_data_folder_without_a_window_and_without_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-desktop-services-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        try
        {
            var store = new SettingsStore(data);
            var services = DesktopServices.Create(store, new SupportController(data));
            Assert.Same(store, services.Store);
            Assert.NotNull(services.SetupService);
            Assert.NotNull(services.CompanionService);
            Assert.NotNull(services.Memory);
            Assert.NotNull(services.ConversationHistory);
            Assert.NotNull(services.Lorebooks);
            Assert.NotNull(services.Parakeet);
            Assert.NotNull(services.Recovery);
            Assert.NotNull(services.Captions);
            Assert.Same(services.SetupOperations, services.SetupOperations);
            Assert.True(services.LocalVoices.Available);

            // Without a data folder (Martlet couldn't open one) the window still gets every service it always needs.
            var none = DesktopServices.Create(null, new SupportController(null));
            Assert.Null(none.SetupService);
            Assert.Null(none.Memory);
            Assert.Null(none.Lorebooks);
            Assert.Null(none.Parakeet);
            Assert.False(none.LocalVoices.Available);
            Assert.NotNull(none.SmartHome);
            Assert.NotNull(none.Avatar);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public Task The_window_uses_the_services_it_is_given() => OnDispatcher(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-desktop-services-window-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var services = DesktopServices.Create(new SettingsStore(data), new SupportController(data));
        // The main window stays unshown, so nothing it starts once shown runs.
        var main = new MainWindow(null, services) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            // The window wired the services it was given: messaging answers through the window, and the record of conversations
            // is the one built here.
            Assert.NotNull(services.Messaging.Answer);
            Assert.Same(services.ConversationHistory, services.Messaging.History);
            Assert.Same(services.ConversationHistory, services.Discord.History);
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

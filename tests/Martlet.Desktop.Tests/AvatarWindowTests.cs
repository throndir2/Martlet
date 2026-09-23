using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AvatarWindowTests
{
    [Fact]
    public async Task Real_wpf_surface_loads_saved_enabled_preference_without_renderer_network_or_credentials()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) =>
            {
                e.Handled = true; completion.TrySetException(e.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    using var scope = new AvatarHostingTests.Scope();
                    var settings = new SettingsStore(scope.DirectoryPath);
                    var initial = AppSettings.CreateUnconfigured();
                    Assert.True((await settings.SaveAsync(initial with
                        { Profile = initial.Profile with { Id = scope.ProfileId } }, null)).Saved);
                    var store = new AvatarProfileStore(scope.DirectoryPath);
                    await store.SaveAsync(scope.Profile() with
                    {
                        Configuration = AvatarProfile.ConfigurationElement(AvatarConfiguration.Disabled with { Enabled = true })
                    }, null);
                    var calls = 0;
                    await using var controller = new AvatarController(createRenderer: () =>
                    {
                        calls++; throw new InvalidOperationException("Opening must be passive.");
                    });
                    var window = new AvatarWindow(controller, store, new SetupService(settings, new ForbiddenVault()),
                        new SetupOperationRunner()) { ShowActivated = false, ShowInTaskbar = false };
                    window.Show();
                    try
                    {
                        var result = (TextBlock)window.FindName("ResultText");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while (!result.Text.Contains("loaded", StringComparison.Ordinal)) await Task.Delay(10, timeout.Token);
                        Assert.Equal(0, calls);
                        Assert.False(controller.IsActive);
                        Assert.False(controller.Observer.IsEnabled);
                        Assert.False(((CheckBox)window.FindName("AnalysisPermission")).IsChecked);
                        Assert.False(((CheckBox)window.FindName("InspectPermission")).IsChecked);
                        Assert.Contains("\"enabled\": true", ((TextBox)window.FindName("ConfigurationText")).Text);
                    }
                    finally { window.Close(); }
                    completion.TrySetResult();
                }
                catch (Exception e) { completion.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }

    private sealed class ForbiddenVault : ICredentialStore
    {
        public CredentialError Write(CredentialBinding binding, SecretLease secret) => throw new InvalidOperationException();
        public CredentialReadResult Read(CredentialBinding binding) => throw new InvalidOperationException();
        public CredentialError Delete(CredentialBinding binding) => throw new InvalidOperationException();
    }
}

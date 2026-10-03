using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AvatarWindowTests
{
    [Fact]
    public Task Closing_and_passively_reopening_configuration_preserves_explicit_session_activation() => OnDispatcher(async () =>
    {
        using var scope = new AvatarHostingTests.Scope();
        var settings = new SettingsStore(scope.DirectoryPath);
        var initial = AppSettings.CreateUnconfigured();
        Assert.True((await settings.SaveAsync(initial with { Profile = initial.Profile with { Id = scope.ProfileId } }, null)).Saved);
        var store = new AvatarProfileStore(scope.DirectoryPath);
        var renderer = new ControlledRenderer();
        await using var controller = new AvatarController(createRenderer: () => renderer);
        await controller.InspectAsync(scope.Profile(), default);
        await controller.UpdateThemeAsync(default);
        Assert.False(renderer.LastTheme!.Dark);
        await SaveMappingAsync(store, controller);
        AvatarWindow Open() => new(controller, store, new SetupService(settings, new ForbiddenVault()), new SetupOperationRunner())
            { ShowActivated = false, ShowInTaskbar = false };
        var window = Open();
        window.Show();
        try
        {
            await Until(() => ((TextBlock)window.FindName("SaveStateText")).Text == "All changes saved.");
            Assert.Same(window.TryFindResource("CanvasBrush"), window.Background);
            Assert.Same(window.TryFindResource("SurfaceBrush"),
                ((TextBox)window.FindName("ModelPathText")).Background);
            ((CheckBox)window.FindName("AnalysisPermission")).IsChecked = true;
            ((Button)window.FindName("ActivateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => controller.IsActive);
        }
        finally { window.Close(); }
        Assert.True(controller.IsActive);
        Assert.True(controller.Observer.IsEnabled);
        var reopened = Open();
        reopened.Show();
        try
        {
            await Until(() => ((TextBlock)reopened.FindName("SaveStateText")).Text == "All changes saved.");
            Assert.True(controller.IsActive);
            Assert.True(controller.Observer.IsEnabled);
            ((TextBox)reopened.FindName("EndpointText")).Text = "http://127.0.0.1:52001/";
            Assert.False(controller.IsActive);
            Assert.False(controller.Observer.IsEnabled);
            // No Save button: the edited endpoint is written on its own.
            await Until(() => ((TextBlock)reopened.FindName("SaveStateText")).Text == "All changes saved.");
            Assert.Equal("http://127.0.0.1:52001/", (await store.LoadAsync(scope.ProfileId)).Profile!.Endpoint);
        }
        finally { reopened.Close(); }
    });

    [Fact]
    public Task Closing_while_configuration_acknowledgement_is_held_revokes_pending_activation() => OnDispatcher(async () =>
    {
        using var scope = new AvatarHostingTests.Scope();
        var settings = new SettingsStore(scope.DirectoryPath);
        var initial = AppSettings.CreateUnconfigured();
        Assert.True((await settings.SaveAsync(initial with { Profile = initial.Profile with { Id = scope.ProfileId } }, null)).Saved);
        var store = new AvatarProfileStore(scope.DirectoryPath);
        var renderer = new ControlledRenderer { ConfigureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var controller = new AvatarController(createRenderer: () => renderer);
        await controller.InspectAsync(scope.Profile(), default);
        await SaveMappingAsync(store, controller);
        var window = new AvatarWindow(controller, store, new SetupService(settings, new ForbiddenVault()), new SetupOperationRunner())
            { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var result = (TextBlock)window.FindName("ResultText");
            await Until(() => ((TextBlock)window.FindName("SaveStateText")).Text == "All changes saved.");
            ((CheckBox)window.FindName("AnalysisPermission")).IsChecked = true;
            ((Button)window.FindName("ActivateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await renderer.ConfigureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            renderer.ConfigureRelease.TrySetResult();
            await Until(() => result.Text.Contains("canceled", StringComparison.Ordinal));
            Assert.False(controller.IsActive);
            Assert.False(controller.Observer.IsEnabled);
        }
        finally { renderer.ConfigureRelease.TrySetResult(); window.Close(); }
    });

    private static Task<string> SaveMappingAsync(AvatarProfileStore store, AvatarController controller)
    {
        var configuration = new AvatarConfiguration
        {
            Version = Martlet.Core.Contracts.ContractVersion.Current, Enabled = false,
            PreferredBackend = AvatarBackend.Audio2Face, RequestedAspects = [AvatarAspect.Mouth], OmittedAspects = [],
            Assignments = [new() { Aspect = AvatarAspect.Mouth, SourceId = AvatarController.SourceId,
                MappingId = "jaw", AcceptReduced = true }],
            MappingProfiles = [new() { Id = "jaw", SourceId = AvatarController.SourceId, ModelId = controller.Capabilities!.ModelId,
                Mappings = [new() { Source = new() { Blendshape = "jawOpen" }, TargetParameterId = "Jaw", OutputMinimum = 0, OutputMaximum = 1 }] }]
        };
        return store.SaveAsync(controller.InspectedProfile! with
            { Configuration = AvatarProfile.ConfigurationElement(configuration) }, null);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task OnDispatcher(Func<Task> action)
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
                try { await action(); completion.TrySetResult(); }
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

    private sealed class ControlledRenderer : IAvatarRenderer
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited => exited.Task.IsCompleted;
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        internal TaskCompletionSource? ConfigureRelease { get; init; }
        internal TaskCompletionSource ConfigureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RendererTheme? LastTheme { get; private set; }
        public Task StartAsync(AvatarProfile profile, string revision, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", 0, 1, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public async Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested();
            if (kind == "theme") LastTheme = Assert.IsType<RendererTheme>(data);
            if (kind == "configure")
            {
                ConfigureEntered.TrySetResult();
                if (ConfigureRelease is { } held) await held.Task;
            }
            return RendererProtocol.Message("ok", Guid.NewGuid(), new { });
        }
        public ValueTask DisposeAsync() { exited.TrySetResult(); return ValueTask.CompletedTask; }
    }

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
                        var state = (TextBlock)window.FindName("SaveStateText");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while (state.Text != "All changes saved.") await Task.Delay(10, timeout.Token);
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

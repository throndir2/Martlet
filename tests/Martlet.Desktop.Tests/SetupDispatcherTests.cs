using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Core.Contracts;
using Martlet.Core.Tests;
using Martlet.Credentials.Windows;
using Martlet.Desktop;
using Xunit.Abstractions;

namespace Martlet.Desktop.Tests;

public sealed class SetupDispatcherTests(ITestOutputHelper output)
{
    private const string Canary = "SYNTHETIC-SECRET-CANARY";

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Delete")]
    public Task BlockingNativeKeepsDispatcherAndCloseResponsiveWithoutEarlyRelease(string action) => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        await fixture.PrepareAsync(action);
        var runner = new SetupOperationRunner();
        var window = fixture.Open(runner);
        SetupWindow? reopened = null;
        try
        {
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            var uiThread = Environment.CurrentManagedThreadId;
            fixture.Native.Block = action;
            Control<TabControl>(window, "Steps").SelectedIndex = 2;
            if (action == "Write") Control<PasswordBox>(window, "KeyInput").Password = Canary;
            var entryStarted = Stopwatch.GetTimestamp();
            Click(window, ActionId(action));
            string EntryState() => $"action={action}; elapsed_ms={Stopwatch.GetElapsedTime(entryStarted).TotalMilliseconds:F0}; " +
                $"runner_active={runner.IsRunning}; editor_enabled={Control<StackPanel>(window, "EditorPanel").IsEnabled}; " +
                $"action_enabled={Button(window, ActionId(action)).IsEnabled}; " +
                $"removal_selected={Control<ComboBox>(window, "RemovalChoice").SelectedItem is PendingCredentialRemoval}; " +
                $"loads={fixture.Service.LoadCalls}; removes={fixture.Service.RemoveCalls}; " +
                $"remove_task={fixture.Service.RemovalTaskStatus}; native_calls={fixture.Native.BlockedCalls}; entered={fixture.Native.Entered.Task.Status}";
            output.WriteLine("Before native entry wait: " + EntryState());
            try { await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException error)
            {
                var state = EntryState();
                output.WriteLine("Native entry timeout: " + state);
                throw new TimeoutException("Native entry wait failed: " + state, error);
            }
            output.WriteLine("Native entry observed: " + EntryState());
            Assert.NotEqual(uiThread, fixture.Native.ThreadId);
            await Heartbeat();
            Assert.True(runner.IsRunning);
            Assert.False(Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Assert.True(Button(window, "SetupCancel").IsEnabled);
            Assert.Equal("", Control<PasswordBox>(window, "KeyInput").Password);
            Assert.Equal(1, fixture.Native.BlockedCalls);

            // Programmatic duplicate delivery must be rejected as well as disabled keyboard controls.
            Click(window, ActionId(action));
            Assert.Equal(1, fixture.Native.BlockedCalls);
            if (action == "Write")
                fixture.Service.OfferedSecret!.Use(value => Assert.Equal(Canary, new string(value)));
            var pendingBefore = (await Task.Run(() => fixture.Store.LoadAsync())).Settings!.Setup!.PendingRemovals.Count;
            if (action is "Write" or "Delete") Assert.Equal(1, pendingBefore);

            Click(window, "SetupCancel");
            await WaitUntil(() => Control<TextBox>(window, "ResultText").Text.Contains("Cancellation requested", StringComparison.Ordinal));
            Assert.True(runner.IsRunning);
            Assert.False(Button(window, "SetupReload").IsEnabled);
            if (action is "Write" or "Delete")
            {
                var current = await Task.Run(() => fixture.Store.LoadAsync());
                var conflict = await Task.Run(() => fixture.Store.SaveAsync(current.Settings!, current.Revision));
                Assert.False(conflict.Saved);
                Assert.Equal(ErrorCode.SettingsInaccessible, conflict.Error!.Code);
            }
            await Heartbeat();

            var resultBeforeClose = Control<TextBox>(window, "ResultText").Text;
            var statusBeforeClose = Control<TextBox>(window, "SetupStatus").Text;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            Assert.True(closed);
            Assert.True(runner.IsRunning);
            if (action == "Write")
                fixture.Service.OfferedSecret!.Use(value => Assert.Equal(Canary, new string(value)));

            // The owner can reopen setup while the old native call is still blocked, but not start I/O.
            var loadCount = fixture.Service.LoadCalls;
            reopened = fixture.Open(runner);
            await Heartbeat();
            Assert.Equal(loadCount, fixture.Service.LoadCalls);
            Assert.False(Control<StackPanel>(reopened, "EditorPanel").IsEnabled);
            Assert.False(Button(reopened, "SetupReload").IsEnabled);
            reopened.Close();
            reopened = null;
            Assert.True(runner.IsRunning);

            fixture.Native.Release.Set();
            await WaitUntil(() => !runner.IsRunning);
            await Heartbeat();
            Assert.Equal(resultBeforeClose, Control<TextBox>(window, "ResultText").Text);
            Assert.Equal(statusBeforeClose, Control<TextBox>(window, "SetupStatus").Text);
            if (action == "Write")
            {
                Assert.Throws<ObjectDisposedException>(() => fixture.Service.OfferedSecret!.Use(_ => { }));
                var saved = (await Task.Run(() => fixture.Store.LoadAsync())).Settings!;
                Assert.Null(saved.Setup!.Routes.Single().CredentialId);
                Assert.Single(saved.Setup.PendingRemovals);
                Assert.Empty(fixture.Native.Keys);
            }
            if (action == "Read")
                Assert.Throws<ObjectDisposedException>(() => fixture.Native.ReadLease!.Use(_ => { }));
            if (action == "Delete")
            {
                // The fake delete is access-denied: a late failure cannot erase recovery metadata.
                Assert.Single((await Task.Run(() => fixture.Store.LoadAsync())).Settings!.Setup!.PendingRemovals);
                Assert.Single(fixture.Native.Keys);
            }
        }
        finally
        {
            fixture.Native.Release.Set();
            reopened?.Close();
            window.Close();
            await WaitUntil(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task InitialLoadSynchronousPrefixCannotBlockDispatcherOrClose() => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        fixture.Service.BlockLoad = true;
        var runner = new SetupOperationRunner();
        var window = fixture.Open(runner);
        try
        {
            await fixture.Service.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEqual(Environment.CurrentManagedThreadId, fixture.Service.LoadThread);
            await Heartbeat();
            Assert.False(Directory.Exists(fixture.Directory));
            Assert.True(runner.IsRunning);
            var status = Control<TextBox>(window, "SetupStatus").Text;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            Assert.True(closed);
            Assert.True(runner.IsRunning);
            fixture.Service.LoadRelease.Set();
            await WaitUntil(() => !runner.IsRunning);
            await Heartbeat();
            Assert.Equal(status, Control<TextBox>(window, "SetupStatus").Text);
            Assert.False(Directory.Exists(fixture.Directory));
            Assert.Empty(fixture.Native.Events);
        }
        finally
        {
            fixture.Service.LoadRelease.Set();
            window.Close();
            await WaitUntil(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task SettingsSaveSynchronousPrefixStaysOffDispatcherAndCannotApplyAfterClose() => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        await fixture.PrepareAsync("Write");
        var original = await File.ReadAllBytesAsync(fixture.Store.FilePath);
        var runner = new SetupOperationRunner();
        var window = fixture.Open(runner);
        try
        {
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            fixture.Service.BlockSave = true;
            Control<TabControl>(window, "Steps").SelectedIndex = 3;
            Click(window, "SetupSave");
            await fixture.Service.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEqual(Environment.CurrentManagedThreadId, fixture.Service.SaveThread);
            await Heartbeat();
            var result = Control<TextBox>(window, "ResultText").Text;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            Assert.True(closed);
            Assert.True(runner.IsRunning);
            fixture.Service.SaveRelease.Set();
            await WaitUntil(() => !runner.IsRunning);
            await Heartbeat();
            Assert.Equal(result, Control<TextBox>(window, "ResultText").Text);
            Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Store.FilePath));
            Assert.Empty(fixture.Native.Events);
        }
        finally
        {
            fixture.Service.SaveRelease.Set();
            window.Close();
            await WaitUntil(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task TimeoutDiscardsLateSaveButKeepsWorkerLeaseAndRequiresExplicitReload() => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        await fixture.PrepareAsync("Write");
        var runner = new SetupOperationRunner();
        var clock = new ManualClock();
        var window = fixture.Open(runner, clock);
        try
        {
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            fixture.Native.Block = "Write";
            Control<TabControl>(window, "Steps").SelectedIndex = 2;
            Control<PasswordBox>(window, "KeyInput").Password = Canary;
            Click(window, "SetupStoreKey");
            await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(6));
            await WaitUntil(() => Control<TextBox>(window, "ResultText").Text.Contains("timed out", StringComparison.Ordinal));
            var timedOut = Control<TextBox>(window, "ResultText").Text;
            Assert.True(runner.IsRunning);
            Assert.False(Button(window, "SetupReload").IsEnabled);
            fixture.Service.OfferedSecret!.Use(value => Assert.Equal(Canary, new string(value)));
            await Heartbeat();
            fixture.Native.Release.Set();
            await WaitUntil(() => !runner.IsRunning && Button(window, "SetupReload").IsEnabled);
            Assert.Equal(timedOut, Control<TextBox>(window, "ResultText").Text);
            Assert.False(Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Assert.Throws<ObjectDisposedException>(() => fixture.Service.OfferedSecret!.Use(_ => { }));
            Click(window, "SetupReload");
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Assert.Contains("cleanup pending", Control<TextBox>(window, "SetupStatus").Text);
            Assert.Single((await Task.Run(() => fixture.Store.LoadAsync())).Settings!.Setup!.PendingRemovals);
        }
        finally { fixture.Native.Release.Set(); window.Close(); await WaitUntil(() => !runner.IsRunning); }
    });

    [Fact]
    public Task WorkerFaultIsReportedWithoutExceptionOrSecretContent() => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        await fixture.PrepareAsync("Read");
        fixture.Native.FaultRead = true;
        var runner = new SetupOperationRunner();
        var window = fixture.Open(runner);
        try
        {
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Click(window, "SetupReadKey");
            await WaitUntil(() => Control<TextBox>(window, "ResultText").Text.Contains("action failed", StringComparison.Ordinal));
            Assert.DoesNotContain(Canary, Control<TextBox>(window, "ResultText").Text);
            Assert.False(runner.IsRunning);
            Assert.False(Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Assert.True(Button(window, "SetupReload").IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CompatibleModelCatalogSelectionPersistsAndUnsupportedInputPreservesRoute() => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        var runner = new SetupOperationRunner();
        var window = fixture.Open(runner);
        try
        {
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Control<RadioButton>(window, "ApiChoice").IsChecked = true;
            Control<TabControl>(window, "Steps").SelectedIndex = (int)SetupStep.Destinations;
            Control<ComboBox>(window, "RoleChoice").SelectedItem = SetupRole.Llm;
            var catalog = Control<ComboBox>(window, "ModelCatalogChoice");
            Assert.Contains("gpt-4.1-2025-04-14", catalog.Items.Cast<string>());
            catalog.SelectedItem = "gpt-4.1-2025-04-14";
            Click(window, "SetupUseCatalogModel");
            Assert.Equal("gpt-4.1-2025-04-14", Control<TextBox>(window, "ModelId").Text);
            Control<CheckBox>(window, "ConsentChoice").IsChecked = true;
            Click(window, "SetupApplyRoute");
            Click(window, "SetupSave");
            await WaitUntil(() => Control<TextBox>(window, "ResultText").Text.Contains("saved", StringComparison.OrdinalIgnoreCase));
            var saved = await Task.Run(() => fixture.Store.LoadAsync());
            var route = saved.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
            Assert.Equal("gpt-4.1-2025-04-14", route.ModelId);
            Assert.Equal(route.Selection(), route.Consent);

            Control<TextBox>(window, "ModelId").Text = "unsupported-model";
            Control<CheckBox>(window, "ConsentChoice").IsChecked = true;
            Click(window, "SetupApplyRoute");
            Assert.Contains("does not support", Control<TextBox>(window, "ResultText").Text);
            Assert.Equal(route, (await Task.Run(() => fixture.Store.LoadAsync())).Settings!.Setup!.Routes
                .Single(item => item.Role == SetupRole.Llm));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LlmProviderChoiceSavesOpenRouterNvidiaAndCustomChatCompletionsRoutes() => OnDispatcher(async () =>
    {
        using var fixture = new SetupFixture();
        var runner = new SetupOperationRunner();
        var window = fixture.Open(runner);
        try
        {
            await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
            Control<RadioButton>(window, "ApiChoice").IsChecked = true;
            Control<TabControl>(window, "Steps").SelectedIndex = (int)SetupStep.Destinations;
            Control<ComboBox>(window, "RoleChoice").SelectedItem = SetupRole.Stt;
            Assert.False(Control<ComboBox>(window, "ProviderChoice").IsEnabled);
            Control<ComboBox>(window, "RoleChoice").SelectedItem = SetupRole.Llm;
            var providers = Control<ComboBox>(window, "ProviderChoice");
            Assert.True(providers.IsEnabled);
            var names = providers.Items.Cast<object>().Select(item => item.ToString()!).ToArray();
            Assert.Equal(4, names.Length);
            foreach (var (name, baseUrl, model) in new[]
            {
                ("OpenRouter", ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "meta-llama/llama-3.3-70b-instruct:free"),
                ("NVIDIA Build", ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "meta/llama-3.3-70b-instruct"),
                ("Custom", "http://127.0.0.1:1234/v1", "local-model")
            })
            {
                providers.SelectedItem = providers.Items.Cast<object>().Single(item => item.ToString()!.StartsWith(name, StringComparison.Ordinal));
                var url = Control<TextBox>(window, "BaseUrl");
                Assert.True(url.IsEnabled);
                if (name == "Custom") url.Text = baseUrl;
                else Assert.Equal(baseUrl, url.Text);
                Assert.False(Control<ComboBox>(window, "ModelCatalogChoice").IsEnabled);
                Control<TextBox>(window, "ModelId").Text = model;
                Control<CheckBox>(window, "ConsentChoice").IsChecked = true;
                Click(window, "SetupApplyRoute");
                Assert.Contains("Route applied", Control<TextBox>(window, "ResultText").Text);
                Click(window, "SetupSave");
                await WaitUntil(() => Control<TextBox>(window, "ResultText").Text.Contains("saved", StringComparison.OrdinalIgnoreCase));
                var saved = await Task.Run(() => fixture.Store.LoadAsync());
                var route = saved.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
                Assert.Equal(SetupRouteType.ChatCompletions, route.RouteType);
                Assert.Equal(baseUrl, route.Origin);
                Assert.Equal(model, route.ModelId);
                Assert.Equal(route.Selection(), route.Consent);
                await WaitUntil(() => Control<StackPanel>(window, "EditorPanel").IsEnabled);
                Assert.Same(providers.SelectedItem, providers.Items.Cast<object>().Single(item => item.ToString()!.StartsWith(name, StringComparison.Ordinal)));
            }
        }
        finally { window.Close(); }
    });

    private static string ActionId(string action) => action switch
    {
        "Write" => "SetupStoreKey", "Read" => "SetupReadKey", _ => "SetupRemoveKey"
    };

    private static T Control<T>(SetupWindow window, string name) => Assert.IsType<T>(window.FindName(name));
    private static Button Button(SetupWindow window, string id) =>
        Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id);
    private static void Click(SetupWindow window, string id) => Button(window, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task Heartbeat()
    {
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => heartbeat.TrySetResult());
        await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The bounded setup condition was not reached.");
            await Task.Delay(10);
        }
    }

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
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private sealed class SetupFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Setup.Dispatcher." + Guid.NewGuid().ToString("N"));
        public BlockingNative Native { get; } = new();
        public SettingsStore Store { get; }
        public TrackingService Service { get; }

        public SetupFixture()
        {
            Store = new(Directory);
            Service = new(new SetupService(Store, new WindowsCredentialStore(Native)));
        }

        public async Task PrepareAsync(string action)
        {
            var settings = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Stt, "whisper-1", null);
            if (action == "Write") { Assert.True((await Task.Run(() => Store.SaveAsync(settings, null))).Saved); return; }
            using var secret = new SecretLease(Canary);
            var saved = await Task.Run(() => Service.ReplaceCredentialAsync(settings, null, SetupRole.Stt, secret));
            Assert.True(saved.Save.Saved);
            if (action == "Delete")
                Assert.True((await Task.Run(() => Service.DetachCredentialAsync(saved.Settings, saved.Save.Revision, SetupRole.Stt))).Save.Saved);
            Native.Events.Clear();
        }

        public SetupWindow Open(SetupOperationRunner runner, TimeProvider? clock = null)
        {
            var window = new SetupWindow(Service, runner, _ => true, clock);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            return window;
        }

        public void Dispose()
        {
            Native.Dispose();
            Service.LoadRelease.Dispose();
            Service.SaveRelease.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class TrackingService(SetupService inner) : ISetupService
    {
        public SecretLease? OfferedSecret { get; private set; }
        public bool BlockLoad { get; set; }
        private int loadCalls, removeCalls;
        private Task<SetupSaveResult>? removalTask;
        public int LoadCalls => Volatile.Read(ref loadCalls);
        public int RemoveCalls => Volatile.Read(ref removeCalls);
        public TaskStatus? RemovalTaskStatus => Volatile.Read(ref removalTask)?.Status;
        public int LoadThread { get; private set; }
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim LoadRelease { get; } = new();
        public bool BlockSave { get; set; }
        public int SaveThread { get; private set; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim SaveRelease { get; } = new();

        public Task<SettingsLoadResult> LoadAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref loadCalls);
            LoadThread = Environment.CurrentManagedThreadId;
            if (BlockLoad)
            {
                LoadEntered.TrySetResult();
                if (!LoadRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Load test boundary was not released.");
            }
            return inner.LoadAsync(token);
        }

        public Task<SetupSaveResult> ReplaceCredentialAsync(AppSettings settings, string? revision, SetupRole role, SecretLease secret, CancellationToken token = default)
        {
            OfferedSecret = secret;
            return inner.ReplaceCredentialAsync(settings, revision, role, secret, token);
        }
        public Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default)
        {
            SaveThread = Environment.CurrentManagedThreadId;
            if (BlockSave)
            {
                SaveEntered.TrySetResult();
                if (!SaveRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Save test boundary was not released.");
            }
            return inner.SaveAsync(settings, revision, token);
        }
        public Task<SetupSaveResult> DetachCredentialAsync(AppSettings settings, string? revision, SetupRole role, CancellationToken token = default) => inner.DetachCredentialAsync(settings, revision, role, token);
        public Task<SetupSaveResult> RemoveDetachedAsync(AppSettings settings, string? revision, PendingCredentialRemoval removal, CancellationToken token = default)
        {
            Interlocked.Increment(ref removeCalls);
            var task = inner.RemoveDetachedAsync(settings, revision, removal, token);
            Volatile.Write(ref removalTask, task);
            return task;
        }
        public CredentialError CheckCredential(AppSettings settings, SetupRole role) => inner.CheckCredential(settings, role);
    }

    private sealed class BlockingNative : ICredentialNative, IDisposable
    {
        public bool IsSupported => true;
        public string? Block { get; set; }
        public bool FaultRead { get; set; }
        public int ThreadId { get; private set; }
        private int blockedCalls;
        public int BlockedCalls => Volatile.Read(ref blockedCalls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public Dictionary<string, char[]> Keys { get; } = [];
        public List<string> Events { get; } = [];
        public SecretLease? ReadLease { get; private set; }

        private void Wait(string action)
        {
            Events.Add(action);
            if (Block != action) return;
            ThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref blockedCalls);
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native test boundary was not released.");
        }
        public int Write(string target, ReadOnlySpan<char> secret)
        {
            Wait("Write");
            Assert.True(secret.SequenceEqual(Canary));
            Keys.Add(target, secret.ToArray());
            return 0;
        }
        public int Read(string target, out SecretLease? secret)
        {
            if (FaultRead) throw new InvalidOperationException(Canary);
            ReadLease = new(Canary);
            Wait("Read");
            secret = ReadLease;
            return 0;
        }
        public int Delete(string target)
        {
            Wait("Delete");
            if (Block == "Delete") return 5;
            if (!Keys.Remove(target, out var key)) return 1168;
            Array.Clear(key);
            return 0;
        }
        public void Dispose()
        {
            Release.Dispose();
            ReadLease?.Dispose();
            foreach (var key in Keys.Values) Array.Clear(key);
        }
    }
}

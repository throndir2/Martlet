using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Reflection;
using Martlet.Core.Settings;
using Martlet.Core.Tests;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ConfigurationRecoveryDispatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MainConversationSetupRecoveryUsesExistingOwnerAndStaysPassive(bool busy) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var support = new SupportController(scope.Data);
        var main = new MainWindow(scope.Store, null, support) { ShowActivated = false, ShowInTaskbar = false };
        var runner = Private<SetupOperationRunner>(main, "setupOperations");
        var existingRecovery = Private<ConfigurationRecoveryController>(main, "recovery");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationWindow? live = null;
        SetupWindow? setup = null;
        ConfigurationRecoveryWindow? recovery = null;
        SetupOperation? worker = null;
        main.Show();
        try
        {
            await Until(() => Field<Button>(main, "ConversationButton").IsEnabled);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    live = main.OwnedWindows.OfType<LiveConversationWindow>().Single();
                    await Until(() => Field<TextBlock>(live, "ResultText").Text.Contains("Choices loaded", StringComparison.Ordinal));
                    Assert.False(Field<CheckBox>(live, "AcceptAction").IsChecked);
                    Assert.False(Field<CheckBox>(live, "VoiceChoice").IsChecked);
                    _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            setup = live.OwnedWindows.OfType<SetupWindow>().Single();
                            await Until(() => Field<StackPanel>(setup, "EditorPanel").IsEnabled);
                            Assert.False(Directory.Exists(scope.Data));
                            if (busy)
                                worker = runner.TryStart(async _ => { await release.Task; return new(SetupWorkOutcome.Completed); });
                            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
                            {
                                try
                                {
                                    await Until(() => setup.OwnedWindows.OfType<ConfigurationRecoveryWindow>().Any());
                                    recovery = setup.OwnedWindows.OfType<ConfigurationRecoveryWindow>().Single();
                                    Assert.Same(setup, recovery.Owner);
                                    Assert.Same(existingRecovery, Private<ConfigurationRecoveryController>(recovery, "controller"));
                                    Assert.True(recovery.IsVisible);
                                    Assert.True(IsWindowEnabled(new WindowInteropHelper(recovery).Handle));
                                    Assert.False(IsWindowEnabled(new WindowInteropHelper(setup).Handle));
                                    Assert.Equal(!busy, Field<StackPanel>(recovery, "Actions").IsEnabled);
                                    Assert.False(Field<Button>(recovery, "RestoreButton").IsEnabled);
                                    Assert.Null(existingRecovery.Preview);
                                    Assert.False(support.HasResources);
                                    Assert.False(Directory.Exists(scope.Data));
                                    if (busy)
                                    {
                                        Field<TextBox>(recovery, "BackupPath").Text = scope.Backup;
                                        Click(recovery, "RecoveryBackup"); // Programmatic delivery cannot bypass the shared slot.
                                        Assert.True(runner.IsRunning);
                                        Assert.False(Directory.Exists(scope.Data));
                                        Assert.Null(existingRecovery.Receipt);
                                    }
                                    await Heartbeat();
                                    Click(recovery, "RecoveryClose");
                                    Assert.False(recovery.IsVisible);
                                    if (busy) Assert.False(worker!.Completion.IsCompleted);
                                    inspected.TrySetResult();
                                }
                                catch (Exception error) { inspected.TrySetException(error); }
                                finally { recovery?.Close(); release.TrySetResult(); }
                            });
                            Click(setup, "SetupRecovery"); // Real nested modal dispatcher frame after the fix.
                            await inspected.Task;
                        }
                        catch (Exception error) { inspected.TrySetException(error); }
                        finally { setup?.Close(); release.TrySetResult(); }
                    });
                    Field<Button>(live, "SetupButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await inspected.Task;
                }
                catch (Exception error) { inspected.TrySetException(error); }
                finally { live?.Close(); }
            });
            Field<Button>(main, "ConversationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await inspected.Task.WaitAsync(TimeSpan.FromSeconds(12));
        }
        finally
        {
            release.TrySetResult();
            recovery?.Close(); setup?.Close(); live?.Close();
            await Until(() => !runner.IsRunning);
            main.Close();
            await Until(() => !main.IsVisible);
        }
    });

    private static T Private<T>(object target, string name) where T : class =>
        Assert.IsType<T>(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [Fact]
    public Task ShownPassiveRecoveryAndDefaultNoHaveNoEffects() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var runner = new SetupOperationRunner();
        var controller = new ConfigurationRecoveryController(scope.Store, runner);
        var window = Open(controller, _ => false);
        try
        {
            await Heartbeat();
            Assert.False(Directory.Exists(scope.Data));
            Assert.False(runner.IsRunning);
            Assert.False(Field<Button>(window, "RestoreButton").IsEnabled);
            var confirmation = new ConfirmationDialog("Review local configuration action", "Confirm?");
            Assert.True(Field<Button>(confirmation, "NoButton").IsDefault);
            Field<TextBox>(window, "BackupPath").Text = scope.Backup;
            Click(window, "RecoveryBackup");
            await Heartbeat();
            Assert.False(Directory.Exists(scope.Data));
            Assert.False(runner.IsRunning);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ActualPreviewAndConfirmationApplyOnlyExactInertCandidate(bool consent) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        var original = await File.ReadAllBytesAsync(scope.Store.FilePath);
        var controller = new ConfigurationRecoveryController(scope.Store, new());
        var confirmations = 0;
        var window = Open(controller, text =>
        {
            confirmations++;
            Assert.Contains("Snapshot SHA-256:", text);
            Assert.Contains("Current revision:", text);
            Assert.Contains("key unbound", text);
            return consent;
        });
        try
        {
            await Preview(window, controller, scope.Backup);
            var candidate = Field<TextBox>(window, "CandidateText").Text;
            Assert.DoesNotContain("\"credential_id\"", candidate);
            Assert.Contains("gpt-4o-mini-tts", candidate);
            Assert.DoesNotContain("\"consent\"", candidate);
            Click(window, "RecoveryRestore");
            await Until(() => !controller.IsBusy);
            await Heartbeat();
            Assert.Equal(1, confirmations);
            Assert.Equal(consent ? candidate : System.Text.Encoding.UTF8.GetString(original),
                await File.ReadAllTextAsync(scope.Store.FilePath));
            if (consent)
            {
                Assert.NotNull(controller.Receipt!.OriginalSnapshot);
                Assert.Equal(original, await File.ReadAllBytesAsync(controller.Receipt.OriginalSnapshot));
            }
            else Assert.Null(controller.Receipt);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("path")]
    [InlineData("bytes")]
    [InlineData("revision")]
    public Task MutationsInsideConfirmationCannotAuthorizeUnseenRestore(string mutation) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        var controller = new ConfigurationRecoveryController(scope.Store, new());
        ConfigurationRecoveryWindow? window = null;
        window = Open(controller, _ =>
        {
            if (mutation == "path") Field<TextBox>(window!, "SourcePath").Text = scope.Backup + ".other";
            if (mutation == "bytes") File.AppendAllText(scope.Backup, "\n");
            if (mutation == "revision") File.AppendAllText(scope.Store.FilePath, "\n");
            return true;
        });
        try
        {
            await Preview(window, controller, scope.Backup);
            Click(window, "RecoveryRestore");
            await Until(() => !controller.IsBusy);
            await Until(() => Field<TextBox>(window, "ResultText").Text.Contains(mutation == "path" ? "changed" : "Conflict", StringComparison.Ordinal));
            Assert.Null(controller.Receipt);
            Assert.Null(controller.Preview);
            Assert.Empty(Directory.GetFiles(scope.Data, "settings.recovery.*.bak"));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BlockingBackupOrRestoreRetainsActualOwnerAcrossTimeoutCloseAndReopen(bool restore) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        var clock = new ManualClock();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var ioThread = 0;
        var store = new SettingsStore(scope.Data) { RecoveryIo = (point, _) =>
        {
            if (point != SettingsIoPoint.BeforeStage) return;
            Interlocked.Increment(ref calls);
            ioThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled recovery IO not released.");
        } };
        var runner = new SetupOperationRunner();
        var controller = new ConfigurationRecoveryController(store, runner);
        var window = Open(controller, _ => true, clock);
        ConfigurationRecoveryWindow? reopened = null;
        try
        {
            if (restore) await Preview(window, controller, scope.Backup);
            else Field<TextBox>(window, "BackupPath").Text = scope.Backup + ".new";
            Click(window, restore ? "RecoveryRestore" : "RecoveryBackup");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEqual(Environment.CurrentManagedThreadId, ioThread);
            await Heartbeat();
            Assert.True(runner.IsRunning);
            Assert.Null(runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            Click(window, restore ? "RecoveryRestore" : "RecoveryBackup");
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(6));
            await Until(() => Field<TextBox>(window, "ResultText").Text.Contains("timed out", StringComparison.Ordinal));
            Assert.True(runner.IsRunning);
            window.Close();
            reopened = Open(controller);
            Assert.False(Field<StackPanel>(reopened, "Actions").IsEnabled);
            Assert.Null(controller.Preview);
            Click(reopened, "RecoveryPreview");
            Assert.Equal(1, calls);
            await Heartbeat();
            release.Set();
            await Until(() => !runner.IsRunning);
            Assert.False(controller.HasResources);
            Assert.Null(controller.Receipt);
            Assert.Null(controller.Preview);
        }
        finally
        {
            release.Set(); window.Close(); reopened?.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task NoncooperativeCancellationCallbacksKeepSlotAfterActualWriteFinishes() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        using var writeRelease = new ManualResetEventSlim();
        using var callbackRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SettingsStore(scope.Data) { RecoveryIo = (point, token) =>
        {
            if (point != SettingsIoPoint.BeforeStage) return;
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!callbackRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled cancellation callback not released.");
            });
            entered.TrySetResult();
            if (!writeRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled IO not released.");
        } };
        var runner = new SetupOperationRunner();
        var controller = new ConfigurationRecoveryController(store, runner);
        var window = Open(controller, _ => true);
        try
        {
            Field<TextBox>(window, "BackupPath").Text = scope.Backup + ".new";
            Click(window, "RecoveryBackup");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Click(window, "RecoveryCancel");
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            writeRelease.Set();
            await Until(() => controller.Message.Contains("canceled", StringComparison.Ordinal));
            await Heartbeat();
            Assert.True(runner.IsRunning);
            Assert.True(controller.HasResources);
            Assert.Null(runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            callbackRelease.Set();
            await Until(() => !runner.IsRunning);
        }
        finally
        {
            writeRelease.Set(); callbackRelease.Set(); window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task CleanupFailureSurvivesPresentationReopenAndExplicitRetry() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        var fail = true;
        var store = new SettingsStore(scope.Data) { RecoveryIo = (point, _) =>
        {
            if (fail && point is SettingsIoPoint.BeforeFlush or SettingsIoPoint.BeforeCleanup)
                throw new UnauthorizedAccessException("PRIVATE-NATIVE-ERROR");
        } };
        var runner = new SetupOperationRunner();
        var controller = new ConfigurationRecoveryController(store, runner);
        var window = Open(controller, _ => true);
        try
        {
            Field<TextBox>(window, "BackupPath").Text = scope.Backup + ".new";
            Click(window, "RecoveryBackup");
            await Until(() => controller.NeedsCleanup && !runner.IsRunning);
            Assert.True(controller.HasResources);
            Assert.DoesNotContain("PRIVATE-NATIVE-ERROR", controller.Message);
            window.Close();
            window = Open(controller);
            Assert.True(Field<Button>(window, "CleanupButton").IsEnabled);
            Assert.False(Field<StackPanel>(window, "Actions").IsEnabled);
            fail = false;
            Click(window, "RecoveryCleanup");
            await Until(() => !controller.HasResources);
            Assert.Empty(Directory.GetFiles(scope.Data, "*.tmp"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ExistingEffectOrSupportOwnerBlocksRecoveryWithoutAutoCancel() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new SetupOperationRunner();
        var worker = runner.TryStart(async _ => { await release.Task; return new(SetupWorkOutcome.Completed); })!;
        var controller = new ConfigurationRecoveryController(scope.Store, runner);
        var window = Open(controller, _ => true);
        try
        {
            Field<TextBox>(window, "BackupPath").Text = scope.Backup + ".new";
            Click(window, "RecoveryBackup");
            Assert.False(File.Exists(scope.Backup + ".new"));
            Assert.True(runner.IsRunning);
            window.Close(); // Must not cancel someone else's action.
            Assert.False(worker.Completion.IsCompleted);
            release.SetResult();
            await worker.Completion;
            var supportOwned = true;
            controller = new(scope.Store, runner, () => !supportOwned);
            Assert.Null(controller.Backup(scope.Backup + ".new"));
            Assert.Contains("recording OFF", controller.Message);
            supportOwned = false;
            Assert.Equal(SetupWorkOutcome.Completed, (await controller.Backup(scope.Backup + ".new")!.Completion).Outcome);
        }
        finally { release.TrySetResult(); window.Close(); await worker.Completion; }
    });

    [Fact]
    public Task LivePauseDoesNotReleaseBlockedVaultOwnerForConfigurationReplacement() => OnDispatcher(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var source = Path.Combine(fixture.DirectoryPath, "local.martlet-config");
        await Task.Run(() => fixture.Store.CreateConfigurationSnapshotAsync(source));
        var recovery = new ConfigurationRecoveryController(fixture.Store, fixture.Runner);
        await recovery.ReadPreview(source)!.Completion;
        var plan = recovery.Preview!;
        fixture.Native.Block = true;
        var operation = fixture.Start();
        try
        {
            await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var calls = fixture.Native.Targets.Count;
            Assert.Null(recovery.Restore(plan, plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision)));
            fixture.Controller.SetControls(true, false, false);
            await Heartbeat();
            Assert.True(fixture.Runner.IsRunning);
            Assert.Null(recovery.ReadPreview(source));
            Assert.Equal(calls, fixture.Native.Targets.Count);
            Assert.Equal(0, fixture.Llm.Calls);
            fixture.Native.Release.Set();
            await fixture.Finish(operation);
            Assert.False(fixture.Runner.IsRunning);
            Assert.Equal(0, fixture.Llm.Calls);
            await recovery.ReadPreview(source)!.Completion;
            Assert.NotNull(recovery.Preview);
            Assert.Equal(calls, fixture.Native.Targets.Count);
        }
        finally { fixture.Native.Release.Set(); await fixture.Finish(operation); }
    });

    [Fact]
    public Task OwnedWindowCloseRetiresPreviewEvenWhenWpfSkipsClosing() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await scope.Prepare();
        var controller = new ConfigurationRecoveryController(scope.Store, new());
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        owner.Show();
        var window = new ConfigurationRecoveryWindow(controller) { Owner = owner, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            await Preview(window, controller, scope.Backup);
            owner.Close();
            Assert.Null(controller.Preview);
            Assert.Empty(Field<TextBox>(window, "CandidateText").Text);
            Assert.False(controller.HasResources);
        }
        finally { window.Close(); owner.Close(); }
    });

    private static ConfigurationRecoveryWindow Open(ConfigurationRecoveryController controller, Func<string, bool>? confirm = null, TimeProvider? clock = null)
    {
        var window = new ConfigurationRecoveryWindow(controller, confirm, clock) { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        return window;
    }
    private static async Task Preview(Window window, ConfigurationRecoveryController controller, string source)
    {
        Field<TextBox>(window, "SourcePath").Text = source;
        Click(window, "RecoveryPreview");
        await Until(() => controller.Preview is not null && Field<Button>(window, "RestoreButton").IsEnabled);
    }
    private static T Field<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));
    private static void Click(DependencyObject root, string id)
    {
        Find(root)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button? Find(DependencyObject current)
        {
            if (current is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                if (Find(child) is { } found) return found;
            return null;
        }
    }
    private static async Task Heartbeat() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "The bounded recovery condition was not reached.");
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
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private sealed class Scope : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Recovery.Dispatcher", Guid.NewGuid().ToString("N"));
        internal SettingsStore Store => new(Data);
        internal string Backup => Path.Combine(Data, "source.martlet-config");
        internal async Task Prepare()
        {
            var settings = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Tts, "gpt-4o-mini-tts", "coral");
            Assert.True((await Task.Run(() => Store.SaveAsync(settings, null))).Saved);
            await Task.Run(() => Store.CreateConfigurationSnapshotAsync(Backup));
        }
        public void Dispose() { if (Directory.Exists(Data)) Directory.Delete(Data, recursive: true); }
    }
}

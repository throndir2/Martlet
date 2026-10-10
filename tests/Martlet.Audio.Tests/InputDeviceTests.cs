using System.Reflection;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

public sealed class InputDeviceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DiscoveryIsExplicitAndDoesNotAuthorizeCapture()
    {
        var device = new ControlledDiscovery();
        await using var monitor = new InputDeviceMonitor(device);
        Assert.Equal(0, device.Opens);
        await Assert.ThrowsAsync<ContractException>(() => monitor.StartAsync(new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RefreshAsync());
        var list = await monitor.StartAsync(new(true)).WaitAsync(WaitLimit);
        Assert.Null(list.Error);
        var endpoint = Assert.Single(list.Endpoints);
        Assert.Equal("private-endpoint-id", endpoint.EndpointId);
        Assert.True(endpoint.IsDefault);
        Assert.Equal(1, device.Opens);
        Assert.Equal(1, device.Enumerations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.StartAsync(new(true)));
        var release = await monitor.StopAsync();
        Assert.True(release.Released);
        Assert.Equal(1, device.Disposals);
        Assert.Single(device.Threads.Distinct());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.RefreshAsync());
    }

    [Fact]
    public async Task NotificationsCarryBoundedMetadataAndNeverTriggerEnumerationOrReopen()
    {
        var device = new ControlledDiscovery();
        await using var monitor = new InputDeviceMonitor(device);
        var initial = await monitor.StartAsync(new(true)).WaitAsync(WaitLimit);
        var events = new List<InputDeviceEvent>();
        foreach (var change in new[] { InputDeviceChanges.Added, InputDeviceChanges.Removed,
            InputDeviceChanges.Default, InputDeviceChanges.State, InputDeviceChanges.Properties })
        {
            Interlocked.Or(ref device.PendingChanges, (int)change);
            var notification = await monitor.Events.ReadAsync().AsTask().WaitAsync(WaitLimit);
            Assert.Equal(change, notification.Changes);
            Assert.True(notification.Generation > initial.Generation);
            events.Add(notification);
        }
        Assert.Equal(1, device.Opens);
        Assert.Equal(1, device.Enumerations);
        var json = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("private-endpoint-id", json);
        Assert.DoesNotContain("Private headset", json);
        Assert.Equal(events.Select(e => e.Generation).OrderBy(g => g), events.Select(e => e.Generation));
        await monitor.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Equal(2, device.Enumerations);
        await monitor.StopAsync();
        Interlocked.Or(ref device.PendingChanges, (int)InputDeviceChanges.Added);
        Assert.False(monitor.Events.TryRead(out _));
        Assert.Equal(1, device.Opens);
    }

    [Fact]
    public async Task NotificationOverflowDropsOnlyOldMetadata()
    {
        var source = new ControlledDiscovery();
        await using var monitor = new InputDeviceMonitor(source);
        await monitor.StartAsync(new(true)).WaitAsync(WaitLimit);
        for (var i = 0; i < 80; i++)
        {
            Interlocked.Or(ref source.PendingChanges, (int)InputDeviceChanges.Properties);
            await CaptureTests.Until(() => Volatile.Read(ref source.PendingChanges) == 0);
        }
        await monitor.StopAsync();
        var events = new List<InputDeviceEvent>();
        await foreach (var item in monitor.Events.ReadAllAsync()) events.Add(item);
        Assert.InRange(events.Count, 1, 32);
        Assert.True(events[^1].DroppedEvents > 0);
        Assert.Equal(1, source.Opens);
        Assert.Equal(1, source.Enumerations);
    }

    [Fact]
    public async Task NoncooperativeDiscoveryOpenCannotEnumerateAfterStop()
    {
        var clock = new CaptureClock();
        using var barrier = new ManualResetEventSlim();
        var source = new ControlledDiscovery { OpenBlock = barrier };
        await using var monitor = new InputDeviceMonitor(source, clock);
        var starting = monitor.StartAsync(new(true));
        try
        {
            await CaptureTests.Until(() => source.OpenEntered.IsSet);
            var stopping = monitor.StopAsync();
            Assert.NotNull((await starting.WaitAsync(WaitLimit)).Error);
            await CaptureTests.Until(() => clock.TimerCount > 0);
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False((await stopping.WaitAsync(WaitLimit)).Released);
            Assert.False(monitor.DeviceRelease.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync(new(true)));
        }
        finally
        {
            barrier.Set();
            Assert.True((await monitor.DeviceRelease.WaitAsync(WaitLimit)).Released);
        }
        Assert.Equal(0, source.Enumerations);
        Assert.Equal(1, source.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryDisposalFailureAndHungUnregisterAreTruthful(bool blocked)
    {
        var clock = new CaptureClock();
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledDiscovery
        {
            DisposeBlock = blocked ? barrier : null,
            DisposeFailure = blocked ? null : new Exception("private-exception")
        };
        await using var monitor = new InputDeviceMonitor(device, clock);
        await monitor.StartAsync(new(true)).WaitAsync(WaitLimit);
        try
        {
            var stop = monitor.StopAsync();
            await CaptureTests.Until(() => device.DisposeEntered.IsSet);
            if (blocked)
            {
                await CaptureTests.Until(() => clock.TimerCount > 0);
                clock.Advance(TimeSpan.FromSeconds(2));
            }
            var result = await stop.WaitAsync(WaitLimit);
            Assert.False(result.Released);
            Assert.Equal(ErrorCode.AudioCaptureFailed, result.Error!.Code);
            Assert.DoesNotContain("private-exception", result.Error.Summary);
            Assert.False(monitor.Events.TryRead(out _));
        }
        finally
        {
            barrier.Set();
            await monitor.DeviceRelease.WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task DiscoveryFailureHasNoInventedSuccessfulEmptyList()
    {
        var device = new ControlledDiscovery { Failure = new CaptureDeviceException(ErrorCode.AudioAccessDenied) };
        await using var monitor = new InputDeviceMonitor(device);
        var list = await monitor.StartAsync(new(true)).WaitAsync(WaitLimit);
        Assert.Empty(list.Endpoints);
        Assert.Equal(ErrorCode.AudioAccessDenied, list.Error!.Code);
        Assert.True((await monitor.DeviceRelease.WaitAsync(WaitLimit)).Released);
    }

#if WINDOWS
    [Theory]
    [InlineData(44100, 1, false)]
    [InlineData(48000, 2, true)]
    [InlineData(96000, 8, true)]
    public void ActualNAudioFormatSignaturesAreValidatedWithoutNativeActivation(int rate, int channels, bool floating)
    {
        var format = floating ? NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)
            : new NAudio.Wave.WaveFormat(rate, 16, channels);
        var source = Windows.WasapiCaptureDeviceFactory.DescribeFormat(format);
        Assert.Equal(rate, source.SampleRate);
        Assert.Equal(channels, source.Channels);
        Assert.Equal(floating ? DeviceSampleEncoding.IeeeFloat : DeviceSampleEncoding.IntegerPcm, source.Encoding);
        Assert.Equal(source, Windows.WasapiCaptureDeviceFactory.DescribeFormat(new NAudio.Wave.WaveFormatExtensible(rate, floating ? 32 : 16, channels)));
    }

    [Fact]
    public void ExtensibleContainersAndUnsupportedEncodingsAreNotBlindlyReinterpreted()
    {
        foreach (var format in new NAudio.Wave.WaveFormat[]
        {
            new NAudio.Wave.WaveFormat(48000, 24, 2),
            new NAudio.Wave.WaveFormatExtensible(48000, 32, 2, false, 24, 3),
            new NAudio.Wave.WaveFormatExtensible(48000, 16, 2, false, 12, 3),
            new NAudio.Wave.WaveFormatExtensible(48000, 32, 2, Guid.NewGuid(), 32, 3),
            new NAudio.Wave.WaveFormat(192000, 16, 2),
            NAudio.Wave.WaveFormat.CreateALawFormat(16000, 1)
        })
            Assert.Equal(ErrorCode.AudioFormatUnsupported,
                Assert.Throws<CaptureDeviceException>(() => Windows.WasapiCaptureDeviceFactory.DescribeFormat(format)).Code);
    }

    [Theory]
    [InlineData(InputPolicy.FixedEndpoint, false)]
    [InlineData(InputPolicy.FollowDefaultOnNextPress, true)]
    public void ActualNotificationHandlersEnforceSelectionAndNeverRearm(InputPolicy policy, bool defaultStops)
    {
        // Exercise the production managed callback handlers only. Open is NEVER invoked.
        var type = typeof(Windows.WasapiCaptureDeviceFactory).Assembly.GetType("Martlet.Audio.Windows.NativeInputNotifications")!;
        using var state = (IDisposable)Activator.CreateInstance(type)!;
        type.GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, 1);
        type.GetMethod("Bind")!.Invoke(state, new object[] { "selected-input", policy });
        void Notify(string name, object args) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, new[] { null, args });
        void Check() => type.GetMethod("CheckSelected")!.Invoke(state, null);
        Notify("DefaultChanged", new NAudio.CoreAudioApi.DefaultDeviceChangedEventArgs(
            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, "speaker"));
        Check();
        Notify("DefaultChanged", new NAudio.CoreAudioApi.DefaultDeviceChangedEventArgs(
            NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console, "another-input"));
        if (defaultStops)
            Assert.Equal(ErrorCode.AudioDeviceChanged, Assert.IsType<CaptureDeviceException>(Assert.Throws<TargetInvocationException>(Check).InnerException).Code);
        else Check();
        Notify("Removed", new NAudio.CoreAudioApi.DeviceNotificationEventArgs("selected-input"));
        Assert.Equal(ErrorCode.AudioDeviceLost, Assert.IsType<CaptureDeviceException>(Assert.Throws<TargetInvocationException>(Check).InnerException).Code);
        Notify("Added", new NAudio.CoreAudioApi.DeviceNotificationEventArgs("selected-input"));
        Assert.Equal(ErrorCode.AudioDeviceLost, Assert.IsType<CaptureDeviceException>(Assert.Throws<TargetInvocationException>(Check).InnerException).Code);
        state.Dispose();
        type.GetMethod("PollChanges")!.Invoke(state, null);
        Notify("Added", new NAudio.CoreAudioApi.DeviceNotificationEventArgs("selected-input"));
        Assert.Equal(InputDeviceChanges.None, type.GetMethod("PollChanges")!.Invoke(state, null));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), ErrorCode.AudioAccessDenied)]
    [InlineData(unchecked((int)0x80070490), ErrorCode.AudioDeviceUnavailable)]
    [InlineData(unchecked((int)0x88890004), ErrorCode.AudioDeviceLost)]
    [InlineData(unchecked((int)0x88890008), ErrorCode.AudioFormatUnsupported)]
    [InlineData(unchecked((int)0x8889000A), ErrorCode.AudioDeviceBusy)]
    [InlineData(unchecked((int)0x80004005), ErrorCode.AudioCaptureFailed)]
    public void NativeErrorsExposeOnlySanitizedActionableCodes(int hresult, ErrorCode expected)
    {
        var normalize = typeof(Windows.WasapiCaptureDeviceFactory).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
        var exception = new System.Runtime.InteropServices.COMException("endpoint-id and private-native-message", hresult);
        var failure = Assert.IsType<CaptureDeviceException>(normalize.Invoke(null, new object?[] { exception, null }));
        Assert.Equal(expected, failure.Code);
        Assert.DoesNotContain("private-native-message", failure.Message);
        Assert.Equal($"Windows error 0x{hresult:X8}", failure.Detail);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void PropertyChangesAloneNeverStopCaptureButAreKeptForTheLog()
    {
        // Some drivers rewrite properties (even the format) whenever a stream starts; that stopped always listening every try.
        var type = typeof(Windows.WasapiCaptureDeviceFactory).Assembly.GetType("Martlet.Audio.Windows.NativeInputNotifications")!;
        var lines = new List<string>();
        AudioDiagnostics.SetSink(line => { lock (lines) lines.Add(line); });
        try
        {
            var state = (IDisposable)Activator.CreateInstance(type)!;
            type.GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, 1);
            void Notify(string device, NAudio.CoreAudioApi.PropertyKey key) => type.GetMethod("PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(state, new object?[] { null, new NAudio.CoreAudioApi.DevicePropertyChangedEventArgs(device, key) });
            var unknown = new NAudio.CoreAudioApi.PropertyKey(Guid.NewGuid(), 7);
            // Before the input is bound, a property change doesn't count as the inputs changing.
            Notify("selected-input", NAudio.CoreAudioApi.PropertyKeys.PKEY_AudioEngine_DeviceFormat);
            type.GetMethod("Bind")!.Invoke(state, new object[] { "selected-input", InputPolicy.FixedEndpoint });
            Notify("selected-input", NAudio.CoreAudioApi.PropertyKeys.PKEY_AudioEngine_DeviceFormat);
            Notify("selected-input", unknown);
            Notify("another-input", NAudio.CoreAudioApi.PropertyKeys.PKEY_AudioEndpoint_JackSubType);
            type.GetMethod("CheckSelected")!.Invoke(state, null);
            var summary = Assert.IsType<string>(type.GetProperty("PropertyChanges")!.GetValue(state));
            Assert.Contains("2 property changes", summary);
            Assert.Contains("PKEY_AudioEngine_DeviceFormat", summary);
            Assert.Contains(unknown.formatId.ToString("B") + ",7", summary);
            Assert.DoesNotContain("JackSubType", summary);
            Assert.DoesNotContain("selected-input", summary);
            state.Dispose();
            string line;
            lock (lines) line = Assert.Single(lines, entry => entry.Contains(unknown.formatId.ToString("B"), StringComparison.Ordinal));
            Assert.DoesNotContain("selected-input", line);
        }
        finally { AudioDiagnostics.SetSink(null); }
    }
#endif
}

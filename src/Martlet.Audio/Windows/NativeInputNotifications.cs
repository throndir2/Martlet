using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;

namespace Martlet.Audio.Windows;

// OS callbacks only coalesce metadata. They never enumerate, acquire a run lock, open or restart audio.
internal sealed class NativeInputNotifications : IDisposable
{
    private MMDeviceEnumerator? enumerator;
    private MMDeviceNotificationClient? notifications;
    private string? selectedId;
    private InputPolicy policy;
    private int active, changes, selectedFailure;
    public MMDeviceEnumerator Enumerator => enumerator ?? throw new InvalidOperationException("Device discovery is not open.");

    public void Open()
    {
        enumerator = new();
        notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        Volatile.Write(ref active, 1);
        notifications.DeviceAdded += Added;
        notifications.DeviceRemoved += Removed;
        notifications.DeviceStateChanged += StateChanged;
        notifications.DefaultDeviceChanged += DefaultChanged;
        notifications.PropertyValueChanged += PropertyChanged;
    }

    public void Bind(string endpointId, InputPolicy selectionPolicy)
    {
        policy = selectionPolicy;
        Volatile.Write(ref selectedId, endpointId);
        if (Volatile.Read(ref changes) != 0) Interlocked.CompareExchange(ref selectedFailure, 1, 0);
    }

    public InputDeviceChanges PollChanges() => (InputDeviceChanges)Interlocked.Exchange(ref changes, 0);

    public void CheckSelected()
    {
        var failure = Volatile.Read(ref selectedFailure);
        if (failure != 0) throw new CaptureDeviceException(failure == 2 ? ErrorCode.AudioDeviceLost : ErrorCode.AudioDeviceChanged);
    }

    private void Change(InputDeviceChanges kind)
    {
        if (Volatile.Read(ref active) != 0) Interlocked.Or(ref changes, (int)kind);
    }

    private bool Selected(string? id) => Volatile.Read(ref active) != 0
        && Volatile.Read(ref selectedId) is { } selected && string.Equals(selected, id, StringComparison.Ordinal);

    private void Added(object? sender, DeviceNotificationEventArgs args) => Change(InputDeviceChanges.Added);
    private void Removed(object? sender, DeviceNotificationEventArgs args)
    {
        if (Selected(args.DeviceId)) Interlocked.Exchange(ref selectedFailure, 2);
        Change(InputDeviceChanges.Removed);
    }
    private void StateChanged(object? sender, DeviceStateChangedEventArgs args)
    {
        if (Selected(args.DeviceId) && args.NewState != DeviceState.Active) Interlocked.Exchange(ref selectedFailure, 2);
        Change(InputDeviceChanges.State);
    }
    private void DefaultChanged(object? sender, DefaultDeviceChangedEventArgs args)
    {
        if (args.Flow != DataFlow.Capture || args.Role != Role.Console) return;
        if (Volatile.Read(ref active) != 0 && Volatile.Read(ref selectedId) is { } selected
            && policy == InputPolicy.FollowDefaultOnNextPress && !string.Equals(selected, args.DeviceId, StringComparison.Ordinal))
            Interlocked.CompareExchange(ref selectedFailure, 1, 0);
        Change(InputDeviceChanges.Default);
    }
    private void PropertyChanged(object? sender, DevicePropertyChangedEventArgs args)
    {
        // Conservatively invalidate any selected property change, including format/profile churn.
        if (Selected(args.DeviceId)) Interlocked.CompareExchange(ref selectedFailure, 1, 0);
        Change(InputDeviceChanges.Properties);
    }

    public void Dispose()
    {
        Volatile.Write(ref active, 0);
        if (notifications is not null)
        {
            notifications.DeviceAdded -= Added;
            notifications.DeviceRemoved -= Removed;
            notifications.DeviceStateChanged -= StateChanged;
            notifications.DefaultDeviceChanged -= DefaultChanged;
            notifications.PropertyValueChanged -= PropertyChanged;
        }
        // Unregister through the live enumerator before releasing it. Windows' enumerator is a process-wide
        // singleton that does not AddRef the client, so releasing our reference first left the callback
        // registered while NAudio dropped its only CCW reference; once collected, the next endpoint
        // property change crashed in MMDevApi (CDeviceEnumerator::OnPropertyValueChanged).
        notifications?.Dispose();
        notifications = null;
        enumerator?.Dispose();
        enumerator = null;
        selectedId = null;
    }
}

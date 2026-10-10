using System.Collections.Concurrent;
using System.Reflection;
using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;

namespace Martlet.Audio.Windows;

// OS callbacks only coalesce metadata. They never enumerate, acquire a run lock, open or restart audio.
internal sealed class NativeInputNotifications : IDisposable
{
    private const int KeptProperties = 8;
    // Names for the property keys NAudio knows; others show as {format id},property id.
    private static readonly Dictionary<(Guid, int), string> names = KnownProperties();
    // Properties already in the log this run, so a driver that rewrites one all the time can't flood it.
    private static readonly ConcurrentDictionary<string, byte> logged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> properties = new(StringComparer.Ordinal);
    private MMDeviceEnumerator? enumerator;
    private MMDeviceNotificationClient? notifications;
    private string? selectedId;
    private Failure? failure;
    private InputPolicy policy;
    private int active, changes, propertyChanges;
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
        // An input added, removed or switched while this one was chosen may mean it isn't the one meant; a property change can't.
        var before = (InputDeviceChanges)Volatile.Read(ref changes) & ~InputDeviceChanges.Properties;
        if (before != InputDeviceChanges.None) Fail(ErrorCode.AudioDeviceChanged, $"devices changed while it opened ({before})");
    }

    public InputDeviceChanges PollChanges() => (InputDeviceChanges)Interlocked.Exchange(ref changes, 0);

    public void CheckSelected()
    {
        if (Volatile.Read(ref failure) is { } failed) throw new CaptureDeviceException(failed.Code, detail: failed.Reason);
    }

    /// <summary>For the log: how many property changes Windows reported on the selected input and which, or null when none.</summary>
    public string? PropertyChanges
    {
        get
        {
            var count = Volatile.Read(ref propertyChanges);
            return count == 0 ? null : $"Windows reported {count} property change{(count == 1 ? "" : "s")} on it first " +
                $"({string.Join(", ", properties.Keys.Order(StringComparer.Ordinal))})";
        }
    }

    private void Change(InputDeviceChanges kind)
    {
        if (Volatile.Read(ref active) != 0) Interlocked.Or(ref changes, (int)kind);
    }

    private bool Selected(string? id) => Volatile.Read(ref active) != 0
        && Volatile.Read(ref selectedId) is { } selected && string.Equals(selected, id, StringComparison.Ordinal);

    // A lost input outranks a changed one; otherwise the first reason stays.
    private void Fail(ErrorCode code, string reason)
    {
        var next = new Failure(code, reason);
        while (Volatile.Read(ref failure) is var current
            && (current is null || (code == ErrorCode.AudioDeviceLost && current.Code != ErrorCode.AudioDeviceLost)))
            if (Interlocked.CompareExchange(ref failure, next, current) == current) return;
    }

    private void Added(object? sender, DeviceNotificationEventArgs args) => Change(InputDeviceChanges.Added);
    private void Removed(object? sender, DeviceNotificationEventArgs args)
    {
        if (Selected(args.DeviceId)) Fail(ErrorCode.AudioDeviceLost, "Windows removed it");
        Change(InputDeviceChanges.Removed);
    }
    private void StateChanged(object? sender, DeviceStateChangedEventArgs args)
    {
        if (Selected(args.DeviceId) && args.NewState != DeviceState.Active) Fail(ErrorCode.AudioDeviceLost, $"Windows set it to {args.NewState}");
        Change(InputDeviceChanges.State);
    }
    private void DefaultChanged(object? sender, DefaultDeviceChangedEventArgs args)
    {
        if (args.Flow != DataFlow.Capture || args.Role != Role.Console) return;
        if (Volatile.Read(ref active) != 0 && Volatile.Read(ref selectedId) is { } selected
            && policy == InputPolicy.FollowDefaultOnNextPress && !string.Equals(selected, args.DeviceId, StringComparison.Ordinal))
            Fail(ErrorCode.AudioDeviceChanged, "Windows' default microphone changed");
        Change(InputDeviceChanges.Default);
    }
    private void PropertyChanged(object? sender, DevicePropertyChangedEventArgs args)
    {
        // A property change alone never stops capture. Some drivers rewrite properties (even the format) whenever a stream
        // starts, and that stopped always listening about a second into every try. When the format or hardware really changes,
        // Windows ends the stream itself (AUDCLNT_E_DEVICE_INVALIDATED, so AudioDeviceLost). Each one is kept for the log.
        if (Selected(args.DeviceId))
        {
            Interlocked.Increment(ref propertyChanges);
            if (properties.Count < KeptProperties) properties.TryAdd(Name(args.PropertyKey), 0);
        }
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
        NoteProperties();
    }

    // On the capture's own thread, once per property per run: which properties Windows changed on the microphone in use.
    private void NoteProperties()
    {
        if (Volatile.Read(ref propertyChanges) == 0) return;
        var fresh = properties.Keys.Order(StringComparer.Ordinal).Where(key => logged.TryAdd(key, 0)).ToArray();
        if (fresh.Length == 0) return;
        AudioDiagnostics.Note($"Microphone: Windows reported a change to {string.Join(", ", fresh)} on the microphone in use; " +
            "a property change alone doesn't stop listening (each property is noted once per run).");
    }

    private static string Name(PropertyKey key) =>
        names.TryGetValue((key.formatId, key.propertyId), out var name) ? name : $"{key.formatId:B},{key.propertyId}";

    private static Dictionary<(Guid, int), string> KnownProperties()
    {
        var known = new Dictionary<(Guid, int), string>();
        foreach (var field in typeof(PropertyKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is PropertyKey key) known.TryAdd((key.formatId, key.propertyId), field.Name);
        return known;
    }

    private sealed record Failure(ErrorCode Code, string Reason);
}

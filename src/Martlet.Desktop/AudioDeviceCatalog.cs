using System.Runtime.InteropServices;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using NAudio.CoreAudioApi;

namespace Martlet.Desktop;

public sealed record AudioEndpoint(string EndpointId, string DisplayName, bool IsDefault)
{
    public override string ToString() => DisplayName + (IsDefault ? " (Windows default)" : "");
}

public sealed record AudioDeviceList(IReadOnlyList<AudioEndpoint> Inputs, IReadOnlyList<AudioEndpoint> Outputs)
{
    public bool SameAs(AudioDeviceList? other) =>
        other is not null && Inputs.SequenceEqual(other.Inputs) && Outputs.SequenceEqual(other.Outputs);

    /// <summary>Whether the chosen device is connected: the Windows default counts when any device of that kind exists.</summary>
    public static bool? Present(AudioChoice? choice, IReadOnlyList<AudioEndpoint>? found) =>
        found is null ? null : choice?.EndpointId is { } id ? found.Any(item => item.EndpointId == id) : found.Count > 0;
}

public interface IAudioDeviceCatalog
{
    // Lists endpoint names only, never opening one. Return only after releasing enumeration resources.
    AudioDeviceList Discover(CancellationToken token);
}

/// <summary>Checks off the UI thread which microphones and speakers are plugged in, for status pages. A failed or slow
/// check reports nothing, and callers then assume the Windows defaults work.</summary>
public sealed class AudioDevicePresence
{
    private readonly IAudioDeviceCatalog catalog;
    private Task<AudioDeviceList?>? running;
    public AudioDevicePresence(IAudioDeviceCatalog? catalog = null) => this.catalog = catalog ?? new WindowsAudioDeviceCatalog();
    public AudioDeviceList? Last { get; private set; }

    public async Task<AudioDeviceList?> CheckAsync(CancellationToken token)
    {
        // One check at a time: a stuck driver call keeps its one thread, never a growing pile of them.
        running = running is { IsCompleted: false } busy ? busy : Task.Run(() =>
        {
            try { return catalog.Discover(token); }
            catch (Exception) { return null; }
        }, CancellationToken.None);
        try { Last = await running.WaitAsync(TimeSpan.FromSeconds(3), token); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { Last = null; }
        return Last;
    }
}

public sealed class AudioDiscoveryException(ErrorCode code, bool released = true) : Exception
{
    public ErrorCode Code { get; } = code;
    public bool Released { get; } = released;
}

// UI-only enumeration; construction is inert. Never opens an audio client or queries volume/privacy.
public sealed class WindowsAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly IInputDeviceDiscoveryFactory inputs;
    private IInputDeviceDiscovery? input;
    private MMDeviceEnumerator? enumerator;
    private MMDeviceCollection? collection;
    private MMDevice? endpoint;
    private bool quarantined;
    private readonly HashSet<IDisposable> failedReleases = new(ReferenceEqualityComparer.Instance);

    public WindowsAudioDeviceCatalog() : this(new WasapiInputDeviceDiscoveryFactory()) { }
    public WindowsAudioDeviceCatalog(IInputDeviceDiscoveryFactory inputs) => this.inputs = inputs;

    public AudioDeviceList Discover(CancellationToken token)
    {
        if (quarantined) throw new AudioDiscoveryException(ErrorCode.AudioDeviceBusy, false);
        Exception? failure = null;
        AudioDeviceList? result = null;
        try
        {
            token.ThrowIfCancellationRequested();
            input = inputs.Open(token);
            var captured = input.Enumerate(token).Select(item => new AudioEndpoint(item.EndpointId, item.DisplayName, item.IsDefault)).ToArray();
            Validate(captured);
            token.ThrowIfCancellationRequested();
            enumerator = new MMDeviceEnumerator();
            token.ThrowIfCancellationRequested();
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var current))
            {
                endpoint = current;
                defaultId = endpoint.ID;
                Release(ref endpoint);
            }
            token.ThrowIfCancellationRequested();
            collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var count = collection.Count;
            if (count is < 0 or > 128) throw new AudioDiscoveryException(ErrorCode.PayloadTooLarge);
            var rendered = new List<AudioEndpoint>(count);
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                endpoint = collection[i];
                var id = endpoint.ID;
                rendered.Add(new(id, endpoint.FriendlyName, id == defaultId));
                Release(ref endpoint);
            }
            Validate(rendered);
            token.ThrowIfCancellationRequested();
            result = new(Array.AsReadOnly(captured), rendered.AsReadOnly());
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Failed COM release is sticky. Keep that wrapper rooted; don't retry a possibly no-op Dispose.
            Cleanup(ref endpoint);
            Cleanup(ref collection);
            Cleanup(ref enumerator);
            Cleanup(ref input);
        }
        if (quarantined || failure is CaptureDeviceException { ResourcesReleased: false })
        {
            quarantined = true;
            throw new AudioDiscoveryException(ErrorCode.AudioCaptureFailed, false);
        }
        if (failure is OperationCanceledException) throw new OperationCanceledException(token);
        if (failure is not null) throw new AudioDiscoveryException(failure switch
        {
            CaptureDeviceException capture => capture.Code,
            AudioDiscoveryException discovery => discovery.Code,
            UnauthorizedAccessException => ErrorCode.AudioAccessDenied,
            COMException { HResult: unchecked((int)0x80070005) } => ErrorCode.AudioAccessDenied,
            _ => ErrorCode.AudioDeviceUnavailable
        });
        token.ThrowIfCancellationRequested();
        return result!;
    }

    public static void Validate(IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (endpoints.Count > 128 || endpoints.Select(item => item.EndpointId).Distinct(StringComparer.Ordinal).Count() != endpoints.Count)
            throw new AudioDiscoveryException(ErrorCode.PayloadTooLarge);
        foreach (var item in endpoints)
        {
            new InputSelection(InputPolicy.FixedEndpoint, item.EndpointId).Validate();
            if (item.DisplayName is not { Length: > 0 and <= 256 } || item.DisplayName.Any(char.IsControl))
                throw new AudioDiscoveryException(ErrorCode.AudioDeviceUnavailable);
        }
    }

    private void Release<T>(ref T? value) where T : class, IDisposable
    {
        try { value?.Dispose(); value = null; }
        catch
        {
            quarantined = true;
            if (value is not null) failedReleases.Add(value);
            throw;
        }
    }

    private void Cleanup<T>(ref T? value) where T : class, IDisposable
    {
        if (value is not null && failedReleases.Contains(value)) return;
        try { Release(ref value); }
        catch (Exception) { quarantined = true; }
    }
}

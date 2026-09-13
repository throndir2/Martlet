using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;

namespace Martlet.Audio.Windows;

public sealed class WasapiInputDeviceDiscoveryFactory : IInputDeviceDiscoveryFactory
{
    private int leased;
    private Discovery? retainedSource;

    public IInputDeviceDiscovery Open(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref leased, 1, 0) != 0)
            throw new CaptureDeviceException(ErrorCode.AudioDeviceBusy);
        var source = new Discovery(() =>
        {
            retainedSource = null;
            Volatile.Write(ref leased, 0);
        });
        retainedSource = source;
        try
        {
            source.Open(cancellationToken);
            return source;
        }
        catch (Exception ex)
        {
            try { source.Dispose(); }
            catch (Exception)
            {
                throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            }
            if (ex is OperationCanceledException) throw;
            throw WasapiCaptureDeviceFactory.Normalize(ex);
        }
    }

    private sealed class Discovery(Action released) : IInputDeviceDiscovery
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private readonly NativeInputNotifications notifications = new();
        private MMDevice? inspectedEndpoint;
        private MMDeviceCollection? collection;
        private bool endpointReleaseFailed, collectionReleaseFailed, notificationReleaseFailed;
        private bool disposed;

        public void Open(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            notifications.Open();
            token.ThrowIfCancellationRequested();
        }

        public IReadOnlyList<InputEndpoint> Enumerate(CancellationToken cancellationToken)
        {
            CheckThread();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string? defaultId = null;
                if (notifications.Enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var defaultEndpoint))
                {
                    inspectedEndpoint = defaultEndpoint;
                    defaultId = inspectedEndpoint.ID;
                    ReleaseEndpoint();
                }
                collection = notifications.Enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var count = collection.Count;
                if (count is < 0 or > 128) throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                var endpoints = new List<InputEndpoint>(count);
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inspectedEndpoint = collection[i];
                    var id = inspectedEndpoint.ID;
                    endpoints.Add(new(id, inspectedEndpoint.FriendlyName, string.Equals(id, defaultId, StringComparison.Ordinal)));
                    ReleaseEndpoint();
                }
                ReleaseCollection();
                return endpoints.AsReadOnly();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public InputDeviceChanges PollChanges()
        {
            CheckThread();
            return notifications.PollChanges();
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed) return;
            // Failed temporary enumeration wrappers are part of this lease too. A later no-op
            // Dispose from a wrapper must not turn an earlier uncertain COM release into success.
            if (!endpointReleaseFailed)
            {
                try { ReleaseEndpoint(); }
                catch (Exception) { endpointReleaseFailed = true; }
            }
            if (!collectionReleaseFailed)
            {
                try { ReleaseCollection(); }
                catch (Exception) { collectionReleaseFailed = true; }
            }
            if (!notificationReleaseFailed)
            {
                try { notifications.Dispose(); }
                catch (Exception) { notificationReleaseFailed = true; }
            }
            if (endpointReleaseFailed || collectionReleaseFailed || notificationReleaseFailed)
                throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            disposed = true;
            released();
        }

        private void ReleaseEndpoint()
        {
            try { inspectedEndpoint?.Dispose(); inspectedEndpoint = null; }
            catch (Exception) { endpointReleaseFailed = true; throw; }
        }

        private void ReleaseCollection()
        {
            try { collection?.Dispose(); collection = null; }
            catch (Exception) { collectionReleaseFailed = true; throw; }
        }

        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("Device discovery must remain on its owning worker thread.");
        }
    }
}

using Martlet.Companion.Platform;
using Martlet.Platform.Linux.DBus;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Martlet.Platform.Linux.Capture;

/// <summary>A ScreenCast portal session for "watch my screen" on Wayland. Starting it shows the desktop's own dialog where
/// the user picks a screen (or declines). Martlet asks with persist_mode 0, so the choice is never remembered: every time
/// watching is turned on the system asks again, and turning watching off (or "Stop sharing" in the system UI) ends it.
/// Each look opens the portal's PipeWire remote and takes one frame.</summary>
internal sealed class ScreenCastSession : IDisposable
{
    private const string Interface = "org.freedesktop.portal.ScreenCast";
    private readonly SessionBus bus;
    private readonly string session;
    private readonly uint node;
    private IDisposable? closedWatch;
    private volatile bool closed;

    private ScreenCastSession(SessionBus bus, string session, uint node)
    {
        this.bus = bus;
        this.session = session;
        this.node = node;
    }

    public bool IsOpen => !closed;

    public static async Task<(ScreenCastSession? Session, FeatureStatus Status)> StartAsync(CancellationToken cancellationToken)
    {
        var (bus, error) = await SessionBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (bus is null) return (null, FeatureStatus.No($"The ScreenCast portal is unreachable: {error}"));
        string? session = null;
        try
        {
            await Portal.RegisterAppAsync(bus).ConfigureAwait(false);
            var token = Portal.NewToken();
            var created = await Portal.RequestAsync(bus, Interface, "CreateSession", "a{sv}", (ref MessageWriter w) => w.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["session_handle_token"] = VariantValue.String(Portal.NewToken())
            }), token, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            session = created.Succeeded ? Portal.SessionHandle(created) : null;
            if (session is null) { bus.Dispose(); return (null, FeatureStatus.No("The ScreenCast portal refused to start a session.")); }

            var cursorModes = await bus.GetPropertyAsync(Portal.Service, Portal.Path, Interface, "AvailableCursorModes").ConfigureAwait(false);
            var selectToken = Portal.NewToken();
            var selected = await Portal.RequestAsync(bus, Interface, "SelectSources", "oa{sv}", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                w.WriteDictionary(SelectSourcesOptions(selectToken, cursorModes is { } modes ? modes.GetUInt32() : 0));
            }, selectToken, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!selected.Succeeded) return Fail(bus, session, "The ScreenCast portal refused the screen selection.");

            var startToken = Portal.NewToken();
            var started = await Portal.RequestAsync(bus, Interface, "Start", "osa{sv}", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                w.WriteString("");
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(startToken) });
            }, startToken, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
            if (started.Cancelled) return Fail(bus, session, "You declined screen sharing, so Martlet can't watch your screen.");
            if (!started.Succeeded) return Fail(bus, session, "The desktop refused screen sharing.");
            if (FirstNode(started) is not { } node) return Fail(bus, session, "The desktop shared no screen.");

            var cast = new ScreenCastSession(bus, session, node);
            cast.closedWatch = await bus.WatchAsync(session, "org.freedesktop.portal.Session", "Closed",
                static (_, _) => true, _ => cast.closed = true).ConfigureAwait(false);
            return (cast, FeatureStatus.Yes("ScreenCast portal + PipeWire: sharing the screen you picked until watching stops"));
        }
        catch (DBusErrorReplyException reply)
        {
            return Fail(bus, session, $"The ScreenCast portal answered {reply.ErrorName}: {reply.ErrorMessage}");
        }
        catch (TimeoutException)
        {
            return Fail(bus, session, "The screen sharing dialog was not answered in time.");
        }
        catch (OperationCanceledException)
        {
            Fail(bus, session, "");
            throw;
        }
    }

    private static (ScreenCastSession?, FeatureStatus) Fail(SessionBus bus, string? session, string reason)
    {
        if (session is not null) Portal.CloseAsync(bus, session, "org.freedesktop.portal.Session").Wait(TimeSpan.FromSeconds(2));
        bus.Dispose();
        return (null, FeatureStatus.No(reason));
    }

    /// <summary>One monitor, cursor drawn into the picture when the portal can, and persist_mode 0: never remembered.</summary>
    internal static Dictionary<string, VariantValue> SelectSourcesOptions(string token, uint availableCursorModes) => new()
    {
        ["handle_token"] = VariantValue.String(token),
        ["types"] = VariantValue.UInt32(1),
        ["multiple"] = VariantValue.Bool(false),
        ["cursor_mode"] = VariantValue.UInt32((availableCursorModes & 2) != 0 ? 2u : 1u),
        ["persist_mode"] = VariantValue.UInt32(0)
    };

    /// <summary>The PipeWire node of the first shared stream in Start's answer (streams: a(ua{sv})).</summary>
    internal static uint? FirstNode(PortalResponse response)
    {
        if (!response.Results.TryGetValue("streams", out var streams) || streams.Type != VariantValueType.Array || streams.Count == 0) return null;
        var first = streams.GetItem(0);
        if (first.Type != VariantValueType.Struct || first.Count < 1) return null;
        var id = first.GetItem(0);
        return id.Type == VariantValueType.UInt32 ? id.GetUInt32() : null;
    }

    public async Task<ScreenShot?> CaptureAsync(ScreenCaptureRequest request, CancellationToken cancellationToken)
    {
        if (closed) return null;
        int fd;
        using (var handle = await bus.CallAsync(bus.Call(Portal.Service, Portal.Path, Interface, "OpenPipeWireRemote", "oa{sv}",
                   (ref MessageWriter w) =>
                   {
                       w.WriteObjectPath(session);
                       w.WriteDictionary(new Dictionary<string, VariantValue>());
                   }), static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>()).ConfigureAwait(false))
        {
            if (handle is null || handle.IsInvalid) return null;
            fd = (int)handle.DangerousGetHandle();
            handle.SetHandleAsInvalid();
        }
        var frame = await PipeWireFrameGrab.GrabAsync(fd, node, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        try { return FrameEncoder.Encode(frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.Format, request); }
        finally { Array.Clear(frame.Pixels); }
    }

    public void Dispose()
    {
        closed = true;
        closedWatch?.Dispose();
        try { Portal.CloseAsync(bus, session, "org.freedesktop.portal.Session").Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        bus.Dispose();
    }
}

/// <summary>The Screenshot portal, used on Wayland when PipeWire is missing. The portal writes a PNG and returns its URI;
/// Martlet reads it into memory and deletes it at once.</summary>
internal static class ScreenshotPortal
{
    public static async Task<ScreenShot?> CaptureAsync(ScreenCaptureRequest request, CancellationToken cancellationToken)
    {
        var (bus, _) = await SessionBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (bus is null) return null;
        using (bus)
        {
            await Portal.RegisterAppAsync(bus).ConfigureAwait(false);
            var token = Portal.NewToken();
            var answer = await Portal.RequestAsync(bus, "org.freedesktop.portal.Screenshot", "Screenshot", "sa{sv}", (ref MessageWriter w) =>
            {
                w.WriteString("");
                w.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["handle_token"] = VariantValue.String(token),
                    ["interactive"] = VariantValue.Bool(false)
                });
            }, token, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (!answer.Succeeded || !answer.Results.TryGetValue("uri", out var uri) ||
                !Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var location) || !location.IsFile) return null;
            var path = location.LocalPath;
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { return FrameEncoder.EncodeFile(bytes, request); }
            finally { Array.Clear(bytes); }
        }
    }
}

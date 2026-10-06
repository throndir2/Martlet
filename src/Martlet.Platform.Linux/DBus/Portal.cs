using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace Martlet.Platform.Linux.DBus;

/// <summary>Names the companion uses on the Linux desktop. The installers (packaging/linux) ship
/// <c>io.github.throndir2.Martlet.desktop</c>, which the portals use to name the app in their consent dialogs.</summary>
public static class LinuxDesktopIds
{
    public const string AppId = "io.github.throndir2.Martlet";
    public const string DisplayName = "Martlet";
}

/// <summary>Writes a method call's arguments. MessageWriter is a ref struct, so it is passed by reference and never
/// held across an await.</summary>
internal delegate void WriteArguments(ref MessageWriter writer);

/// <summary>A private connection to the user's session bus. Each service opens its own so a portal session, its signals and
/// any file descriptors it receives belong to that service alone.</summary>
internal sealed class SessionBus : IDisposable
{
    private SessionBus(DBusConnection connection) => Connection = connection;

    public DBusConnection Connection { get; }
    public string UniqueName => Connection.UniqueName ?? "";

    /// <summary>Connects, or returns null with the reason when there is no session bus (no desktop session, a container).</summary>
    public static async Task<(SessionBus? Bus, string? Error)> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return (null, "not Linux");
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) return (null, "no session bus (DBUS_SESSION_BUS_ADDRESS is not set)");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return (new SessionBus(connection), null);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            connection.Dispose();
            return (null, $"cannot reach the session bus: {error.Message}");
        }
    }

    /// <summary>A method call message, ready for <see cref="DBusConnection.CallMethodAsync(MessageBuffer)"/>.</summary>
    public MessageBuffer Call(string destination, string path, string @interface, string member, string? signature = null,
        WriteArguments? arguments = null)
    {
        var writer = Connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
            arguments?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    public Task<T> CallAsync<T>(MessageBuffer message, MessageValueReader<T> reader) => Connection.CallMethodAsync(message, reader, null);

    /// <summary>Watches a signal on one object; the handler gets only successfully read values.</summary>
    public async Task<IDisposable> WatchAsync<T>(string path, string @interface, string signal, MessageValueReader<T> reader, Action<T> handler) =>
        await Connection.WatchSignalAsync(null, path, @interface, signal, reader,
            (Notification<T> notification) => { if (notification.HasValue) handler(notification.Value); },
            ObserverFlags.None, false, null).ConfigureAwait(false);

    public Task<bool> NameHasOwnerAsync(string name) =>
        CallAsync(Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s",
            (ref MessageWriter w) => w.WriteString(name)), static (m, _) => m.GetBodyReader().ReadBool());

    /// <summary>A property value, or null when the object, interface or property does not exist.</summary>
    public async Task<VariantValue?> GetPropertyAsync(string destination, string path, string @interface, string property)
    {
        try
        {
            return await CallAsync(Call(destination, path, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter w) =>
            {
                w.WriteString(@interface);
                w.WriteString(property);
            }), static (m, _) => m.GetBodyReader().ReadVariantValue()).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException) { return null; }
    }

    public void Dispose() => Connection.Dispose();
}

/// <summary>What a portal request answered: 0 success, 1 the user cancelled, 2 anything else.</summary>
internal readonly record struct PortalResponse(uint Code, Dictionary<string, VariantValue> Results)
{
    public bool Succeeded => Code == 0;
    public bool Cancelled => Code == 1;
}

/// <summary>Calls on xdg-desktop-portal (org.freedesktop.portal.Desktop). Requests answer through a Response signal on a
/// request object whose path is known in advance from the caller's bus name and a handle token, so Martlet subscribes before
/// calling and cannot miss a quick answer.</summary>
internal static class Portal
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string Path = "/org/freedesktop/portal/desktop";

    /// <summary>A fresh handle token: a valid object path element, unique per request.</summary>
    public static string NewToken() => "martlet_" + RandomNumberGenerator.GetHexString(16, lowercase: true);

    /// <summary>The bus name as an object path element: ":1.42" becomes "1_42".</summary>
    public static string SenderElement(string uniqueName) => uniqueName.TrimStart(':').Replace('.', '_');

    public static string RequestPath(string uniqueName, string token) => $"{Path}/request/{SenderElement(uniqueName)}/{token}";

    public static string SessionPath(string uniqueName, string token) => $"{Path}/session/{SenderElement(uniqueName)}/{token}";

    /// <summary>Tells the portal which app this unsandboxed process is (xdg-desktop-portal 1.19+), so dialogs and stored
    /// permissions name Martlet. Must come before other portal calls on the connection; older portals lack it, which is fine.</summary>
    public static async Task RegisterAppAsync(SessionBus bus)
    {
        try
        {
            await bus.Connection.CallMethodAsync(bus.Call(Service, Path, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}",
                (ref MessageWriter w) =>
                {
                    w.WriteString(LinuxDesktopIds.AppId);
                    w.WriteDictionary(new Dictionary<string, VariantValue>());
                })).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException) { }
    }

    /// <summary>The interface's version property, or null when this desktop's portal does not offer the interface.</summary>
    public static async Task<uint?> VersionAsync(SessionBus bus, string @interface)
    {
        try
        {
            var value = await bus.GetPropertyAsync(Service, Path, @interface, "version").ConfigureAwait(false);
            return value is { } v ? v.GetUInt32() : null;
        }
        catch (DBusExceptionBase) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Calls a portal method that answers through a Request object and waits for its Response.
    /// <paramref name="arguments"/> writes the arguments; <paramref name="token"/> must also be in its options as
    /// handle_token.</summary>
    public static async Task<PortalResponse> RequestAsync(SessionBus bus, string @interface, string member, string signature,
        WriteArguments arguments, string token, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var expected = RequestPath(bus.UniqueName, token);
        var answer = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await Watch(bus, expected, answer).ConfigureAwait(false);
        IDisposable? second = null;
        try
        {
            var handle = await bus.CallAsync(bus.Call(Service, Path, @interface, member, signature, arguments),
                static (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
            // Portals before 0.9 choose their own path; listen there too.
            if (handle != expected) second = await Watch(bus, handle, answer).ConfigureAwait(false);
            try { return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                await CloseAsync(bus, handle, "org.freedesktop.portal.Request").ConfigureAwait(false);
                throw;
            }
        }
        finally { second?.Dispose(); }
    }

    internal static PortalResponse ReadResponse(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var code = reader.ReadUInt32();
        return new PortalResponse(code, reader.ReadDictionaryOfStringToVariantValue());
    }

    private static Task<IDisposable> Watch(SessionBus bus, string path, TaskCompletionSource<PortalResponse> answer) =>
        bus.WatchAsync(path, "org.freedesktop.portal.Request", "Response", ReadResponse, response => answer.TrySetResult(response));

    /// <summary>Closes a portal Request or Session object; errors (already closed) are ignored.</summary>
    public static async Task CloseAsync(SessionBus bus, string path, string @interface)
    {
        try { await bus.Connection.CallMethodAsync(bus.Call(Service, path, @interface, "Close")).ConfigureAwait(false); }
        catch (DBusExceptionBase) { }
    }

    /// <summary>The session handle from a CreateSession response (a string in current portals, an object path in old ones).</summary>
    public static string? SessionHandle(PortalResponse response) =>
        response.Results.TryGetValue("session_handle", out var value)
            ? value.Type == VariantValueType.ObjectPath ? value.GetObjectPathAsString() : value.GetString()
            : null;

    /// <summary>A{sv} options with just a handle token.</summary>
    public static Dictionary<string, VariantValue> Options(string token) => new() { ["handle_token"] = VariantValue.String(token) };
}

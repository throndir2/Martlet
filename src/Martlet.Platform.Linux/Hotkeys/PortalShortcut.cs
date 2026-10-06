using Martlet.Companion.Platform;
using Martlet.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Martlet.Platform.Linux.Hotkeys;

/// <summary>Push-to-talk through the xdg-desktop-portal GlobalShortcuts interface (KDE Plasma, GNOME 48+, Hyprland):
/// works on Wayland while any app has focus. Binding shows the desktop's own dialog where the user confirms or changes the
/// key; the portal then reports Activated on key down and Deactivated on key up.</summary>
internal sealed class PortalShortcut : IDisposable
{
    public const string ShortcutId = "push-to-talk";
    private const string Interface = "org.freedesktop.portal.GlobalShortcuts";
    private readonly SessionBus bus;
    private readonly string session;
    private readonly List<IDisposable> subscriptions = [];

    private PortalShortcut(SessionBus bus, string session)
    {
        this.bus = bus;
        this.session = session;
    }

    public static async Task<(PortalShortcut? Shortcut, FeatureStatus Status)> StartAsync(HotkeyGesture gesture, Action down, Action up,
        CancellationToken cancellationToken)
    {
        var trigger = LinuxKeys.PortalTrigger(gesture);
        if (trigger is null) return (null, FeatureStatus.No($"{gesture.Key} can't be used as a global key on Linux."));
        var (bus, error) = await SessionBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (bus is null) return (null, FeatureStatus.No($"The GlobalShortcuts portal is unreachable: {error}"));
        try
        {
            await Portal.RegisterAppAsync(bus).ConfigureAwait(false);
            var createToken = Portal.NewToken();
            var sessionToken = Portal.NewToken();
            var created = await Portal.RequestAsync(bus, Interface, "CreateSession", "a{sv}",
                (ref MessageWriter w) => w.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["handle_token"] = VariantValue.String(createToken),
                    ["session_handle_token"] = VariantValue.String(sessionToken)
                }), createToken, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var session = created.Succeeded ? Portal.SessionHandle(created) : null;
            if (session is null) { bus.Dispose(); return (null, FeatureStatus.No("The GlobalShortcuts portal refused to start a session.")); }

            var shortcut = new PortalShortcut(bus, session);
            try
            {
                await shortcut.WatchAsync("Activated", down).ConfigureAwait(false);
                await shortcut.WatchAsync("Deactivated", up).ConfigureAwait(false);
                var bindToken = Portal.NewToken();
                var bound = await Portal.RequestAsync(bus, Interface, "BindShortcuts", "oa(sa{sv})sa{sv}",
                    (ref MessageWriter w) => WriteBind(ref w, session, trigger, bindToken), bindToken, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
                var status = BindResult(bound, gesture);
                if (!status.Available) { shortcut.Dispose(); return (null, status); }
                return (shortcut, status);
            }
            catch
            {
                shortcut.Dispose();
                throw;
            }
        }
        catch (DBusErrorReplyException reply)
        {
            bus.Dispose();
            return (null, FeatureStatus.No($"The GlobalShortcuts portal answered {reply.ErrorName}: {reply.ErrorMessage}"));
        }
        catch (TimeoutException)
        {
            bus.Dispose();
            return (null, FeatureStatus.No("The shortcut dialog was not answered in time."));
        }
        catch (OperationCanceledException)
        {
            bus.Dispose();
            throw;
        }
    }

    internal static void WriteBind(ref MessageWriter writer, string session, string trigger, string token)
    {
        writer.WriteObjectPath(session);
        var shortcuts = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteStructureStart();
        writer.WriteString(ShortcutId);
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["description"] = VariantValue.String("Martlet push-to-talk (hold to talk)"),
            ["preferred_trigger"] = VariantValue.String(trigger)
        });
        writer.WriteArrayEnd(shortcuts);
        writer.WriteString("");
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) });
    }

    /// <summary>Reads BindShortcuts' answer: which key the user (or the desktop) actually assigned, if any.</summary>
    internal static FeatureStatus BindResult(PortalResponse response, HotkeyGesture gesture)
    {
        if (response.Cancelled) return FeatureStatus.No("You closed the shortcut dialog, so there is no global push-to-talk key.");
        if (!response.Succeeded) return FeatureStatus.No("The desktop refused the push-to-talk shortcut.");
        if (!response.Results.TryGetValue("shortcuts", out var shortcuts) || shortcuts.Type != VariantValueType.Array)
            return FeatureStatus.Yes($"GlobalShortcuts portal: {gesture} (the desktop did not say which key it assigned)");
        for (var i = 0; i < shortcuts.Count; i++)
        {
            var item = shortcuts.GetItem(i);
            if (item.Type != VariantValueType.Struct || item.Count < 2 || item.GetItem(0).GetString() != ShortcutId) continue;
            var options = item.GetItem(1);
            string? described = null;
            for (var j = 0; j < options.Count; j++)
            {
                var entry = options.GetDictionaryEntry(j);
                if (entry.Key.GetString() != "trigger_description") continue;
                var value = entry.Value.Type == VariantValueType.Variant ? entry.Value.GetVariantValue() : entry.Value;
                described = value.GetString();
            }
            return FeatureStatus.Yes(string.IsNullOrWhiteSpace(described)
                ? $"GlobalShortcuts portal: {gesture}"
                : $"GlobalShortcuts portal: hold {described} to talk");
        }
        return FeatureStatus.No("The desktop did not assign the push-to-talk shortcut; change it in the system keyboard settings.");
    }

    private async Task WatchAsync(string signal, Action action) =>
        subscriptions.Add(await bus.WatchAsync(Portal.Path, Interface, signal,
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (Session: reader.ReadObjectPathAsString(), Id: reader.ReadString());
            },
            value =>
            {
                if (value.Session != session || value.Id != ShortcutId) return;
                try { action(); }
                catch (Exception) { }
            }).ConfigureAwait(false));

    public void Dispose()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
        try { Portal.CloseAsync(bus, session, "org.freedesktop.portal.Session").Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        bus.Dispose();
    }
}

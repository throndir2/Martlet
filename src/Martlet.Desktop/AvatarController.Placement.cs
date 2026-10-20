using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Windows;
using Martlet.Audio;
using Martlet.Avatar.Audio2Face;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>Where and how the character shows: theme, position and lock, camera framing, zoom, click-through, the muted voice and the speech bubble.</summary>
internal sealed partial class AvatarController
{
    internal async Task UpdateThemeAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && Application.Current is App app)
                await current.SendAsync("theme", new RendererTheme(app.IsDarkTheme, app.ThemeColors), token);
        }
        finally { changes.Release(); }
    }

    /// <summary>Returns the character overlay to its default spot, size and zoom on the main screen, even when its position is
    /// locked (it then stays locked there). Hidden, it forgets the saved place, so the character next shows at its default spot,
    /// unlocked. Returns where the character now is (null once forgotten).</summary>
    internal async Task<RendererPlacement?> ResetPositionAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
            {
                var reply = await current.SendAsync("home", new { }, token);
                var place = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
                if (place is not { IsValid: true }) throw new InvalidDataException("The character didn't confirm its position.");
                Placement = place;
            }
            else Placement = null;
            return Placement;
        }
        finally { changes.Release(); }
    }

    /// <summary>Asks the showing character where it is now (after it was moved or resized) and remembers it. Null while hidden.</summary>
    internal async Task<RendererPlacement?> ReadPlacementAsync(CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is not { HasExited: false } current || profile is null) return null;
            var reply = await current.SendAsync("where", new { }, token);
            var place = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
            if (place is not { IsValid: true }) throw new InvalidDataException("The character didn't report its position.");
            return Placement = place;
        }
        finally { changes.Release(); }
    }

    /// <summary>Puts the character where a character profile left it on this PC: that spot, size and monitor, locked or not. A
    /// showing character moves there now (it isn't a move of the user's, so the character doesn't react to it); hidden, it
    /// shows there next. Returns where the character now is.</summary>
    internal async Task<RendererPlacement?> PlaceAsync(RendererPlacement place, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
            {
                var reply = await current.SendAsync("place", place, token);
                var placed = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
                if (placed is not { IsValid: true }) throw new InvalidDataException("The character didn't confirm its position.");
                Placement = placed;
            }
            else Placement = place;
            return Placement;
        }
        finally { changes.Release(); }
    }

    private RendererPlacement? placement;

    /// <summary>Where the character was last left on this PC's desktop (and on which monitor), locked or not, or null for its
    /// default spot. A newly shown character goes back there, locked again if it was.</summary>
    internal RendererPlacement? Placement
    {
        get => Volatile.Read(ref placement);
        set => Volatile.Write(ref placement, value is { IsValid: true } ? value : null);
    }

    /// <summary>Where the character is locked, or null while it moves freely.</summary>
    internal RendererPlacement? LockedPlacement => Placement is { Locked: true } locked ? locked : null;

    internal bool PlacementLocked => LockedPlacement is not null;

    private int voiceMuted;
    private RendererCamera? camera;

    /// <summary>The camera view (its background color, any picture and the framing it opens with) while the character shows in
    /// its own 16:9 window for OBS (Martlet in your Discord calls), or null for the usual overlay.</summary>
    internal RendererCamera? Camera => Volatile.Read(ref camera);

    /// <summary>Opens the camera view (the character on <paramref name="view"/>'s solid color or picture in an ordinary 16:9
    /// window, framed as asked when it opens; a change of background keeps the current framing) or closes it (null). Returns
    /// whether the showing character now shows it; hidden, it shows that way when it shows next.</summary>
    internal async Task<bool> SetCameraAsync(RendererCamera? view, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            view = view is { On: true } ? view : null;
            Volatile.Write(ref camera, view);
            if (renderer is not { HasExited: false } current || profile is null) return false;
            await current.SendAsync("camera", view ?? new RendererCamera(false), token);
            return view is not null;
        }
        finally { changes.Release(); }
    }

    /// <summary>Remembers how the character is framed in the open camera view, so it opens framed that way when it shows
    /// again.</summary>
    internal void RememberCameraFraming(double zoom, double x, double y)
    {
        if (Camera is { } open) Volatile.Write(ref camera, open with { Zoom = zoom, X = x, Y = y });
    }

    /// <summary>Martlet's voice is muted (Speak Martlet's replies aloud is off): a newly shown character's menu offers Unmute
    /// voice instead of Mute voice. Set it before showing; <see cref="SetVoiceMutedAsync"/> also tells a showing character.</summary>
    internal bool VoiceMuted
    {
        get => Volatile.Read(ref voiceMuted) != 0;
        set => Volatile.Write(ref voiceMuted, value ? 1 : 0);
    }

    /// <summary>Records whether Martlet's voice is muted and tells the showing character, so its menu offers the other choice.
    /// Hidden, the next showing starts with it.</summary>
    internal async Task SetVoiceMutedAsync(bool muted, CancellationToken token)
    {
        VoiceMuted = muted;
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
                await current.SendAsync("voice", new RendererVoice(VoiceMuted), token);
        }
        finally { changes.Release(); }
    }

    private int clickThrough;

    /// <summary>Clicks pass through the character to the windows under it (this PC's choice, saved by Martlet). Set it before
    /// showing; <see cref="SetClickThroughAsync"/> also tells a showing character.</summary>
    internal bool ClickThrough
    {
        get => Volatile.Read(ref clickThrough) != 0;
        set => Volatile.Write(ref clickThrough, value ? 1 : 0);
    }

    /// <summary>Lets clicks pass through the character (<paramref name="on"/>) or makes it catch them again, and tells the showing
    /// character at once. Hidden, the next showing starts with it.</summary>
    internal async Task SetClickThroughAsync(bool on, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            ClickThrough = on;
            if (renderer is { HasExited: false } current && profile is not null)
                await current.SendAsync("click-through", new RendererClickThrough(on), token);
        }
        finally { changes.Release(); }
    }

    /// <summary>
    /// Locks the showing character where it is, or unlocks it (also while it is hidden, keeping where it was). Returns where the
    /// character now is (null when hidden with no saved place). Locking needs the character showing: its place is wherever it is now.
    /// </summary>
    internal async Task<RendererPlacement?> LockPlacementAsync(bool locked, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is { HasExited: false } current && profile is not null)
            {
                var reply = await current.SendAsync("lock", new RendererLock(locked), token);
                var place = reply.Kind == "placement" ? RendererProtocol.Data<RendererPlacement>(reply) : null;
                if (place is null || place.Locked != locked || !place.IsValid)
                    throw new InvalidDataException("The character didn't confirm its position.");
                Placement = place;
            }
            else if (locked) throw new InvalidOperationException("Show the character first, then lock it where you want it.");
            else Placement = Placement is { } saved ? saved with { Locked = false } : null;
            return Placement;
        }
        finally { changes.Release(); }
    }

    /// <summary>
    /// Zooms the character overlay "in", "out", "reset"s it to the default size without moving it, or reads its
    /// "status". Returns the resulting view, or null while the character is hidden.
    /// </summary>
    internal async Task<RendererView?> ZoomAsync(string action, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            if (renderer is not { HasExited: false } current) return null;
            var reply = await current.SendAsync("zoom", new RendererZoom(action), token);
            return reply.Kind == "view" ? RendererProtocol.Data<RendererView>(reply) : null;
        }
        finally { changes.Release(); }
    }

    /// <summary>
    /// Shows (or with null text, hides) a speech bubble beside the character, placed as <paramref name="say"/> asks. A no-op
    /// while the character is hidden. Returns where the overlay put the bubble, or null when the character isn't showing.
    /// </summary>
    internal async Task<RendererBubble?> SayAsync(RendererSay say, CancellationToken token)
    {
        if (!IsShowing || renderer is not { HasExited: false } current) return null;
        var reply = await current.SendAsync("say", say, token);
        return reply.Kind == "bubble" ? RendererProtocol.Data<RendererBubble>(reply) : new("unknown", 0, 0, 0, 0);
    }
}

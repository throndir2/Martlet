using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Capture;
using Martlet.Platform.Linux.DBus;
using Martlet.Platform.Linux.Hotkeys;
using Tmds.DBus.Protocol;

namespace Martlet.Platform.Linux.Tests;

public sealed class PortalAndKeyTests
{
    [Fact]
    public void RequestPathsUseTheEscapedSenderAndToken()
    {
        Assert.Equal("1_42", Portal.SenderElement(":1.42"));
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/martlet_x", Portal.RequestPath(":1.42", "martlet_x"));
        Assert.Equal("/org/freedesktop/portal/desktop/session/1_7/t", Portal.SessionPath(":1.7", "t"));
        var token = Portal.NewToken();
        Assert.Matches("^martlet_[0-9a-f]{16}$", token);
        Assert.NotEqual(token, Portal.NewToken());
    }

    [Theory]
    [InlineData("F8", "F8")]
    [InlineData("F24", "F24")]
    [InlineData("A", "a")]
    [InlineData("D7", "7")]
    [InlineData("Space", "space")]
    [InlineData("ScrollLock", "Scroll_Lock")]
    [InlineData("PageUp", "Prior")]
    [InlineData("NumPad3", "KP_3")]
    [InlineData("OemTilde", "grave")]
    [InlineData("Banana", null)]
    [InlineData("", null)]
    public void KeyNamesMapToKeysyms(string key, string? keysym) => Assert.Equal(keysym, LinuxKeys.Keysym(key));

    [Fact]
    public void PortalTriggersFollowTheShortcutsSpec()
    {
        Assert.Equal("F8", LinuxKeys.PortalTrigger(HotkeyGesture.Default));
        Assert.Equal("CTRL+ALT+space", LinuxKeys.PortalTrigger(new HotkeyGesture("Space", HotkeyModifiers.Control | HotkeyModifiers.Alt)));
        Assert.Equal("SHIFT+LOGO+Pause", LinuxKeys.PortalTrigger(new HotkeyGesture("Pause", HotkeyModifiers.Shift | HotkeyModifiers.Meta)));
        Assert.Null(LinuxKeys.PortalTrigger(new HotkeyGesture("Banana")));
    }

    [Fact]
    public void X11GrabsIgnoreCapsAndNumLock()
    {
        var mask = LinuxKeys.X11Mask(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        Assert.Equal(4u | 8u, mask);
        Assert.Equal([12u, 14u, 28u, 30u], LinuxKeys.GrabMasks(mask));
        Assert.Equal(64u, LinuxKeys.X11Mask(HotkeyModifiers.Meta));
    }

    private static PortalResponse Response(uint code, params (string Key, VariantValue Value)[] results) =>
        new(code, results.ToDictionary(r => r.Key, r => r.Value));

    [Fact]
    public void BindResultReadsTheAssignedTrigger()
    {
        var options = new Dict<string, VariantValue> { ["trigger_description"] = VariantValue.String("Ctrl+Space") };
        var shortcuts = new Array<Struct<string, Dict<string, VariantValue>>>([new(PortalShortcut.ShortcutId, options)]).AsVariantValue();
        var status = PortalShortcut.BindResult(Response(0, ("shortcuts", shortcuts)), HotkeyGesture.Default);
        Assert.True(status.Available);
        Assert.Contains("Ctrl+Space", status.Reason);

        var other = new Array<Struct<string, Dict<string, VariantValue>>>([new("something-else", new Dict<string, VariantValue>())]).AsVariantValue();
        Assert.False(PortalShortcut.BindResult(Response(0, ("shortcuts", other)), HotkeyGesture.Default).Available);
        Assert.False(PortalShortcut.BindResult(Response(1), HotkeyGesture.Default).Available);
        Assert.Contains("closed", PortalShortcut.BindResult(Response(1), HotkeyGesture.Default).Reason);
        Assert.False(PortalShortcut.BindResult(Response(2), HotkeyGesture.Default).Available);
    }

    [Fact]
    public void ScreenCastNeverPersistsAndEmbedsTheCursorWhenOffered()
    {
        var options = ScreenCastSession.SelectSourcesOptions("t", availableCursorModes: 1 | 2 | 4);
        Assert.Equal(0u, options["persist_mode"].GetUInt32());
        Assert.Equal(1u, options["types"].GetUInt32());
        Assert.False(options["multiple"].GetBool());
        Assert.Equal(2u, options["cursor_mode"].GetUInt32());
        Assert.Equal(1u, ScreenCastSession.SelectSourcesOptions("t", 1)["cursor_mode"].GetUInt32());
        Assert.Equal("t", options["handle_token"].GetString());
    }

    [Fact]
    public void ScreenCastStartAnswerGivesThePipeWireNode()
    {
        var stream = new Struct<uint, Dict<string, VariantValue>>(57, new Dict<string, VariantValue> { ["source_type"] = VariantValue.UInt32(1) });
        var streams = new Array<Struct<uint, Dict<string, VariantValue>>>([stream]).AsVariantValue();
        Assert.Equal(57u, ScreenCastSession.FirstNode(Response(0, ("streams", streams))));
        Assert.Null(ScreenCastSession.FirstNode(Response(0)));
    }

    [Fact]
    public void SessionHandleAcceptsStringsAndObjectPaths()
    {
        Assert.Equal("/s/1", Portal.SessionHandle(Response(0, ("session_handle", VariantValue.String("/s/1")))));
        Assert.Equal("/s/2", Portal.SessionHandle(Response(0, ("session_handle", VariantValue.ObjectPath(new ObjectPath("/s/2"))))));
        Assert.Null(Portal.SessionHandle(Response(0)));
    }
}

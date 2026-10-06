using System.Buffers.Binary;
using Martlet.Companion.Platform;
using Martlet.Platform.Linux.Capture;
using Martlet.Platform.Linux.Credentials;
using Martlet.Platform.Linux.Desktop;
using Martlet.Platform.Linux.Native;
using Martlet.Platform.Linux.Overlay;
using SkiaSharp;

namespace Martlet.Platform.Linux.Tests;

public sealed class CaptureAndDesktopTests
{
    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

    [Fact]
    public void EnumFormatIsAnAlignedVideoFormatObject()
    {
        var pod = SpaPod.EnumFormat();
        Assert.Equal(0, pod.Length % 8);
        Assert.Equal((uint)pod.Length - 8, U32(pod, 0));
        Assert.Equal(15u, U32(pod, 4));          // SPA_TYPE_Object
        Assert.Equal(0x40003u, U32(pod, 8));     // SPA_TYPE_OBJECT_Format
        Assert.Equal(3u, U32(pod, 12));          // SPA_PARAM_EnumFormat
        Assert.Equal(1u, U32(pod, 16));          // first property: mediaType
        Assert.Equal(2u, U32(pod, 32));          // = video
    }

    private static byte[] Fixed(uint format, uint width, uint height, bool asChoice = false)
    {
        static byte[] Prop(uint key, uint type, byte[] body, bool choice)
        {
            var value = choice ? [.. Le(0), .. Le(0), .. Le((uint)body.Length), .. Le(type), .. body] : body;
            var padded = (value.Length + 7) & ~7;
            var bytes = new byte[16 + padded];
            Le(key).CopyTo(bytes, 0);
            Le((uint)value.Length).CopyTo(bytes, 8);
            Le(choice ? 19u : type).CopyTo(bytes, 12);
            value.CopyTo(bytes, 16);
            return bytes;
        }
        byte[] props = [.. Prop(1, 3, Le(2), false), .. Prop(2, 3, Le(1), false), .. Prop(0x20001, 3, Le(format), asChoice),
            .. Prop(0x20003, 10, [.. Le(width), .. Le(height)], asChoice)];
        return [.. Le((uint)props.Length + 8), .. Le(15), .. Le(0x40003), .. Le(4), .. props];
    }

    private static byte[] Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    [Fact]
    public void NegotiatedFormatParsesPlainAndChoiceValues()
    {
        Assert.Equal((SpaVideoFormat.BGRx, 2560, 1440), SpaPod.ParseVideoFormat(Fixed(8, 2560, 1440)));
        Assert.Equal((SpaVideoFormat.RGBA, 800, 600), SpaPod.ParseVideoFormat(Fixed(11, 800, 600, asChoice: true)));
        Assert.Null(SpaPod.ParseVideoFormat(Fixed(2, 800, 600)));   // I420 is not offered
        Assert.Null(SpaPod.ParseVideoFormat(Fixed(8, 0, 600)));
        Assert.Null(SpaPod.ParseVideoFormat(new byte[8]));
    }

    [Theory]
    [InlineData(3840, 2160, 1280, 1280, 720)]
    [InlineData(1080, 1920, 1280, 720, 1280)]
    [InlineData(800, 600, 1280, 800, 600)]
    public void LooksAreScaledToTheLongEdge(int width, int height, int max, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), FrameEncoder.Fit(width, height, max));

    [Theory]
    [InlineData(8u)]
    [InlineData(7u)]
    [InlineData(9u)]
    [InlineData(10u)]
    public void EncodeMakesAScaledJpegWithTheRightColors(uint spaFormat)
    {
        var format = (SpaVideoFormat)spaFormat;
        const int width = 64, height = 32, stride = width * 4 + 16;
        var pixels = new byte[stride * height];
        var (r, g, b) = FrameEncoder.Channels(format);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * stride + x * 4;
                pixels[at + r] = 220;
                pixels[at + g] = 30;
                pixels[at + b] = 40;
            }
        var shot = FrameEncoder.Encode(pixels, width, height, stride, format, new ScreenCaptureRequest(MaxLongEdge: 32, JpegQuality: 90));
        Assert.Equal((32, 16), (shot.Width, shot.Height));
        Assert.Equal([0xFF, 0xD8], shot.Jpeg[..2]);
        using var decoded = SKBitmap.Decode(shot.Jpeg);
        var color = decoded.GetPixel(16, 8);
        Assert.InRange(color.Red, 200, 240);
        Assert.InRange(color.Green, 15, 50);
        Assert.InRange(color.Blue, 25, 60);
    }

    [Fact]
    public void X11PixmapLayoutComesFromItsMasks()
    {
        Assert.Equal(SpaVideoFormat.BGRx, X11Capture.Layout(0xFF0000, 0xFF, 0));
        Assert.Equal(SpaVideoFormat.RGBx, X11Capture.Layout(0xFF, 0xFF0000, 0));
        Assert.Null(X11Capture.Layout(0xF800, 0x1F, 0));
        Assert.Null(X11Capture.Layout(0xFF0000, 0xFF, 1));
    }

    [Fact]
    public void InteractiveRegionsBecomeClippedXRectangles()
    {
        var rectangles = OverlayGeometry.ToXRectangles([new PixelRect(10, 20, 300, 400), new PixelRect(0, 0, 0, 10), new PixelRect(-70000, 5, 100000, 5)]);
        Assert.Equal(2, rectangles.Length);
        Assert.Equal((10, 20, 300, 400), (rectangles[0].X, rectangles[0].Y, rectangles[0].Width, rectangles[0].Height));
        Assert.Equal(short.MinValue, rectangles[1].X);
        Assert.Equal(ushort.MaxValue, rectangles[1].Width);
        Assert.Empty(OverlayGeometry.ToXRectangles([]));
    }

    [Fact]
    public void CharacterOverlayAsksForAboveStickyAndNoTaskbar() =>
        Assert.Equal(["_NET_WM_STATE_ABOVE", "_NET_WM_STATE_STICKY", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER"],
            OverlayGeometry.States(OverlayBehavior.Character));

    [Fact]
    public void AutostartEntryQuotesTheExecutable()
    {
        Assert.Equal("/opt/martlet/Martlet.Companion", XdgAutostart.QuoteExec("/opt/martlet/Martlet.Companion"));
        Assert.Equal("\"/home/me/My Apps/Martlet.AppImage\"", XdgAutostart.QuoteExec("/home/me/My Apps/Martlet.AppImage"));
        Assert.Equal("\"/a b/\\\\$x%%\"", XdgAutostart.QuoteExec("/a b/$x%"));
        var entry = XdgAutostart.Entry("/opt/martlet/Martlet.Companion");
        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.Contains("\nExec=/opt/martlet/Martlet.Companion\n", entry);
        Assert.Contains("\nType=Application\n", entry);
        Assert.Contains("\nIcon=io.github.throndir2.Martlet\n", entry);
        Assert.Contains("\nExec=/usr/bin/dotnet \"/opt/my martlet/Martlet.Companion.dll\"\n",
            XdgAutostart.Entry("/usr/bin/dotnet", "/opt/my martlet/Martlet.Companion.dll"));
    }

    [Fact]
    public void AutostartTurnsOnAndOffInTheAutostartFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-autostart-" + Guid.NewGuid().ToString("N"), "autostart");
        try
        {
            var autostart = new XdgAutostart(directory, "/opt/martlet/Martlet.Companion");
            Assert.False(autostart.IsEnabled);
            Assert.True(autostart.SetEnabled(true).Available);
            Assert.True(autostart.IsEnabled);
            Assert.True(File.Exists(Path.Combine(directory, "martlet.desktop")));
            Assert.True(autostart.SetEnabled(false).Available);
            Assert.False(autostart.IsEnabled);
            Assert.False(File.Exists(Path.Combine(directory, "martlet.desktop")));
        }
        finally { Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true); }
    }

    [Fact]
    public void SecretsAreFoundByAppAndName()
    {
        var attributes = SecretServiceStore.Attributes("openai-api-key");
        Assert.Equal("io.github.throndir2.Martlet", attributes["application"]);
        Assert.Equal("openai-api-key", attributes["name"]);
        SecretServiceStore.Validate("host/pairing:abc_1");
        Assert.Throws<ArgumentException>(() => SecretServiceStore.Validate("bad name"));
        Assert.Throws<ArgumentException>(() => SecretServiceStore.Validate(""));
    }
}

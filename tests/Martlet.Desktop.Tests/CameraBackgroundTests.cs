using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Core.Pictures;
using Martlet.Desktop;
using Martlet.Discord.Calls;

namespace Martlet.Desktop.Tests;

public sealed class CameraBackgroundTests
{
    [Fact]
    public void SetCameraBackgroundTakesExactlyOneOfColorPictureOrDraw()
    {
        Assert.Equal(DiscordCameraBackground.Blue, CameraBackgroundTool.Parse("""{"color":"Blue"}""").Arguments!.Color);
        Assert.Equal("a1b2c3d4", CameraBackgroundTool.Parse("""{"picture":" a1b2c3d4 "}""").Arguments!.Picture);
        var draw = CameraBackgroundTool.Parse("""{"draw":"a neon city street at night, rain","title":"Neon"}""").Arguments!.Draw!;
        Assert.Equal(PictureShape.Wide, draw.Shape);
        Assert.Equal("Neon", draw.Title);
        Assert.Contains("neon city", draw.Description);
        Assert.Null(CameraBackgroundTool.Parse("{}").Arguments);
        Assert.Null(CameraBackgroundTool.Parse("""{"color":"green","picture":"a1b2c3d4"}""").Arguments);
        Assert.Null(CameraBackgroundTool.Parse("""{"color":"purple"}""").Arguments);
        Assert.Null(CameraBackgroundTool.Parse("not json").Arguments);
        Assert.Contains("color", CameraBackgroundTool.Parse("{}").Problem);
    }

    [Fact]
    public void APictureBecomesAJpegSmallEnoughForOneRendererMessage()
    {
        var png = Gradient(1344, 768);
        var jpeg = PictureView.CameraJpeg(png);
        Assert.NotNull(jpeg);
        Assert.True(jpeg.Length <= 180_000);
        var probe = PictureImages.Probe(jpeg)!.Value;
        Assert.Equal("image/jpeg", probe.MediaType);
        Assert.True(probe.Width <= 1280 && probe.Height <= 720);
        Assert.Null(PictureView.CameraJpeg(png, maximumBytes: 100));
        Assert.Null(PictureView.CameraJpeg([1, 2, 3]));
    }

    [Fact]
    public async Task SetCameraBackgroundIsOfferedLastOnlyWhileMartletIsInYourDiscordCalls()
    {
        await using var fixture = await LiveFixture.Create(history: true, tools: true);
        fixture.Controller.AutoCapture = false;
        fixture.Answer("Hello!");
        await fixture.Finish(fixture.Start("Hi there."));
        var before = ToolNames(fixture.Llm.Body);
        var camera = new FakeCamera { Offered = false };
        fixture.Controller.CallCamera = camera;
        await fixture.Finish(fixture.Start("Hi again."));
        Assert.Equal(before, ToolNames(fixture.Llm.Body));

        camera.Offered = true;
        await fixture.Finish(fixture.Start("And once more."));
        var after = ToolNames(fixture.Llm.Body);
        Assert.Equal([.. before, CameraBackgroundTool.Name], after);
        await fixture.Finish(fixture.Start("Still here."));
        Assert.Equal(after, ToolNames(fixture.Llm.Body));
    }

    private sealed class FakeCamera : ICallCamera
    {
        public bool Offered { get; set; }
        public Task<string> SetBackgroundAsync(DiscordCameraBackground? color, string? picture, CancellationToken token) =>
            Task.FromResult("ok");
    }

    private static string[] ToolNames(byte[] body)
    {
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }

    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = (byte)((x ^ y) & 0xFF);
                pixels[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

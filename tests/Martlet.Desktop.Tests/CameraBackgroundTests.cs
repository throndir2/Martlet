using Martlet.Core.Pictures;
using Martlet.Desktop;
using Martlet.Discord.Calls;

namespace Martlet.Desktop.Tests;

/// <summary>set_camera_background: Martlet changes its own webcam background in the owner's Discord calls.</summary>
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
        // "Picture" is the saved picture's choice, not a color the model can name; numbers aren't colors either.
        Assert.Null(CameraBackgroundTool.Parse("""{"color":"picture"}""").Arguments);
        Assert.Null(CameraBackgroundTool.Parse("""{"color":"1"}""").Arguments);
        Assert.Null(CameraBackgroundTool.Parse("not json").Arguments);
        Assert.Contains("color", CameraBackgroundTool.Parse("{}").Problem);
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
        public Task<string> SetBackgroundAsync(DiscordCameraBackground? color, string? picture, bool drawn, CancellationToken token) =>
            Task.FromResult("ok");
    }

    private static string[] ToolNames(byte[] body)
    {
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }
}

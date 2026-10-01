using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Desktop;
using Xunit;

namespace Martlet.Desktop.Tests;

public sealed class VideoSourceTests
{
    [Fact]
    public void AddressesNeverExposeCredentials()
    {
        Assert.Equal("192.168.1.20:8080", WatchSource.SafeName("http://me:secret@192.168.1.20:8080/shot.jpg?token=x"));
        Assert.Equal("http://192.168.1.20:8080/shot.jpg", WatchSource.WithoutCredentials("http://me:secret@192.168.1.20:8080/shot.jpg"));
        Assert.Equal("clip.mp4", WatchSource.SafeName(@"C:\videos\clip.mp4"));
        Assert.Equal("the camera at phone:8080", new WatchSource(WatchKind.Url, "http://phone:8080/video", "phone:8080").Label);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsSnapshotAndMjpegAddresses(bool mjpeg)
    {
        var jpeg = Jpeg(320, 240);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            const string boundary = "frame";
            var header = mjpeg
                ? $"HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary={boundary}\r\nConnection: close\r\n\r\n"
                : $"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            if (!mjpeg) { await stream.WriteAsync(jpeg); return; }
            for (var i = 0; i < 3; i++)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
                await stream.WriteAsync(jpeg);
                await stream.WriteAsync("\r\n"u8.ToArray());
            }
        });
        var input = new VideoInput();
        var result = input.Capture(new(WatchKind.Url, $"http://localhost:{port}/{(mjpeg ? "video" : "shot.jpg")}", $"localhost:{port}"));
        input.Release();
        await serving;
        Assert.True(result.Frame is not null, result.Note);
        Assert.Equal(320, result.Frame!.Width);
        Assert.Equal(240, result.Frame.Height);
    }

    [Fact]
    public void UnreachableAddressExplainsItself()
    {
        var result = new VideoInput().Capture(new(WatchKind.Url, "http://localhost:1/shot.jpg", "localhost:1"));
        Assert.Null(result.Frame);
        Assert.Equal(GlanceSkip.CaptureFailed, result.Skip);
        Assert.False(string.IsNullOrWhiteSpace(result.Note));
    }

    [Fact]
    public void ListsCamerasWithoutOpeningThem() => Assert.NotNull(new VideoInput().Cameras());

    private static byte[] Jpeg(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i % 251); pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4)));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}

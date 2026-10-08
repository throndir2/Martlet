using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Martlet.Desktop.Tests;

public sealed class ActiveAppTests
{
    private static readonly (int, int, int, int) Monitor = (0, 0, 1920, 1080);

    [Fact]
    public void A_window_fills_its_monitor_only_when_it_covers_it_and_is_not_an_ordinary_maximized_window()
    {
        // A borderless full-screen game or video, an exclusive full-screen game, a maximized borderless window.
        Assert.True(ActiveApp.Fills((0, 0, 1920, 1080), Monitor, maximized: false, titleBar: false));
        Assert.True(ActiveApp.Fills((-1, -1, 1921, 1081), Monitor, maximized: false, titleBar: false));
        Assert.True(ActiveApp.Fills((0, 0, 1920, 1080), Monitor, maximized: true, titleBar: false));
        Assert.True(ActiveApp.Fills((1920, 0, 4480, 1440), (1920, 0, 4480, 1440), maximized: false, titleBar: false));
        // A maximized window with a title bar is an ordinary window, also when the taskbar hides itself and it covers it all.
        Assert.False(ActiveApp.Fills((-8, -8, 1928, 1088), Monitor, maximized: true, titleBar: true));
        Assert.False(ActiveApp.Fills((-8, -8, 1928, 1048), Monitor, maximized: true, titleBar: true));
        // A window that leaves part of its monitor uncovered, or that is on another monitor.
        Assert.False(ActiveApp.Fills((100, 100, 1500, 900), Monitor, maximized: false, titleBar: true));
        Assert.False(ActiveApp.Fills((0, 0, 1920, 1079), Monitor, maximized: false, titleBar: false));
        Assert.False(ActiveApp.Fills((1920, 0, 3840, 1080), Monitor, maximized: false, titleBar: false));
        Assert.False(ActiveApp.Fills((0, 0, 1920, 1080), (0, 0, 0, 0), maximized: false, titleBar: false));
    }

    [Fact]
    public void The_app_is_named_the_way_the_model_and_the_conversation_get_it()
    {
        Assert.Equal("Google Chrome", ActiveApp.Describe("Google Chrome", false));
        Assert.Equal("Google Chrome (full screen)", ActiveApp.Describe("Google Chrome", true));
        Assert.Equal("unknown", ActiveApp.Describe("", false));
        Assert.Equal("unknown (full screen)", ActiveApp.Describe(null, true));
        Assert.Equal("Google Chrome, full screen", ActiveApp.Label("Google Chrome", true));
        Assert.Equal("Google Chrome", ActiveApp.Label("Google Chrome", false));
        Assert.Equal("full screen", ActiveApp.Label("", true));
        Assert.Equal("", ActiveApp.Label(null, false));
        // One line without quotes or control characters, at most 60 characters.
        Assert.Equal("My Game Deluxe", ActiveApp.Clean("  My \"Game\"\r\n\tDeluxe "));
        Assert.Equal(ActiveApp.MaximumName, ActiveApp.Clean(new string('x', 200)).Length);
        Assert.Equal("", ActiveApp.Clean(null));
    }

    [Fact]
    public void A_program_is_named_by_its_file_description_or_else_its_file_name()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var named = ActiveApp.NameOf(explorer);
        Assert.False(string.IsNullOrWhiteSpace(named));
        Assert.NotEqual("explorer", named, StringComparer.OrdinalIgnoreCase);
        // A file without version information, or one that isn't there, goes by its file name.
        var directory = Directory.CreateTempSubdirectory("martlet-active-app-").FullName;
        try
        {
            var plain = Path.Combine(directory, "Fixture Game.exe");
            File.WriteAllBytes(plain, [0x4D, 0x5A, 0, 0]);
            Assert.Equal("Fixture Game", ActiveApp.NameOf(plain));
            Assert.Equal("Gone Game", ActiveApp.NameOf(Path.Combine(directory, "Gone Game.exe")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void A_real_window_is_named_by_its_program_and_fills_its_monitor_only_while_it_covers_it() => RunSta(() =>
    {
        Assert.Equal(("", false), ActiveApp.Of(0));
        double width = SystemParameters.PrimaryScreenWidth, height = SystemParameters.PrimaryScreenHeight;
        // Borderless and see-through, so nothing shows while the test runs: first over the whole primary monitor, then smaller.
        foreach (var (covers, left, top, w, h) in new[] { (true, -20.0, -20.0, width + 40, height + 40), (false, 40.0, 40.0, width / 2, height / 2) })
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0,
                ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = left, Top = top, Width = w, Height = h
            };
            window.Show();
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                Assert.Equal(ActiveApp.NameOf(Environment.ProcessPath!), ActiveApp.Name(handle));
                Assert.Equal(covers, ActiveApp.FullScreen(handle));
            }
            finally { window.Close(); }
        }
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}

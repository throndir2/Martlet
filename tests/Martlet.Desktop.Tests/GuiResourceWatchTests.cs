using System.Windows.Interop;

namespace Martlet.Desktop.Tests;

public sealed class GuiResourceWatchTests
{
    private static GuiCount Count(int user, int gdi = 50) => new(user, user, gdi, gdi, 10_000, 10_000);

    [Fact]
    public void The_log_gets_the_first_reading_an_hourly_line_and_a_line_when_a_count_grew_by_a_thousand()
    {
        var watch = new GuiResourceWatch();
        var start = TimeSpan.FromHours(5);
        Assert.Equal(GuiResourceWatch.Note.Hourly, watch.Next(Count(300), start));
        Assert.Equal(GuiResourceWatch.Note.None, watch.Next(Count(900), start + TimeSpan.FromMinutes(5)));
        Assert.Equal(GuiResourceWatch.Note.Grew, watch.Next(Count(1_300), start + TimeSpan.FromMinutes(10)));
        Assert.Equal(GuiResourceWatch.Note.Grew, watch.Next(Count(1_300, gdi: 1_100), start + TimeSpan.FromMinutes(15)));
        Assert.Equal(GuiResourceWatch.Note.None, watch.Next(Count(1_400, gdi: 1_100), start + TimeSpan.FromMinutes(70)));
        Assert.Equal(GuiResourceWatch.Note.Hourly, watch.Next(Count(1_400, gdi: 1_100), start + TimeSpan.FromMinutes(75)));
    }

    [Fact]
    public void A_warning_comes_once_at_each_share_of_the_limit_and_again_after_the_count_fell_well_below_it()
    {
        var watch = new GuiResourceWatch();
        var at = TimeSpan.Zero;
        TimeSpan Later() => at += TimeSpan.FromMinutes(5);
        Assert.Equal(GuiResourceWatch.Note.Hourly, watch.Next(Count(4_000), Later()));
        Assert.Equal(GuiResourceWatch.Note.Warning, watch.Next(Count(5_000), Later()));
        Assert.Equal(GuiResourceWatch.Note.None, watch.Next(Count(5_200), Later()));
        Assert.Equal(GuiResourceWatch.Note.Warning, watch.Next(Count(9_500), Later()));
        Assert.Equal(GuiResourceWatch.Note.None, watch.Next(Count(9_600), Later()));
        // GDI objects count too.
        Assert.Equal(GuiResourceWatch.Note.Grew, watch.Next(Count(9_600, gdi: 8_000), Later()));
        Assert.Equal(GuiResourceWatch.Note.None, watch.Next(Count(8_500, gdi: 8_000), Later()));
        Assert.Equal(GuiResourceWatch.Note.None, watch.Next(Count(300), Later()));
        Assert.Equal(GuiResourceWatch.Note.Warning, watch.Next(Count(300, gdi: 7_600), Later()));
    }

    [Fact]
    public void The_reading_counts_this_process_and_its_hidden_windows_by_class()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("Martlet resource test")
                {
                    WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0
                });
                var count = GuiResources.Read(Environment.ProcessId);
                Assert.NotNull(count);
                Assert.True(count.User > 0 && count.UserPeak >= count.User && count.UserQuota > 0 && count.GdiQuota > 0);
                Assert.Contains("Window Manager objects", count.Describe());
                var census = GuiResources.Census(Environment.ProcessId);
                // The HwndSource window and the thread's dispatcher window (message-only), their GUIDs as "*".
                Assert.True(census.Windows >= 2 && census.Threads >= 1);
                var wrappers = census.Classes.Where(c => c.Class.StartsWith("HwndWrapper[", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(wrappers);
                Assert.All(wrappers, c => Assert.EndsWith("*]", c.Class));
                Assert.True(wrappers.Sum(c => c.Count) >= 2);
                Assert.StartsWith($"{census.Windows:N0} windows on", census.Describe());
            }
            catch (Exception error) { failure = error; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.Null(GuiResources.Read(int.MaxValue - 1));
    }

    [Fact]
    public void Mcp_reports_the_same_reading_for_a_desktop_process()
    {
        var resources = System.Text.Json.JsonSerializer.SerializeToElement(Martlet.Mcp.DesktopAutomation.Resources(Environment.ProcessId));
        Assert.True(resources.GetProperty("windowManagerLimit").GetInt32() > 0);
        Assert.True(resources.GetProperty("gdiLimit").GetInt32() > 0);
        Assert.True(resources.GetProperty("windowManagerPeak").GetInt32() >= resources.GetProperty("windowManagerObjects").GetInt32());
        Assert.True(resources.GetProperty("windows").GetInt32() >= 0);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, resources.GetProperty("windowClasses").ValueKind);
        Assert.Null(Martlet.Mcp.DesktopAutomation.Resources(null));
        Assert.Null(Martlet.Mcp.DesktopAutomation.Resources(int.MaxValue - 1));
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Martlet.Desktop.Tests;

public sealed class PrepareHostPowerTests
{
    // 300 is about the checklist's width at the window's 820 minimum; 540 is about its width at the 1200 default.
    [Theory]
    [InlineData(300, 370)]
    [InlineData(300, 450)]
    [InlineData(540, 370)]
    [InlineData(540, 100)]
    public void Power_slider_fits_its_line_and_shows_its_handle(double width, double current)
    {
        RunSta(() =>
        {
            var gpu = new PrepareGpu
            {
                Index = 0, Uuid = "GPU-1", Name = "NVIDIA GeForce RTX 3090", MemoryMb = 24576,
                Power = new PreparePower { Current = current, Default = 370, Min = 100, Max = 450 }
            };
            var (row, slider) = PrepareHostWindow.BuildPowerRow(gpu);
            Assert.NotNull(slider);
            var host = new Border { Width = width, Child = row };
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
            host.UpdateLayout();

            Assert.Null(LayoutInformation.GetLayoutClip(slider));
            var sliderBox = slider.TransformToAncestor(host).TransformBounds(new Rect(slider.RenderSize));
            Assert.InRange(sliderBox.Right, 0, width + 0.5);
            Assert.True(sliderBox.Width > 100, $"slider is only {sliderBox.Width} wide");

            var thumb = ((Track)slider.Template.FindName("PART_Track", slider)).Thumb;
            var thumbBox = thumb.TransformToAncestor(host).TransformBounds(new Rect(thumb.RenderSize));
            Assert.True(thumbBox.Width > 0 && thumbBox.Left >= sliderBox.Left - 0.5 && thumbBox.Right <= sliderBox.Right + 0.5,
                $"handle {thumbBox} is outside the slider {sliderBox}");

            Assert.Equal($"{current:0} W", Text(row, "PreparePowerValue-0"));
            Assert.Equal($"Now {current:0} W, default 370 W, allowed 100-450 W.", Text(row, "PreparePowerDetail-0"));
            slider.Value = 300;
            Assert.Equal("300 W", Text(row, "PreparePowerValue-0"));
        });
    }

    private static string Text(DependencyObject root, string id)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && AutomationProperties.GetAutomationId(block) == id) return block.Text;
            var found = TextOrNull(child, id);
            if (found is not null) return found;
        }
        throw new InvalidOperationException($"{id} not found.");
    }

    private static string? TextOrNull(DependencyObject root, string id)
    {
        try { return Text(root, id); }
        catch (InvalidOperationException) { return null; }
    }

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

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop.Tests;

public sealed class HostRunPairingTests
{
    [Fact]
    public void Copied_code_is_marked_to_stay_out_of_clipboard_history_and_the_cloud_clipboard()
    {
        var data = SecretClipboard.Data("K7QM-4XPA");
        Assert.Equal("K7QM-4XPA", data.GetData(DataFormats.UnicodeText));
        foreach (var format in new[] { SecretClipboard.NoHistoryFormat, SecretClipboard.NoCloudFormat })
            Assert.Equal(new byte[4], Assert.IsType<MemoryStream>(data.GetData(format)).ToArray());
    }

    [Fact]
    public void Pairing_panel_shows_the_code_with_a_copy_button_and_says_it_does_not_expire()
    {
        RunSta(() =>
        {
            var window = (HostRunWindow)typeof(HostRunWindow)
                .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!
                .Invoke(["Pair your main PC"]);
            window.ShowPairingCode("192.168.1.20", "K7QM-4XPA");
            Assert.Equal(Visibility.Visible, Find<Border>(window, "HostRunPairing").Visibility);
            Assert.Equal("K7QM-4XPA", Find<TextBlock>(window, "HostRunPairCode").Text);
            var copy = Find<Button>(window, "HostRunPairCopy");
            Assert.Equal("Copy code", copy.Content);
            Assert.True(copy.IsEnabled);
            var note = Find<TextBlock>(window, "HostRunPairNote").Text;
            Assert.Contains("doesn't expire", note);
            Assert.DoesNotContain("five minutes", note);

            window.ShowPairingCode(null, null);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "HostRunPairing").Visibility);
            Assert.Equal("", Find<TextBlock>(window, "HostRunPairCode").Text);
            window.Close();
        });
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .Select(child => child is T match && AutomationProperties.GetAutomationId(child) == id ? match : FindOrNull<T>(child, id))
            .FirstOrDefault(found => found is not null) ?? throw new InvalidOperationException($"{id} not found.");

    private static T? FindOrNull<T>(DependencyObject root, string id) where T : DependencyObject
    {
        try { return Find<T>(root, id); }
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

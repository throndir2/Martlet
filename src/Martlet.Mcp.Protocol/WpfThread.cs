namespace Martlet.Mcp;

/// <summary>Runs all of the MCP server's WPF work (Test vision's picture of a word, <c>VisionTestPicture</c>, and the character
/// theme previews with their XAML styles) on one long-lived STA thread whose dispatcher keeps running, as in an app. One thread
/// only: WPF's XAML resource loading is not safe on several UI threads at once (#1092).</summary>
internal static class WpfThread
{
    internal static Task<T> RunAsync<T>(Func<T> work) => Dispatcher.Value.InvokeAsync(work).Task;

    private static readonly Lazy<System.Windows.Threading.Dispatcher> Dispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Registers the pack: scheme, which an app's Application does in Martlet.Desktop.
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });
}

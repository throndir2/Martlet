namespace Martlet.Mcp;

/// <summary>Runs WPF drawing for the checks (Test vision's picture of a word, <c>VisionTestPicture</c>) on one long-lived STA thread
/// whose dispatcher keeps running, as in an app.</summary>
internal static class WpfThread
{
    internal static Task<T> RunAsync<T>(Func<T> work) => Dispatcher.Value.InvokeAsync(work).Task;

    private static readonly Lazy<System.Windows.Threading.Dispatcher> Dispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });
}

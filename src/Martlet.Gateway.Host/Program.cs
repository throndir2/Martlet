namespace Martlet.Gateway.Host;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            return await HostApplication.RunAsync(args, new LocalConsole(), cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}

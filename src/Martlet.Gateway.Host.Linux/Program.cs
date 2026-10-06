using System.Runtime.InteropServices;

namespace Martlet.Gateway.Host.Linux;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                cancellation.Cancel();
            }) : null;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await HostApplication.RunAsync(args, Console.Out, cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }
}

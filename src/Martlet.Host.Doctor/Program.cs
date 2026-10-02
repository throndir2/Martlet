namespace Martlet.Host.Doctor;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; _ = cancel.CancelAsync(); };
        Console.CancelKeyPress += handler;
        try { return await new DoctorCommand().RunAsync(args, Console.Out, Console.Error, cancel.Token).ConfigureAwait(false); }
        finally { Console.CancelKeyPress -= handler; }
    }
}

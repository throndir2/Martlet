using Martlet.Gateway.Host;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Martlet.Gateway.Host.Tests;

internal sealed class TestConsole(params string?[] input) : ILocalConsole
{
    private readonly Queue<string?> input = new(input);
    internal List<string> Output { get; } = [];
    internal string Text => string.Join('\n', Output);
    public bool IsInteractive { get; set; } = true;
    internal Func<GatewayPairingCard, CancellationToken, Task>? Disclosure { get; set; }
    internal Func<string, CancellationToken, Task>? BeforeRead { get; set; }
    internal Action<string>? OnWrite { get; set; }
    public void Write(string text)
    {
        Output.Add(text);
        OnWrite?.Invoke(text);
    }
    public async ValueTask<string?> ReadAsync(string prompt, int maximum, CancellationToken cancellation)
    {
        Output.Add(prompt);
        if (BeforeRead is not null)
            await BeforeRead(prompt, cancellation);
        return input.Count > 0 ? input.Dequeue() : null;
    }
    public async ValueTask DiscloseAsync(GatewayPairingCard card, CancellationToken cancellation)
    {
        Assert.NotNull(Disclosure);
        await Disclosure(card, cancellation);
    }
}

internal static class ProbeProgram
{
    internal static async Task<int> Main(string[] args)
    {
        if (args is ["--console-probe", var consoleState, var consoleOrigin])
            return await NativeConsoleProbe.Run(consoleState, consoleOrigin);
        if (args is not ["--owned-probe", var state, var origin])
            return 2;
        var console = new TestConsole("yes", "start", "yes")
        {
            OnWrite = text =>
            {
                if (text.StartsWith("Loopback listener started.", StringComparison.Ordinal))
                    Console.WriteLine("OWNED-READY");
            }
        };
        var reads = 0;
        console.BeforeRead = async (_, cancellation) =>
        {
            if (++reads == 4)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        };
        return await HostApplication.RunAsync(
            ["open", "--state", state, "--host-id", "fixture-host", "--origin", origin], console);
    }
}

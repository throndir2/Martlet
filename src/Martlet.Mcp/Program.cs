using Martlet.Mcp;

if (args is not [] and not ["--allow-ui-effects"])
{
    Console.Error.WriteLine("Usage: Martlet.Mcp [--allow-ui-effects]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
await new McpServer(new DesktopAutomation(args.Length != 0)).RunAsync(
    Console.In, Console.Out, cancellation.Token);
return 0;

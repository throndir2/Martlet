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
// MCP's stdio messages are UTF-8, whatever the console's code page is.
using var input = new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
await new McpServer(new DesktopAutomation(args.Length != 0)).RunAsync(
    input, Console.Out, cancellation.Token);
return 0;

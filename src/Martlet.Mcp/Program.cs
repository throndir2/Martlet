using Martlet.Mcp;

bool allowUiEffects = false, allowChanges = false, allTools = false;
foreach (var argument in args)
{
    switch (argument)
    {
        case "--allow-ui-effects": allowUiEffects = true; break;
        case "--allow-changes": allowChanges = true; break;
        case "--all-tools": allTools = true; break;
        default:
            Console.Error.WriteLine("Usage: Martlet.Mcp [--allow-changes] [--allow-ui-effects] [--all-tools]");
            return 2;
    }
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
// MCP's stdio messages are UTF-8, whatever the console's code page is.
using var input = new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
await new McpServer(new DesktopAutomation(allowUiEffects), allowChanges, allTools).RunAsync(
    input, Console.Out, cancellation.Token);
return 0;

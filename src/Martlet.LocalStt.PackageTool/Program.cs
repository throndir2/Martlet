using Martlet.LocalStt.PackageTool;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
return PackageToolCommand.Run(
    args,
    Console.Out,
    Console.Error,
    cancellation.Token);

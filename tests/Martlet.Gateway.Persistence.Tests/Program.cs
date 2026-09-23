using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--crash-probe" ||
            !Guid.TryParse(args[2], out var device) || !Enum.TryParse<StoreStep>(args[3], out var step))
            return 2;
        var armed = false;
        using var host = DurableGatewayHost.Start(args[1], false, false, TimeProvider.System,
            observed =>
            {
                if (armed && observed == step)
                    Environment.Exit(71);
            });
        armed = true;
        host.RevokeDevice(device);
        host.CloseCleanly();
        return 3;
    }
}

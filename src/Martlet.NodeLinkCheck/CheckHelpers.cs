using Martlet.HostClient;

namespace Martlet.NodeLinkCheck;

/// <summary>Waits and expected refusals that the node link checks share.</summary>
internal static class CheckHelpers
{
    /// <summary>Asks <paramref name="done"/> every 100 ms until it is true or <paramref name="seconds"/> pass, then asks once more.</summary>
    internal static async Task<bool> UntilAsync(Func<Task<bool>> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await done()) return true;
            await Task.Delay(100);
        }
        return await done();
    }

    /// <summary>True when <paramref name="call"/> is refused with the gateway error <paramref name="code"/>.</summary>
    internal static async Task<bool> Expect<T>(Func<Task<T>> call, string code)
    {
        try
        {
            await call();
            return false;
        }
        catch (HostConnectionException error) { return error.Code == code; }
    }
}

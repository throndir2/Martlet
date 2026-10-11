using System.Text.Json;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class MemoryRecallCheckTests
{
    [Fact]
    public async Task MemoryRecallCheckRehearsesRankingAndRanksTheCallersQuery()
    {
        var result = JsonSerializer.SerializeToElement(await MemoryRecallCheck.RunAsync(null, null, CancellationToken.None));
        var steps = result.GetProperty("steps").EnumerateArray().ToArray();
        Assert.True(steps.Length >= 14);
        // The timing steps depend on this PC's load; every other step is exact.
        static bool Timing(JsonElement step) => step.GetProperty("name").GetString()! is var name &&
            (name.StartsWith("512 facts", StringComparison.Ordinal) || name.StartsWith("a full store", StringComparison.Ordinal));
        Assert.All(steps.Where(step => !Timing(step)),
            step => Assert.True(step.GetProperty("passed").GetBoolean(), step.GetProperty("name").GetString()));
        var timings = steps.Where(Timing).Select(step => step.GetProperty("detail")).ToArray();
        Assert.Equal([512, Martlet.Memory.MemoryLimits.MaximumFacts], timings.Select(t => t.GetProperty("facts").GetInt32()));
        Assert.All(timings, t => Assert.True(t.GetProperty("rankMedianMicroseconds").GetDouble() > 0));

        var sample = JsonSerializer.SerializeToElement(await MemoryRecallCheck.RunAsync("Did my tomato plants get staked?", null, CancellationToken.None));
        Assert.Equal("The garden tomatoes need staking before the weekend.",
            sample.GetProperty("query").GetProperty("ranked")[0].GetProperty("fact").GetString());

        using var arguments = JsonDocument.Parse("""{"facts":["The user drinks black coffee every morning.","Coffee beans are kept in the freezer."]}""");
        var own = JsonSerializer.SerializeToElement(await MemoryRecallCheck.RunAsync("what do I drink in the mornings",
            MemoryRecallCheck.Facts(arguments.RootElement), CancellationToken.None));
        var ranked = own.GetProperty("query").GetProperty("ranked").EnumerateArray().ToArray();
        Assert.Equal("The user drinks black coffee every morning.", Assert.Single(ranked).GetProperty("fact").GetString());
        Assert.Equal("drink mornings", own.GetProperty("query").GetProperty("words").GetString());
    }
}

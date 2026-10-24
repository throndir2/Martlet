using Martlet.Core.Planning;
using Xunit;

namespace Martlet.Core.Tests;

public sealed class ThinkingSpeedTests
{
    private const string Host = "https://192.0.2.10:9443";
    private const string Model = "gemma4-12b";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static MeasuredFirstWords Replies(MeasuredFirstWords list, params int[] ms)
    {
        foreach (var time in ms) list = list.With(Host, Model, time, At);
        return list;
    }

    [Fact]
    public void AModelThatGotMuchSlowerThanUsualIsFlaggedAndClearsWhenFastAgain()
    {
        var list = Replies(new(), 9_000, 2_100, 1_900, 2_300, 2_000);
        Assert.Equal(2_000, list.Find(Host, Model)!.UsualMs);
        Assert.Null(ThinkingSpeed.Slowdown(list.Find(Host, Model)));

        list = Replies(list, 51_000, 102_000, 113_000);
        var slow = ThinkingSpeed.Slowdown(list.Find(Host, Model));
        Assert.NotNull(slow);
        Assert.True(slow.SlowerThanUsual);
        Assert.Equal(102_000, slow.RecentMs);
        Assert.Equal(2_000, slow.UsualMs);
        Assert.Contains("usually takes 2.0 s", slow.Describe("aibox01"), StringComparison.Ordinal);

        list = Replies(list, 2_000, 1_900, 2_200);
        Assert.Null(ThinkingSpeed.Slowdown(list.Find(Host, Model)));
    }

    [Fact]
    public void OneSlowFirstLoadAndSmallChangesAreNotASlowdown()
    {
        Assert.Null(ThinkingSpeed.Slowdown(Replies(new(), 400, 450, 420, 30_000).Find(Host, Model)));
        // Three times slower but only about a second later: not worth a warning.
        Assert.Null(ThinkingSpeed.Slowdown(Replies(new(), 400, 450, 420, 1_300, 1_400, 1_350).Find(Host, Model)));
        // Fewer than three replies say nothing.
        Assert.Null(ThinkingSpeed.Slowdown(Replies(new(), 90_000, 90_000).Find(Host, Model)));
    }

    [Fact]
    public void AModelThatWasNeverFastIsSlowAfterFifteenSeconds()
    {
        var slow = ThinkingSpeed.Slowdown(Replies(new(), 50_000, 60_000, 70_000).Find(Host, Model));
        Assert.NotNull(slow);
        Assert.False(slow.SlowerThanUsual);
        Assert.Null(ThinkingSpeed.Slowdown(Replies(new(), 9_000, 10_000, 11_000).Find(Host, Model)));
    }

    [Fact]
    public void TheUsualTimeIsSavedAndReadBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.ThinkingSpeed." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(Replies(new(), 2_000, 2_100, 1_900).Save(directory));
            Assert.Equal(2_000, MeasuredFirstWords.Load(directory).Find(Host, Model)!.UsualMs);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(9_000_000_000L, 9_000_000_000L, ModelPlacement.GraphicsCard)]
    [InlineData(9_000_000_000L, 2_700_000_000L, ModelPlacement.Split)]
    [InlineData(9_000_000_000L, 0L, ModelPlacement.Processor)]
    public void PlacementSaysWhereALoadedModelSits(long bytes, long graphics, ModelPlacement expected) =>
        Assert.Equal(expected, ThinkingSpeed.Placement(bytes, graphics));

    [Fact]
    public void PlacedMatchesTheHostRoutesNameForAnOllamaModel()
    {
        var memory = new MeasuredModelMemory().With(new MeasuredModelUse
        {
            Host = Host, Model = "gemma4:12b", Bytes = 9_000_000_000, GraphicsBytes = 2_700_000_000, MeasuredAt = At
        });
        Assert.NotNull(ThinkingSpeed.Placed(memory, Host + "/v1", "gemma4-12b"));
        Assert.Null(ThinkingSpeed.Placed(memory, Host, "gemma4-e4b"));
        Assert.Null(ThinkingSpeed.Placed(memory, "https://192.0.2.11:9443", "gemma4-12b"));
    }

    [Fact]
    public void ATestReplyIsSlowWhenItStartsLateOrWritesSlowly()
    {
        Assert.False(ThinkingSpeed.Judge(Model, "aibox01", 1_200, 45, 2_000).Slow);
        Assert.True(ThinkingSpeed.Judge(Model, "aibox01", 16_000, 45, null).Slow);
        Assert.True(ThinkingSpeed.Judge(Model, "aibox01", 9_000, 45, 2_000).Slow);
        var (slow, text) = ThinkingSpeed.Judge(Model, "aibox01", 1_200, 3, 2_000);
        Assert.True(slow);
        Assert.Contains("processor", text, StringComparison.Ordinal);
    }
}

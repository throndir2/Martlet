using System.Text;
using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The background-work check-in tool set through the real controller: a check-in starts a think after an exchange, the
/// think continues that exchange's request and its result comes back as the reply's would.</summary>
public sealed class BackgroundWorkCheckInTests
{
    private static TextToolCall Call(string name, string arguments) => new("call-1", name, arguments);

    [Fact]
    public async Task ACheckInStartsAThinkThatContinuesTheLastExchangeAndComesBack()
    {
        await using var fixture = await LiveFixture.Create();
        var controller = fixture.Controller;
        Assert.True(controller.OffersBackgroundWork);
        var think = Call(ThinkLonger.Name, """{"task":"Plan a three-day trip to Kyoto (FIXTURE)","reason":"many steps"}""");

        // Nothing to continue before the first exchange.
        var early = controller.StartBackgroundWork(think, "Actions");
        Assert.True(early.IsError);
        Assert.StartsWith("Not started: there is no exchange to continue yet.", early.Output, StringComparison.Ordinal);

        fixture.Answer("Ooh, let me think that over and get back to you.");
        var exchange = fixture.Start("Can you plan my Kyoto trip?");
        await fixture.Finish(exchange);

        // Bad arguments and research while Web research is off don't start anything.
        Assert.True(controller.StartBackgroundWork(Call(ThinkLonger.Name, "{}"), "Actions").IsError);
        var research = controller.StartBackgroundWork(Call(WebResearch.Name, """{"topic":"Kyoto temples","what_to_find":"opening hours"}"""), "Actions");
        Assert.True(research.IsError);
        Assert.StartsWith("Not started: Web research is off.", research.Output, StringComparison.Ordinal);
        Assert.True(controller.StartBackgroundWork(Call("nope", "{}"), "Actions").IsError);

        fixture.Answer("Day 1: Fushimi Inari. Day 2: Arashiyama. Day 3: Gion.");
        var started = controller.StartBackgroundWork(think, "Actions");
        Assert.False(started.IsError, started.Output);
        // The first line (shown on the card) names the job only, never the task.
        var first = started.Output.Split('\n')[0];
        Assert.Matches("^(Started think-1\\.|think-1 waits in line\\.)$", first);
        Assert.DoesNotContain("Kyoto", first, StringComparison.Ordinal);
        Assert.Contains("brings the result up", started.Output, StringComparison.Ordinal);

        // It waits while the reply holds the live floor, then runs on the Thinking pool and comes back with news.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!controller.Jobs.HasNews)
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
                try { await Task.Delay(1, timeout.Token); }
                catch (TaskCanceledException)
                {
                    Assert.Fail(string.Join("; ", controller.Jobs.Active.Select(j => $"{j.Id} {j.State} {j.Progress}")) +
                        $" | floor {controller.LiveFloor.Level}");
                }
            }
        // The think continued the exchange: what the user said and the reply, then the task.
        var body = Encoding.UTF8.GetString(fixture.Llm.Body);
        Assert.Contains("Can you plan my Kyoto trip?", body, StringComparison.Ordinal);
        Assert.Contains("let me think that over", body, StringComparison.Ordinal);
        Assert.Contains("Plan a three-day trip to Kyoto (FIXTURE)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSetTakesOverTheRepliesThinkLongerAndResearchButNotCancelThinking()
    {
        var set = Assert.Single(CheckInToolSets.All, s => s.Id == BackgroundWorkTools.SetId);
        Assert.Equal([ThinkLonger.Name, WebResearch.Name], set.Replaces);
        Assert.DoesNotContain(ThinkLonger.CancelName, set.Replaces);
        Assert.Equal(ThinkLonger.ParametersJson, set.Tools.Single(t => t.Name == ThinkLonger.Name).ParametersJson);
        Assert.Equal(WebResearch.ParametersJson, set.Tools.Single(t => t.Name == WebResearch.Name).ParametersJson);
    }
}

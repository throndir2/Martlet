using Martlet.Core.Cluster;

namespace Martlet.Core.Tests;

// One sentence for an area's list (PoolListSummary): the list card and Devices › Sharing's line say the same thing.
public sealed class PoolListSummaryTests
{
    private static PoolList List(string area, params PoolMember[] members) => new() { Area = area, Members = members };

    [Fact]
    public void Nothing_on_says_what_happens_instead_the_same_way_in_both_places()
    {
        foreach (var names in new[] { false, true })
        {
            Assert.Equal("Off: nothing in the list is on.", PoolListSummary.Describe(PoolAreas.Speaking, null, names: names));
            Assert.Equal("Off: nothing in the list is on.",
                PoolListSummary.Describe(PoolAreas.Speaking, List("speaking", PoolMember.ThisPc() with { Off = true }), names: names));
            Assert.Equal("Its own model only: a reply waits for it when it is busy.", PoolListSummary.Describe(PoolAreas.Thinking, null, names: names));
            Assert.Equal("Nothing in the list is on. Instead: Voice loudness on this PC.", PoolListSummary.Describe(PoolAreas.LipSync, null, names: names));
            // Vision gives the decision's words for what takes each picture; without them, its fallback.
            Assert.Equal("Nothing in the list is on. Instead: Thinking's own model (gemma4:e2b) takes the picture with a reply.",
                PoolListSummary.Describe(PoolAreas.Vision, List("vision"), "Thinking's own model (gemma4:e2b) takes the picture with a reply", names));
        }
    }

    [Fact]
    public void Members_that_are_on_are_counted_on_the_card_and_named_on_the_line()
    {
        var two = List("speaking", PoolMember.ThisPc(), PoolMember.Computer("diva"), PoolMember.Computer("ripley") with { Off = true });
        Assert.Equal("2 members are on. A request goes to the first free one, in this order; when all are busy it waits for the first to finish.",
            PoolListSummary.Describe(PoolAreas.Speaking, two));
        Assert.Equal("In order: This PC, diva.", PoolListSummary.Describe(PoolAreas.Speaking, two, names: true));
        Assert.Equal("One member is on. Requests go to it.", PoolListSummary.Describe(PoolAreas.Speaking, List("speaking", PoolMember.ThisPc())));

        var thinking = List("thinking", PoolMember.Computer("diva"));
        Assert.Equal("The conversation's own model goes first. When it is busy, a reply goes to the first free one of 1 below.",
            PoolListSummary.Describe(PoolAreas.Thinking, thinking));
        Assert.Equal("Its own model first, then diva.", PoolListSummary.Describe(PoolAreas.Thinking, thinking, names: true));
        // The decision's words are only for an empty list.
        Assert.Equal("One member is on. Requests go to it.", PoolListSummary.Describe(PoolAreas.Vision, List("vision", PoolMember.ThisPc()), "ignored"));
    }
}

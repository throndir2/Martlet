using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

/// <summary>One moment (<see cref="MomentTurn"/>): whatever starts a reply takes everything else that waits.</summary>
public sealed class MomentTurnTests
{
    [Fact]
    public void TheUserTakesWhatIsThereAndFinishedWorkAlways()
    {
        var plan = MomentTurn.Plan(MomentTrigger.User, pcWaiting: true, jobsWaiting: false, lookDue: true);
        Assert.Equal(new MomentPlan(MomentTrigger.User, true, true, true, true), plan);
        Assert.Equal(MomentRoute.Reply, plan.Route);
        Assert.True(plan.Combined);
        var alone = MomentTurn.Plan(MomentTrigger.User, pcWaiting: false, jobsWaiting: false, lookDue: false);
        Assert.Equal(MomentRoute.Reply, alone.Route);
        Assert.False(alone.PcAudio || alone.Look);
    }

    [Fact]
    public void WhatThisPcPlayedTakesFinishedWorkOnlyWhenMartletMayBringItUp()
    {
        Assert.Equal(new MomentPlan(MomentTrigger.PcAudio, false, true, true, false),
            MomentTurn.Plan(MomentTrigger.PcAudio, pcWaiting: true, jobsWaiting: true, lookDue: false));
        var held = MomentTurn.Plan(MomentTrigger.PcAudio, pcWaiting: true, jobsWaiting: false, lookDue: true);
        Assert.False(held.Jobs);
        Assert.True(held.Look);
        Assert.Equal(MomentRoute.Reply, held.Route);
    }

    [Fact]
    public void AReportTakesThePcAndADueLookAndBecomesAReplyWhenThePcPlayed()
    {
        var report = MomentTurn.Plan(MomentTrigger.Report, pcWaiting: false, jobsWaiting: true, lookDue: true);
        Assert.Equal(MomentRoute.Report, report.Route);
        Assert.True(report.Look && report.Jobs && report.Combined);
        var withPc = MomentTurn.Plan(MomentTrigger.Report, pcWaiting: true, jobsWaiting: true, lookDue: false);
        Assert.Equal(MomentRoute.Reply, withPc.Route);
        Assert.True(withPc.Jobs && withPc.PcAudio);
    }

    [Fact]
    public void ADueLookIsAPlainGlanceOnlyWhenNothingElseWaits()
    {
        Assert.Equal(MomentRoute.Glance, MomentTurn.Plan(MomentTrigger.Look, false, false, true).Route);
        Assert.False(MomentTurn.Plan(MomentTrigger.Look, false, false, true).Combined);
        var jobs = MomentTurn.Plan(MomentTrigger.Look, false, true, true);
        Assert.Equal(MomentRoute.Report, jobs.Route);
        var pc = MomentTurn.Plan(MomentTrigger.Look, true, true, true);
        Assert.Equal(MomentRoute.Reply, pc.Route);
        Assert.True(pc.Jobs && pc.PcAudio && pc.Look);
    }

    [Fact]
    public void DescribeNamesWhatWasTakenNeverWhatItSaid()
    {
        Assert.Equal("your words", MomentTurn.Describe(true, 0, false, null, 0));
        Assert.Equal("your words, 2 lines this PC played, the picture (a notification) and 1 finished job",
            MomentTurn.Describe(true, 2, true, "a notification", 1));
        Assert.Equal("1 line this PC played and 3 finished jobs", MomentTurn.Describe(false, 1, false, null, 3));
        Assert.Equal("the picture and finished work", MomentTurn.Describe(false, 0, true, null, 0, report: true));
        Assert.Equal("nothing", MomentTurn.Describe(false, 0, false, null, 0));
    }
}

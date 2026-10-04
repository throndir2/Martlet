using Martlet.Core.Nodes;

namespace Martlet.Core.Tests;

public sealed class OwnHostFollowerTests
{
    private const string App = "0.38.1";
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 21, 50, 10, TimeSpan.Zero);

    private static OwnHostStep Decide(OwnHostFollower follower, OwnHostReading? reading, DateTimeOffset? now = null,
        bool claimed = false, bool appUpdateFirst = false, bool conversation = false) =>
        follower.Decide(App, reading, claimed, appUpdateFirst, conversation, now ?? Start);

    [Fact]
    public void AfterAnAppUpdateAnOlderRunningHostServiceIsUpdatedWhateverElseIsSet()
    {
        var follower = new OwnHostFollower();

        Assert.True(follower.Due(App, Start));
        Assert.Equal(OwnHostStep.Update, Decide(follower, new("0.38.0", Running: true)));
        Assert.Equal(OwnHostStep.Update, Decide(follower, new(null, Running: true)));
    }

    [Fact]
    public void ACurrentOrNewerHostServiceIsRememberedAndNotReadAgain()
    {
        var follower = new OwnHostFollower();

        Assert.Equal(OwnHostStep.Current, Decide(follower, new("0.38.1", Running: true)));
        Assert.False(follower.Due(App, Start.AddHours(1)));
        Assert.Equal(OwnHostStep.Idle, Decide(follower, new("0.37.0", Running: true)));

        var newer = new OwnHostFollower();
        Assert.Equal(OwnHostStep.Current, Decide(newer, new("0.39.0", Running: false)));
        // The next Martlet version makes it due again.
        Assert.True(follower.Due("0.39.0", Start));
    }

    [Fact]
    public void NothingFoundOrAStoppedHostServiceIsReadAgainLater()
    {
        var follower = new OwnHostFollower();

        Assert.Equal(OwnHostStep.NotFound, Decide(follower, null));
        Assert.Equal(OwnHostStep.Stopped, Decide(follower, new("0.38.0", Running: false)));
        Assert.True(follower.Due(App, Start));
        Assert.Equal(OwnHostStep.Update, Decide(follower, new("0.38.0", Running: true)));
    }

    [Fact]
    public void AnotherRouteThisPcsOwnUpdateAndTheConversationGoFirst()
    {
        var follower = new OwnHostFollower();
        var older = new OwnHostReading("0.38.0", Running: true);

        Assert.Equal(OwnHostStep.OtherRoute, Decide(follower, older, claimed: true, appUpdateFirst: true, conversation: true));
        Assert.Equal(OwnHostStep.AppUpdateFirst, Decide(follower, older, appUpdateFirst: true, conversation: true));
        Assert.Equal(OwnHostStep.Conversation, Decide(follower, older, conversation: true));
        Assert.Equal(OwnHostStep.Update, Decide(follower, older));
    }

    [Fact]
    public void ABusyHostServiceIsTriedAgainAfterTheRetryDelay()
    {
        var follower = new OwnHostFollower(TimeSpan.FromMinutes(3));
        var older = new OwnHostReading("0.38.0", Running: true);

        follower.Busy(Start);

        Assert.Equal(Start.AddMinutes(3), follower.RetryAt);
        Assert.False(follower.Due(App, Start.AddMinutes(2)));
        Assert.Equal(OwnHostStep.Idle, Decide(follower, older, Start.AddMinutes(2)));
        Assert.Equal(OwnHostStep.Update, Decide(follower, older, Start.AddMinutes(3)));
    }

    [Fact]
    public void AFailedUpdateIsNotRetriedForThatVersionButAnotherRouteCanStillFinishIt()
    {
        var follower = new OwnHostFollower();
        follower.Busy(Start);

        follower.Failed(App);

        Assert.Null(follower.RetryAt);
        Assert.False(follower.Due(App, Start.AddHours(1)));
        Assert.True(follower.Due("0.39.0", Start));

        follower.Updated(App);
        Assert.False(follower.Due(App, Start));
        Assert.Equal(OwnHostStep.Idle, Decide(follower, new("0.38.0", Running: true)));
    }

    [Theory]
    [InlineData("0.38.0", "0.38.1", true)]
    [InlineData(null, "0.38.1", true)]
    [InlineData("latest", "0.38.1", true)]
    [InlineData("0.38.1", "0.38.1", false)]
    [InlineData("0.40.0", "0.38.1", false)]
    public void VersionsCompareNumerically(string? reported, string target, bool older) =>
        Assert.Equal(older, OwnHostFollower.IsOlder(reported, target));
}

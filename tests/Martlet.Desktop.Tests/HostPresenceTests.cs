using Martlet.Core.Tests;
using Xunit;

namespace Martlet.Desktop.Tests;

public sealed class HostPresenceTests : IDisposable
{
    private readonly ManualClock clock = new();
    private readonly List<(string Host, bool Online)> changes = [];

    public HostPresenceTests()
    {
        HostPresence.Reset();
        HostPresence.Clock = clock;
        HostPresence.Changed += Record;
    }

    public void Dispose()
    {
        HostPresence.Changed -= Record;
        HostPresence.Clock = TimeProvider.System;
        HostPresence.Reset();
    }

    private void Record(string host, bool online) => changes.Add((host, online));

    [Fact]
    public void A_host_never_checked_counts_as_online()
    {
        Assert.False(HostPresence.IsOffline("diva"));
        Assert.Empty(HostPresence.Offline);
        Assert.Null(HostPresence.OfflineSince("diva"));
        HostPresence.Note("diva", true);
        Assert.False(HostPresence.IsOffline("diva"));
        Assert.Empty(changes);
    }

    [Fact]
    public void Changed_fires_only_on_a_transition_with_the_time_it_went_offline()
    {
        var start = clock.GetUtcNow();
        HostPresence.Note("diva", false);
        clock.Advance(TimeSpan.FromSeconds(15));
        HostPresence.Note("diva", false);
        Assert.True(HostPresence.IsOffline("diva"));
        Assert.Equal(["diva"], HostPresence.Offline);
        Assert.Equal(start, HostPresence.OfflineSince("diva"));

        clock.Advance(TimeSpan.FromSeconds(30));
        HostPresence.Note("diva", true);
        HostPresence.Note("diva", true);
        Assert.False(HostPresence.IsOffline("diva"));
        Assert.Null(HostPresence.OfflineSince("diva"));
        Assert.Equal(TimeSpan.FromSeconds(45), HostPresence.LastAbsence("diva"));
        Assert.Equal([("diva", false), ("diva", true)], changes);
    }

    [Fact]
    public void A_listener_that_fails_doesnt_stop_the_others_or_the_check()
    {
        static void Throws(string host, bool online) => throw new InvalidOperationException("listener failed");
        HostPresence.Changed += Throws;
        try
        {
            HostPresence.Note("ripley", false);
            Assert.True(HostPresence.IsOffline("ripley"));
            Assert.Equal([("ripley", false)], changes);
        }
        finally { HostPresence.Changed -= Throws; }
    }
}

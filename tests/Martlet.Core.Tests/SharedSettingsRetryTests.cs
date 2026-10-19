using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>A shared setting this computer applied but that read back differently (or not at all) is tried again later
/// instead of staying different until Martlet restarts (#950).</summary>
public sealed class SharedSettingsRetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-shared-retry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>A setting whose apply only takes effect while <see cref="Ready"/> is true; before that it reads back the old
    /// value, or (with <see cref="NothingUntilReady"/>) nothing, and then the applied value once it is ready.</summary>
    private sealed class LateSection(string key, string value) : ISharedSection
    {
        private string current = value;
        private string? pending;
        internal bool Ready { get; set; } = true;
        internal bool NothingUntilReady { get; init; }
        internal int Applied { get; private set; }
        internal string Current => current;
        public string Key => key;
        public string Title => "Voice ID";

        public Task<SharedLocal?> ReadAsync(CancellationToken token)
        {
            if (Ready && pending is not null) (current, pending) = (pending, null);
            if (!Ready && NothingUntilReady) return Task.FromResult<SharedLocal?>(null);
            return Task.FromResult<SharedLocal?>(new(current, null, false, DateTimeOffset.UnixEpoch.AddDays(1)));
        }

        public Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token)
        {
            Applied++;
            if (Ready) current = setting.Value;
            else if (NothingUntilReady) pending = setting.Value;
            return Task.FromResult(SharedApply.Done);
        }
    }

    [Fact]
    public async Task A_setting_that_reads_back_differently_is_applied_again_after_its_retry_time()
    {
        var start = DateTimeOffset.UtcNow;
        var a = new SharedSettingsNode(Path.Combine(root, "a"), "desktop-a", [new LateSection("voice-id", "\"on\"")]);
        await a.SyncAsync([], true, start, CancellationToken.None);

        var section = new LateSection("voice-id", "\"off\"") { Ready = false };
        var b = new SharedSettingsNode(Path.Combine(root, "b"), "desktop-b", [section]);
        // B's own value is older than A's choice, so B follows A, but its part isn't ready: the value reads back different.
        var first = await b.SyncAsync([a.Document], true, start.AddSeconds(1), CancellationToken.None);
        Assert.Equal(1, section.Applied);
        Assert.Contains("tries again", first.Waiting["voice-id"]);

        // Before the retry time it waits without applying again.
        await b.SyncAsync([a.Document], true, start.AddSeconds(30), CancellationToken.None);
        Assert.Equal(1, section.Applied);

        // After it, the part is ready and the setting is applied again and holds.
        section.Ready = true;
        var later = await b.SyncAsync([a.Document], true, start + SharedSettingsNode.MismatchRetry(1) + TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(2, section.Applied);
        Assert.Equal("\"on\"", section.Current);
        Assert.DoesNotContain("voice-id", later.Waiting.Keys);
        Assert.Contains(later.Applied, c => c.Key == "voice-id" && c.By == "desktop-a");
    }

    [Fact]
    public async Task A_setting_that_reads_back_nothing_waits_and_is_tried_on_the_next_sync()
    {
        var start = DateTimeOffset.UtcNow;
        var a = new SharedSettingsNode(Path.Combine(root, "a"), "desktop-a", [new LateSection("voice-id", "\"on\"")]);
        await a.SyncAsync([], true, start, CancellationToken.None);

        var section = new LateSection("voice-id", "\"off\"") { Ready = false, NothingUntilReady = true };
        var b = new SharedSettingsNode(Path.Combine(root, "b"), "desktop-b", [section]);
        var first = await b.SyncAsync([a.Document], true, start.AddSeconds(1), CancellationToken.None);
        Assert.Contains("isn't ready", first.Waiting["voice-id"]);

        section.Ready = true;
        var next = await b.SyncAsync([a.Document], true, start.AddSeconds(2), CancellationToken.None);
        Assert.Equal("\"on\"", section.Current);
        Assert.DoesNotContain("voice-id", next.Waiting.Keys);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void The_retry_time_doubles_to_at_most_half_an_hour(int tries, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SharedSettingsNode.MismatchRetry(tries));
}

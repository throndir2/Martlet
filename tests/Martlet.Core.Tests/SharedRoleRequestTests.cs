using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Another computer asking this one to become a host PC: a role entry it writes travels with the shared settings and
/// this computer follows it, while a later choice made on this computer itself wins over it.</summary>
public sealed class SharedRoleRequestTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-role-request-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class RoleSection(string key) : ISharedSection
    {
        internal string Role { get; set; } = "companion";
        internal int Applied { get; private set; }
        public string Key => key;
        public string Title => "Companion or host PC";
        public Task<SharedLocal?> ReadAsync(CancellationToken token) =>
            Task.FromResult<SharedLocal?>(new($"\"{Role}\"", null, false, DateTimeOffset.UnixEpoch.AddDays(1)));
        public Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token)
        {
            Role = setting.Value.Trim('"');
            Applied++;
            return Task.FromResult(SharedApply.Done);
        }
    }

    [Fact]
    public async Task A_computer_follows_another_computers_ask_to_become_a_host_and_its_own_later_choice_wins()
    {
        Assert.True(SharedSettings.IsDeviceKey("role.desktop-b"));
        Assert.True(SharedSettings.IsDeviceKey("pc.desktop-b"));
        Assert.False(SharedSettings.IsDeviceKey("thinking"));

        var bRole = new RoleSection("role.desktop-b");
        var a = new SharedSettingsNode(Path.Combine(root, "a"), "desktop-a", [new RoleSection("role.desktop-a")]);
        var b = new SharedSettingsNode(Path.Combine(root, "b"), "desktop-b", [bRole]);
        var start = DateTimeOffset.UtcNow;
        await b.SyncAsync([], true, start, CancellationToken.None);
        Assert.Equal("\"companion\"", b.Document.Find("role.desktop-b")!.Value);

        // A asks B to be a host PC; its own section can't be written this way.
        await a.SyncAsync([b.Document], true, start.AddSeconds(1), CancellationToken.None);
        a.Put("role.desktop-b", "\"host\"", start.AddSeconds(2));
        Assert.Throws<ContractException>(() => a.Put("role.desktop-a", "\"host\"", start.AddSeconds(2)));
        Assert.Equal("desktop-a", a.Document.Find("role.desktop-b")!.UpdatedBy);

        var followed = await b.SyncAsync([a.Document], true, start.AddSeconds(3), CancellationToken.None);
        Assert.Equal("host", bRole.Role);
        Assert.Contains(followed.Applied, c => c.Key == "role.desktop-b" && c.By == "desktop-a");
        Assert.Empty(followed.Recorded);

        // Nothing more happens on later syncs, and A's saved copy kept the ask.
        await b.SyncAsync([a.Document], true, start.AddSeconds(4), CancellationToken.None);
        Assert.Equal(1, bRole.Applied);
        Assert.Equal("\"host\"", SharedSettingsState.Load(Path.Combine(root, "a")).Document.Find("role.desktop-b")!.Value);

        // B made a companion again on B itself: recorded there and wins over the older ask everywhere.
        bRole.Role = "companion";
        var recorded = await b.SyncAsync([a.Document], true, start.AddSeconds(5), CancellationToken.None);
        Assert.Contains("role.desktop-b", recorded.Recorded);
        var merged = SharedSettings.Merge(a.Document, b.Document).Find("role.desktop-b")!;
        Assert.Equal(("\"companion\"", "desktop-b"), (merged.Value, merged.UpdatedBy));
        Assert.Equal(1, bRole.Applied);
    }
}

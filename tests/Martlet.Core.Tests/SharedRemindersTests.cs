using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Each computer's reminders travel with the shared settings as its own entry ("reminders.desktop-a"): it reaches the
/// other computers, and like the entries about one computer it is the first to leave when a copy is full.</summary>
public sealed class SharedRemindersTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-shared-reminders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Each_computers_reminders_reach_the_others_and_never_push_out_a_setting()
    {
        Assert.True(SharedSettings.IsDeviceKey(SharedSettings.RemindersPrefix + "desktop-a"));
        var a = new SharedSettingsNode(Path.Combine(root, "a"), "desktop-a", []);
        var b = new SharedSettingsNode(Path.Combine(root, "b"), "desktop-b", []);
        var start = DateTimeOffset.UtcNow;
        a.Put("reminders.desktop-a", """{"reminders":[{"id":"abc123","text":"do the dishes"}]}""", start);
        b.Put("reminders.desktop-b", """{"marks":[{"id":"abc123","kind":"bid"}]}""", start.AddSeconds(1));

        await b.SyncAsync([a.Document], true, start.AddSeconds(2), CancellationToken.None);
        await a.SyncAsync([b.Document], true, start.AddSeconds(3), CancellationToken.None);
        foreach (var node in new[] { a, b })
        {
            Assert.Equal("desktop-a", node.Document.Find("reminders.desktop-a")!.UpdatedBy);
            Assert.Equal("desktop-b", node.Document.Find("reminders.desktop-b")!.UpdatedBy);
        }

        // A full copy keeps every setting, even an old one, over the newest reminders.
        var full = SharedSettings.Empty;
        for (var i = 0; i < SharedSettings.MaximumSettings; i++) full = full.Put($"setting{i:00}", "1", null, "desktop-a", start, 1 + i);
        full = full.Put("reminders.desktop-c", "{}", null, "desktop-c", start, long.MaxValue / 8);
        Assert.Null(full.Find("reminders.desktop-c"));
        Assert.Equal(SharedSettings.MaximumSettings, full.Settings.Count);
    }
}

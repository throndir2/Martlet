using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Show advanced settings is one choice for all the person's computers: advanced-settings.json travels as the account
/// setting <c>advanced-settings</c>, a computer without a choice never overrides one made elsewhere, and turning it off on one
/// computer turns it off on the other.</summary>
public sealed class AdvancedSettingsFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-advanced-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Two_computers_show_the_same_pages()
    {
        Assert.True(SettingScopes.IsAccountKey(AdvancedSettingsFile.SharedKey));
        Assert.Equal(SettingsFileScope.Account, SettingsFiles.Find(AdvancedSettingsFile.FileName)!.Scope);
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var followedOnB = 0;
        var nodeA = new SharedSettingsNode(a, "desktop-a", [AdvancedSettingsFile.Section(a)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [AdvancedSettingsFile.Section(b, () => followedOnB++)]);
        var start = DateTimeOffset.UtcNow;

        // Nothing chosen yet: only the essentials show, and B's default doesn't override A's choice.
        Assert.False(AdvancedSettingsFile.Load(a).Show);
        await nodeB.SyncAsync([], true, start, CancellationToken.None);
        AdvancedSettingsFile.Save(a, new AdvancedSettingsFile { Show = true });
        await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(15), CancellationToken.None);
        await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(30), CancellationToken.None);
        Assert.True(AdvancedSettingsFile.Load(b).Show);
        Assert.Equal(1, followedOnB);

        // Turned off on B: A follows.
        AdvancedSettingsFile.Save(b, new AdvancedSettingsFile { Show = false });
        await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(45), CancellationToken.None);
        await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(60), CancellationToken.None);
        Assert.False(AdvancedSettingsFile.Load(a).Show);
    }

    [Fact]
    public void A_broken_or_newer_file_shows_only_the_essentials()
    {
        var directory = Path.Combine(root, "broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(AdvancedSettingsFile.PathOf(directory), "not json");
        Assert.False(AdvancedSettingsFile.Load(directory).Show);
        Assert.Null(AdvancedSettingsFile.Parse("{\"schema_version\":2,\"show\":true}"));
        Assert.True(AdvancedSettingsFile.Parse("{\"schema_version\":1,\"show\":true}")!.Show);
    }
}

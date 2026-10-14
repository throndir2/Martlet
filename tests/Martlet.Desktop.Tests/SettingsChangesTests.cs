using Martlet.Core.Settings;
using Martlet.Core.Tests;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The settings change coordinator (<see cref="SettingsChanges"/>): where each version of settings.json came from and
/// which one Martlet applied, so the file watcher and the settings sync don't apply each other's saves again.</summary>
public sealed class SettingsChangesTests
{
    [Fact]
    public void A_save_outside_a_scope_is_a_local_edit_and_a_version_this_martlet_did_not_write_is_the_file()
    {
        var changes = new SettingsChanges(new ManualClock());
        changes.Written("A");
        Assert.Equal(SettingsChangeSource.LocalEdit, changes.SourceOf("A"));
        Assert.Equal(SettingsChangeSource.File, changes.SourceOf("B"));
    }

    [Fact]
    public async Task A_save_in_a_scope_keeps_its_source_across_awaits_and_other_flows_do_not_see_it()
    {
        var changes = new SettingsChanges(new ManualClock());
        var outside = new TaskCompletionSource();
        var other = Task.Run(async () =>
        {
            await outside.Task;
            changes.Written("page");
        });
        using (changes.From(SettingsChangeSource.RemoteSync))
        {
            await Task.Yield();
            changes.Written("synced");
            outside.SetResult();
            await other;
        }
        changes.Written("after");
        Assert.Equal(SettingsChangeSource.RemoteSync, changes.SourceOf("synced"));
        Assert.Equal(SettingsChangeSource.LocalEdit, changes.SourceOf("page"));
        Assert.Equal(SettingsChangeSource.LocalEdit, changes.SourceOf("after"));
    }

    [Fact]
    public void A_version_is_applied_once_and_keeps_the_source_it_was_written_with()
    {
        var clock = new ManualClock();
        var changes = new SettingsChanges(clock);
        using (changes.From(SettingsChangeSource.RemoteSync)) changes.Written("R1");
        Assert.False(changes.IsApplied("R1"));
        // The sync's own refresh applies it; the watcher's later look at the same file finds it applied.
        var applied = changes.Applied("R1", SettingsChangeSource.RemoteSync);
        Assert.Equal(new SettingsChange("R1", SettingsChangeSource.RemoteSync, clock.GetUtcNow()), applied);
        Assert.True(changes.IsApplied("R1"));
        Assert.Null(changes.Applied("R1", SettingsChangeSource.File));
        // A page's save that the page didn't apply: the watcher applies it, and it stays a local edit.
        changes.Written("R2");
        Assert.False(changes.IsApplied("R2"));
        Assert.Equal(SettingsChangeSource.LocalEdit, changes.Applied("R2", SettingsChangeSource.File)!.Source);
        // Written outside Martlet (its MCP server or by hand).
        Assert.Equal(SettingsChangeSource.File, changes.Applied("R3", SettingsChangeSource.File)!.Source);
        Assert.Equal("R3", changes.Last!.Revision);
        Assert.Null(changes.Applied(null, SettingsChangeSource.File));
        Assert.False(changes.IsApplied(null));
    }

    [Fact]
    public void Only_the_newest_saves_are_remembered()
    {
        var changes = new SettingsChanges(new ManualClock());
        for (var i = 0; i <= SettingsChanges.Remembered; i++) changes.Written("R" + i);
        Assert.Equal(SettingsChangeSource.File, changes.SourceOf("R0"));
        Assert.Equal(SettingsChangeSource.LocalEdit, changes.SourceOf("R1"));
    }

    [Fact]
    public async Task The_settings_store_reports_each_save_with_the_revision_a_load_gives()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-settings-changes-").FullName;
        try
        {
            var store = new SettingsStore(directory);
            var changes = new SettingsChanges(new ManualClock());
            store.Saved += changes.Written;
            var settings = ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null), "https://example.test/api/v1", "org/model:q4");
            SettingsSaveResult saved;
            using (changes.From(SettingsChangeSource.RemoteSync)) saved = await store.SaveAsync(settings, null);
            Assert.True(saved.Saved);
            var loaded = await store.LoadAsync();
            Assert.Equal(saved.Revision, loaded.Revision);
            Assert.Equal(SettingsChangeSource.RemoteSync, changes.SourceOf(loaded.Revision!));
        }
        finally { Directory.Delete(directory, true); }
    }
}

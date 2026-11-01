using Martlet.Core.Activities;
using Martlet.Core.Preferences;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Your preferences (<see cref="UserPreferences"/>): the one owner of the user's own rules for how Martlet behaves. Where
/// each applies follows <see cref="ActivityScope"/>, the note for a request is bounded, the file keeps its bounds, and two
/// computers keep one list through the account setting <c>user-preferences</c>.</summary>
public sealed class UserPreferencesTests : IDisposable
{
    private const string Elden = "gaming/ELDEN RING";
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-user-preferences-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Preferences_apply_where_they_were_made_for_and_the_note_says_them_most_specific_last()
    {
        var owner = new UserPreferences(Path.Combine(root, "one"));
        Assert.Null(owner.Note(Elden, null));
        Assert.Equal("No preferences yet.", owner.Status());
        var changed = 0;
        owner.Changed += () => changed++;

        var hints = owner.Add("  \"Stop   giving hints\"  ", Elden, UserPreferences.Said);
        var swear = owner.Add("Don't swear", null, UserPreferences.Typed);
        var spoilers = owner.Add("No story spoilers", ActivityScope.Gaming, UserPreferences.Typed);
        Assert.True(hints.Changed && swear.Changed && spoilers.Changed);
        Assert.Equal(3, changed);
        Assert.Equal("Stop giving hints", hints.Preference!.Text);
        Assert.Equal("3 preferences: 1 everywhere, 1 while gaming, 1 for one game.", owner.Status());

        Assert.Equal(["Don't swear", "No story spoilers", "Stop giving hints"], owner.ApplyingNow("gaming/elden   ring").Select(p => p.Text));
        Assert.Equal(["Don't swear", "No story spoilers"], owner.ApplyingNow("gaming/Palworld").Select(p => p.Text));
        Assert.Equal(["Don't swear"], owner.ApplyingNow(null).Select(p => p.Text));

        var note = owner.Note(Elden, null)!;
        Assert.Contains("manage_preferences", note);
        Assert.Contains("- Don't swear\n- No story spoilers (while gaming)\n- Stop giving hints (while playing ELDEN RING)", note);
        // An emptied prompt sends nothing.
        Assert.Null(owner.Note(Elden, new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.UserPreferences] = "" } }));

        // The same rule for the same place is kept once; for another place it is its own.
        var same = owner.Add("stop giving hints", "gaming/Elden Ring", UserPreferences.Said);
        Assert.False(same.Changed);
        Assert.Equal(hints.Preference.Id, same.Preference!.Id);
        Assert.True(owner.Add("Stop giving hints", "gaming/Palworld", UserPreferences.Said).Changed);
        Assert.Equal(4, owner.All.Count);
    }

    [Fact]
    public void Update_and_remove_go_by_id_and_bad_input_is_refused()
    {
        var owner = new UserPreferences(Path.Combine(root, "two"));
        var hint = owner.Add("Stop giving hints", Elden, UserPreferences.Said).Preference!;

        var updated = owner.Update(hint.Id.ToUpperInvariant(), "Only give hints when I ask", setActivity: false, null, UserPreferences.Said);
        Assert.True(updated.Changed);
        Assert.Equal(Elden, updated.Preference!.Activity);
        Assert.NotNull(updated.Preference.ChangedAt);
        var everywhere = owner.Update(hint.Id, null, setActivity: true, null, UserPreferences.Typed);
        Assert.Null(everywhere.Preference!.Activity);
        Assert.Equal(UserPreferences.Typed, everywhere.Preference.Source);
        Assert.False(owner.Update(hint.Id, null, setActivity: true, null, UserPreferences.Typed).Changed);

        Assert.NotNull(owner.Add("", null, UserPreferences.Said).Problem);
        Assert.NotNull(owner.Add(new string('a', UserPreferences.MaximumTextCharacters + 1), null, UserPreferences.Said).Problem);
        Assert.NotNull(owner.Add("bell\a", null, UserPreferences.Said).Problem);
        Assert.NotNull(owner.Add("ok", "Gaming/Bad", UserPreferences.Said).Problem);
        Assert.NotNull(owner.Update("p000000", "x", false, null, UserPreferences.Said).Problem);
        Assert.NotNull(owner.Remove(null).Problem);

        Assert.True(owner.Remove(hint.Id).Changed);
        Assert.Empty(owner.All);
        Assert.Null(owner.Remove(hint.Id).Preference);
    }

    [Fact]
    public void The_list_and_the_note_are_bounded_and_the_file_keeps_its_bounds()
    {
        var directory = Path.Combine(root, "bounds");
        var owner = new UserPreferences(directory);
        for (var i = 0; i < UserPreferences.MaximumPreferences; i++)
            Assert.True(owner.Add($"Rule {i} " + new string('x', 200), i % 2 == 0 ? null : Elden, UserPreferences.Typed).Changed);
        Assert.NotNull(owner.Add("One more", null, UserPreferences.Typed).Problem);

        var lines = UserPreferences.Note(owner.ApplyingNow(Elden), new PromptSettings
        {
            Overrides = new Dictionary<string, string> { [PromptCatalog.UserPreferences] = "{preferences}" }
        })!;
        Assert.True(lines.Length <= UserPreferences.MaximumNoteCharacters + 80, $"{lines.Length} characters");
        Assert.EndsWith("more not shown; manage_preferences list shows them all.)", lines);
        // The most specific (the game's) are the ones kept.
        Assert.Contains("(while playing ELDEN RING)", lines.Split('\n')[0]);

        Assert.Null(UserPreferences.Parse("not json"));
        Assert.Null(UserPreferences.Parse("""{"schema_version":2,"preferences":[]}"""));
        Assert.Null(UserPreferences.Parse("""{"schema_version":1,"preferences":[{"id":"p1","text":"","source":"said"}]}"""));
        Assert.Null(UserPreferences.Parse("""{"schema_version":1,"preferences":[{"id":"p1","text":"ok","source":"someone"}]}"""));
        File.WriteAllText(UserPreferences.PathOf(directory), "not json");
        Assert.Empty(new UserPreferences(directory).All);
    }

    [Fact]
    public void Where_reads_from_plain_words()
    {
        Assert.True(ActivityScope.TryParse(null, Elden, out var everywhere, out _));
        Assert.Null(everywhere);
        Assert.True(ActivityScope.TryParse("Games", null, out var games, out _));
        Assert.Equal(ActivityScope.Gaming, games);
        Assert.True(ActivityScope.TryParse("this game", Elden, out var here, out _));
        Assert.Equal(Elden, here);
        Assert.False(ActivityScope.TryParse("this game", null, out _, out var problem));
        Assert.Contains("No game", problem);
        Assert.True(ActivityScope.TryParse(" Elden  Ring ", null, out var named, out _));
        Assert.Equal("gaming/Elden Ring", named);
        Assert.True(ActivityScope.TryParse("gaming/Palworld", null, out var value, out _));
        Assert.Equal("gaming/Palworld", value);
        Assert.Equal("while playing Elden Ring", ActivityScope.Describe(named));
    }

    [Fact]
    public async Task Two_computers_keep_one_list_of_preferences()
    {
        Assert.True(SettingScopes.IsAccountKey(UserPreferences.SharedKey));
        Assert.Equal(SettingsFileScope.Account, SettingsFiles.Find(UserPreferences.FileName)!.Scope);
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var onA = new UserPreferences(a);
        var onB = new UserPreferences(b);
        var followedOnB = 0;
        onB.Changed += () => followedOnB++;
        var nodeA = new SharedSettingsNode(a, "desktop-a", [UserPreferences.Section(a, onA.Followed)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [UserPreferences.Section(b, onB.Followed)]);
        var start = DateTimeOffset.UtcNow;

        // Neither has preferences yet; B's empty default never overrides a list made elsewhere.
        await nodeB.SyncAsync([], true, start, CancellationToken.None);
        var hint = onA.Add("Stop giving hints", Elden, UserPreferences.Said).Preference!;
        var recorded = await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(15), CancellationToken.None);
        Assert.Contains(UserPreferences.SharedKey, recorded.Recorded);

        var taken = await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(30), CancellationToken.None);
        Assert.Contains(taken.Applied, change => change.Key == UserPreferences.SharedKey && change.By == "desktop-a");
        Assert.Equal(1, followedOnB);
        Assert.Equal(UserPreferences.Write(onA.All), UserPreferences.Write(onB.All));
        Assert.Equal(onA.Note(Elden, null), onB.Note(Elden, null));

        // "You can give hints again" on B: A follows.
        Assert.True(onB.Remove(hint.Id).Changed);
        await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(45), CancellationToken.None);
        await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(60), CancellationToken.None);
        Assert.Empty(onA.All);
        Assert.Null(onA.Note(Elden, null));

        // A list from a newer Martlet waits on this one, which keeps its own.
        var newer = nodeA.Document.Find(UserPreferences.SharedKey)!;
        Assert.Null(UserPreferences.Parse(newer.Value.Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal)));
    }
}

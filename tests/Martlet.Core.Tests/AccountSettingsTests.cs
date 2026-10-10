using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Each account's settings (docs/ACCOUNTS.md, "Account settings"): which settings are the household's and which each
/// account's, an account's own copy, and switching the files in use from one account to another without ever taking one
/// account's settings for another's.</summary>
public sealed class AccountSettingsTests : IDisposable
{
    private static readonly Guid Sam = Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7");
    private static readonly Guid Alex = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-account-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>One setting kept in a "file" every node of this computer shares, like the theme in appearance.txt.</summary>
    private sealed class FileSetting
    {
        internal string Value { get; set; } = "\"light\"";
    }

    private sealed class Section(string key, FileSetting file, string? defaultValue = "\"light\"") : ISharedSection
    {
        internal bool Wait { get; set; }
        internal int Applied { get; private set; }
        public string Key => key;
        public string Title => key;
        public string? Default => defaultValue;
        public Task<SharedLocal?> ReadAsync(CancellationToken token) =>
            Task.FromResult<SharedLocal?>(new(file.Value, null, file.Value == defaultValue, Start));
        public Task<SharedApply> ApplyAsync(SharedSetting setting, string? secret, CancellationToken token)
        {
            if (Wait) return Task.FromResult(SharedApply.Waiting("The window is open."));
            file.Value = setting.Value;
            Applied++;
            return Task.FromResult(SharedApply.Done);
        }
    }

    private SharedSettingsNode Node(Guid account, params ISharedSection[] sections) =>
        new(AccountWorkingCopy.Folder(root, account), "desktop-a", sections);

    [Theory]
    [InlineData("companion", true)]
    [InlineData("replies", true)]
    [InlineData("prompts", true)]
    [InlineData("memory", true)]
    [InlineData("lorebooks", true)]
    [InlineData("character", true)]
    [InlineData("talk", true)]
    [InlineData("speech-display", true)]
    [InlineData("appearance", true)]
    [InlineData("appearance-custom", true)]
    [InlineData("voice-id", true)]
    [InlineData("touch-temperament", true)]
    [InlineData("reminders.desktop-a", true)]
    [InlineData("thinking", false)]
    [InlineData("listening", false)]
    [InlineData("speaking", false)]
    [InlineData("thinking-fallback", false)]
    [InlineData("character-actions", false)]
    [InlineData("voice-recognition", false)]
    [InlineData("smart-home", false)]
    [InlineData("updates", false)]
    [InlineData("model-abilities", false)]
    [InlineData("work-sharing", false)]
    [InlineData("pools", false)]
    [InlineData("pc.desktop-a", false)]
    [InlineData("role.desktop-a", false)]
    [InlineData("setup-run.desktop-a", false)]
    [InlineData("sharing.5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7", false)]
    public void Each_setting_belongs_to_the_household_or_to_each_account(string key, bool account)
    {
        Assert.Equal(account, SettingScopes.IsAccountKey(key));
        Assert.Equal(account ? SettingScopes.Account : SettingScopes.Household, SettingScopes.Of(key));
    }

    [Fact]
    public void Only_keeps_the_chosen_entries_as_they_are_and_only_the_secrets_they_use()
    {
        var household = SharedSettings.Empty
            .Put("thinking", "{\"type\":\"openai\",\"model\":\"m\"}", "sk-household", "desktop-a", Start)
            .Put("companion", "{\"name\":\"Sam's\"}", null, "desktop-b", Start.AddSeconds(1))
            .Put("reminders.desktop-b", "{}", null, "desktop-b", Start.AddSeconds(2));

        var account = household.Only(SettingScopes.IsAccountKey);

        Assert.Equal(["companion", "reminders.desktop-b"], account.Settings.Select(s => s.Key));
        Assert.Empty(account.Secrets);
        Assert.Equal(household.Find("companion"), account.Find("companion"));
        // Merging the part back changes nothing: the entries keep their revisions and writers.
        Assert.Equal(household.Digest(), SharedSettings.Merge(household, account).Digest());
        Assert.Single(household.Only(k => k == "thinking").Secrets);
    }

    [Fact]
    public async Task Switching_gives_each_account_its_own_settings_and_never_records_one_accounts_for_another()
    {
        var theme = new FileSetting();
        var sam = Node(Sam, new Section("appearance", theme));
        await sam.AdoptAsync(CancellationToken.None);
        theme.Value = "\"dark\"";
        Assert.Equal(["appearance"], (await sam.SyncAsync([], true, Start.AddMinutes(1), CancellationToken.None)).Recorded);

        // Alex signs in: Sam's changes are already in Sam's copy; Alex never chose a theme, so the files take the default.
        await sam.SyncAsync([], false, Start.AddMinutes(2), CancellationToken.None);
        var alex = Node(Alex, new Section("appearance", theme));
        Assert.Empty(await alex.AdoptAsync(CancellationToken.None));
        Assert.Equal("\"light\"", theme.Value);
        var first = await alex.SyncAsync([], true, Start.AddMinutes(3), CancellationToken.None);
        Assert.Empty(first.Recorded);
        Assert.Null(alex.Document.Find("appearance"));

        theme.Value = "\"custom\"";
        await alex.SyncAsync([], true, Start.AddMinutes(4), CancellationToken.None);
        Assert.Equal("\"custom\"", alex.Document.Find("appearance")!.Value);

        // Back to Sam: Sam's own theme returns, and Sam's copy never saw Alex's.
        var samAgain = Node(Sam, new Section("appearance", theme));
        Assert.Equal("\"dark\"", samAgain.Document.Find("appearance")!.Value);
        await samAgain.AdoptAsync(CancellationToken.None);
        Assert.Equal("\"dark\"", theme.Value);
        Assert.Empty((await samAgain.SyncAsync([], true, Start.AddMinutes(5), CancellationToken.None)).Recorded);
        Assert.Equal("\"custom\"", Node(Alex).Document.Find("appearance")!.Value);
    }

    [Fact]
    public async Task An_account_takes_its_settings_from_the_hosts_and_a_section_without_a_default_keeps_what_the_files_have()
    {
        var theme = new FileSetting { Value = "\"dark\"" };
        var palette = new FileSetting { Value = "{\"accent\":\"#123456\"}" };
        var fromHosts = SharedSettings.Empty.Put("appearance", "\"custom\"", null, "desktop-b", Start);
        var alex = Node(Alex, new Section("appearance", theme), new Section("appearance-custom", palette, defaultValue: null));

        // The hosts' copy, read as a switch does before the files take the account's settings.
        Assert.Empty(await alex.AdoptAsync([fromHosts], CancellationToken.None));
        Assert.Null(alex.Document.Find("appearance-custom"));

        Assert.Equal("\"custom\"", theme.Value);
        Assert.Equal("{\"accent\":\"#123456\"}", palette.Value);
        var synced = await alex.SyncAsync([fromHosts], true, Start.AddMinutes(2), CancellationToken.None);
        Assert.Empty(synced.Recorded);
        Assert.Empty(synced.Applied);
    }

    [Fact]
    public async Task A_setting_that_cant_be_taken_yet_waits_is_never_recorded_and_is_taken_on_a_later_sync()
    {
        var character = new FileSetting { Value = "\"sams-character\"" };
        var section = new Section("character", character, defaultValue: "\"builtin\"") { Wait = true };
        var alex = Node(Alex, section);

        var waiting = await alex.AdoptAsync(CancellationToken.None);
        Assert.Equal("The window is open.", waiting["character"]);
        Assert.Equal(["character"], alex.Adopting);

        // Whatever the files hold meanwhile is Sam's (or a change made on top of it): never recorded as Alex's.
        character.Value = "\"sams-character-moved\"";
        var meanwhile = await alex.SyncAsync([], true, Start.AddMinutes(1), CancellationToken.None);
        Assert.Empty(meanwhile.Recorded);
        Assert.Contains("character", meanwhile.Waiting.Keys);
        Assert.Equal(0, await alex.ClaimAllAsync(Start.AddMinutes(2), CancellationToken.None));

        // A restarted Martlet still knows the setting is not Alex's yet.
        section.Wait = false;
        var restarted = Node(Alex, section);
        Assert.Equal(["character"], restarted.Adopting);
        var later = await restarted.SyncAsync([], false, Start.AddMinutes(3), CancellationToken.None);
        Assert.Equal("\"builtin\"", character.Value);
        Assert.Empty(later.Recorded);
        Assert.Empty(restarted.Adopting);
        Assert.Null(restarted.Document.Find("character"));
    }

    [Fact]
    public async Task Adopting_again_is_safe_and_takes_nothing_from_the_files()
    {
        var theme = new FileSetting { Value = "\"dark\"" };
        var sam = Node(Sam, new Section("appearance", theme));
        await sam.SyncAsync([], true, Start, CancellationToken.None);
        Assert.Equal("\"dark\"", sam.Document.Find("appearance")!.Value);

        await sam.AdoptAsync(CancellationToken.None);
        await sam.AdoptAsync(CancellationToken.None);

        Assert.Equal("\"dark\"", theme.Value);
        Assert.Empty((await sam.SyncAsync([], true, Start.AddMinutes(1), CancellationToken.None)).Recorded);
    }

    [Fact]
    public void The_working_copy_marker_names_the_account_whose_settings_the_files_hold()
    {
        Assert.Null(AccountWorkingCopy.Load(root));
        Assert.Equal(Path.Combine(root, "accounts", "5a3f0c9e8b7d4e21a6c3b2f1d0e9a8b7"), AccountWorkingCopy.Folder(root, Sam));

        AccountWorkingCopy.Save(root, Sam, Start);
        Assert.Equal(Sam, AccountWorkingCopy.Load(root)!.Value.Account);
        Assert.Equal(Start, AccountWorkingCopy.Load(root)!.Value.Since);
        AccountWorkingCopy.Save(root, Alex, Start.AddMinutes(1));
        Assert.Equal(Alex, AccountWorkingCopy.Load(root)!.Value.Account);
        Assert.Equal(["working-copy.json"], Directory.GetFiles(Path.Combine(root, "accounts")).Select(Path.GetFileName));

        File.WriteAllText(Path.Combine(root, "accounts", AccountWorkingCopy.FileName), "{ not json");
        Assert.Null(AccountWorkingCopy.Load(root));
        Assert.Throws<ArgumentException>(() => AccountWorkingCopy.Folder(root, Guid.Empty));
    }

    [Fact]
    public async Task The_first_account_on_a_data_folder_from_before_accounts_goes_on_where_the_folder_left_off()
    {
        var theme = new FileSetting { Value = "\"dark\"" };
        var thinking = new FileSetting { Value = "{\"model\":\"m\"}" };
        var before = new SharedSettingsNode(root, "desktop-a", [new Section("appearance", theme), new Section("thinking", thinking, null)]);
        await before.SyncAsync([], true, Start, CancellationToken.None);
        before.Put("reminders.desktop-b", "{}", Start.AddSeconds(1));

        var folder = AccountWorkingCopy.Folder(root, Sam);
        Assert.True(AccountWorkingCopy.Seed(root, folder));
        Assert.False(AccountWorkingCopy.Seed(root, folder));

        var sam = Node(Sam, new Section("appearance", theme));
        Assert.Equal(["appearance", "reminders.desktop-b"], sam.Document.Settings.Select(s => s.Key));
        Assert.Equal(before.Document.Find("appearance"), sam.Document.Find("appearance"));
        // What this computer last saw came along, so the first sync records nothing and changes nothing.
        var first = await sam.SyncAsync([], true, Start.AddMinutes(1), CancellationToken.None);
        Assert.Empty(first.Recorded);
        Assert.Empty(first.Applied);
        Assert.Equal("\"dark\"", theme.Value);

        // A folder with nothing shared before seeds nothing.
        var empty = Path.Combine(root, "fresh");
        Assert.False(AccountWorkingCopy.Seed(empty, AccountWorkingCopy.Folder(empty, Alex)));
    }
}

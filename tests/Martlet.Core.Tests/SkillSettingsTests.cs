using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Skills (Companion › Skills, docs/CONVERSATION.md#skills): the built-in ones and the user's own, which every reply gets
/// (the pinned ones), the prompts that became skills and the skills that travel to every computer.</summary>
public sealed class SkillSettingsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-skills-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Built_in_skills_come_first_and_only_edits_and_own_skills_are_saved()
    {
        Assert.Null(SkillSettings.Normalize(SkillSettings.All(null)));
        var songs = SkillSettings.Find(null, SkillCatalog.Songs)! with { Pinned = true };
        var recipe = new Skill { Key = "recipes", Name = "Recipes", UseWhen = "When the user asks for a recipe.", Body = "Give one recipe." };
        var saved = SkillSettings.Normalize([.. SkillSettings.All(null).Select(s => s.Key == songs.Key ? songs : s), recipe])!;
        saved.Validate();
        Assert.Equal([SkillCatalog.Songs, "recipes"], saved.Skills.Select(s => s.Key));
        var all = SkillSettings.All(saved);
        Assert.Equal([.. SkillCatalog.All.Select(s => s.Key), "recipes"], all.Select(s => s.Key));
        Assert.True(all.Single(s => s.Key == SkillCatalog.Songs).Pinned);
        Assert.False(recipe.BuiltIn);
        Assert.Equal(["sing_song", "play_song", "stop_singing"], songs.Tools);
    }

    [Fact]
    public void Every_reply_closes_with_the_pinned_skills_and_a_pinned_tool_skill_is_not_sent_twice()
    {
        var closing = PromptSettings.ReplyClosing(null, null, true, "pass")!;
        Assert.StartsWith(SkillCatalog.DefaultLookIntoIt, closing, StringComparison.Ordinal);
        Assert.Equal(PromptCatalog.DefaultSingingInstructions.Replace("{silent}", "pass", StringComparison.Ordinal),
            SkillSettings.Guidance(null, SkillCatalog.Songs, ("silent", "pass")));

        var pinnedSongs = new SkillSettings { Skills = [SkillSettings.Find(null, SkillCatalog.Songs)! with { Pinned = true }] };
        var prompts = PromptSettings.ForPersonal(null, null, pinnedSongs);
        Assert.Null(SkillSettings.Guidance(prompts, SkillCatalog.Songs, ("silent", "pass")));
        Assert.Contains("call sing_song", PromptSettings.ReplyClosing(prompts, null, true, "pass"), StringComparison.Ordinal);

        // An emptied pinned skill sends nothing.
        var emptied = new SkillSettings { Skills = [SkillSettings.Find(null, SkillCatalog.LookIntoIt)! with { Body = "" }] };
        Assert.DoesNotContain(SkillCatalog.DefaultLookIntoIt, PromptSettings.ReplyClosing(PromptSettings.ForPersonal(null, null, emptied), null, true, "pass"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_skill_keeps_its_limits()
    {
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() =>
            new SkillSettings { Skills = [new Skill { Key = "Bad Key", Name = "x", UseWhen = "", Body = "" }] }.Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() =>
            new SkillSettings { Skills = [new Skill { Key = "a", Name = "", UseWhen = "", Body = "" }] }.Validate());
        var twice = new Skill { Key = "a", Name = "A", UseWhen = "", Body = "" };
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => new SkillSettings { Skills = [twice, twice] }.Validate());
    }

    [Fact]
    public void An_edit_of_a_prompt_that_became_a_skill_moves_to_the_skill_once()
    {
        var prompts = new PromptSettings
        {
            Overrides = new Dictionary<string, string> { ["singing"] = "Sing only on Fridays.", ["reply_length"] = "Be brief." }
        };
        Assert.Equal(["reply_length"], prompts.Overrides.Keys);
        var moved = AppSettings.MoveSkillPrompts(AppSettings.CreateUnconfigured() with { Prompts = prompts });
        Assert.Equal("Sing only on Fridays.", SkillSettings.Find(moved.Skills, SkillCatalog.Songs)!.Body);
        Assert.Equal("Be brief.", moved.Prompts!.Overrides["reply_length"]);
        Assert.Null(moved.Prompts.Moved);

        // A skill already edited keeps its edit; prompts with only moved edits leave the settings.
        var edited = new SkillSettings { Skills = [SkillSettings.Find(null, SkillCatalog.Songs)! with { Body = "Mine." }] };
        var only = new PromptSettings { Overrides = new Dictionary<string, string> { ["singing"] = "Old." } };
        var kept = AppSettings.MoveSkillPrompts(AppSettings.CreateUnconfigured() with { Prompts = only, Skills = edited });
        Assert.Null(kept.Prompts);
        Assert.Equal("Mine.", SkillSettings.Find(kept.Skills, SkillCatalog.Songs)!.Body);
    }

    [Fact]
    public async Task Skills_made_on_one_computer_reach_the_next_one()
    {
        var a = new Pc(Path.Combine(root, "a"), "desktop-a");
        var b = new Pc(Path.Combine(root, "b"), "desktop-b");
        await a.EditAsync(s => s);
        await b.EditAsync(s => s);
        await a.Node.SyncAsync([], true, Start, CancellationToken.None);
        await b.Node.SyncAsync([a.Node.Document], true, Start, CancellationToken.None);
        Assert.True(SettingScopes.IsAccountKey(AppSettingsSections.Skills));

        var recipe = new Skill { Key = "recipes", Name = "Recipes", UseWhen = "When the user asks for a recipe.", Body = "Give one.", Pinned = true };
        await a.EditAsync(s => s with { Skills = SkillSettings.Normalize([.. SkillSettings.All(null), recipe]) });
        var recorded = await a.Node.SyncAsync([], true, Start.AddMinutes(1), CancellationToken.None);
        Assert.Contains(AppSettingsSections.Skills, recorded.Recorded);
        var followed = await b.Node.SyncAsync([a.Node.Document], true, Start.AddMinutes(2), CancellationToken.None);
        Assert.Contains(followed.Applied, x => x.Key == AppSettingsSections.Skills);
        var onB = (await b.Setup.LoadAsync()).Settings!.Skills;
        Assert.Equal(recipe, Assert.Single(onB!.Skills));

        // An older computer still sends an edit of the Singing prompt: it becomes the Songs skill here, not a prompt.
        var older = SharedSettings.Empty.Put(AppSettingsSections.Prompts, """{"overrides":{"singing":"Sing softly."}}""", null, "desktop-old",
            Start.AddMinutes(3));
        await b.Node.SyncAsync([older], true, Start.AddMinutes(4), CancellationToken.None);
        var after = (await b.Setup.LoadAsync()).Settings!;
        Assert.Null(after.Prompts);
        Assert.Equal("Sing softly.", SkillSettings.Find(after.Skills, SkillCatalog.Songs)!.Body);
        Assert.Equal(recipe, after.Skills!.Skills.Single(s => s.Key == "recipes"));
    }

    private sealed class NoVault : ICredentialStore
    {
        public CredentialError Write(CredentialBinding binding, SecretLease secret) => CredentialError.None;
        public CredentialReadResult Read(CredentialBinding binding) => new(CredentialError.Missing, null);
        public CredentialError Delete(CredentialBinding binding) => CredentialError.Missing;
    }

    private sealed class Pc
    {
        internal SetupService Setup { get; }
        internal SharedSettingsNode Node { get; }

        internal Pc(string data, string device)
        {
            Directory.CreateDirectory(data);
            Setup = new SetupService(new SettingsStore(data), new NoVault());
            var sections = new AppSettingsSections(Setup, new NoVault(), data, new LorebookStore(data));
            Node = new SharedSettingsNode(data, device, sections.Sections, sections.Invalidate);
        }

        internal async Task EditAsync(Func<AppSettings, AppSettings> change)
        {
            var loaded = await Setup.LoadAsync();
            var settings = SetupSettings.Begin(loaded.Settings);
            settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.Api } };
            var saved = await Setup.SaveAsync(change(settings), loaded.Revision);
            Assert.True(saved.Save.Saved, saved.Summary);
        }
    }
}

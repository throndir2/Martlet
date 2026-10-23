using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

/// <summary>Companion › Skills › Find skills (<see cref="SkillDirectoryClient"/>): ClawHub's list and search answers, the filters
/// and orders, and a SKILL.md made one of the user's own skills.</summary>
public sealed class SkillDirectoryTests
{
    private const string List = """
        {"items":[
          {"ownerHandle":"pskoett","slug":"self-improving-agent","displayName":"self-improving agent","summary":"Learns.","topics":["context"],
           "stats":{"downloads":487670,"installs":18499,"stars":4004},"createdAt":1767632598365,"updatedAt":1792672926676},
          {"ownerHandle":"steipete","slug":"weather","displayName":"Weather","summary":"Forecasts.","stats":{"downloads":172275,"installs":6521,"stars":430}},
          {"slug":"no-owner","displayName":"Broken"}
        ],"nextCursor":"abc"}
        """;

    private const string Search = """
        {"results":[
          {"source":"clawhub","ownerHandle":"demonkit","slug":"cooking-recipe","displayName":"Cooking Recipe","summary":"Recipes.","downloads":1413,
           "official":false,"featured":false,"metrics":{"rolling60DayInstalls":3},
           "native":{"skill":{"isSuspicious":false,"stats":{"installs":40,"stars":2},"createdAt":1773055614388,"updatedAt":1778491788483,"categories":["lifestyle"]}}},
          {"source":"clawhub","ownerHandle":"ivangdavila","slug":"cooking","displayName":"Cooking","summary":"Cooks.","downloads":2684,"official":true,
           "metrics":{"rolling60DayInstalls":30},"native":{"skill":{"isSuspicious":false,"stats":{"installs":68,"stars":9},"createdAt":1770723094144}}},
          {"source":"skills-sh","ownerHandle":"gmgnai","slug":"gmgn-cooking","displayName":"gmgn-cooking","summary":"Trades.","downloads":15591,
           "native":null,"sourceIdentity":{"owner":"gmgnai","repo":"gmgn-skills"},"links":{"source":"https://www.skills.sh/gmgnai/gmgn-skills/gmgn-cooking"},
           "trust":{"upstreamScanners":{"snyk":{"status":"fail"}}}},
          {"source":"elsewhere","ownerHandle":"x","slug":"y"}
        ]}
        """;

    [Fact]
    public void A_list_keeps_ClawHubs_order_and_a_search_is_filtered_and_sorted_here()
    {
        using var list = JsonDocument.Parse(List);
        var listed = SkillDirectoryClient.ParseList(list.RootElement, unflaggedOnly: true);
        Assert.Equal(["self-improving-agent", "weather"], listed.Select(e => e.Slug));
        Assert.Equal(4004, listed[0].Stars);
        Assert.False(listed[0].Flagged);
        Assert.Equal("https://clawhub.ai/steipete/skills/weather", listed[1].Page!.AbsoluteUri);
        Assert.Equal(["self-improving-agent"], SkillDirectoryClient.Arrange(listed, new(MinimumDownloads: 200_000, Sort: SkillDirectorySort.Stars))
            .Select(e => e.Slug));

        using var search = JsonDocument.Parse(Search);
        var found = SkillDirectoryClient.ParseSearch(search.RootElement);
        Assert.Equal(["cooking-recipe", "cooking", "gmgn-cooking"], found.Select(e => e.Slug));
        var sh = found[2];
        Assert.Equal("gmgnai/gmgn-skills", sh.Repository);
        Assert.True(sh.Flagged);
        Assert.Equal("skills.sh", sh.SourceName);
        Assert.Equal(["lifestyle"], found[0].Topics);

        IEnumerable<string> Slugs(SkillDirectoryQuery query) => SkillDirectoryClient.Arrange(found, query).Select(e => e.Slug);
        // Most popular first by default; flagged skills are hidden unless asked for.
        Assert.Equal(["cooking", "cooking-recipe"], Slugs(new("cook")));
        Assert.Equal(["gmgn-cooking", "cooking", "cooking-recipe"], Slugs(new("cook", HideFlagged: false)));
        Assert.Equal(["cooking-recipe", "cooking", "gmgn-cooking"], Slugs(new("cook", SkillDirectorySort.BestMatch, HideFlagged: false)));
        Assert.Equal(["cooking", "cooking-recipe"], Slugs(new("cook", SkillDirectorySort.Trending)));
        Assert.Equal(["cooking-recipe", "cooking"], Slugs(new("cook", SkillDirectorySort.Newest)));
        Assert.Equal(["cooking"], Slugs(new("cook", MinimumDownloads: 2000)));
        Assert.Equal(["gmgn-cooking"], Slugs(new("cook", HideFlagged: false, Source: SkillDirectorySource.SkillsSh)));
    }

    [Fact]
    public void A_SKILL_md_becomes_an_own_skill_with_a_free_key_and_text_that_fits()
    {
        var entry = new SkillDirectoryEntry("clawhub", "steipete", "Weather", "Weather", "Forecasts.", 1, null, null, null, null, null,
            false, false, false, null, null, []);
        var markdown = "\uFEFF---\r\nname: weather\r\ndescription: >\r\n  Get current weather\r\n  and forecasts.\r\nmetadata: {\"a\":1}\r\n---\r\n\r\n# Weather\r\n\r\nUse wttr.in.\u0007\r\n";
        var skill = SkillDirectoryClient.ToSkill(entry, markdown, ["weather", "weather-2"], out var shortened);
        Assert.False(shortened);
        Assert.Equal("weather-3", skill.Key);
        Assert.Equal("Weather", skill.Name);
        Assert.Equal("Get current weather and forecasts.", skill.UseWhen);
        Assert.Equal("# Weather\n\nUse wttr.in.", skill.Body);
        Assert.False(skill.Pinned);
        skill.Validate();

        var longText = "---\nname: x\ndescription: \"" + new string('w', 300) + "\"\n---\n" +
            string.Join("\n", Enumerable.Range(0, 400).Select(i => $"Line {i}: " + new string('a', 30)));
        var cut = SkillDirectoryClient.ToSkill(entry with { Name = new string('N', 90) }, longText, [], out shortened);
        Assert.True(shortened);
        Assert.True(cut.Body.Length <= PromptSettings.MaximumTextCharacters);
        Assert.EndsWith(new string('a', 30), cut.Body, StringComparison.Ordinal);
        Assert.Equal(SkillSettings.MaximumNameCharacters, cut.Name.Length);
        Assert.True(cut.UseWhen.Length <= SkillSettings.MaximumUseWhenCharacters);
        new SkillSettings { Skills = [cut] }.Validate();
    }

    [Fact]
    public void New_keys_are_valid_and_not_used()
    {
        Assert.Equal("my-skill-v2", SkillDirectoryClient.NewKey("My_Skill v2!", []));
        Assert.Equal("skill", SkillDirectoryClient.NewKey("技能", []));
        Assert.Equal("songs-2", SkillDirectoryClient.NewKey("songs", SkillSettings.All(null).Select(s => s.Key)));
        var longKey = SkillDirectoryClient.NewKey(new string('k', 60), [new string('k', 40)]);
        Assert.True(SkillSettings.IsKey(longKey));
        Assert.EndsWith("-2", longKey, StringComparison.Ordinal);
    }
}

using Martlet.Conversation.Guides;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class ProgramKnowledgeTests : IDisposable
{
    private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("martlet-program-knowledge-");

    public void Dispose() => folder.Delete(recursive: true);

    [Fact]
    public async Task An_unknown_program_is_looked_up_once_and_what_was_learned_is_kept()
    {
        var search = new Search();
        var thinker = new Thinker("{\"kind\":\"game\",\"name\":\"Honkai: Star Rail\"}");
        using var knowledge = new ProgramKnowledge(folder.FullName, search, () => thinker);
        Assert.True(knowledge.CanLookUp("StarRail"));

        var found = await knowledge.LookUpAsync("StarRail", "Star Rail", "HoYoVerse SG");

        Assert.Null(found.Problem);
        Assert.Equal(("Honkai: Star Rail", true, KnownProgramsFile.ByWeb), (found.Known!.Name, found.Known.Game, found.Known.By));
        Assert.Equal(["Star Rail HoYoVerse SG"], search.Queries);
        var ask = Assert.Single(thinker.Asks);
        Assert.Equal(ProgramKnowledge.Instructions, ask.Instructions);
        Assert.Contains("Program file: StarRail.exe", ask.Text, StringComparison.Ordinal);
        Assert.Contains("(hsr.example)", ask.Text, StringComparison.Ordinal);
        Assert.False(knowledge.CanLookUp("StarRail"));
        using var again = new ProgramKnowledge(folder.FullName, search, () => thinker);
        Assert.Equal("Honkai: Star Rail", again.Find("starrail.exe")?.Name);
    }

    [Fact]
    public async Task A_program_the_member_cant_tell_is_not_kept_and_not_looked_up_again_this_session()
    {
        var thinker = new Thinker("I am not sure.");
        using var knowledge = new ProgramKnowledge(folder.FullName, new Search(), () => thinker);
        var found = await knowledge.LookUpAsync("Mystery", "Mystery", null);
        Assert.Null(found.Known);
        Assert.NotNull(found.Problem);
        Assert.Null(knowledge.Find("Mystery"));
        Assert.False(knowledge.CanLookUp("Mystery"));
        Assert.True(knowledge.CanLookUp("Other"));
    }

    [Fact]
    public void Nothing_is_looked_up_without_a_pool_member_and_the_files_never_replace_the_web_or_the_owner()
    {
        var search = new Search();
        var thinker = new Thinker("{}") { CanThink = false };
        using var knowledge = new ProgramKnowledge(folder.FullName, search, () => thinker);
        Assert.False(knowledge.CanLookUp("StarRail"));
        Assert.False(knowledge.StartLookUp("StarRail", "Star Rail", null));
        Assert.Empty(search.Queries);
        using var none = new ProgramKnowledge(folder.FullName, search, () => null);
        Assert.False(none.CanLookUp("StarRail"));

        knowledge.Remember("StarRail", "Honkai: Star Rail", true, KnownProgramsFile.ByWeb);
        Assert.Equal("Honkai: Star Rail", knowledge.Remember("StarRail", "Star Rail", true, KnownProgramsFile.ByFiles)?.Name);
        knowledge.Remember("PaintStudio", "Paint Studio", false, KnownProgramsFile.ByOwner);
        Assert.False(knowledge.Remember("PaintStudio", "Paint Studio", true, KnownProgramsFile.ByWeb)!.Game);
    }

    [Theory]
    [InlineData("{\"kind\":\"game\",\"name\":\"Honkai: Star Rail\"}", true, "Honkai: Star Rail")]
    [InlineData("Here you go: {\"kind\": \"APP\", \"name\": \"Paint Studio\"} done", false, "Paint Studio")]
    [InlineData("{\"kind\":\"game\"}", true, null)]
    public void The_answer_is_read_from_its_json(string answer, bool game, string? name) =>
        Assert.Equal<(bool, string?)?>((game, name), ProgramKnowledge.Parse(answer));

    [Theory]
    [InlineData("{\"kind\":\"unknown\"}")]
    [InlineData("{\"kind\":3}")]
    [InlineData("game")]
    [InlineData("")]
    public void An_answer_that_doesnt_tell_is_nothing(string answer) => Assert.Null(ProgramKnowledge.Parse(answer));

    [Fact]
    public void The_search_names_the_maker_only_when_the_name_doesnt()
    {
        Assert.Equal("Star Rail HoYoVerse SG", ProgramKnowledge.Query("Star Rail", "HoYoVerse SG"));
        Assert.Equal("Steam by Valve", ProgramKnowledge.Query("Steam by Valve", "Valve"));
        Assert.Equal("Tool", ProgramKnowledge.Query("Tool", null));
    }

    private sealed class Search : IWebSearch
    {
        private readonly List<string> queries = [];
        public IReadOnlyList<string> Queries { get { lock (queries) return [.. queries]; } }

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            lock (queries) queries.Add(query);
            return Task.FromResult<IReadOnlyList<WebSearchResult>>(
                [new("Honkai: Star Rail - Official Site", "https://hsr.example/", "A space fantasy RPG.")]);
        }
    }

    private sealed class Thinker(string answer) : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink { get; set; } = true;
        public IReadOnlyList<GuideThinkingAsk> Asks { get { lock (asks) return [.. asks]; } }

        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
        {
            lock (asks) asks.Add(ask);
            return Task.FromResult<(string?, string?)>((answer, null));
        }
    }
}

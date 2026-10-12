using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

/// <summary>Find a character's backup finder: pages on the web that may have a character's model or voice recordings.</summary>
public sealed class CharacterWebFinderTests
{
    public CharacterWebFinderTests() => CharacterWebFinder.Pause = TimeSpan.Zero;

    private sealed class FixtureSearch(Func<string, IReadOnlyList<WebSearchResult>> answer) : IWebSearch
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(answer(query));
        }
    }

    [Fact]
    public async Task ModelPagesFromModelSitesComeFirstAndUnrelatedPagesAreLeftOut()
    {
        var search = new FixtureSearch(query => query.EndsWith("voice lines", StringComparison.Ordinal)
            ? [new("Rem - Voice lines | Re:Zero Wiki", "https://rezero.fandom.com/wiki/Rem/Voice_Lines", "All of Rem's voice lines."),
               new("Rem voice AI generator", "https://elevenlabs.io/rem", "Make Rem say anything."),
               new("Rem Voice Generator - free TTS", "https://example-tts.com/rem", "Rem voice lines from text."),
               new("Rem clips", "https://www.youtube.com/watch?v=abc", "Rem voice clips compilation.")]
            : [new("Rem VRM model | Booth", "https://booth.pm/en/items/123", "Rem 3D model for VRChat, VRM."),
               new("Rem figure", "https://www.amazon.com/rem", "Rem figure 1/7 scale."),
               new("Emilia VRM", "https://booth.pm/en/items/456", "Emilia model."),
               new("Rem MMD model", "https://www.deviantart.com/x/art/rem-mmd", "Rem PMX download."),
               new("Rem model", "http://insecure.example/rem", "Rem VRM.")]);

        var found = await CharacterWebFinder.FindAsync(search, "Rem", models: true, voices: true, default);

        Assert.Equal(["https://booth.pm/en/items/123", "https://www.deviantart.com/x/art/rem-mmd"], found.Models.Select(l => l.Url));
        Assert.Equal("booth.pm", found.Models[0].Site);
        Assert.Equal(["https://rezero.fandom.com/wiki/Rem/Voice_Lines", "https://www.youtube.com/watch?v=abc"], found.Voices.Select(l => l.Url));
        Assert.All(found.Models, l => Assert.Equal(CharacterWebFinder.ModelKind, l.Kind));
        Assert.Empty(found.Notes);
        Assert.Equal(["Rem VRM OR Live2D OR MMD model download", "Rem voice lines"], search.Queries);
        Assert.Contains(found.Searches, s => s.Url == "https://hub.vroid.com/en/search/Rem");
        Assert.Contains(found.Searches, s => s.Kind == CharacterWebFinder.VoiceKind && s.Url.StartsWith("https://www.youtube.com/results", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedSearchIsANoteAndTheSiteSearchesStillComeBack()
    {
        var search = new FixtureSearch(_ => throw new WebResearchException("the search engine refused automated searches"));

        var found = await CharacterWebFinder.FindAsync(search, "Hatsune Miku", models: true, voices: false, default);

        Assert.Empty(found.Models);
        Assert.Empty(found.Voices);
        Assert.Single(found.Notes);
        Assert.Contains("refused", found.Notes[0]);
        Assert.NotEmpty(found.Searches);
        Assert.All(found.Searches, s => Assert.Equal(CharacterWebFinder.ModelKind, s.Kind));
        Assert.Contains(found.Searches, s => s.Url == "https://booth.pm/en/search/Hatsune%20Miku%20VRM");
    }

    [Fact]
    public async Task NamesMatchWithoutTheirPunctuationAndAnEmptySearchIsANote()
    {
        var search = new FixtureSearch(query => query.EndsWith("voice lines", StringComparison.Ordinal) ? []
            : [new("Rem Re Zero MMD model", "https://www.deviantart.com/a/art/rem", "PMX download."),
               new("Ram ReZero VRM", "https://booth.pm/en/items/9", "Ram model.")]);

        var found = await CharacterWebFinder.FindAsync(search, "Rem Re:Zero", models: true, voices: true, default);

        Assert.Equal(["https://www.deviantart.com/a/art/rem"], found.Models.Select(l => l.Url));
        Assert.Contains(found.Notes, n => n.Contains("voice recordings gave no results", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyWhatIsAskedForIsSearched()
    {
        var search = new FixtureSearch(_ => []);
        await CharacterWebFinder.FindAsync(search, "Rem", models: false, voices: true, default);
        Assert.All(search.Queries, q => Assert.Contains("voice", q));
        await Assert.ThrowsAsync<ArgumentException>(() => CharacterWebFinder.FindAsync(search, " ", true, true, default));
    }
}

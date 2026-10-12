using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The memory brief (docs/MEMORY.md, "The memory brief"): written from the remembered facts with whose each is, sent with
/// the first message of each conversation only, held back once a fact it was written from is forgotten until it is written again,
/// and never replaced on its own when the owner wrote it.</summary>
public sealed class MemoryBriefTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-memory-brief-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new(Start);
    private readonly List<MemoryFact> facts = [];
    private readonly Dictionary<string, string> people = new(StringComparer.Ordinal) { ["v1"] = "Sam", ["v2"] = "Alex" };
    private readonly List<string> inputs = [];
    private PromptSettings? prompts;
    private string? answer = "The user loves tea and hiking.\n[Alex] Plays the cello.";

    public void Dispose()
    {
        // Another process (a virus scanner) can hold the brief file a moment after it is written: try again, then leave it.
        for (var attempt = 0; Directory.Exists(directory); attempt++)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch (IOException) { return; }
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private MemoryFact Add(string content, string? voice = null, MemorySourceKind source = MemorySourceKind.Conversation)
    {
        var provenance = source == MemorySourceKind.UserEntry ? MemoryProvenance.UserEntry(Guid.NewGuid(), clock.Now)
            : MemoryProvenance.Conversation(Guid.NewGuid(), clock.Now);
        var fact = new MemoryFact
        {
            Id = Guid.NewGuid(), Revision = 1, Content = content, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now,
            CreatedFrom = provenance, LastModifiedBy = provenance, Retention = MemoryRetention.UntilDeleted(), VoiceId = voice
        };
        facts.Add(fact);
        clock.Now += TimeSpan.FromSeconds(1);
        return fact;
    }

    private MemoryBriefService Service(bool busy = false) => new(directory,
        _ => Task.FromResult<MemoryBriefFacts?>(new([.. facts], people, ["Sam"], prompts)),
        (instructions, text, _) =>
        {
            Assert.Equal(PromptSettings.Text(prompts, PromptCatalog.MemoryBriefWriting), instructions);
            inputs.Add(text);
            return Task.FromResult<(string?, string?)>((answer, answer is null ? "no Thinking pool member can take it" : null));
        }, () => busy, clock, start: false);

    private static TextHistoryMessage User(string text) => new(TextHistoryRole.User, text);

    [Fact]
    public async Task Writes_the_brief_from_whose_facts_and_sends_it_with_the_first_message_only()
    {
        Add("Likes tea.", "v1");
        Add("Plays the cello.", "v2");
        Add("Lives in Oslo.");
        Add("Works nights.", "v9"); // a forgotten voice: left out
        using var brief = Service();

        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));

        var input = Assert.Single(inputs);
        Assert.Contains("The user goes by [Sam]", input);
        Assert.Contains("- [Sam] Likes tea.", input);
        Assert.Contains("- [Alex] Plays the cello.", input);
        Assert.Contains("- Lives in Oslo.", input);
        Assert.DoesNotContain("Works nights", input);
        var saved = MemoryBriefFile.Load(directory);
        Assert.Equal((MemoryBriefFile.ByThinking, 3), (saved.By, saved.Facts));
        Assert.Contains("~v1=Sam", saved.Basis);

        var notes = brief.Notes([], prompts);
        Assert.NotNull(notes);
        Assert.StartsWith(MemoryBriefWriter.Label, notes);
        Assert.Contains("[Alex] Plays the cello.", notes);
        Assert.EndsWith(MemoryBriefWriter.EndLabel, notes);
        // The conversation carries it now: the next message goes without it, so the request is what it always was.
        Assert.Null(brief.Notes([User("Hi\n\n[MARTLET_NOTES]\n" + notes + "\n[/MARTLET_NOTES]"), new(TextHistoryRole.Assistant, "Hello!")], prompts));
        // A new conversation gets it again.
        Assert.NotNull(brief.Notes([], prompts));
        Assert.Equal("up to date", await brief.CheckAsync(false, CancellationToken.None));
        Assert.Contains("Written just now from 3 memories", brief.Status(memoryOn: true));
    }

    [Fact]
    public async Task A_forgotten_fact_holds_the_brief_back_until_it_is_written_again_without_it()
    {
        Add("Likes tea.", "v1");
        var secret = Add("Has a surprise party planned.", "v1");
        Add("Lives in Oslo.");
        using var brief = Service();
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));

        facts.Remove(secret);
        Assert.Equal("waiting", await brief.CheckAsync(false, CancellationToken.None));
        Assert.False(brief.Usable);
        Assert.Null(brief.Notes([], prompts));
        Assert.Contains("doesn't send it", brief.Status(memoryOn: true));

        clock.Now += MemoryBriefService.GapAfterLoss;
        answer = "The user likes tea and lives in Oslo, near the sea.";
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));
        Assert.DoesNotContain("surprise", inputs[^1]);
        Assert.True(brief.Usable);
        Assert.Contains("lives in Oslo", brief.Notes([], prompts));
    }

    [Fact]
    public async Task Search_words_keep_the_brief_and_a_replaced_fact_never_goes_in_it()
    {
        Add("Likes tea.", "v1");
        var home = Add("Lives in Portland.");
        Add("Plays the cello.", "v2");
        using var brief = Service();
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));

        // Tidying memory gives the facts search words: a new revision, the same words, so the brief stays as it is.
        for (var i = 0; i < facts.Count; i++)
            facts[i] = facts[i] with { Revision = facts[i].Revision + 1, SearchWords = "words, for, search" };
        Assert.Equal("up to date", await brief.CheckAsync(false, CancellationToken.None));
        Assert.True(brief.Usable);

        // A correction keeps the old version as history: the brief it was written from waits, and history never goes in it.
        var index = facts.FindIndex(f => f.Id == home.Id);
        facts[index] = facts[index] with { Revision = 3, Content = "Lives in Seattle.", SearchWords = null };
        facts.Add(home with { Id = Guid.NewGuid(), ReplacedAtUtc = clock.Now, ReplacedBy = home.Id });
        Assert.Equal("waiting", await brief.CheckAsync(false, CancellationToken.None));
        clock.Now += MemoryBriefService.GapAfterLoss;
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));
        Assert.Contains("- Lives in Seattle.", inputs[^1]);
        Assert.DoesNotContain("Portland", inputs[^1]);
    }

    [Fact]
    public async Task It_waits_for_enough_facts_and_new_facts_and_never_reads_while_the_conversation_is_busy()
    {
        Add("Likes tea.");
        Add("Lives in Oslo.");
        using (var busy = Service(busy: true))
            Assert.Equal("busy", await busy.CheckAsync(false, CancellationToken.None));
        using var brief = Service();
        Assert.Equal("too few facts", await brief.CheckAsync(false, CancellationToken.None));
        Assert.Null(brief.Notes([], prompts));

        Add("Has a dog named Rex.");
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));
        Add("Started a new job.");
        Add("Learns Spanish.");
        Assert.Equal("up to date", await brief.CheckAsync(false, CancellationToken.None));
        Add("Runs on Sundays.");
        // Three new facts make it due, within the limits: not again within ten minutes of the last.
        Assert.Equal("waiting", await brief.CheckAsync(false, CancellationToken.None));
        clock.Now += MemoryBriefService.Gap;
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));
        Assert.Equal(6, MemoryBriefFile.Load(directory).Facts);
    }

    [Fact]
    public async Task The_owners_own_text_stays_and_off_sends_and_writes_nothing()
    {
        Add("Likes tea.");
        Add("Lives in Oslo.");
        Add("Has a dog named Rex.");
        using var brief = Service();
        Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));

        Assert.Null(brief.SaveOwn("I'm Sam. I love tea and my dog Rex.\r\nCall me Sammy."));
        var own = MemoryBriefFile.Load(directory);
        Assert.Equal((MemoryBriefFile.ByOwner, "I'm Sam. I love tea and my dog Rex.\nCall me Sammy."), (own.By, own.Text));
        Add("Started a new job.");
        Add("Learns Spanish.");
        Add("Runs on Sundays.");
        clock.Now += MemoryBriefService.Gap;
        Assert.Equal("the owner's", await brief.CheckAsync(false, CancellationToken.None));
        Assert.Contains("Call me Sammy.", brief.Notes([], prompts));
        Assert.NotNull(brief.SaveOwn(new string('a', MemoryBriefFile.MaximumCharacters + 1)));

        // Write it again replaces the owner's text on request.
        Assert.Equal("written", await brief.WriteNowAsync());
        Assert.Equal(MemoryBriefFile.ByThinking, MemoryBriefFile.Load(directory).By);

        Assert.Null(brief.SetOn(false));
        Assert.False(MemoryBriefFile.Load(directory).On);
        Assert.Null(brief.Notes([], prompts));
        Assert.Equal("off", await brief.CheckAsync(false, CancellationToken.None));
        Assert.StartsWith("Off:", brief.Status(memoryOn: true));
    }

    [Fact]
    public async Task An_emptied_prompt_or_a_failed_model_writes_no_brief()
    {
        Add("Likes tea.");
        Add("Lives in Oslo.");
        Add("Has a dog named Rex.");
        prompts = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.MemoryBriefWriting] = "" } };
        using (var brief = Service())
        {
            Assert.Equal("no prompt", await brief.CheckAsync(false, CancellationToken.None));
            Assert.Contains("Writing the memory brief is empty", brief.Status(memoryOn: true));
        }
        prompts = null;
        answer = null;
        using (var brief = Service())
        {
            Assert.Equal("failed", await brief.CheckAsync(false, CancellationToken.None));
            Assert.Contains("no Thinking pool member can take it", brief.Status(memoryOn: true));
            Assert.Null(MemoryBriefFile.Load(directory).Text);
        }
        // The notes prompt emptied: the brief is written but never sent.
        answer = "The user loves tea and has a dog named Rex.";
        using (var brief = Service())
        {
            Assert.Equal("written", await brief.CheckAsync(false, CancellationToken.None));
            Assert.Null(brief.Notes([], new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.MemoryBrief] = "" } }));
        }
    }

    [Fact]
    public async Task The_reply_carries_the_brief_in_its_notes_once_and_the_start_of_every_request_stays_the_same()
    {
        await using var fixture = await LiveFixture.Create(data: true);
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        MemoryBriefFile.Save(fixture.DirectoryPath, new MemoryBriefFile
        {
            Text = "The user loves green tea.\n[Alex] Plays the cello.", By = MemoryBriefFile.ByThinking, WrittenAt = DateTimeOffset.UtcNow, Facts = 2
        });
        fixture.Controller.MemoryBrief!.Followed();

        fixture.Answer("Hi!");
        var first = fixture.Start("Hello there.");
        await fixture.Finish(first);
        Assert.Equal("runtime.Completed", first.Status.Code);
        string instructions;
        using (var body = System.Text.Json.JsonDocument.Parse(fixture.Llm.Body))
        {
            instructions = body.RootElement.GetProperty("instructions").GetString()!;
            Assert.DoesNotContain(MemoryBriefWriter.Label, instructions);
            var users = UserTexts(body);
            Assert.Contains(MemoryBriefWriter.Label, users[^1]);
            Assert.Contains("[Alex] Plays the cello.", users[^1]);
            Assert.True(users[^1].IndexOf(MemoryBriefWriter.Label, StringComparison.Ordinal) > users[^1].IndexOf("[MARTLET_NOTES]", StringComparison.Ordinal));
        }

        fixture.Answer("Green tea, of course.");
        var second = fixture.Start("What do I like to drink?");
        await fixture.Finish(second);
        Assert.Equal("runtime.Completed", second.Status.Code);
        using (var body = System.Text.Json.JsonDocument.Parse(fixture.Llm.Body))
        {
            Assert.Equal(instructions, body.RootElement.GetProperty("instructions").GetString());
            var users = UserTexts(body);
            Assert.DoesNotContain(MemoryBriefWriter.Label, users[^1]);
            // The first message is carried exactly as it was sent, with the brief in its notes.
            Assert.Contains(MemoryBriefWriter.Label, users[0]);
        }
        Assert.Equal(1, fixture.Controller.MemoryBrief.Sent);

        static string[] UserTexts(System.Text.Json.JsonDocument body) => [.. body.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: System.Text.Json.JsonValueKind.String } text ? text.GetString()!
                : string.Join("\n", item.GetProperty("content").EnumerateArray().Where(part => part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString())))];
    }

    [Fact]
    public void Clean_keeps_plain_lines_and_drops_what_isnt_a_brief()
    {
        Assert.Null(MemoryBriefWriter.Clean("NONE"));
        Assert.Null(MemoryBriefWriter.Clean("Too short."));
        Assert.Equal("The user loves tea.\n[Alex] Plays the cello every day.",
            MemoryBriefWriter.Clean("<think>who is this</think>\n## **The user loves tea.**\n```\n[Alex] Plays the cello every day.\n[MARTLET_MEMORY_BRIEF]"));
        var longer = MemoryBriefWriter.Clean(string.Join('\n', Enumerable.Repeat("The user likes long walks in the park.", 80)))!;
        Assert.True(longer.Length <= MemoryBriefFile.MaximumCharacters);
        Assert.Null(MemoryBriefFile.Parse("{\"schema_version\":2}"));
    }
}

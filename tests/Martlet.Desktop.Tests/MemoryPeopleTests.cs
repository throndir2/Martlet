using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Conversation.Tests;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Desktop;
using Martlet.Memory;
using Martlet.Providers.Tests;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Whose memories: facts that belong to a person Martlet knows by voice.</summary>
public sealed class MemoryPeopleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan by) => now += by;
    }

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    /// <summary>Sam (named, the owner), a voice with no name yet, and a third voice merged into Sam.</summary>
    private static (VoiceRoster Roster, KnownVoice Sam, KnownVoice Other, string Merged) Roster()
    {
        var roster = VoiceRoster.Empty;
        (roster, var sam) = roster.Add(Print(1), 3, "desk", Start);
        (roster, var other) = roster.Add(Print(2), 3, "desk", Start);
        (roster, var twin) = roster.Add(Print(3), 3, "desk", Start);
        roster = roster.SetNames(sam!.Id, "Sam", [], "desk", Start).SetOwner(sam.Id, true, "desk", Start)
            .Join(twin!.Id, sam.Id, "desk", Start);
        return (roster, roster.Resolve(sam.Id)!, roster.Resolve(other!.Id)!, twin.Id);
    }

    private static MemoryFact Fact(string content, string? voiceId)
    {
        var provenance = MemoryProvenance.Conversation(Guid.NewGuid(), Start);
        return new()
        {
            Id = Guid.NewGuid(), Revision = 1, Content = content, CreatedAtUtc = Start, UpdatedAtUtc = Start,
            CreatedFrom = provenance, LastModifiedBy = provenance, Retention = MemoryRetention.UntilDeleted(), VoiceId = voiceId
        };
    }

    [Fact]
    public void PeopleFollowMergesAndNameForgottenVoices()
    {
        var (roster, sam, other, merged) = Roster();
        Assert.Equal("Sam", MemoryPeople.Label(sam.Id, roster));
        Assert.Equal(other.Tag, MemoryPeople.Label(other.Id, roster));
        Assert.Equal("Sam", MemoryPeople.Label(merged, roster));
        Assert.Equal(MemoryPeople.Forgotten, MemoryPeople.Label("0123456789abcdef", roster));
        Assert.Equal(MemoryPeople.Someone, MemoryPeople.Label(sam.Id, null));
        Assert.Null(MemoryPeople.Label(null, roster));
        Assert.Equal(sam.Id, MemoryPeople.Canonical(merged, roster));
        Assert.Equal(new[] { merged, sam.Id }.Order(), MemoryPeople.Ids(sam, roster)!.Order());
        Assert.Null(MemoryPeople.Ids(null, roster));
    }

    [Fact]
    public void RecalledFactsSayWhoseTheyAreAndExplainItOnlyWhenOneBelongsToSomeone()
    {
        var (roster, sam, _, _) = Roster();
        var samFact = Fact("Likes tea.", sam.Id);
        var everyone = Fact("The wifi router is upstairs.", null);
        var gone = Fact("Plays chess.", "0123456789abcdef");
        var people = MemoryPeople.Labels([samFact, everyone, gone], roster);

        Assert.Equal("[Sam] Likes tea. (from conversation 2026-10-01)", MemoryPromptContext.Line(samFact, people));
        Assert.Equal("The wifi router is upstairs. (from conversation 2026-10-01)", MemoryPromptContext.Line(everyone, people));
        Assert.StartsWith("[a forgotten voice] Plays chess.", MemoryPromptContext.Line(gone, people));
        var whose = PromptCatalog.Default(PromptCatalog.MemoryPeople);
        Assert.Contains(whose, MemoryPromptContext.Instructions([samFact, everyone], null, people));
        Assert.DoesNotContain(whose, MemoryPromptContext.Instructions([everyone], null, people));
        // An emptied prompt sends nothing; the names still say whose each fact is.
        var emptied = PromptSettings.Normalize(new Dictionary<string, string> { [PromptCatalog.MemoryPeople] = "" });
        var text = MemoryPromptContext.Instructions([samFact], emptied, people);
        Assert.DoesNotContain(whose, text);
        Assert.Contains("- [Sam] Likes tea.", text);
    }

    [Fact]
    public void RememberLinesBelongToTheSpeakerUnlessAnotherVoiceHeardIsNamed()
    {
        var (_, sam, other, _) = Roster();
        var voices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sam.Tag] = sam.Id, [other.Tag] = other.Id };
        var operations = MemoryCapture.Parse(
            $"REMEMBER: The user's dog is called Biscuit.\nREMEMBER {other.Tag}: Likes jazz.\n- **remember (v99):** Plays chess.\n" +
            $"REMEMBER {other.Tag}: The user's dog is called Biscuit.", 0, voices, sam.Id);
        Assert.Equal(3, operations.Count);
        Assert.Equal(new string?[] { sam.Id, other.Id, sam.Id }, operations.Select(o => o.VoiceId));
        Assert.Equal("Plays chess.", operations[2].Content);

        // The same words for two people are two facts; for one person, one.
        Assert.Equal(2, MemoryCapture.Parse($"REMEMBER: Likes tea.\nREMEMBER {other.Tag}: Likes tea.", 0, voices, sam.Id).Count);
        Assert.Single(MemoryCapture.Parse("REMEMBER: Likes tea.\nREMEMBER: Likes tea!", 0, voices, sam.Id));
        // Nobody recognized: no one's fact.
        Assert.Null(Assert.Single(MemoryCapture.Parse("REMEMBER: Likes tea.", 0)).VoiceId);
    }

    [Fact]
    public void RememberingIsToldWhoseFactsAreAndWhoIsSpeaking()
    {
        var (roster, sam, other, _) = Roster();
        var heard = new HeardVoices([new(sam, VoiceMatchKind.Known, 0.9, 3, false), new(other, VoiceMatchKind.Known, 0.8, 2, false)], false);
        IReadOnlyList<MemoryFact> known = [Fact("The user likes tea.", sam.Id), Fact("The wifi router is upstairs.", null)];

        var prompt = AfterReply.Prompt(known, null, null, null, "[Sam] I love jazz too.", "Nice!", null,
            present: heard, people: MemoryPeople.Labels(known, roster));
        Assert.False(prompt.Continued);
        Assert.Equal(sam.Id, prompt.Voices[sam.Tag]);
        Assert.Equal(other.Id, prompt.Voices[other.Tag]);
        var text = prompt.Input.UserText;
        Assert.Contains("1. [Sam] The user likes tea.", text);
        Assert.Contains("2. The wifi router is upstairs.", text);
        Assert.Contains($"{sam.Tag}: goes by Sam (the one speaking to Martlet)", text);
        Assert.Contains($"A new fact is saved as {sam.Tag}'s, the one speaking to Martlet.", text);
        Assert.Contains("REMEMBER V<number>: <fact>", text);
        Assert.Contains($"User ({sam.Tag}): [Sam] I love jazz too.", text);
        Assert.DoesNotContain("NAME V<number>", prompt.Input.Personality);

        // Nobody recognized: the excerpt is as before voices.
        var plain = AfterReply.Prompt(known, null, null, null, "I love jazz too.", "Nice!", null);
        Assert.Empty(plain.Voices);
        Assert.DoesNotContain("Voices heard", plain.Input.UserText);
        Assert.Contains("User: I love jazz too.", plain.Input.UserText);
    }

    [Fact]
    public async Task RecallPutsTheSpeakersFactsFirstAndRememberingKeepsEachPersonsFactsApart()
    {
        using var scope = new Scope();
        var (roster, sam, other, merged) = Roster();
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        using var memory = new DesktopMemoryService(store, clock,
            (preview, approval, token) => MemoryStore.Open(preview, approval, clock, null, token));
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true, MemoryStoragePolicy.AppLocalData, null);
        var settings = configured.Settings.Memory!;
        async Task Save(string content, string? voice)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await memory.SaveFactAsync(settings.ConfigurationRevision, content, MemoryRetention.UntilDeleted(), voice);
        }
        await Save("Alex likes green tea.", other.Id);
        await Save("Sam likes coffee.", merged);
        await Save("The wifi router is upstairs.", null);
        await Save("Alex plays the cello.", other.Id);

        string[] Contents(DesktopMemoryRecall recall) => recall.Facts.Select(f => f.Content).ToArray();
        // Nothing matches: Sam's facts (under any of Sam's voices) and everyone's come before Alex's, newest first.
        Assert.Equal(new[] { "The wifi router is upstairs.", "Sam likes coffee.", "Alex plays the cello." },
            Contents(await memory.RecallAsync(settings, "zebra", 3, MemoryPeople.Ids(sam, roster))));
        Assert.Equal(new[] { "Alex plays the cello.", "The wifi router is upstairs.", "Sam likes coffee." },
            Contents(await memory.RecallAsync(settings, "zebra", 3)));
        // The best match comes first whoever it belongs to.
        Assert.Equal("Alex likes green tea.", Contents(await memory.RecallAsync(settings, "green tea", 3, MemoryPeople.Ids(sam, roster)))[0]);

        var shown = (await memory.KnownFactsAsync(settings, "coffee", 4, MemoryPeople.Ids(sam, roster))).Facts;
        Assert.Equal("Sam likes coffee.", shown[0].Content);
        var changes = await memory.RememberAsync(settings.ConfigurationRevision, shown,
        [
            new(MemoryCaptureKind.Remember, Content: "Alex likes green tea.", VoiceId: sam.Id),
            new(MemoryCaptureKind.Remember, Content: "Sam likes coffee.", VoiceId: sam.Id),
            new(MemoryCaptureKind.Remember, Content: "The wifi router is upstairs.", VoiceId: other.Id),
        ], id => MemoryPeople.Canonical(id, roster));
        var update = await memory.RememberAsync(settings.ConfigurationRevision, shown,
            [new(MemoryCaptureKind.Update, 1, "Sam likes espresso.", other.Id)], id => MemoryPeople.Canonical(id, roster));

        // Sam's own "green tea" fact is new (Alex's is someone else's); the coffee fact is Sam's already (merged voice); the
        // wifi fact is everyone's already.
        var added = Assert.Single(changes);
        Assert.Equal((MemoryCaptureKind.Remember, "Alex likes green tea.", sam.Id), (added.Kind, added.Content, added.VoiceId));
        // An update keeps whose the fact is.
        Assert.Equal((MemoryCaptureKind.Update, merged), (Assert.Single(update).Kind, update[0].VoiceId));
        var facts = (await memory.InspectAsync(settings.ConfigurationRevision)).Facts;
        Assert.Equal(5, facts.Count);
        Assert.Equal(merged, facts.Single(f => f.Content == "Sam likes espresso.").VoiceId);
        Assert.Equal(sam.Id, facts.Single(f => f.Content == "Alex likes green tea." && f.VoiceId != other.Id).VoiceId);
    }

    [Fact]
    public async Task SpokenFactsBelongToTheSpeakerAndLaterRepliesAreToldWhoseEachIs()
    {
        await using var fixture = await LiveFixture.Create(voices: true);
        var (roster, sam, other, _) = Roster();
        fixture.Voices!.Merge(roster);
        await fixture.EnableMemory();
        var reports = new ConcurrentQueue<MemoryCaptureReport>();
        fixture.Controller.MemoryCaptured += reports.Enqueue;
        var requests = new ConcurrentQueue<string>();
        fixture.Llm.Respond = (_, _) =>
        {
            var body = Decoded(fixture.Llm.Body);
            requests.Enqueue(body);
            return Task.FromResult(TextRecordingHandler.Sse(Harness.Trace(
                body.Contains("long-term memory", StringComparison.Ordinal)
                    ? $"REMEMBER: Sam's dog is called Biscuit.\nREMEMBER {other.Tag}: Likes jazz."
                    : "Biscuit is a lovely name.")));
        };
        var heard = new HeardVoices([new(sam, VoiceMatchKind.Known, 0.9, 3, false), new(other, VoiceMatchKind.Known, 0.8, 2, false)], false);

        var first = fixture.Controller.Start("My dog is called Biscuit.", voice: false, microphone: false, approved: true,
            spoken: true, heard: heard);
        await fixture.Finish(first);
        await fixture.FinishRemembering();
        Assert.Equal("runtime.Completed", first.Status.Code);
        var revision = (await fixture.Store.LoadAsync()).Settings!.Memory!.ConfigurationRevision;
        var facts = (await fixture.Memory.InspectAsync(revision)).Facts;
        Assert.Equal(sam.Id, facts.Single(f => f.Content == "Sam's dog is called Biscuit.").VoiceId);
        Assert.Equal(other.Id, facts.Single(f => f.Content == "Likes jazz.").VoiceId);
        var report = Assert.Single(reports);
        Assert.Equal(new string?[] { "Sam", other.Tag }, report.Changes!.Select(c => c.Person));
        Assert.Contains($"A new fact is saved as {sam.Tag}'s", requests.ElementAt(1));

        // Sam asks again: the facts come back saying whose each is, with what that means.
        var second = fixture.Controller.Start("What is my dog called?", voice: false, microphone: false, approved: true,
            spoken: true, heard: new([new(sam, VoiceMatchKind.Known, 0.9, 3, false)], false));
        await fixture.Finish(second);
        await fixture.FinishRemembering();
        var asked = requests.ElementAt(2);
        Assert.Contains("[Sam] Sam's dog is called Biscuit. (from conversation", asked);
        Assert.Contains($"[{other.Tag}] Likes jazz. (from conversation", asked);
        Assert.Contains(PromptCatalog.Default(PromptCatalog.MemoryPeople), asked);

        // A later fact of Sam's is noted on its own: what the names mean was said once already in the conversation sent.
        await fixture.SaveMemoryFact("Sam plays the piano.", sam.Id);
        var third = fixture.Controller.Start("Do I play the piano?", voice: false, microphone: false, approved: true,
            spoken: true, heard: new([new(sam, VoiceMatchKind.Known, 0.9, 3, false)], false));
        await fixture.Finish(third);
        await fixture.FinishRemembering();
        var later = requests.ElementAt(4);
        Assert.Contains("[Sam] Sam plays the piano. (saved by the user", later);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(later,
            System.Text.RegularExpressions.Regex.Escape(PromptCatalog.Default(PromptCatalog.MemoryPeople))));

        static string Decoded(byte[] body)
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("instructions").GetString() + "\n" + string.Join("\n",
                json.RootElement.GetProperty("input").EnumerateArray().Select(item => item.GetProperty("content").GetString()));
        }
    }

    [Fact]
    public Task MemoryWindowShowsAndChoosesWhoseFactsAre() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var (roster, sam, other, _) = Roster();
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        using var memory = new DesktopMemoryService(store);
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true, MemoryStoragePolicy.AppLocalData, null);
        var revision = configured.Settings.Memory!.ConfigurationRevision;
        await memory.SaveFactAsync(revision, "Plays the cello.", MemoryRetention.UntilDeleted(), other.Id);
        await memory.SaveFactAsync(revision, "Plays chess.", MemoryRetention.UntilDeleted(), "0123456789abcdef");
        var runner = new SetupOperationRunner();
        // Opened from People for the voice with no name: only its facts show.
        var window = new MemoryWindow(memory, runner, voices: () => roster, person: other.Id) { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var list = Control<ListBox>(window, "FactsList");
            await Until(() => !runner.IsRunning && list.Items.Count == 1);
            Assert.Equal($"Voice {other.Number} · Plays the cello.", list.Items[0]!.ToString());
            Assert.Equal("2 facts remembered: 1 belongs to 1 person Martlet knows by voice, 1 to a forgotten voice. Showing 1.",
                Text(window, "FactStatus"));
            Assert.Equal("MemoryFactStatus", AutomationProperties.GetAutomationId(Control<TextBlock>(window, "FactStatus")));
            Assert.Contains(Control<ComboBox>(window, "PersonFilter").Items.Cast<object>(), o => o.ToString() == "Forgotten voices");

            // A new fact belongs to the voice shown; with all facts shown, to the owner's (you typed it).
            var person = Control<ComboBox>(window, "PersonChoice");
            Assert.Equal($"Voice {other.Number}", person.SelectedItem!.ToString());
            var filter = Control<ComboBox>(window, "PersonFilter");
            filter.SelectedIndex = 0;
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("Sam (you)", person.SelectedItem!.ToString());
            Control<TextBox>(window, "FactContent").Text = "Takes the bus to work.";
            Control<ComboBox>(window, "RetentionChoice").SelectedIndex = 0;
            Click(window, "MemorySaveFact");
            await Until(() => !runner.IsRunning && list.Items.Count == 3);
            Assert.StartsWith("Fact added. 3 facts remembered: 2 belong to 2 people", Text(window, "FactStatus"));
            var bus = list.Items.Cast<MemoryWindow.FactItem>().Single(i => i.Fact.Content == "Takes the bus to work.");
            Assert.Equal(sam.Id, bus.Fact.VoiceId);

            // A forgotten voice's fact keeps that voice when updated, or goes to everyone when chosen.
            list.SelectedItem = list.Items.Cast<MemoryWindow.FactItem>().Single(i => i.Fact.Content == "Plays chess.");
            Assert.Equal("A forgotten voice", person.SelectedItem!.ToString());
            Assert.Contains("Belongs to: A forgotten voice", Text(window, "FactDetails"));
            person.SelectedIndex = 0;
            Click(window, "MemoryEditFact");
            await Until(() => !runner.IsRunning && list.Items.Cast<MemoryWindow.FactItem>().Any(i => i.Fact.Content == "Plays chess." && i.Fact.VoiceId is null));
            Assert.DoesNotContain(filter.Items.Cast<object>(), o => o.ToString() == "Forgotten voices");
            Assert.Equal("Plays chess.", list.Items.Cast<MemoryWindow.FactItem>().Single(i => i.Person is null).ToString());
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task MemoryWindowSearchesAndDeletesOnePersonsFactsOrEverything() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var (roster, sam, other, merged) = Roster();
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        using var memory = new DesktopMemoryService(store);
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true, MemoryStoragePolicy.AppLocalData, null);
        var revision = configured.Settings.Memory!.ConfigurationRevision;
        await memory.SaveFactAsync(revision, "Sam's dog is called Biscuit.", MemoryRetention.UntilDeleted(), sam.Id);
        await memory.SaveFactAsync(revision, "Sam plays the piano.", MemoryRetention.UntilDeleted(), merged);
        await memory.SaveFactAsync(revision, "Plays the cello.", MemoryRetention.UntilDeleted(), other.Id);
        await memory.SaveFactAsync(revision, "The house has a red door.", MemoryRetention.UntilDeleted());
        var runner = new SetupOperationRunner();
        var questions = new List<string>();
        var window = new MemoryWindow(memory, runner, voices: () => roster,
            confirm: (_, text, title) => { questions.Add(title + ": " + text); return true; }) { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var list = Control<ListBox>(window, "FactsList");
            await Until(() => !runner.IsRunning && list.Items.Count == 4);
            var deleteShown = Control<Button>(window, "DeleteShownButton");
            Assert.Equal(Visibility.Collapsed, deleteShown.Visibility);

            // Search finds a fact by its words or by whose it is.
            var search = Control<TextBox>(window, "SearchBox");
            search.Text = "piano";
            Assert.Equal("Sam plays the piano.", Assert.Single(list.Items.Cast<MemoryWindow.FactItem>()).Fact.Content);
            search.Text = "SAM";
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(Visibility.Visible, deleteShown.Visibility);
            Assert.Equal("Delete all 2 found", deleteShown.Content);
            search.Text = "";
            Assert.Equal(4, list.Items.Count);

            // Selecting several shows them together; deleting them asks once and deletes both.
            list.SelectedItems.Add(list.Items.Cast<MemoryWindow.FactItem>().Single(i => i.Fact.Content == "Plays the cello."));
            list.SelectedItems.Add(list.Items.Cast<MemoryWindow.FactItem>().Single(i => i.Fact.VoiceId is null));
            Assert.Equal("2 facts selected", Text(window, "EditorHeading"));
            Assert.Equal(Visibility.Collapsed, Control<Button>(window, "EditFactButton").Visibility);
            Click(window, "MemoryDeleteFact");
            await Until(() => !runner.IsRunning && list.Items.Count == 2);
            Assert.StartsWith("Delete remembered facts: Delete the 2 selected facts?", questions[^1]);

            // Show Sam: their facts (a merged voice's too), and one button deletes them all.
            var filter = Control<ComboBox>(window, "PersonFilter");
            filter.SelectedItem = filter.Items.Cast<object>().Single(o => o.ToString() == "Sam (you)");
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("Delete all of Sam's facts (2)", deleteShown.Content);
            Click(window, "MemoryDeleteShown");
            await Until(() => !runner.IsRunning && list.Items.Count == 0);
            Assert.Contains("all 2 of Sam's facts", questions[^1]);
            Assert.StartsWith("Deleted 2 facts. 0 facts remembered", Text(window, "FactStatus"));

            // Delete everything forgets every fact, whoever's it is.
            await memory.SaveFactAsync(revision, "Likes jazz.", MemoryRetention.UntilDeleted(), other.Id);
            await memory.SaveFactAsync(revision, "The car is blue.", MemoryRetention.UntilDeleted());
            filter.SelectedIndex = 0;
            Click(window, "MemoryReload");
            await Until(() => !runner.IsRunning && list.Items.Count == 2);
            Click(window, "MemoryDeleteAll");
            await Until(() => !runner.IsRunning && list.Items.Count == 0);
            Assert.StartsWith("Delete all memories: Delete everything Martlet remembers (2 facts)", questions[^1]);
            Assert.Empty((await memory.InspectAsync(revision)).Facts);
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public async Task ManageMemoriesFindsRemembersMovesAndForgetsFacts()
    {
        using var scope = new Scope();
        var (roster, sam, other, merged) = Roster();
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        using var memory = new DesktopMemoryService(store);
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true, MemoryStoragePolicy.AppLocalData, null);
        var revision = configured.Settings.Memory!.ConfigurationRevision;
        await memory.SaveFactAsync(revision, "Has a dog called Biscuit.", MemoryRetention.UntilDeleted(), merged);
        await memory.SaveFactAsync(revision, "Plays the cello.", MemoryRetention.UntilDeleted(), sam.Id);
        Task<MemoryToolOutcome> Run(string json, KnownVoice? speaker = null) =>
            MemoryTools.RunAsync(memory, revision, json, roster, speaker, CancellationToken.None);
        static JsonElement[] Found(MemoryToolOutcome outcome) =>
            JsonDocument.Parse(outcome.Result.Output.Split('\n')[0]).RootElement.GetProperty("facts").EnumerateArray().ToArray();

        // find by words, and by person (a merged voice's facts are Sam's).
        var dog = Assert.Single(Found(await Run("""{"action":"find","query":"dog"}""")));
        Assert.Equal("Sam", dog.GetProperty("person").GetString());
        Assert.Equal(2, Found(await Run("""{"action":"find","person":"sam"}""")).Length);
        Assert.Empty(Found(await Run("""{"action":"find","person":"everyone"}""")));

        // remember: "me" is the one speaking; a tag names another voice; a repeat isn't saved twice.
        var remembered = await Run("""{"action":"remember","fact":"Likes jazz.","person":"me"}""", other);
        Assert.False(remembered.Result.IsError);
        Assert.Equal(other.Id, Assert.Single(remembered.Changes).VoiceId);
        Assert.Contains("Already remembered", (await Run($$"""{"action":"remember","fact":"Likes jazz","person":"{{other.Tag}}"}""")).Result.Output);
        var unknown = await Run("""{"action":"remember","fact":"Works at the bakery.","person":"Alex"}""");
        Assert.True(unknown.Result.IsError);
        Assert.Contains("Sam (V", unknown.Result.Output);

        // update: give the cello fact to the other voice and correct its words.
        var cello = Found(await Run("""{"action":"find","query":"cello"}"""))[0].GetProperty("id").GetString()!;
        var moved = await Run($$"""{"action":"update","ids":["{{cello}}"],"fact":"Plays the cello on Sundays.","person":"{{other.Tag}}"}""");
        Assert.False(moved.Result.IsError, moved.Result.Output);
        var facts = (await memory.InspectAsync(revision)).Facts;
        var updated = facts.Single(f => f.Content == "Plays the cello on Sundays.");
        Assert.Equal(other.Id, updated.VoiceId);
        Assert.Equal(MemorySourceKind.Conversation, updated.LastModifiedBy.SourceKind);

        // forget: several ids at once; an unknown id is refused without deleting anything.
        Assert.True((await Run("""{"action":"forget","ids":["ffffffff"]}""")).Result.IsError);
        Assert.Equal(3, (await memory.InspectAsync(revision)).Facts.Count);
        var ids = Found(await Run($$"""{"action":"find","person":"{{other.Tag}}"}""")).Select(f => f.GetProperty("id").GetString()).ToArray();
        Assert.Equal(2, ids.Length);
        var forgot = await Run(JsonSerializer.Serialize(new { action = "forget", ids }));
        Assert.Equal("Forgot 2 facts.", forgot.Result.Output);
        Assert.Equal("Has a dog called Biscuit.", Assert.Single((await memory.InspectAsync(revision)).Facts).Content);
        Assert.True((await Run("""{"action":"explode"}""")).Result.IsError);
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));

    private static void Click(Window window, string id) =>
        Descendants(window).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == id)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Text(Window window, string name) => window.FindName(name) switch
    {
        TextBlock block => block.Text,
        TextBox box => box.Text,
        _ => throw new InvalidOperationException("Required text control missing: " + name)
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "The bounded UI condition was not reached.");
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "memory-people", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Memory;
using Martlet.Providers;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Memories for a game (docs/MEMORY.md): what was said while playing one game is recalled in that game, not in others;
/// facts about the user's life are recalled everywhere.</summary>
public sealed class MemoryActivityTests
{
    private const string Palworld = "gaming/Palworld";
    private const string Elden = "gaming/ELDEN RING";

    [Fact]
    public async Task RecallLeavesOutOtherGamesAndFillsWithThisGamesFacts()
    {
        using var scope = new Scope();
        var (memory, settings) = await OpenAsync(scope);
        using var opened = memory;
        var revision = settings.ConfigurationRevision;
        await memory.SaveFactAsync(revision, "In Palworld, the user's favorite pal is Chillet.", MemoryRetention.UntilDeleted(), activity: Palworld);
        await memory.SaveFactAsync(revision, "In Elden Ring, the user plays a mage.", MemoryRetention.UntilDeleted(), activity: Elden);
        await memory.SaveFactAsync(revision, "The user likes co-op games.", MemoryRetention.UntilDeleted(), activity: MemoryActivity.Gaming);
        await memory.SaveFactAsync(revision, "The user's cat is called Miso.", MemoryRetention.UntilDeleted());

        static string[] Contents(DesktopMemoryRecall recall) => [.. recall.Facts.Select(f => f.Content)];
        var elden = Contents(await memory.RecallAsync(settings, "What is my favorite?", 12, activity: Elden));
        Assert.DoesNotContain("In Palworld, the user's favorite pal is Chillet.", elden);
        Assert.Equal(["In Elden Ring, the user plays a mage.", "The user likes co-op games.", "The user's cat is called Miso."], elden);
        var palworld = Contents(await memory.RecallAsync(settings, "What is my favorite?", 12, activity: Palworld));
        Assert.Contains("In Palworld, the user's favorite pal is Chillet.", palworld);
        Assert.DoesNotContain("In Elden Ring, the user plays a mage.", palworld);
        Assert.Equal(["The user's cat is called Miso."], Contents(await memory.RecallAsync(settings, "What is my favorite?", 12)));
        // Naming the game recalls its facts outside it.
        Assert.Contains("In Palworld, the user's favorite pal is Chillet.",
            Contents(await memory.RecallAsync(settings, "Which Palworld pal do I like?", 12)));
        // Remembering is shown only the facts that apply too.
        Assert.DoesNotContain("In Elden Ring, the user plays a mage.",
            Contents(await memory.KnownFactsAsync(settings, "mage", 10, activity: Palworld)));
    }

    [Fact]
    public async Task RememberingKeepsNewFactsForTheirGame()
    {
        using var scope = new Scope();
        var (memory, settings) = await OpenAsync(scope);
        using var opened = memory;
        var revision = settings.ConfigurationRevision;
        await memory.SaveFactAsync(revision, "The user's favorite pal is Chillet.", MemoryRetention.UntilDeleted(), activity: Palworld);
        await memory.SaveFactAsync(revision, "The user likes tea.", MemoryRetention.UntilDeleted());
        var operations = MemoryCapture.Parse("""
            REMEMBER HERE: The user's favorite pal is Chillet.
            REMEMBER: The user built a base by the lake.
            REMEMBER GAMES: The user prefers co-op games.
            """, 0, activity: "gaming/Ark");
        Assert.Equal(["gaming/Ark", "gaming/Ark", MemoryActivity.Gaming], operations.Select(o => o.Activity));
        var everywhere = Assert.Single(MemoryCapture.Parse("REMEMBER EVERYWHERE: The user likes tea!", 0, activity: "gaming/Ark"));
        Assert.Null(everywhere.Activity);

        var changes = await memory.RememberAsync(revision, [], [.. operations, everywhere]);
        // The same words for another game are a new fact; the same fact for everywhere is already remembered.
        Assert.Equal(3, changes.Count);
        var facts = (await memory.InspectAsync(revision)).Facts;
        Assert.Equal(2, facts.Count(f => f.Content == "The user's favorite pal is Chillet."));
        Assert.Equal("gaming/Ark", facts.Single(f => f.Content == "The user built a base by the lake.").Activity);
        Assert.Equal(MemoryActivity.Gaming, facts.Single(f => f.Content == "The user prefers co-op games.").Activity);
        Assert.Single(facts, f => f.Content.StartsWith("The user likes tea", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsingAndPromptsSayWhereFactsMatter()
    {
        var voices = new Dictionary<string, string> { ["V2"] = "voice-2" };
        var parsed = MemoryCapture.Parse("""
            REMEMBER V2 HERE: Sam's favorite pal is Anubis.
            REMEMBER (GAMES) V2: Sam plays on a controller.
            REMEMBER - Game night is on Fridays.
            """, 0, voices, "speaker", Palworld);
        Assert.Equal([("voice-2", Palworld), ("voice-2", MemoryActivity.Gaming), ("speaker", Palworld)],
            parsed.Select(o => (o.VoiceId, o.Activity)));
        Assert.Equal("Game night is on Fridays.", parsed[2].Content);
        // Without a game every new fact is for everywhere, whatever the line says.
        Assert.All(MemoryCapture.Parse("REMEMBER HERE: The user likes tea.\nREMEMBER: The user has a cat.", 0), o => Assert.Null(o.Activity));

        var playing = MemoryCapture.Prompt(null, null, "Chillet is my favorite!", "Cute pick!", [], activity: Palworld).Input.UserText;
        Assert.Contains("The user is playing Palworld now.", playing);
        Assert.Contains("REMEMBER HERE", playing);
        Assert.DoesNotContain("REMEMBER HERE", MemoryCapture.Prompt(null, null, "Hi", "Hello", []).Input.UserText);
        Assert.DoesNotContain("REMEMBER HERE",
            MemoryCapture.Prompt(null, null, "Hi", "Hello", [], activity: MemoryActivity.Gaming).Input.UserText);
        // The owner can empty the prompt (Companion › Prompts) to stop asking.
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.MemoryCaptureGame] = "" } };
        Assert.DoesNotContain("REMEMBER HERE", MemoryCapture.Prompt(null, null, "Hi", "Hello", [], emptied, activity: Palworld).Input.UserText);

        var known = Fact("In Palworld, the user's base is by the lake.", Palworld);
        var continued = AfterReply.Prompt([known], null, null, null, "I moved my base.", "Nice!", null,
            new BoundedTextInput("I moved my base.", "You are Martlet."), _ => true, activity: Palworld);
        Assert.True(continued.Continued);
        Assert.Contains("1. In Palworld, the user's base is by the lake. (while playing Palworld)", continued.Input.UserText);
        Assert.Contains("The user is playing Palworld now.", continued.Input.UserText);

        Assert.EndsWith("(from conversation 2026-10-01; while playing Palworld)", MemoryPromptContext.Line(known));
        Assert.EndsWith("(from conversation 2026-10-01)", MemoryPromptContext.Line(Fact("The user likes tea.", null)));
    }

    [Fact]
    public void TheBriefLeavesOutGameFacts()
    {
        var now = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        Assert.True(MemoryBriefWriter.Current(Fact("The user likes tea.", null), now));
        Assert.False(MemoryBriefWriter.Current(Fact("In Palworld, the user's favorite pal is Chillet.", Palworld), now));
        Assert.False(MemoryBriefWriter.Current(Fact("The user likes co-op games.", MemoryActivity.Gaming), now));
    }

    [Fact]
    public async Task ManageMemoriesSaysAndChangesWhereFactsMatter()
    {
        using var scope = new Scope();
        var (memory, settings) = await OpenAsync(scope);
        using var opened = memory;
        var revision = settings.ConfigurationRevision;
        Task<MemoryToolOutcome> Run(string json, string? now = Palworld) =>
            MemoryTools.RunAsync(memory, revision, json, null, null, CancellationToken.None, activity: now);

        var here = await Run("""{"action":"remember","fact":"In Palworld, Chillet is the user's favorite.","where":"here"}""");
        Assert.False(here.Result.IsError, here.Result.Output);
        Assert.EndsWith("while playing Palworld.", here.Result.Output);
        Assert.Equal(Palworld, Assert.Single(here.Changes).Activity);
        Assert.Null(Assert.Single((await Run("""{"action":"remember","fact":"The user likes tea."}""")).Changes).Activity);
        Assert.Equal("gaming/Stardew Valley",
            Assert.Single((await Run("""{"action":"remember","fact":"The farm has cows.","where":"playing Stardew Valley"}""")).Changes).Activity);
        Assert.True((await Run("""{"action":"remember","fact":"The user is tired.","where":"here"}""", null)).Result.IsError);

        var found = JsonDocument.Parse((await Run("""{"action":"find","query":"Chillet"}""", null)).Result.Output.Split('\n')[0])
            .RootElement.GetProperty("facts")[0];
        Assert.Equal("while playing Palworld", found.GetProperty("where").GetString());
        var moved = await Run($$"""{"action":"update","ids":["{{found.GetProperty("id").GetString()}}"],"where":"everywhere"}""");
        Assert.False(moved.Result.IsError, moved.Result.Output);
        Assert.Null((await memory.InspectAsync(revision)).Facts.Single(f => f.Content.Contains("Chillet", StringComparison.Ordinal)).Activity);

        Assert.Equal((true, MemoryActivity.Gaming, (string?)null), MemoryTools.Where("games", null));
        Assert.Equal((false, (string?)null, (string?)null), MemoryTools.Where(null, Palworld));
    }

    [Fact]
    public async Task WhileYouPlayGoesInTheNotesOncePerGameAndNeverChangesTheInstructions()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var configuration = LiveConversationConfiguration.From(loaded)!;
        ConversationRequest Ask(IReadOnlyList<TextHistoryMessage> history, string? activity, LiveConversationConfiguration? with = null) =>
            (with ?? configuration).Request(new("What now?"), false, history, null, null, out _, out _, out _, activity: activity);

        var plain = Ask([], null);
        var playing = Ask([], Palworld);
        Assert.Equal(plain.Input.Personality, playing.Input.Personality);
        Assert.Contains("The user is playing Palworld now", playing.Input.Notes);
        Assert.DoesNotContain("playing", plain.Input.Notes ?? "");
        Assert.Equal("While you play", Assert.Single(playing.Parts!.Notes, part => part.Text.Contains("Palworld", StringComparison.Ordinal)).Name);

        // Carried by the conversation sent, it isn't sent again; another game is.
        TextHistoryMessage[] history = [new(TextHistoryRole.User, playing.Input.KeptUserText), new(TextHistoryRole.Assistant, "Go catch pals!")];
        Assert.DoesNotContain("right now", Ask(history, Palworld).Input.Notes ?? "");
        Assert.Contains("The user is playing ELDEN RING now", Ask(history, Elden).Input.Notes);
        Assert.DoesNotContain("right now", Ask([], MemoryActivity.Gaming).Input.Notes ?? "");

        var emptied = LiveConversationConfiguration.From(loaded with { Settings = loaded.Settings! with { Skills = SkillSettings.Edited(SkillCatalog.WhilePlaying, "")!.Skills } })!;
        Assert.DoesNotContain("right now", Ask([], Palworld, emptied).Input.Notes ?? "");
        fixture.NoEffects();
    }

    [Fact]
    public Task MemoryWindowChoosesWhereAFactMatters() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var (memory, settings) = await OpenAsync(scope);
        using var opened = memory;
        await memory.SaveFactAsync(settings.ConfigurationRevision, "In Palworld, the user's favorite pal is Chillet.",
            MemoryRetention.UntilDeleted(), activity: Palworld);
        var runner = new SetupOperationRunner();
        var window = new MemoryWindow(memory, runner) { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var list = Control<ListBox>(window, "FactsList");
            await Until(() => !runner.IsRunning && list.Items.Count == 1);
            var item = Assert.IsType<MemoryWindow.FactItem>(list.Items[0]);
            Assert.Contains("while playing Palworld", item.Caption);
            Assert.Contains("1 kept for 1 game or activity", Text(window, "FactStatus"));
            var where = Control<ComboBox>(window, "WhereChoice");
            Assert.Equal("MemoryWhere", AutomationProperties.GetAutomationId(where));
            Assert.Equal(["Everywhere", "While gaming (any game)", "While playing Palworld"], where.Items.Cast<object>().Select(o => o.ToString()));
            Assert.Equal("Everywhere", where.SelectedItem!.ToString());

            list.SelectedIndex = 0;
            Assert.Equal("While playing Palworld", where.SelectedItem!.ToString());
            Assert.Contains("Matters: while playing Palworld", Text(window, "FactDetails"));
            where.SelectedIndex = 1;
            Click(window, "MemoryEditFact");
            await Until(() => !runner.IsRunning && list.Items.Cast<MemoryWindow.FactItem>().Single().Fact.Activity == MemoryActivity.Gaming);

            Click(window, "MemoryNewFact");
            Control<TextBox>(window, "FactContent").Text = "The user's cat is called Miso.";
            Assert.Equal("Everywhere", where.SelectedItem!.ToString());
            Click(window, "MemorySaveFact");
            await Until(() => !runner.IsRunning && list.Items.Count == 2);
            Assert.Null(list.Items.Cast<MemoryWindow.FactItem>().Single(i => i.Fact.Content.Contains("Miso", StringComparison.Ordinal)).Fact.Activity);
        }
        finally
        {
            window.Close();
        }
    });

    private static MemoryFact Fact(string content, string? activity)
    {
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var provenance = MemoryProvenance.Conversation(Guid.NewGuid(), at);
        return new()
        {
            Id = Guid.NewGuid(), Revision = 1, Content = content, CreatedAtUtc = at, UpdatedAtUtc = at, CreatedFrom = provenance,
            LastModifiedBy = provenance, Retention = MemoryRetention.UntilDeleted(), Activity = activity
        };
    }

    private static async Task<(DesktopMemoryService Memory, MemorySettings Settings)> OpenAsync(Scope scope)
    {
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        var memory = new DesktopMemoryService(store);
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true, MemoryStoragePolicy.AppLocalData, null);
        return (memory, configured.Settings.Memory!);
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
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "memory-activity", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

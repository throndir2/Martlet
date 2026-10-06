using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Conversation;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The record of conversations (Companion › Memory › Conversation history) through the real controller and window.</summary>
public sealed class ConversationHistoryDesktopTests
{
    [Fact]
    public async Task ExchangesAreRecordedAndAnEarlierConversationIsRecalledOnlyWhenMentioned()
    {
        await using var fixture = await LiveFixture.Create(history: true);
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        var history = fixture.History!;
        fixture.Answer("Radiohead is a great choice.");
        var first = fixture.Start("My favorite band is Radiohead.");
        await fixture.Finish(first);
        Assert.Equal("runtime.Completed", first.Status.Code);
        await history.Idle;
        await history.Store.LoadAsync();
        var recorded = Assert.Single(history.Store.Conversations());
        var exchange = Assert.Single(history.Store.Exchanges(recorded.Id));
        Assert.Equal(("My favorite band is Radiohead.", "Radiohead is a great choice.", HistoryInputKind.Typed),
            (exchange.User, exchange.Reply, exchange.Kind));
        Assert.Equal(fixture.Controller.ConversationId, recorded.Id);
        Assert.DoesNotContain(PastConversations.Label, Notes(fixture.Llm.Body) ?? "");

        // Refresh context starts a new conversation; the earlier one comes back once it is mentioned.
        Assert.True(fixture.Controller.ForgetContext());
        Assert.NotEqual(recorded.Id, fixture.Controller.ConversationId);
        fixture.Answer("Radiohead, of course.");
        var asked = fixture.Start("Do you remember my favorite band?");
        await fixture.Finish(asked);
        Assert.Equal(1, asked.PastExchanges);
        var askedNotes = Notes(fixture.Llm.Body);
        Assert.NotNull(askedNotes);
        Assert.Contains("[" + PastConversations.Label + "]", askedNotes);
        Assert.Contains("User: \"My favorite band is Radiohead.\" Martlet: \"Radiohead is a great choice.\"",
            askedNotes.Replace("The user:", "User:", StringComparison.Ordinal));
        var askedInstructions = Instructions(fixture.Llm.Body);

        // An ordinary message gets nothing, and its request starts exactly like the one before.
        fixture.Answer("Four.");
        var ordinary = fixture.Start("What is two plus two?");
        await fixture.Finish(ordinary);
        Assert.Equal(0, ordinary.PastExchanges);
        Assert.DoesNotContain(PastConversations.Label, Notes(fixture.Llm.Body) ?? "");
        Assert.Equal(askedInstructions, Instructions(fixture.Llm.Body));
        Assert.False(HasTools(fixture.Llm.Body));

        // Mentioning it again in the same conversation doesn't send the same exchange twice.
        fixture.Answer("Still Radiohead.");
        var again = fixture.Start("Do you remember my favorite band, again?");
        await fixture.Finish(again);
        Assert.Equal(0, again.PastExchanges);
        await history.Idle;
        Assert.Equal(4, history.Store.Stats.Exchanges);
        Assert.Equal(2, history.Store.Stats.Conversations);
    }

    [Fact]
    public async Task NothingIsRecordedWhileMemoryIsOffOrTheOwnerKeepsNoRecord()
    {
        await using var fixture = await LiveFixture.Create(history: true);
        fixture.Answer("Plain answer.");
        var off = fixture.Start("My dog is called Biscuit.");
        await fixture.Finish(off);
        await fixture.History!.Idle;
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, ConversationHistory.DirectoryName)));

        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        Assert.True(fixture.History.SetPreferences(new(Keep: false)));
        var notKept = fixture.Start("Do you remember my dog's name?");
        await fixture.Finish(notKept);
        await fixture.History.Idle;
        Assert.Equal(0, notKept.PastExchanges);
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, ConversationHistory.DirectoryName)));
        Assert.Equal(new ConversationHistoryPreferences(Keep: false), ConversationHistoryPreferences.Load(fixture.DirectoryPath));
    }

    [Fact]
    public async Task TheSearchToolIsOfferedOnlyWhenChosenAndAfterMartletsOwnTools()
    {
        await using var fixture = await LiveFixture.Create(history: true, tools: true);
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        fixture.Answer("Hello!");
        var plain = fixture.Start("Hi there.");
        await fixture.Finish(plain);
        var before = ToolNames(fixture.Llm.Body);
        Assert.DoesNotContain(PastConversations.ToolName, before);

        Assert.True(fixture.History!.SetPreferences(new(Keep: true, Search: true)));
        var offered = fixture.Start("Hi again.");
        await fixture.Finish(offered);
        var after = ToolNames(fixture.Llm.Body);
        // manage_memories (memory is on) stays last.
        Assert.Equal(MemoryTools.Name, before[^1]);
        Assert.Equal([.. before[..^1], PastConversations.ToolName, MemoryTools.Name], after);
        var instructions = Instructions(fixture.Llm.Body);

        var next = fixture.Start("And once more.");
        await fixture.Finish(next);
        Assert.Equal(after, ToolNames(fixture.Llm.Body));
        Assert.Equal(instructions, Instructions(fixture.Llm.Body));
    }

    [Fact]
    public async Task SearchingTheRecordTellsTheModelWhatWasFoundOrWhatWasWrong()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.History.Desktop." + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new DesktopConversationHistory(directory, zone: TimeZoneInfo.Utc);
            Guid earlier = Guid.NewGuid(), current = Guid.NewGuid();
            history.Record(earlier, HistoryInputKind.Spoken, "The treehouse needs a rope ladder.", "Rope ladders are fun.", "Ana");
            history.Record(current, HistoryInputKind.Typed, "Let's talk about the rope ladder.", "Sure.", null);
            await history.Idle;
            Assert.Equal(0, history.Pending);

            var (found, outcome) = await history.SearchAsync(new("call-1", PastConversations.ToolName, "{\"query\":\"rope ladder\"}"), current, default);
            Assert.False(found.IsError);
            Assert.Equal("found 1", outcome);
            Assert.Contains("Ana: \"The treehouse needs a rope ladder.\"", found.Output);
            Assert.DoesNotContain("Let's talk about", found.Output);

            var (wrong, problem) = await history.SearchAsync(new("call-2", PastConversations.ToolName, "{}"), current, default);
            Assert.True(wrong.IsError);
            Assert.Equal("invalid arguments", problem);

            Assert.Contains("It holds 2 conversations (2 exchanges) since ", history.Describe(MemorySettingsOn()));
            Assert.StartsWith("Off while memory is off", history.Describe(null));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreferencesDefaultToKeepingARecordWithoutTheSearchTool()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.History.Prefs." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(new ConversationHistoryPreferences(Keep: true, Search: false), ConversationHistoryPreferences.Load(directory));
            Assert.True(new ConversationHistoryPreferences(Keep: true, Search: true).Save(directory));
            Assert.Equal(new ConversationHistoryPreferences(Keep: true, Search: true), ConversationHistoryPreferences.Load(directory));
            File.WriteAllText(Path.Combine(directory, ConversationHistoryPreferences.FileName), "{ not json");
            Assert.Equal(new ConversationHistoryPreferences(), ConversationHistoryPreferences.Load(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task TheWindowListsSearchesAndDeletesConversations() => OnDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.History.Window." + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new DesktopConversationHistory(directory, zone: TimeZoneInfo.Utc);
            Guid trip = Guid.NewGuid(), cat = Guid.NewGuid();
            history.Record(trip, HistoryInputKind.Spoken, "I'm going to Kyoto.", "Lovely!", null);
            history.Record(trip, HistoryInputKind.Spoken, "In April.", "Cherry blossoms!", null);
            history.Record(cat, HistoryInputKind.Typed, "Biscuit goes to the vet.", "Good luck, Biscuit.", null);
            await history.Idle;
            var asked = new List<string>();
            var window = new ConversationHistoryWindow(history, (_, _, title) => { asked.Add(title); return true; })
            { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                var list = Control<ListBox>(window, "ConversationsList");
                await Until(() => list.Items.Count == 2);
                Assert.Contains("2 conversations and 3 exchanges", Control<TextBlock>(window, "StatusText").Text);
                Assert.Equal("HistoryWindowStatus", AutomationProperties.GetAutomationId(Control<TextBlock>(window, "StatusText")));
                Assert.Contains("Biscuit goes to the vet.", Control<TextBox>(window, "ExchangesText").Text);

                Control<TextBox>(window, "SearchText").Text = "kyoto";
                Click(window, "SearchButton");
                Assert.Single(list.Items);
                Assert.Contains("Found 1 exchange in 1 conversation", Control<TextBlock>(window, "StatusText").Text);
                Assert.StartsWith("▶ ", Control<TextBox>(window, "ExchangesText").Text);

                Click(window, "DeleteButton");
                await Until(() => history.Store.Stats.Exchanges == 1);
                Assert.Equal(["Delete conversation"], asked);
                Click(window, "ShowAllButton");
                Assert.Single(list.Items);

                Click(window, "DeleteAllButton");
                await Until(() => history.Store.Stats.Exchanges == 0 && list.Items.Count == 0);
                Assert.Empty(ConversationHistory.MonthFiles(history.Store.Directory));
            }
            finally { window.Close(); }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    private static Martlet.Core.Settings.MemorySettings MemorySettingsOn() =>
        Martlet.Core.Settings.MemorySettings.Create().Configure(true, Martlet.Core.Settings.MemoryStoragePolicy.AppLocalData, null);

    private static string Instructions(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("instructions", out var instructions) ? instructions.GetString() ?? "" : "";
    }

    private static string[] ToolNames(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }

    private static bool HasTools(byte[] body) => ToolNames(body).Length > 0;

    // The notes on the latest user message, or null.
    private static string? Notes(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var last = json.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)
            .Last();
        var at = last.LastIndexOf("[" + LiveConversationConfiguration.NotesLabel + "]", StringComparison.Ordinal);
        return at < 0 ? null : last[at..];
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
}

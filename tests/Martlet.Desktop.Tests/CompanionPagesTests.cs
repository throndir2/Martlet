using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Companion's side list, where the character has a group of its own pages, and the long lists on those pages, whose rows
/// join a batch at a time once a page just opened has drawn.</summary>
public sealed class CompanionPagesTests
{
    [Fact]
    public void TheCharacterHasAGroupOfItsOwnPages()
    {
        var groups = Enum.GetValues<CompanionTab>().GroupBy(MainWindow.GroupOf).OrderBy(g => g.Key).ToArray();
        Assert.Equal(new[] { "How it works", "Optional extras", "Who it is", "How it looks", "What it does" }, groups.Select(g => MainWindow.GroupTitle(g.Key)));
        Assert.Equal(new[] { "Character", "Speech bubbles", "Emotes and motions", "Eyes", "Touch" },
            groups.Single(g => g.Key == CompanionGroup.HowItLooks).Select(MainWindow.TabTitle));
        Assert.DoesNotContain(CompanionTab.Character, groups.Single(g => g.Key == CompanionGroup.WhoItIs));
    }

    [Fact]
    public void TheJobsMartletNeedsComeFirstAndTheExtrasSayTheyAreOptional()
    {
        var groups = Enum.GetValues<CompanionTab>().GroupBy(MainWindow.GroupOf).ToDictionary(g => g.Key, g => g.ToArray());
        Assert.Equal(new[] { "Thinking", "Listening", "Voice", "Lip-sync" }, groups[CompanionGroup.HowItWorks].Select(MainWindow.TabTitle));
        Assert.Equal(new[] { CompanionTab.DeepThinking, CompanionTab.Singing, CompanionTab.Pictures, CompanionTab.Vision, CompanionTab.Reading },
            groups[CompanionGroup.Extras]);
        Assert.All(groups[CompanionGroup.Extras], tab => Assert.StartsWith("Optional.", MainWindow.TabIntro(tab)));
        Assert.All(groups[CompanionGroup.HowItWorks], tab => Assert.DoesNotContain("Optional", MainWindow.TabIntro(tab)));
    }

    [Theory]
    [InlineData(Martlet.Core.Planning.PlanComponent.Thinking, "Thinking")]
    [InlineData(Martlet.Core.Planning.PlanComponent.Listening, "Listening")]
    [InlineData(Martlet.Core.Planning.PlanComponent.Voice, "Voice")]
    [InlineData(Martlet.Core.Planning.PlanComponent.LipSync, "Lip-sync")]
    public void AWelcomeCardsBackupIsSetUpOnItsOwnPage(Martlet.Core.Planning.PlanComponent component, string page) =>
        Assert.Equal(page, MainWindow.FallbackPage(component));

    [Fact]
    public void EveryPageHasItsOwnTitleIconAndIntro()
    {
        var tabs = Enum.GetValues<CompanionTab>();
        Assert.Equal(tabs.Length, tabs.Select(MainWindow.TabTitle).Distinct().Count());
        Assert.Equal(tabs.Length, tabs.Select(MainWindow.TabGlyph).Distinct().Count());
        Assert.All(tabs, tab => Assert.NotEmpty(MainWindow.TabIntro(tab)));
    }

    [Fact]
    public Task RowsAfterTheFirstJoinTheirPanelsInBatchesInTheirOrder() => OnDispatcher(() =>
    {
        var batches = new RowBatches(first: 2, batch: 3);
        var list = new StackPanel();
        var after = new StackPanel();
        var rows = Enumerable.Range(0, 8).Select(_ => (UIElement)new Border()).ToArray();
        batches.Begin(gradually: true);
        foreach (var row in rows[..7]) batches.Add(list, row);
        batches.Add(after, rows[7]);

        // Only the first rows join at once; the others are built, and wait.
        Assert.Equal(rows[..2], list.Children.Cast<UIElement>());
        Assert.Empty(after.Children);
        (int Rows, int Batches)? done = null;
        var frame = new DispatcherFrame();
        var guard = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        batches.End(Dispatcher.CurrentDispatcher, (count, steps) =>
        {
            done = (count, steps);
            frame.Continue = false;
        });
        Assert.False(batches.Gradually);
        Dispatcher.PushFrame(frame);
        guard.Stop();

        // Then the rest, three at a time, each into its own panel in the order they were added.
        Assert.Equal(rows[..7], list.Children.Cast<UIElement>());
        Assert.Equal(new[] { rows[7] }, after.Children.Cast<UIElement>());
        Assert.Equal((8, 3), done);
    });

    [Fact]
    public Task ADrawingAtOnceAddsEveryRowAndStopsAnOlderDrawingsBatches() => OnDispatcher(() =>
    {
        var batches = new RowBatches(first: 1, batch: 1);
        var old = new StackPanel();
        batches.Begin(gradually: true);
        for (var i = 0; i < 4; i++) batches.Add(old, new Border());
        var oldDone = false;
        batches.End(Dispatcher.CurrentDispatcher, (_, _) => oldDone = true);

        // The page is drawn again further down: every row joins at once, and the old drawing's rows never do.
        var again = new StackPanel();
        batches.Begin(gradually: false);
        for (var i = 0; i < 4; i++) batches.Add(again, new Border());
        batches.End(Dispatcher.CurrentDispatcher);
        Assert.Equal(4, again.Children.Count);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Single(old.Children);
        Assert.False(oldDone);

        // Stop (the window closes) does the same.
        var closing = new StackPanel();
        batches.Begin(gradually: true);
        for (var i = 0; i < 3; i++) batches.Add(closing, new Border());
        batches.End(Dispatcher.CurrentDispatcher);
        batches.Stop();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Single(closing.Children);
    });

    private static async Task OnDispatcher(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); finished.SetResult(); }
            catch (Exception error) { finished.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}

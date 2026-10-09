using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The "?" that holds an explanation (HelpTip), the short explanation that shows its first sentence and puts the rest
/// behind a "?", and the sections you open and close (Fold), which keep their state while Martlet runs.</summary>
public sealed class HelpTipTests
{
    [Fact]
    public void SplitKeepsTheFirstSentenceAndHidesTheRest()
    {
        var (summary, more) = HelpTip.Split("Martlet's replies can make the character show its emotes. Each one plays for a while, " +
            "then the character goes back to how it was before.");
        Assert.Equal("Martlet's replies can make the character show its emotes.", summary);
        Assert.Equal("Each one plays for a while, then the character goes back to how it was before.", more);
    }

    [Fact]
    public void SplitNeverLeavesASummaryOrARestThatIsTooShort()
    {
        // "Off by default." alone says too little, so the next sentence stays with it.
        var (summary, more) = HelpTip.Split("Off by default. When the model has no words after a short wait, the request also goes " +
            "to a backup machine. Whichever starts first gives the reply, and the other one stops.");
        Assert.Equal("Off by default. When the model has no words after a short wait, the request also goes to a backup machine.", summary);
        Assert.Equal("Whichever starts first gives the reply, and the other one stops.", more);
        // A short last sentence isn't worth a "?".
        const string text = "Commands run hidden, one at a time, as you and never as administrator. They stop soon.";
        Assert.Equal((text, null), HelpTip.Split(text));
        // One sentence, and abbreviations aren't sentence ends.
        const string one = "Add a model, e.g. Live2D or VRM, from a folder on this PC and pick it as the character here.";
        Assert.Equal((one, null), HelpTip.Split(one));
    }

    [Fact]
    public Task ExplainShowsTheSummaryWithAHelpTipThatHasAllOfIt() => OnDispatcher(() =>
    {
        const string text = "A check-in is a short question that the Thinking pool answers. Martlet then acts on the answer, so its " +
            "next reply follows it.";
        var block = HelpTip.Explain(text, new Thickness(0, 4, 0, 0), "CheckInsTest", "check-ins");
        Assert.Equal(new Thickness(0, 4, 0, 0), block.Margin);
        Assert.Equal("A check-in is a short question that the Thinking pool answers.", ((Run)block.Inlines.FirstInline).Text);
        var tip = Assert.IsType<HelpTip>(Assert.IsType<InlineUIContainer>(block.Inlines.LastInline).Child);
        Assert.Equal(text, tip.Text);
        Assert.Equal("Help-CheckInsTest", AutomationProperties.GetAutomationId(tip));
        Assert.Equal("About check-ins", AutomationProperties.GetName(tip));
        Assert.Equal(text, AutomationProperties.GetHelpText(tip));
        Assert.Equal(text, ((ToolTip)tip.ToolTip).Content is TextBlock shown ? shown.Text : null);
        Assert.Equal("A check-in is a short question that the Thinking pool answers.", AutomationProperties.GetName(block));
        Assert.Equal(text, AutomationProperties.GetHelpText(block));

        // A short explanation stays as it is, with no "?".
        var plain = HelpTip.Explain("Tick what each machine may do.", new Thickness(0), "Short");
        Assert.Single(plain.Inlines);
    });

    [Fact]
    public Task ClickingTheHelpTipKeepsItsTextOpenUntilEscape() => OnDispatcher(() =>
    {
        var tip = HelpTip.For("check-ins", "A check-in is a short question.", "Open");
        var window = new ThemedWindow
        {
            Content = new StackPanel { Children = { tip } }, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Equal(20, tip.ActualWidth);
            Assert.False(tip.IsOpen);
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(tip).GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            Flush();
            Assert.True(tip.IsOpen);
            tip.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(tip)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.KeyDownEvent });
            Assert.False(tip.IsOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FoldsKeepTheirStateAndIgnoreFoldsInsideThem() => OnDispatcher(() =>
    {
        Fold.Reset();
        var inner = Fold.Create("Inner", "TestInner", false, new TextBlock { Text = "inside" });
        var outer = Fold.Create("More settings", "TestOuter", false, inner);
        Assert.Equal("Fold-TestOuter", AutomationProperties.GetAutomationId(outer));
        Assert.Equal("More settings", AutomationProperties.GetName(outer));
        Assert.False(outer.IsExpanded);

        inner.IsExpanded = true;
        outer.IsExpanded = true;
        inner.IsExpanded = false;
        // Built again (as when the page is shown again): the outer one stays open, the inner one closed.
        Assert.True(Fold.Create("More settings", "TestOuter", false).IsExpanded);
        Assert.False(Fold.Create("Inner", "TestInner", true).IsExpanded);
        // A section never opened starts as its page builds it.
        Assert.True(Fold.Create("Other", "TestOther", true).IsExpanded);
        Fold.Reset();
    });

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

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

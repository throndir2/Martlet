using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using Martlet.Core.Text;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The "?" that holds an explanation (HelpTip), the short explanation that shows its first sentence and puts the rest
/// behind a "?", and the sections you open and close (Fold), which keep their state while Martlet runs.</summary>
public sealed partial class HelpTipTests
{
    [Fact]
    public void SplitKeepsTheFirstSentenceAndHidesTheRest()
    {
        var (summary, more) = HelpTip.Split("Martlet's replies can make the character show its emotes. Each one plays for a while, " +
            "then the character goes back to how it was before.");
        Assert.Equal("Martlet's replies can make the character show its emotes.", summary);
        Assert.Equal("Each one plays for a while, then the character goes back to how it was before.", more);
    }

    [Theory]
    [InlineData("Off by default. When the model has no words after a short wait, the request also goes to a backup machine. " +
        "Whichever starts first gives the reply, and the other one stops.", "Off by default.")]
    [InlineData("Commands run hidden, one at a time, as you and never as administrator. They stop soon.",
        "Commands run hidden, one at a time, as you and never as administrator.")]
    [InlineData("Write what it checks, such as \"Suggest a short break\". Choose when it runs.", "Write what it checks, such as \"Suggest a short break\".")]
    [InlineData("First part.\n\nA new paragraph.", "First part.")]
    [InlineData("Add a model, e.g. Live2D or VRM, from a folder on this PC and pick it as the character here.",
        "Add a model, e.g. Live2D or VRM, from a folder on this PC and pick it as the character here.")]
    public void SplitShowsExactlyOneSentence(string text, string shown)
    {
        var (summary, more) = HelpTip.Split(text);
        Assert.Equal(shown, summary);
        Assert.Equal(1, Sentences.Count(summary));
        Assert.Equal(Sentences.Count(text) > 1, more is not null);
    }

    [Fact]
    public Task AWikiLinkGetsItsCardAndLinkIds() => OnDispatcher(() =>
    {
        var tip = HelpTip.For("check-ins", "A check-in is a short question.", "WikiTest", "Thinking#check-ins");
        Assert.Equal("Help-WikiTest", AutomationProperties.GetAutomationId(tip));
        Assert.Equal("A check-in is a short question.", AutomationProperties.GetHelpText(tip));
        var parts = ((StackPanel)((Border)CardOf(tip).Child).Child).Children;
        var card = (TextBlock)parts[0];
        var link = Assert.IsType<Hyperlink>(((TextBlock)parts[1]).Inlines.FirstInline);
        Assert.Equal("Help-WikiTest-Card", AutomationProperties.GetAutomationId(card));
        Assert.Equal("A check-in is a short question.", AutomationProperties.GetName(card));
        Assert.Equal("Help-WikiTest-Wiki", AutomationProperties.GetAutomationId(link));
        Assert.Equal("Read more in the wiki", AutomationProperties.GetName(link));
        Assert.Equal("https://github.com/throndir2/Martlet/wiki/Thinking#check-ins", AutomationProperties.GetHelpText(link));
        Assert.Equal(Visibility.Visible, ((TextBlock)parts[1]).Visibility);

        // Without a wiki page there's no link; a one-sentence explanation with a wiki page still gets its "?".
        Assert.Equal(Visibility.Collapsed, ((TextBlock)((StackPanel)((Border)CardOf(HelpTip.For("x", "One line.", "NoWiki")).Child).Child).Children[1]).Visibility);
        var line = HelpTip.Explain("One short line.", new Thickness(0), "OneLine", "this", "Thinking#check-ins");
        Assert.IsType<HelpTip>(Assert.IsType<InlineUIContainer>(line.Inlines.LastInline).Child);
        Assert.Throws<ArgumentException>(() => HelpTip.For("x", "One line.", "Bad", "thinking page"));
    });

    /// <summary>Every wiki value a page gives a HelpTip (written as wiki: "Page#anchor" or Wiki="Page#anchor") is a page in
    /// docs/wiki/ with a heading that has that anchor, the way GitHub makes heading anchors.</summary>
    [Fact]
    public void EveryWikiLinkGoesToAPageAndHeadingInTheWiki()
    {
        var root = RepositoryRoot();
        var wiki = Path.Combine(root, "docs", "wiki");
        var values = Directory.EnumerateFiles(Path.Combine(root, "src", "Martlet.Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(path => WikiValues().Matches(File.ReadAllText(path)).Select(match => (Path.GetFileName(path), Value: match.Groups[1].Value)))
            .ToArray();
        Assert.Contains(values, value => value.Value == "Thinking#check-ins");
        var wrong = new List<string>();
        foreach (var (file, value) in values)
        {
            try { HelpTip.WikiUrl(value); }
            catch (ArgumentException) { wrong.Add($"{file}: '{value}' is not Page#anchor"); continue; }
            var parts = value.Split('#');
            var page = Path.Combine(wiki, parts[0] + ".md");
            if (!File.Exists(page)) { wrong.Add($"{file}: {value}: docs/wiki/{parts[0]}.md is missing"); continue; }
            if (parts.Length > 1 && !Anchors(File.ReadAllLines(page)).Contains(parts[1]))
                wrong.Add($"{file}: {value}: docs/wiki/{parts[0]}.md has no heading with the anchor #{parts[1]}");
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>GitHub's heading anchors: lower case, punctuation left out, spaces as hyphens, and -1, -2 for the same heading again.</summary>
    private static HashSet<string> Anchors(IEnumerable<string> lines)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var fenced = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
            if (fenced || Heading().Match(line) is not { Success: true } heading) continue;
            var text = Regex.Replace(heading.Groups[1].Value.Trim(), @"\[([^\]]*)\]\([^)]*\)", "$1");
            var slug = new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Replace(' ', '-');
            var unique = slug;
            for (var n = 1; !anchors.Add(unique); n++) unique = slug + "-" + n;
        }
        return anchors;
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "docs", "wiki")) && Directory.Exists(Path.Combine(folder.FullName, "src")))
                return folder.FullName;
        throw new DirectoryNotFoundException("docs/wiki was not found above " + AppContext.BaseDirectory);
    }

    private static Popup CardOf(HelpTip tip) =>
        (Popup)typeof(HelpTip).GetField("card", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tip)!;

    [GeneratedRegex(@"(?:\bwiki:\s*|\bWiki\s*=\s*)""([^""]*)""")]
    private static partial Regex WikiValues();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

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

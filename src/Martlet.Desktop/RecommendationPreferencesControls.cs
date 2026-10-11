using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>The owner's recommendation preferences as controls (docs/RECOMMENDATION_DESIGN.md): Recommended setup's Your
/// preferences and Settings › Recommended setup preferences build the same controls. Each control's automation ID is
/// <c>prefix</c> plus Quality, Online, Games-&lt;n&gt; (one per companion PC, this PC first), Hearing, HostShare,
/// UseServedModels and PreferHostModels; ServedModels and HostModels are the notes under the last two. A change calls back
/// with the new preferences; the caller saves them and plans again.</summary>
internal static class RecommendationPreferencesControls
{
    internal static readonly string[] QualityItems =
        ["Balanced: first word in 0.4 s or less", "Quick replies: first word in 0.25 s or less", "Smarter replies: first word in 1 s or less"];

    internal static readonly string[] OnlineItems = ["Only as a backup", "Never", "Yes, when they're faster or smarter"];

    internal static string ShareItem(int percent) => $"Up to {percent}%";

    internal static StackPanel Build(string prefix, RecommendationPreferences preferences, IReadOnlyList<GamesComputer> games,
        Action<RecommendationPreferences> changed, string? servedNote = null, string? hostModelsNote = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var panel = new StackPanel();
        var ready = false;
        void Changed(RecommendationPreferences next)
        {
            if (ready && next != preferences) changed(next);
        }

        var quality = Choice(panel, prefix + "Quality", "What matters more in a conversation", QualityItems, (int)preferences.Quality,
            "Martlet picks the smartest Thinking model whose first word comes within this time. Quick replies prefers the smaller of two close models.");
        quality.SelectionChanged += (_, _) => Changed(preferences with { Quality = (ReplyQuality)Math.Max(0, quality.SelectedIndex) });

        var online = Choice(panel, prefix + "Online", "May Martlet use free online services", OnlineItems, (int)preferences.Online,
            "Only as a backup: replies, the voice and listening stay on your computers; Deep thinking and a backup for Thinking may go online.");
        online.SelectionChanged += (_, _) => Changed(preferences with { Online = (OnlineServices)Math.Max(0, online.SelectedIndex) });

        for (var i = 0; i < games.Count; i++)
        {
            var computer = games[i];
            var label = $"I play games or use heavy apps on {(computer.Name == "This PC" ? "this PC" : computer.Name)}" +
                (computer.Answered ? "" : " (Martlet's guess)");
            var box = Check(panel, $"{prefix}Games-{i}", label, computer.Plays,
                "Martlet leaves that computer's graphics card to Thinking and the voice, and only when no host can do them.");
            box.Checked += (_, _) => Changed(preferences.WithGames(computer.Device, true));
            box.Unchecked += (_, _) => Changed(preferences.WithGames(computer.Device, false));
        }

        var hearing = Check(panel, prefix + "Hearing", "Prefer models that hear you", preferences.PreferHearing,
            "A Thinking model that hears your voice wins over a smarter one when it is no more than one step behind. Off: Thinking gets the words you said.");
        hearing.Checked += (_, _) => Changed(preferences with { PreferHearing = true });
        hearing.Unchecked += (_, _) => Changed(preferences with { PreferHearing = false });

        var shares = RecommendationPreferences.HostGpuShares;
        var share = Choice(panel, prefix + "HostShare", "Host graphics card share", [.. shares.Select(ShareItem)],
            Math.Max(0, shares.ToList().IndexOf(preferences.HostGpuShare)),
            "How much of each host's graphics card Martlet may plan with. Choose less for a host that also does other work.");
        share.SelectionChanged += (_, _) => Changed(preferences with { HostGpuShare = shares[Math.Max(0, share.SelectedIndex)] });

        var served = Check(panel, prefix + "UseServedModels", "Use models your apps already run", preferences.UseServedModels,
            "Martlet looks for the chat models that Ollama, LM Studio, llama.cpp, vLLM and other model apps on this PC run, and thinks with the best one that fits.");
        served.Checked += (_, _) => Changed(preferences with { UseServedModels = true });
        served.Unchecked += (_, _) => Changed(preferences with { UseServedModels = false });
        if (servedNote is not null) Note(panel, prefix + "ServedModels", servedNote);

        var kept = Check(panel, prefix + "PreferHostModels", "Prefer models your hosts already have", preferences.PreferHostModels,
            "Thinking uses the best model your hosts already run or keep downloaded, so nothing downloads. Its first word may come later than with a smaller model.");
        kept.Checked += (_, _) => Changed(preferences with { PreferHostModels = true });
        kept.Unchecked += (_, _) => Changed(preferences with { PreferHostModels = false });
        if (hostModelsNote is not null) Note(panel, prefix + "HostModels", hostModelsNote);

        ready = true;
        return panel;
    }

    private static ComboBox Choice(Panel panel, string id, string label, IReadOnlyList<string> items, int selected, string help)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var name = new Label { Content = label, Padding = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Width = 260 };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        var box = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = help };
        foreach (var item in items) box.Items.Add(new ComboBoxItem { Content = item });
        box.SelectedIndex = Math.Clamp(selected, 0, items.Count - 1);
        name.Target = box;
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetHelpText(box, help);
        row.Children.Add(box);
        panel.Children.Add(row);
        return box;
    }

    private static CheckBox Check(Panel panel, string id, string label, bool on, string help)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Margin = new Thickness(0, 0, 0, 6),
            ToolTip = help
        };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetHelpText(box, help);
        panel.Children.Add(box);
        return box;
    }

    private static void Note(Panel panel, string id, string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 0, 10) };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(note, id);
        panel.Children.Add(note);
    }
}

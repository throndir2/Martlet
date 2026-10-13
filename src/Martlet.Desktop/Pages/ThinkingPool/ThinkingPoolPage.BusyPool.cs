using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking pool's Runs on, Backup for slow replies and Busy pool cards.</summary>
internal sealed partial class ThinkingPoolPage
{
    /// <summary>Runs on: for each kind of job (<see cref="ThinkingJobKinds.Rows"/>, by its saved key and the label that names all
    /// the work it controls), which machines in the pool may take it (Any member, Prefer smart, Smart only or These members).
    /// Each check-in can choose its own on its card.</summary>
    private static Border RunsOnCard(ThinkingPoolPageModel model)
    {
        var pool = model.Pool;
        var stack = new List<UIElement>
        {
            Heading("Runs on"),
            Note("Choose which machines do each kind of job. Prefer smart sends it to the smartest free machine; when none of the " +
                "smartest comes free within a short wait (30 seconds at most), a less smart one takes it. Thinking longer, research " +
                "and lyrics take the smartest free machine at once. Smart only waits for a Smart machine; without one, the job uses " +
                "its simple fallback. These members uses only the machines you tick. Each machine's Smarts is above. Your " +
                "conversation's own replies never change.", new Thickness(0, 0, 0, 8))
        };
        foreach (var (kind, label) in ThinkingJobKinds.Rows.Select(row => (row.Key, row.Label)))
        {
            var heading = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
            AutomationProperties.SetAutomationId(heading, "ThinkingPoolRunsOnLabel-" + kind);
            stack.Add(heading);
            Func<ThinkingRunsOn?>? read = null;
            var (view, current) = RunsOnPicker.Create("ThinkingPoolRunsOn-" + kind, "ThinkingPoolRunsOnMember-" + kind, label, pool.RunsOn(kind),
                inherit: false, pool, () =>
                {
                    var where = read?.Invoke() ?? ThinkingRunsOn.Any;
                    model.SavePoolAsync(p => p.WithRunsOn(kind, where), $"{label}: runs on {where.Describe()}.").Forget();
                });
            read = current;
            stack.Add(view);
        }
        return Card([.. stack]);
    }

    /// <summary>Backup for slow replies (Backup Thinking, off by default): when a reply's Thinking model has no words after a short wait, the same request
    /// also goes to a member that may answer for the conversation, and the first to start gives the reply.</summary>
    private static Border BackupThinkingCard(ThinkingPoolPageModel model)
    {
        var pool = model.Pool;
        var on = new CheckBox
        {
            IsChecked = pool.BackupThinking, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = "Ask a machine in the pool too when a reply is slow to start", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetAutomationId(on, "ThinkingPoolBackup");
        AutomationProperties.SetName(on, "Backup for slow replies: ask a machine in the pool too when a reply is slow to start");
        void TurnOn(bool value) => model.SavePoolAsync(p => p with { BackupThinking = value }, value
            ? "Backup for slow replies is on: a slow reply also asks a machine ticked for it."
            : "Backup for slow replies is off.").Forget();
        on.Checked += (_, _) => TurnOn(true);
        on.Unchecked += (_, _) => TurnOn(false);
        var choices = ThinkingPoolSettings.BackupDelayChoices;
        var delay = new ComboBox
        {
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new[] { "Automatic (from recent replies)" }.Concat(choices.Select(ms => (ms / 1000.0).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s")).ToArray(),
            SelectedIndex = pool.BackupDelayMs is { } chosen ? Math.Max(0, IndexOf(choices, chosen) + 1) : 0
        };
        AutomationProperties.SetAutomationId(delay, "ThinkingPoolBackupDelay");
        AutomationProperties.SetName(delay, "How long a reply waits for its first words before a member is asked too");
        delay.SelectionChanged += (_, _) =>
        {
            int? value = delay.SelectedIndex <= 0 ? null : choices[delay.SelectedIndex - 1];
            model.SavePoolAsync(p => p with { BackupDelayMs = value }, value is null
                ? "Backup Thinking waits an automatic time, from how fast recent replies began."
                : $"Backup Thinking waits {value} ms for the first words.").Forget();
        };
        var status = Note(model.BackupStatus, new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(status, "ThinkingPoolBackupStatus");
        return Card(Heading("Backup for slow replies"),
            HelpTip.Explain("Off by default. When the conversation's Thinking model has no words after a short wait, the same request also goes " +
                "to a machine you ticked for it (Backup for slow replies, on a machine above). Whichever starts first gives the reply, and the " +
                "other stops at once, so a slow or busy model doesn't keep you waiting. Choose machines with the same model as the " +
                "conversation, or a similar one. A paid cloud provider is asked only when you tick it, and only for a reply already " +
                "taken.", new Thickness(0, 0, 0, 8), "ThinkingFill", "this setting"),
            on, TerminalRow("Wait for first words", delay), status);

        static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
            return -1;
        }
    }

    /// <summary>Companion › Thinking pool's line of requests (Busy pool): whether higher-priority requests may stop lower ones,
    /// after how many stops a stopped request's priority goes up, and how many times a failed request is tried again.</summary>
    private static Border PriorityCard(ThinkingPoolPageModel model)
    {
        var pool = model.Pool;
        var preempt = new CheckBox
        {
            IsChecked = pool.PreemptLowerPriority, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = "Higher priority requests may stop lower ones", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetAutomationId(preempt, "ThinkingPoolPreempt");
        AutomationProperties.SetName(preempt, "Higher priority requests may stop lower ones");
        void Preempt(bool value) => model.SavePoolAsync(p => p with { PreemptLowerPriority = value }, value
            ? "Higher priority requests may stop lower ones when the pool is full."
            : "Requests wait for a free slot and never stop each other.").Forget();
        preempt.Checked += (_, _) => Preempt(true);
        preempt.Unchecked += (_, _) => Preempt(false);

        var raiseChoices = Enumerable.Range(ThinkingPoolSettings.MinRaiseAfterStops,
            ThinkingPoolSettings.MaxRaiseAfterStops - ThinkingPoolSettings.MinRaiseAfterStops + 1).ToArray();
        var raise = new ComboBox
        {
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = raiseChoices.Select(n => n == 1 ? "After every stop" : $"After {n} stops").ToArray(),
            SelectedIndex = Array.IndexOf(raiseChoices, pool.RaisePriorityAfterStops) is var at and >= 0 ? at : Array.IndexOf(raiseChoices, ThinkingPoolSettings.DefaultRaiseAfterStops),
            IsEnabled = pool.PreemptLowerPriority
        };
        AutomationProperties.SetAutomationId(raise, "ThinkingPoolRaiseAfterStops");
        AutomationProperties.SetName(raise, "Raise a stopped request's priority after this many stops");
        raise.SelectionChanged += (_, _) =>
        {
            if (raise.SelectedIndex < 0) return;
            var value = raiseChoices[raise.SelectedIndex];
            model.SavePoolAsync(p => p with { RaisePriorityAfterStops = value }, value == 1
                ? "A stopped request becomes more important each time it's stopped."
                : $"A stopped request becomes more important after every {value} stops.").Forget();
        };

        var retryChoices = Enumerable.Range(0, ThinkingPoolSettings.MaxRetries + 1).ToArray();
        var retries = new ComboBox
        {
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = retryChoices.Select(n => n switch { 0 => "Don't try again", 1 => "Once more", _ => $"{n} more times" }).ToArray(),
            SelectedIndex = Math.Clamp(pool.RetriesOnFailure, 0, ThinkingPoolSettings.MaxRetries)
        };
        AutomationProperties.SetAutomationId(retries, "ThinkingPoolRetries");
        AutomationProperties.SetName(retries, "Try a failed request again this many times");
        retries.SelectionChanged += (_, _) =>
        {
            if (retries.SelectedIndex < 0) return;
            var value = retryChoices[retries.SelectedIndex];
            model.SavePoolAsync(p => p with { RetriesOnFailure = value }, value switch
            {
                0 => "A failed request isn't tried again.",
                1 => "A failed request is tried once more.",
                _ => $"A failed request is tried up to {value} more times."
            }).Forget();
        };

        var status = Note(model.PriorityStatus, new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(status, "ThinkingPoolPriorityStatus");
        return Card(Heading("Busy pool"),
            Note("When every slot is busy, a more important request can stop a less important one to go first. The stopped request " +
                "keeps its place at the front of its line and starts again when there's room. If it keeps getting stopped, it becomes " +
                "more important so it finishes in the end. A request that fails can be tried again, at the importance it had.",
                new Thickness(0, 0, 0, 8)),
            preempt, TerminalRow("More important after", raise), TerminalRow("Try a failed request again", retries), status);
    }
}

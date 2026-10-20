using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// The app shell's home (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    /// <summary>Said after a job change the open conversation follows by itself (<see cref="FollowSavedSetup"/>).</summary>
    private const string OpenConversationFollows = " An open conversation switches over before its next reply.";

    /// <summary>The talk window takes a setup saved here between replies, keeping what was said so far, so switching a job's
    /// engine, model or computer never needs the conversation reopened. Nothing happens when it already uses
    /// <paramref name="revision"/>.</summary>
    private void FollowSavedSetup(string? revision, string reason = "Your setup changed.")
    {
        if (openConversation is { } talking && revision is not null && conversation?.Configuration?.Revision != revision)
            talking.ReloadWhenIdle(reason);
    }

    /// <summary>Reads settings.json and brings Home, the open conversation and the pages up to date. <paramref name="reason"/>
    /// says why it was read (<see cref="SettingsChanges.Applied"/>; a version this Martlet saved keeps the source it was saved
    /// with).</summary>
    private async Task RefreshHomeAsync(SettingsChangeSource reason = SettingsChangeSource.LocalEdit)
    {
        if (store is null || setupService is null || closing) return;
        if (refreshingHome || setupOperations.IsRunning)
        {
            // Read again once the running refresh or setup is done, so this change isn't missed until the next one.
            refreshHomeAgain = true;
            if (!refreshingHome) settingsFileTimer.Start();
            return;
        }
        refreshingHome = true;
        try
        {
            var loaded = await setupService.LoadAsync(lifetime.Token);
            settingsChanges.Applied(loaded.Revision, reason);
            homeSettings = await LeaveRetiredVoiceAsync(loaded, lifetime.Token) ?? loaded.Settings;
            SpeakingEngineChoice.Sync(store.DataDirectory, homeSettings);
            // No voice goes by the companion's own name: one learned by mistake is dropped (names the owner typed stay).
            localVoices.DropCompanionNames(Martlet.Core.Speakers.CompanionNames.From(homeSettings?.Companion?.Personas.Select(p => p.Name),
                homeSettings?.Companion?.Personas.Select(p => p.Text)));
            homeSettingsState = loaded.State;
            homeSettingsProblem = loaded.Error?.Summary;
            // Speaking, Listening and Thinking get their pool lists once, from Devices › Sharing work and their routes.
            EnsurePools();
            // Touch zones saved before reaction lists get the list they play now (the active persona's temperament is known now).
            characterTouchZones.FillLists();
            // The talk window stays open while you change things in Companion: it picks up a saved change once Martlet is free.
            if (openConversation is { IsReady: true } talking && loaded.Revision is { } revision &&
                (conversation?.Configuration is { } current ? revision != current.Revision : LiveConversationConfiguration.From(loaded) is not null))
                talking.ReloadWhenIdle("Your setup changed.");
            homeAvatar = loaded.Settings is { } settings
                ? (await new AvatarProfileStore(store.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile
                : null;
            await audioPresence.CheckAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException)
        {
            ErrorLog.Warn("Martlet couldn't bring Home and the pages up to date with the saved settings.", error);
        }
        finally
        {
            refreshingHome = false;
            if (refreshHomeAgain && !closing)
            {
                refreshHomeAgain = false;
                settingsFileTimer.Start();
            }
        }
        if (closing) return;
        // A character profile switched to on another computer brings back what it keeps on this PC.
        FollowCharacterProfile();
        // Another persona may be in use now, with another usual gaze.
        avatar.Gaze.Refresh();
        var hostIds = string.Join(",", homeHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId));
        var sharedIds = string.Join(",", sharedHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId));
        try
        {
            var all = HostRegistry.Load(store.DataDirectory, homeAvatar?.RemoteHost, machine.LanAddress ?? HostSetupCommands.ThisPcAddress());
            // Hosts friends share with this PC are kept apart: none of the syncs between your own computers talks to them.
            homeHosts = all.Where(h => !h.Shared).ToArray();
            sharedHosts = all.Where(h => h.Shared).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            homeHosts = [];
            sharedHosts = [];
            ActionText.Text = error.Message;
        }
        // A pairing made elsewhere (Add a computer) is shared with the Martlet network right away.
        if (hostIds != string.Join(",", homeHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId))) QueueNetworkSync();
        // A host a friend just shared with this PC (or one forgotten): read what it offers this PC.
        if (sharedIds != string.Join(",", sharedHosts.Select(h => h.HostId + "/" + h.Pairing.CredentialId)))
        {
            sharedHostsCheckedAt = null;
            if (started) CheckSharedHostsAsync().Forget();
        }
        ObserveLocalJobs();
        UpdateNearby();
        RenderHome();
        RenderDevicePages();
        CheckOwnLipSyncAsync().Forget();
        CheckLocalServicesAsync().Forget();
        // A companion PC paired with its own host service reads it as the host dashboard does, once its pairings are known.
        if (hostState is null && HostsHere) CheckThisPcHostAsync().Forget();
    }

    private static string Greeting() => DateTime.Now.Hour switch
    {
        < 5 => "UP LATE?",
        < 12 => "GOOD MORNING",
        < 18 => "GOOD AFTERNOON",
        _ => "GOOD EVENING"
    };

    private void RenderHome()
    {
        GreetingText.Text = Greeting();
        ShowMode();
        EvaluateCoverage();
        RenderHealth(force: true);
        RenderHost();
        RenderHomeCharacters();
        if (openTab is not null && !tabEdited) RenderTab();
        FollowProviderKeys();
    }

    private void RenderSteps(Panel panel, IReadOnlyList<HomeStep> steps, bool numbered)
    {
        // Steps that didn't change stay as they are, so an open "?" card stays open through the regular checks.
        var signature = numbered + "\n" + string.Join("\n", steps.Select(s => $"{s.Id}|{s.Title}|{s.Detail}|{s.Done}|{s.Optional}|" +
            string.Join(",", s.Commands.Select(c => $"{c.Label}:{c.Primary}:{c.Enabled}"))));
        if (panel.Children.Count > 0 && previousSteps.GetValueOrDefault(panel) == signature) return;
        previousSteps[panel] = signature;
        var firstRender = !previousDone.TryGetValue(panel, out var before) || panel.Children.Count == 0;
        panel.Children.Clear();
        var current = steps.FirstOrDefault(s => !s.Done && !s.Optional) ?? steps.FirstOrDefault(s => !s.Done);
        var done = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Done) done.Add(step.Id);
            var row = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 0, 6) };
            if (ReferenceEquals(step, current)) row.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            if (step.Done)
            {
                mark.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                mark.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                var check = Glyph("\uE73E", 14, default);
                check.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
                mark.Child = check;
            }
            else
            {
                mark.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                mark.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                mark.Child = new TextBlock
                {
                    Text = numbered ? (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : "\u2022", FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
                };
            }
            grid.Children.Add(mark);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
            title.Inlines.Add(new System.Windows.Documents.Run(step.Title));
            if (step.Optional && !step.Done)
            {
                var optional = new System.Windows.Documents.Run("  optional") { FontSize = 12, FontWeight = FontWeights.Normal };
                optional.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
                title.Inlines.Add(optional);
            }
            if (step.Help is { } help)
            {
                var tip = HelpTip.For(help.Topic, help.Text, help.Id, help.Wiki);
                tip.Margin = new Thickness(6, 0, 0, 0);
                title.Inlines.Add(new System.Windows.Documents.InlineUIContainer(tip) { BaselineAlignment = BaselineAlignment.Center });
            }
            // Whether the step is ticked, for screen readers and MCP ("Host service: done").
            AutomationProperties.SetAutomationId(title, $"StepState-{step.Id}");
            AutomationProperties.SetName(title, $"{step.Title}: {(step.Done ? "done" : step.Optional ? "optional, not done" : "to do")}");
            text.Children.Add(title);
            var detail = new TextBlock { Text = step.Detail, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(detail, $"StepDetail-{step.Id}");
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // Many buttons wrap on their own line under the text, so they never squeeze the title and detail to nothing.
            var below = step.Commands.Count > 2 || step.Commands.Sum(c => c.Label.Length) > 32;
            Panel actions = below
                ? new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }
                : new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var command in step.Commands)
            {
                var button = new Button { Content = command.Label, Margin = below ? new Thickness(0, 0, 8, 8) : new Thickness(8, 0, 0, 0), MinWidth = 86,
                    IsEnabled = command.Enabled };
                if (command.Primary && ReferenceEquals(step, current)) button.SetResourceReference(StyleProperty, "PrimaryButton");
                AutomationProperties.SetName(button, $"{command.Label}: {step.Title}");
                AutomationProperties.SetAutomationId(button, $"Step-{step.Id}-{actions.Children.Count}");
                var run = command.Run;
                button.Click += (_, _) => run();
                actions.Children.Add(button);
            }
            if (below)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(actions, 1);
                Grid.SetColumn(actions, 1);
                Grid.SetColumnSpan(actions, 2);
            }
            else Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
            row.Child = grid;
            AutomationProperties.SetName(row, $"{step.Title}: {(step.Done ? "done" : "to do")}. {step.Detail}");
            panel.Children.Add(row);

            if (firstRender) Motion.Enter(row, delay: i * 60);
            if (step.Done && (firstRender || before?.Contains(step.Id) == false)) Motion.Pop(mark, firstRender ? 200 + i * 60 : 0);
        }
        previousDone[panel] = done;
    }

    private void PrimaryStage_Click(object sender, RoutedEventArgs e) => (stageFix ?? (() => OpenCompanion(CompanionTab.Thinking)))();

    /// <summary>Home's first steps on a new PC: Add a computer, which opens on Martlet on your network.</summary>
    private HealthFix ConnectComputersFix() => new("network", "Connect to your other computers", () => RunNodeAction(NodeAction.AddComputer), Passive: true);

    private void ConnectComputers_Click(object sender, RoutedEventArgs e) => ConnectComputersFix().Run();
}

using System.Globalization;
using System.Windows;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Martlet's greeting after it starts (docs/CONVERSATION.md#greeting-when-martlet-starts): once each time Martlet
/// starts, when the first conversation on this companion PC has warmed up (its settings, Thinking's model in Ollama on this PC,
/// Parakeet and the character have loaded), Martlet says hello on its own, in character. It knows the day and time, the end of
/// the last conversation on this PC (while Memory keeps conversations), the reminders due in the next 12 hours and the apps
/// open on this PC. The greeting is a notice in the conversation (<see cref="Greetings.Kind"/>), so it waits while you talk and
/// goes with your first message when you talk first. Settings › Startup and closing › Greet me turns it off.</summary>
public partial class MainWindow
{
    private enum GreetingStep { Waiting, Gathering, Started, Off, Skipped }

    private GreetingStep greetingStep;
    private BackgroundJob? greetingJob;
    private DateTimeOffset greetingAt;
    private string? greetingKnew, greetingSignature;

    /// <summary>Checks on every tick whether the first conversation has warmed up, and then greets you once.</summary>
    private void FollowGreeting()
    {
        if (greetingStep != GreetingStep.Waiting || closing || Role != DeviceRole.Companion) return;
        if (openConversation is not { } talk || !talk.WarmedUp || LocalWarmup().Count > 0) return;
        if (!background.GreetAtStart)
        {
            greetingStep = GreetingStep.Off;
            ErrorLog.Info("Greeting: Martlet warmed up; the greeting is off, so it doesn't greet you.");
            return;
        }
        if (!talk.Fresh)
        {
            greetingStep = GreetingStep.Skipped;
            ErrorLog.Info("Greeting: Martlet warmed up after you were already talking, so it doesn't greet you.");
            return;
        }
        greetingStep = GreetingStep.Gathering;
        var now = DateTimeOffset.Now;
        var reminders = RemindersNow().Pending.Where(r => r.Reminder.Due > now && r.Reminder.Due - now <= Greetings.RemindersAhead)
            .Select(r => r.Reminder).ToArray();
        var record = conversationHistory is { } history && history.Active(homeSettings?.Memory) ? history.Store : null;
        var companion = talk.CharacterName;
        Task.Run(async () =>
        {
            IReadOnlyList<HistoryExchange>? last = null;
            if (record is not null)
            {
                await Task.WhenAny(record.LoadAsync(), Task.Delay(DesktopConversationHistory.LoadWait)).ConfigureAwait(false);
                if (record.Loaded) last = Greetings.LastConversation(record);
            }
            return new GreetingFacts
            {
                Now = now, Zone = TimeZoneInfo.Local, Apps = OpenApps.Read(), LastConversation = last, Reminders = reminders,
                Companion = companion
            };
        }).ContinueWith(gathered => Dispatcher.BeginInvoke(() => Greet(talk, gathered)), TaskScheduler.Default);
    }

    private void Greet(LiveConversationWindow talk, Task<GreetingFacts> gathered)
    {
        if (closing) return;
        var facts = gathered.IsCompletedSuccessfully ? gathered.Result
            : new GreetingFacts { Now = DateTimeOffset.Now, Zone = TimeZoneInfo.Local, Companion = talk.CharacterName };
        if (!gathered.IsCompletedSuccessfully)
            ErrorLog.Warn("Greeting: couldn't read everything for the greeting; Martlet greets you with the time of day only.",
                gathered.Exception?.GetBaseException());
        greetingStep = GreetingStep.Started;
        greetingAt = DateTimeOffset.Now;
        greetingKnew = Greetings.Describe(facts);
        greetingJob = ReferenceEquals(talk, openConversation) ? talk.Greet(Greetings.Facts(facts)) : null;
        ErrorLog.Info(greetingJob is { } job
            ? $"Greeting: Martlet warmed up; {job.Id} waits, and Martlet greets you as soon as it's free (it knows {greetingKnew})."
            : "Greeting: the conversation ended before Martlet could greet you.");
        RenderGreeting();
    }

    /// <summary>Settings › Startup and closing's line under Greet me: whether Martlet greets you, did, or why it didn't.</summary>
    private void RenderGreeting()
    {
        if (GreetingStatusText is null) return;
        var host = Role == DeviceRole.Host;
        var text = host ? "This PC is a Martlet host, so Martlet doesn't greet you here."
            : greetingStep switch
            {
                GreetingStep.Waiting when !background.GreetAtStart => "Martlet doesn't greet you when it starts.",
                GreetingStep.Waiting when openConversation is null =>
                    "Martlet greets you once a conversation has warmed up (Start talking or Start listening on Home).",
                GreetingStep.Waiting or GreetingStep.Gathering => "Martlet greets you once it has warmed up.",
                GreetingStep.Off => "Martlet didn't greet you this time: the greeting was off when it warmed up.",
                GreetingStep.Skipped => "Martlet didn't greet you this time: you were already talking when it warmed up.",
                _ => greetingJob?.Delivery switch
                {
                    BackgroundDeliveryState.Pending or BackgroundDeliveryState.NotFinished =>
                        $"Martlet greets you as soon as it's free. It knows {greetingKnew}.",
                    BackgroundDeliveryState.Reserved => "Martlet is greeting you.",
                    BackgroundDeliveryState.Delivered =>
                        $"Martlet warmed up at {greetingAt.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture)} and greeted you. " +
                        $"It knew {greetingKnew}.",
                    BackgroundDeliveryState.Dropped => "Martlet let the greeting go: you were busy for too long, or the conversation ended first.",
                    _ => "Martlet couldn't greet you: the conversation ended first."
                }
            };
        if (text == greetingSignature) return;
        greetingSignature = text;
        GreetingStatusText.Text = text;
        GreetingStatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GreetAtStart_Changed(object sender, RoutedEventArgs e)
    {
        if (changingBackgroundChoice) return;
        background = background with { GreetAtStart = GreetAtStartCheck.IsChecked == true };
        RenderBackground(background.Save(store?.DataDirectory) ? null : "Couldn't save this choice. It applies until Martlet closes.");
    }
}

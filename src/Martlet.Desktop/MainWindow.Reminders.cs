using System.Runtime.InteropServices;
using System.Windows.Threading;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Reminders ("remind me to do the dishes in an hour"): the reply model sets, lists and cancels them with the reminders
/// tool, and they travel with the shared settings as this PC's own entry ("reminders.desktop-a"), so every companion PC knows
/// them. Every 2 seconds a companion PC checks for due ones; when several companion PCs could say one, each bids how long its
/// user has been idle and the one used most recently says it (<see cref="Reminders.Decide"/>), so it is said once, where you
/// are. The PC that takes it brings it into its conversation (starting it hidden when none runs): Martlet reminds you on its own
/// as soon as it is free, or in its answer to what you say next. Where Martlet can't talk, a Windows notification says it.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer reminderTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    // Reminders this PC took and brought into a conversation, until Martlet said them.
    private readonly Dictionary<string, (BackgroundJob Job, LiveConversationWindow Talk, DateTimeOffset At)> remindersSaying = new(StringComparer.Ordinal);
    private bool remindersBusy;
    private DateTimeOffset remindersStartedAt;
    /// <summary>How long a reminder may wait in a conversation that can't talk (Thinking not set up) before a Windows
    /// notification says it instead.</summary>
    private static readonly TimeSpan ReminderTalkGrace = TimeSpan.FromSeconds(30);

    private void InitializeReminders()
    {
        reminderTimer.Tick += (_, _) => FollowRemindersAsync().Forget();
        if (conversation is not null && settingsNode is not null)
            conversation.RemindersTool = (arguments, token) =>
                Dispatcher.InvokeAsync(() => RunReminderToolAsync(arguments, token)).Task.Unwrap();
    }

    private void StartReminders()
    {
        if (settingsNode is null || closing) return;
        remindersStartedAt = DateTimeOffset.UtcNow;
        reminderTimer.Start();
    }

    /// <summary>Every computer's reminders entry from this PC's copy of the shared settings, by the device that wrote it.</summary>
    internal ReminderBoard RemindersNow()
    {
        if (settingsNode is null) return ReminderBoard.Empty;
        var entries = new Dictionary<string, ReminderEntry>(StringComparer.Ordinal);
        foreach (var setting in settingsNode.Document.Settings.Where(s => s.Key.StartsWith(SharedSettings.RemindersPrefix, StringComparison.Ordinal)))
            if (ReminderEntry.Read(setting.Value) is { } entry) entries[setting.UpdatedBy] = entry;
        return new(entries);
    }

    private ReminderEntry OwnReminders() =>
        settingsNode?.Document.Find(SharedPc.ReminderKey(ClusterDevice)) is { } own && own.UpdatedBy == ClusterDevice
            ? ReminderEntry.Read(own.Value) ?? ReminderEntry.Empty : ReminderEntry.Empty;

    /// <summary>Changes this PC's reminders entry between settings syncs and gives it to the hosts right away.</summary>
    private async Task ChangeRemindersAsync(Func<ReminderBoard, ReminderEntry, ReminderEntry?> change)
    {
        while (settingsBusy && !closing) await Task.Delay(100);
        if (closing || settingsNode is null) return;
        if (change(RemindersNow(), OwnReminders()) is not { } next) return;
        settingsNode.Put(SharedPc.ReminderKey(ClusterDevice), next.Write(), DateTimeOffset.UtcNow);
        QueueSettingsSync();
    }

    private Task MarkReminderAsync(string id, ReminderMarkKind kind, int? idle = null) =>
        ChangeRemindersAsync((board, own) => own.Mark(id, kind, DateTimeOffset.UtcNow, idle).Pruned(board, DateTimeOffset.UtcNow));

    /// <summary>The reminders tool for the reply model: runs on the UI thread, between settings syncs.</summary>
    private async Task<Reminders.ToolOutcome> RunReminderToolAsync(string arguments, CancellationToken token)
    {
        Reminders.ToolOutcome? outcome = null;
        token.ThrowIfCancellationRequested();
        await ChangeRemindersAsync((board, own) =>
        {
            outcome = Reminders.Run(arguments, board, ClusterDevice, own, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            return outcome.Own;
        });
        return outcome ?? new(new("Reminders can't be changed while Martlet is closing.", true), "closing", null);
    }

    /// <summary>No other companion PC could say a reminder: sync is off, or no other computer says it is a companion PC.</summary>
    private bool RemindersAlone() =>
        !clusterEnabled || settingsNode is null || !settingsNode.Document.Settings.Any(s => SharedPc.IsKey(s.Key) && s.UpdatedBy != ClusterDevice &&
            SharedPc.Read(s.Value)?.DeviceRole == DeviceRole.Companion);

    /// <summary>Seconds since someone last used this PC: the keyboard or mouse, or talking with Martlet here.</summary>
    private int ReminderIdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var idle = GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.FromDays(1);
        if (openConversation?.SinceActivity is { } talked && talked < idle) idle = talked;
        return (int)Math.Min(int.MaxValue, Math.Max(0, idle.TotalSeconds));
    }

    private async Task FollowRemindersAsync()
    {
        if (remindersBusy || closing || settingsNode is null || conversation is null || Role != DeviceRole.Companion) return;
        remindersBusy = true;
        try
        {
            await FollowSayingAsync();
            var now = DateTimeOffset.UtcNow;
            // Until the first sync with the hosts, another computer may have said a reminder already.
            if (clusterEnabled && settingsCheckedAt is null && now - remindersStartedAt < TimeSpan.FromSeconds(30)) return;
            var alone = RemindersAlone();
            foreach (var decision in Reminders.Decide(RemindersNow(), ClusterDevice, now, alone))
            {
                if (closing) return;
                var id = decision.Reminder.Reminder.Id;
                if (remindersSaying.ContainsKey(id))
                {
                    // Still waiting in this PC's conversation (Martlet paused, say): keep it, so the others stay quiet.
                    if (decision.Step is ReminderStep.Bid or ReminderStep.Claim) await MarkReminderAsync(id, ReminderMarkKind.Claim);
                    continue;
                }
                switch (decision.Step)
                {
                    case ReminderStep.Miss:
                        await MarkReminderAsync(id, ReminderMarkKind.Missed);
                        ErrorLog.Info($"Reminders: {id} was due over {Reminders.LatestLate.TotalHours:0} hours ago while no companion PC ran; let it go.");
                        break;
                    case ReminderStep.Bid:
                        var idle = ReminderIdleSeconds();
                        await MarkReminderAsync(id, ReminderMarkKind.Bid, idle);
                        ErrorLog.Info($"Reminders: {id} is due; offered to say it here (used {idle} s ago).");
                        break;
                    case ReminderStep.Claim:
                        if (!alone)
                        {
                            // Hear the other companion PCs' bids (and claims) once more before taking it.
                            while (settingsBusy && !closing) await Task.Delay(100);
                            await SyncSettingsAsync();
                            if (!Reminders.Decide(RemindersNow(), ClusterDevice, DateTimeOffset.UtcNow, alone)
                                    .Any(d => d.Reminder.Reminder.Id == id && d.Step == ReminderStep.Claim))
                                break;
                        }
                        await MarkReminderAsync(id, ReminderMarkKind.Claim);
                        ErrorLog.Info($"Reminders: {id} is said on this PC" + (alone ? "." : ", the companion PC used most recently."));
                        Say(decision.Reminder);
                        break;
                    case ReminderStep.Deliver:
                        Say(decision.Reminder);
                        break;
                }
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ContractException or InvalidOperationException)
        {
            ErrorLog.Warn("Reminders: couldn't follow due reminders.", error);
        }
        finally { remindersBusy = false; }
    }

    /// <summary>Brings a due reminder into the conversation (started hidden when none runs), or says it in a Windows notification
    /// where Martlet can't talk.</summary>
    private void Say(BoardReminder due)
    {
        var id = due.Reminder.Id;
        var now = DateTimeOffset.UtcNow;
        var talk = ConversationSession();
        if (talk?.Remind(due.Reminder.Text, Reminders.Due(due.Reminder, now, TimeZoneInfo.Local)) is { } job)
        {
            remindersSaying[id] = (job, talk, now);
            return;
        }
        NotifyReminder(due);
    }

    private void NotifyReminder(BoardReminder due)
    {
        remindersSaying.Remove(due.Reminder.Id);
        tray?.ShowNotice("Reminder", due.Reminder.Text);
        ErrorLog.Info($"Reminders: {due.Reminder.Id} shown as a Windows notification (Martlet can't talk here right now).");
        MarkReminderAsync(due.Reminder.Id, ReminderMarkKind.Done).Forget();
    }

    // What became of the reminders brought into a conversation: said (done), dropped with a conversation that ended (taken again
    // on the next check) or stuck in one that can't talk.
    private async Task FollowSayingAsync()
    {
        foreach (var (id, saying) in remindersSaying.ToArray())
        {
            var settled = RemindersNow().Find(id);
            if (saying.Job.Delivery == BackgroundDeliveryState.Delivered)
            {
                remindersSaying.Remove(id);
                await MarkReminderAsync(id, ReminderMarkKind.Done);
                ErrorLog.Info($"Reminders: Martlet reminded you of {id}.");
            }
            else if (saying.Job.Delivery == BackgroundDeliveryState.Dropped || !ReferenceEquals(saying.Talk, openConversation))
                remindersSaying.Remove(id);
            else if (DateTimeOffset.UtcNow - saying.At > ReminderTalkGrace && saying.Talk.CantTalk && settled is not null)
                NotifyReminder(settled);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

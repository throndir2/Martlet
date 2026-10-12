using System.IO;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>One record of conversations on every computer (docs/MEMORY.md, "One record on every computer"): the record of the
/// person signed in is the same on every computer where they are signed in, through the paired hosts, the way memories are.
/// Every 30 seconds while "Keep Martlet the same on all my computers" is on, and a few seconds after an exchange is recorded,
/// edited or deleted, this PC syncs the record month by month (<see cref="ConversationRecordSync"/>): it takes what the other
/// computers recorded, edited or deleted, and gives the hosts what changed here. It skips its turn while Martlet replies or you
/// talk, runs off the window's thread, and holds the record's files only while it reads or changes one month, so it never adds
/// time before the first word of a reply.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer conversationSyncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private ConversationRecordSync? conversationSync;
    private bool conversationSyncBusy, conversationSyncQueued;
    private string conversationSyncStatus = "Conversations: not synced yet.";

    private void InitializeConversationSync()
    {
        if (conversationHistory is not { } record || store is null) return;
        conversationSyncTimer.Tick += (_, _) => SyncConversationsAsync().Forget();
        if (accounts is not null)
        {
            // The record of the person signed in now, before anything records or recalls; then each switch, between replies.
            record.UseFolder(ConversationFolder(accounts.AccountId));
            accounts.AddChangeStep((change, _) =>
            {
                if (change.From is not null) record.UseFolder(ConversationFolder(change.To));
                return Task.CompletedTask;
            });
            accounts.AccountChanged += () => Dispatcher.BeginInvoke(() =>
            {
                conversationSync = null;
                QueueConversationSync();
            });
        }
        record.Changed += () => Dispatcher.BeginInvoke(QueueConversationSync);
        ShowConversationSyncStatus();
    }

    /// <summary>Where <paramref name="account"/>'s record is kept on this PC.</summary>
    private string ConversationFolder(Guid account) => DesktopConversationHistory.FolderFor(store!.DataDirectory, account, accounts?.OwnerId);

    private void StartConversationSync()
    {
        if (conversationHistory is null || closing) return;
        conversationSyncTimer.Start();
        SyncConversationsAsync().Forget();
    }

    /// <summary>Syncs soon (debounced), so what was said or changed here reaches the other computers quickly.</summary>
    private void QueueConversationSync()
    {
        if (conversationSyncQueued || conversationHistory is null || closing) return;
        conversationSyncQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { conversationSyncQueued = false; }
            await SyncConversationsAsync();
        }
    }

    private async Task SyncConversationsAsync()
    {
        if (conversationHistory is not { } history || conversationSyncBusy || closing) return;
        if (!clusterEnabled)
        {
            SetConversationSyncStatus("Your record of conversations stays on this PC while Keep Martlet the same on all my computers is off; " +
                "what changed here is shared when you turn it on.");
            return;
        }
        if (accounts is null)
        {
            SetConversationSyncStatus("Your record of conversations stays on this PC: Martlet couldn't tell who is signed in here.");
            return;
        }
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            SetConversationSyncStatus("Pair a Martlet host to have the same record of conversations on all your computers.");
            return;
        }
        // Never in a conversation's way: a reply or the owner talking holds the sync for the next check.
        if (Talking) return;
        conversationSyncBusy = true;
        var account = accounts.AccountId;
        var peers = hosts.Select(host => new HostConversationPeer(host.HostId, () => ClusterSync.Connect(host.Pairing), account)).ToArray();
        try
        {
            var record = history.Store;
            if (conversationSync?.History != record) conversationSync = new ConversationRecordSync(record, ClusterDevice);
            var sync = conversationSync;
            var token = lifetime.Token;
            var result = await Task.Run(() => sync.RunAsync(peers, DateTimeOffset.UtcNow, token), token);
            if (result.Taken > 0 || result.Deleted > 0)
            {
                ErrorLog.Info($"Conversations: took {result.Taken} exchange{(result.Taken == 1 ? "" : "s")} and deleted {result.Deleted} " +
                    "because of your other computers.");
                history.NotifyChanged();
            }
            if (result.Problem is not null) ErrorLog.Warn($"Conversations: couldn't keep the record the same here: {result.Problem}");
            SetConversationSyncStatus(ConversationSyncLine(result));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException)
        {
            SetConversationSyncStatus("Conversations: couldn't sync with your other computers: " + error.Message);
            ErrorLog.Warn("Conversation record sync failed; trying again on the next check.", error);
        }
        finally
        {
            foreach (var peer in peers) peer.Dispose();
            conversationSyncBusy = false;
        }
    }

    /// <summary>The status line: how many exchanges the record shares, on how many hosts it is the same, and what came from or
    /// was deleted because of the other computers. Counts only, never what was said.</summary>
    internal static string ConversationSyncLine(ConversationSyncResult result)
    {
        var hosts = result.Hosts;
        return $"Conversations: {result.Exchanges} exchange{(result.Exchanges == 1 ? "" : "s")} kept the same on {result.Same} of {hosts} " +
            $"host{(hosts == 1 ? "" : "s")}; checked {result.At.ToLocalTime():t}." +
            (result.Taken + result.Deleted > 0 ? $" Took {result.Taken} and deleted {result.Deleted} from your other computers." : "") +
            (result.Down > 0 ? $" {result.Down} not responding." : "") +
            (result.Old > 0 ? $" Update {(result.Old == 1 ? "one host" : result.Old + " hosts")} to share conversations." : "") +
            (result.Denied > 0 ? $" {(result.Denied == 1 ? "One host doesn't" : result.Denied + " hosts don't")} know you on this PC yet." : "") +
            (result.Unreadable > 0 ? $" {result.Unreadable} need a newer Martlet on this PC." : "") +
            (result.Problem is not null ? $" This PC couldn't take them yet: {result.Problem}" : "");
    }

    private void SetConversationSyncStatus(string text)
    {
        conversationSyncStatus = text;
        ShowConversationSyncStatus();
    }

    private void ShowConversationSyncStatus()
    {
        if (!closing) ConversationsPage.ShowSync(conversationSyncStatus);
    }
}

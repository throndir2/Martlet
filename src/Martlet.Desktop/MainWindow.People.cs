using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Speakers;

namespace Martlet.Desktop;

/// <summary>Companion › People: the voices Martlet recognizes and the names each goes by, and keeping that list the same on
/// all of the owner's computers. The list travels through the paired Martlet hosts (each keeps a copy, like the shared
/// who-does-what plan), so whichever computer becomes the companion recognizes the same people. People are always shared with
/// the household: the list syncs whatever "Keep Martlet the same on all my computers" says, and never with hosts a friend
/// shares.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer voiceSyncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool voiceSyncBusy, voiceSyncQueued, peopleStale;
    private string voiceSyncStatus = "Not synced yet.";

    private void InitializeVoiceSync()
    {
        InitializeVoiceRecognition();
        voiceSyncTimer.Tick += (_, _) => SyncVoicesAsync().Forget();
        localVoices.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            QueueVoiceSync();
            if (openTab != CompanionTab.People) return;
            // Never rebuild the page under the owner's typing; it refreshes when they leave the field.
            if (CompanionContent.IsKeyboardFocusWithin) peopleStale = true;
            else RenderTab();
        });
        localVoices.ClipsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (closing || openTab != CompanionTab.People) return;
            if (CompanionContent.IsKeyboardFocusWithin) peopleStale = true;
            else RenderTab();
        });
        CompanionContent.LostKeyboardFocus += (_, _) =>
        {
            if (peopleStale && openTab == CompanionTab.People && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
        };
    }

    private void StartVoiceSync()
    {
        if (store is null || closing) return;
        voiceSyncTimer.Start();
        SyncVoicesAsync().Forget();
    }

    /// <summary>Pushes a change soon (debounced), so another computer that becomes the companion sees it.</summary>
    private void QueueVoiceSync()
    {
        if (voiceSyncQueued || closing) return;
        voiceSyncQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { voiceSyncQueued = false; }
            await SyncVoicesAsync();
        }
    }

    /// <summary>Reads every paired host's copy of the voice list, merges it here and gives each host whose copy differs the
    /// merged list. It runs whatever "Keep Martlet the same on all my computers" says, so Martlet learns everyone's voice on
    /// every computer. Only your own hosts take part (<see cref="NetworkMap.Hosts"/> leaves out hosts a friend shares). Hosts
    /// older than voice sharing are skipped; a host older than account links gets the list without them (docs/ACCOUNTS.md).</summary>
    private async Task SyncVoicesAsync()
    {
        if (voiceSyncBusy || closing || store is null) return;
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            voiceSyncStatus = "No other computers are paired yet.";
            return;
        }
        voiceSyncBusy = true;
        try
        {
            var results = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    var linked = true;
                    VoiceRoster copy;
                    try { copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadVoicesAsync(lifetime.Token, linked: true)); }
                    catch (Martlet.HostClient.HostConnectionException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                    {
                        linked = false;
                        copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadVoicesAsync(lifetime.Token));
                    }
                    localVoices.Merge(copy);
                    var mine = localVoices.Roster;
                    if (copy.Digest() != (linked ? mine : mine.WithoutLinks()).Digest())
                        localVoices.Merge(await ClusterSync.WithConnectionAsync(host.Pairing,
                            connection => connection.MergeVoicesAsync(mine, lifetime.Token, linked)));
                    return (host.HostId, Ok: true, Old: false, Linked: linked);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Martlet.HostClient.HostConnectionException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    return (host.HostId, Ok: false, Old: true, Linked: false);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Ok: false, Old: false, Linked: false);
                }
            }));
            var ok = results.Count(r => r.Ok);
            var old = results.Where(r => r.Old).Select(r => r.HostId).ToArray();
            var unlinked = results.Where(r => r.Ok && !r.Linked).Select(r => r.HostId).ToArray();
            voiceSyncStatus = $"Synced with {ok} of {results.Length} computer{(results.Length == 1 ? "" : "s")} at {DateTime.Now:t}." +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} to sync voices there." : "") +
                (unlinked.Length > 0 ? $" Update {string.Join(", ", unlinked)} to share whose voice is whose there." : "");
        }
        catch (OperationCanceledException) { }
        finally
        {
            voiceSyncBusy = false;
            if (!closing && openTab == CompanionTab.People && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
        }
    }

    // ---------- the People page (Pages\People) ----------

    private void RenderPeopleTab(Panel page)
    {
        peopleStale = false;
        new PeoplePage(this).Render(page);
    }

    private void PlayClip(VoiceClip clip)
    {
        try
        {
            voicePlayer?.Stop();
            voicePlayer = new System.Media.SoundPlayer(clip.Path);
            voicePlayer.Play();
            ActionText.Text = $"Playing a clip from {PeoplePageModel.Ago(clip.At)}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't play the clip: {error.Message}";
        }
    }

    private static DockPanel Labeled(string label, UIElement control, double width)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 16, 0), LastChildFill = false };
        row.Children.Add(new TextBlock { Text = label, Width = width, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }
}

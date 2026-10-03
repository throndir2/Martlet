using System.IO;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Desktop;

/// <summary>Every host announces the Martlet release it runs in its network answer (docs/NETWORK.md), which this PC reads on
/// each network sync (every 20 seconds). When a host is updated, by this PC, by another of your computers or by Martlet on
/// that computer itself, this PC takes the new release at once: its Devices card, Home and the update prompts follow, so
/// nothing asks to update a host that already is.</summary>
public partial class MainWindow
{
    /// <summary>The release each paired host last announced; it fills in for hosts this PC hasn't checked this session.</summary>
    private readonly Dictionary<string, string> hostReleases = new(StringComparer.Ordinal);

    private void ObserveHostReleases(IReadOnlyDictionary<string, HostNetworkView> views)
    {
        var hardware = HardwareStore;
        var saved = hardware?.Load() ?? [];
        var updated = new List<string>();
        var shown = false;
        foreach (var view in views.Values)
        {
            if (view.MartletVersion is not { } now) continue;
            var id = view.HostId;
            var check = hostChecks.GetValueOrDefault(id);
            var record = saved.FirstOrDefault(h => h.HostId == id);
            var change = HostRelease.Compare(id, check?.MartletVersion ?? record?.MartletVersion ?? hostReleases.GetValueOrDefault(id), now, Version);
            hostReleases[id] = now;
            if (check is not null && check.MartletVersion != now)
            {
                hostChecks[id] = check with { MartletVersion = now };
                shown = true;
            }
            if (record is not null && record.MartletVersion != now)
            {
                try { hardware!.Save(record with { MartletVersion = now }); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Warn($"Could not save that {id} runs Martlet {now}: {error.Message}");
                }
                shown = true;
            }
            if (change.Changed && change.Before is { } before)
                ErrorLog.Info($"Martlet network: {id} now runs Martlet {now} (was {before}).");
            if (change.Updated) updated.Add(id);
            if (!change.Current) continue;
            // Whatever this PC said it was doing to bring this host up to date is over: it already runs this PC's release.
            if (hostUpdates.Current(id, now, DateTime.Now, seen: true)) shown = true;
        }
        if (closing) return;
        if (updated.Count > 0)
        {
            var releases = updated.Select(id => hostReleases[id]).Distinct(StringComparer.Ordinal).ToArray();
            ActionText.Text = releases.Length == 1
                ? $"{string.Join(", ", updated)} {(updated.Count == 1 ? "was" : "were")} updated to Martlet {releases[0]}."
                : "Updated: " + string.Join(", ", updated.Select(id => $"{id} to Martlet {hostReleases[id]}")) + ".";
            shown = true;
        }
        if (!shown) return;
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }
}

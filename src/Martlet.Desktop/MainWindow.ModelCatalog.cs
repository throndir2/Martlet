using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Martlet.Core.Planning;
using Martlet.Providers.ModelCatalogs;

namespace Martlet.Desktop;

/// <summary>The model catalog's daily refresh (docs/MODEL_CATALOG.md): three minutes after start and then every 15 minutes,
/// Martlet checks whether a day has passed
/// since the last refresh on this PC (any Windows user's Martlet counts: the daily copy is in the PC folder), and if so reads
/// the public sources in the background. It never starts while a conversation is replying or hearing you, and a reply that
/// starts holds its requests until it ends, so it adds no conversation latency. A refresh that fails keeps the last good copy;
/// until the first refresh, the snapshot shipped with Martlet is used.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer modelCatalogTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    private readonly DispatcherTimer modelCatalogWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private volatile bool modelCatalogHold;
    private bool modelCatalogBusy, checkedAtStart;
    private ModelCatalogStore? modelCatalogStore;

    /// <summary>The model catalog for this PC (the daily copy, else the shipped snapshot); null before Martlet has a data folder.
    /// Load it with <see cref="ModelCatalogStore.Load"/> off the reply path.</summary>
    internal ModelCatalogStore? ModelCatalogs => modelCatalogStore;

    private void StartModelCatalog()
    {
        if (store is null || modelCatalogStore is not null) return;
        modelCatalogStore = ModelCatalogStore.For(store.DataDirectory);
        modelCatalogWatch.Tick += (_, _) => modelCatalogHold = Talking || closing;
        modelCatalogTimer.Tick += (_, _) =>
        {
            modelCatalogTimer.Interval = TimeSpan.FromMinutes(15);
            RefreshModelCatalogAsync().Forget();
            // Once, three minutes after start: plan again with what changed since Martlet last ran (the hardware, the network,
            // measured numbers), and look for a retired model, off the reply path.
            if (!checkedAtStart)
            {
                checkedAtStart = true;
                CheckBetterSetupAsync("Martlet started").Forget();
                FollowRetiredAsync().Forget();
            }
        };
        // The first check comes three minutes after start, so it never competes with starting up.
        modelCatalogTimer.Start();
    }

    private async Task RefreshModelCatalogAsync()
    {
        if (modelCatalogBusy || closing || Talking || modelCatalogStore is not { } catalogs) return;
        modelCatalogBusy = true;
        modelCatalogHold = false;
        modelCatalogWatch.Start();
        try
        {
            var result = await Task.Run(async () =>
            {
                if (!catalogs.Due(DateTimeOffset.UtcNow)) return null;
                using var client = ModelCatalogRefresh.CreateClient();
                var refresh = new ModelCatalogRefresh(client, busy: () => modelCatalogHold || conversation?.Replying == true);
                return await refresh.RunIfDueAsync(catalogs, force: false, lifetime.Token).ConfigureAwait(false);
            }, lifetime.Token);
            if (result is not null)
            {
                ErrorLog.Info($"Model catalog refreshed: {result.Models} models and {result.Routes} routes" +
                    (result.Saved ? $", saved in {catalogs.Folder?.Where ?? "the PC folder"}" : ", not saved") +
                    (result.Status.Problem is { } problem ? $". {problem}" : "."));
                // Plan again with the new catalog, and look for a model it says was retired (docs/RECOMMENDATION_DESIGN.md).
                if (result.Saved)
                {
                    CheckBetterSetupAsync("the daily model catalog refresh").Forget();
                    FollowRetiredAsync().Forget();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            ErrorLog.Warn("Martlet couldn't refresh its model catalog; it keeps the last good copy.", error);
        }
        finally
        {
            modelCatalogWatch.Stop();
            modelCatalogBusy = false;
        }
    }
}

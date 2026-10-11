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
    private bool modelCatalogBusy;
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
        };
        // The first check comes three minutes after start, so it never competes with starting up.
        modelCatalogTimer.Start();
        // The planners' options come from the catalog (on a thread-pool thread; nothing talks yet at start).
        PlanningOptionsAsync().Forget();
    }

    /// <summary>The options the planners use (<see cref="PlanningCatalog"/>): the model catalog (the daily copy, else the shipped
    /// snapshot) with its local facts, plus Martlet's own list. It loads on a thread-pool thread, never on the reply path, with
    /// speed estimates for this PC's best graphics card; Martlet's own list stays when the catalog can't be read.</summary>
    internal async Task<FootprintCatalog> PlanningOptionsAsync()
    {
        if (store is null) return PlanningCatalog.Current;
        var directory = store.DataDirectory;
        var bandwidth = GraphicsCardBandwidth.Find(machine.BestGpu?.Name)?.Gbps;
        var before = PlanningCatalog.Current;
        try
        {
            var options = await Task.Run(() => PlanningCatalog.Load(directory, bandwidth), lifetime.Token);
            if (!ReferenceEquals(options, before))
                ErrorLog.Info($"Recommendations plan with {options.From}: {options.Options.Count(o => o.Origin == OptionOrigin.LocalFacts)} local " +
                    $"model option(s) from the catalog's local facts, {options.Options.Count(o => o.Origin == OptionOrigin.Catalog)} hosted model(s) " +
                    $"chosen from it, speed estimates for {(bandwidth is { } gbps ? $"{gbps:0} GB/s" : "an RTX 4070's 504 GB/s")}.");
            return options;
        }
        catch (OperationCanceledException) { return PlanningCatalog.Current; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ErrorLog.Warn("Martlet couldn't read its model catalog, so recommendations use its own list of models.", error);
            return PlanningCatalog.Current;
        }
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
                // The next recommendation plans with the new copy.
                if (result.Saved) await PlanningOptionsAsync();
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

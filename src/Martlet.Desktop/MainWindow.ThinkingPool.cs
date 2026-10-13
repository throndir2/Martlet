using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What MainWindow keeps for the Thinking pool. The page itself (Companion › Thinking pool) is
/// <see cref="ThinkingPoolPage"/> in Pages\ThinkingPool; MainWindow gives it its services here (<see cref="IThinkingPoolPageServices"/>,
/// at the end of this file). Also here: a paired computer joins the pool by itself when a check sees it answer
/// (<see cref="NoteThinkingPoolHost"/>), the Thinking pool's cloud providers, and research reports opened in the browser.</summary>
public partial class MainWindow : IThinkingPoolPageServices
{
    private ThinkingPoolPage? thinkingPoolPage;

    /// <summary>Companion › Thinking pool. One page for the window's life: it keeps its last check of a local model's fit.</summary>
    private ThinkingPoolPage ThinkingPoolPage => thinkingPoolPage ??= new(this);

    /// <summary>The Thinking pool's online providers, each with the smartest free model the model catalog names there (a few
    /// seconds don't matter for background thinks), else the preset's default.</summary>
    internal static IReadOnlyList<CloudProvider> DeepThinkingProviders =>
    [
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, Martlet.Core.Planning.PlanningCatalog.SmartestModel(e.Id) ?? e.DefaultModelId, true)),
        new("OpenAI", OpenAiChatBaseUrl, true, OpenAiTextGenerationCatalog.DefaultModelId, true),
        CustomCloud
    ];

    /// <summary>Opens a research report's web page (written by <see cref="ResearchReports"/>) in the default browser.</summary>
    private static void OpenReportPage(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        ErrorLog.Info("Web research: opened a report in the browser.");
    }

    /// <summary>A check saw <paramref name="hostId"/> answer: a paired computer with a Thinking model joins the Thinking pool by
    /// itself (<see cref="ThinkingPoolAutoJoin"/>; never one the owner took out), a member on its Ollama moves to its Thinking
    /// pool role once it has one, and a member on that role takes the role's slot count, so the broker places that many jobs
    /// there. thinking-pool.json is written only when something changed, and the owner is told once. Returns what it told the
    /// owner, or null.</summary>
    private string? NoteThinkingPoolHost(string hostId)
    {
        if (store is null || closing || hostChecks.GetValueOrDefault(hostId) is not { Reachable: true, Routes: { } routes }) return null;
        // A host a friend shares with this PC never joins the Thinking pool: background work there is its owner's to allow.
        if (sharedHosts.Any(h => h.HostId == hostId)) return null;
        var host = homeHosts.FirstOrDefault(h => h.HostId == hostId) ??
            (homeAvatar?.RemoteHost is { } lipSync && lipSync.HostId == hostId ? new PairedHost { Pairing = lipSync } : null);
        if (host is null) return null;
        // A file this Martlet can't read (a newer Martlet's, or a damaged one) is never written over.
        var (pool, state) = ThinkingPoolSettings.Read(store.DataDirectory);
        if (state == "unreadable") return null;
        var result = ThinkingPoolAutoJoin.For(pool, PoolHost(host, routes), NetworkMap.ThinkingHost(homeSettings),
            WorkSharingRoster.Settings(store.DataDirectory), WorkSharingRoster.Device, Role == DeviceRole.Host, DateTimeOffset.Now);
        if (!result.Changed) return null;
        bool saved;
        try { saved = result.Pool.Save(store.DataDirectory); }
        catch (ContractException) { saved = false; }
        if (!saved)
        {
            if (poolJoinWarned.Add(hostId)) ErrorLog.Warn($"Thinking pool: {hostId} couldn't join, because thinking-pool.json can't be saved.");
            return null;
        }
        poolJoinWarned.Remove(hostId);
        conversation?.ReloadThinkingPool();
        ErrorLog.Info("Thinking pool: " + result.Why);
        if (openTab == CompanionTab.DeepThinking && !tabEdited) RenderTab();
        if (result.OnlySlots) return null;
        ActionText.Text = result.Why;
        return result.Why;
    }

    private readonly HashSet<string> poolJoinWarned = new(StringComparer.Ordinal);

    /// <summary><paramref name="host"/> and the Thinking routes it offers now, as <see cref="ThinkingPoolAutoJoin"/> reads them.</summary>
    internal static ThinkingPoolHost PoolHost(PairedHost host, IReadOnlyList<HostRoute> routes) => new(host.HostId, host.Pairing.Origin,
        host.Pairing.SpkiFingerprint, host.Pairing.DeviceId, HostPairingCredential.ToGuid(host.Pairing.CredentialId),
        [.. routes.Where(r => r.RouteId == HostRoute.OllamaChatRouteId || SelfHostSetup.IsDeepThinkingRoute(r.RouteId))
            .Select(r => new ThinkingPoolOffer(r.RouteId, r.ModelId, r.MaximumConcurrency))]);

    // ---------- the Thinking pool page's services ----------

    string? IThinkingPoolPageServices.DataDirectory => store?.DataDirectory;
    AppSettings? IThinkingPoolPageServices.Settings => homeSettings;
    IReadOnlyList<PairedHost> IThinkingPoolPageServices.PairedHosts => NetworkMap.Hosts(Inputs());
    HostCheck? IThinkingPoolPageServices.CheckOf(string hostId) => hostChecks.GetValueOrDefault(hostId);
    HostHardware? IThinkingPoolPageServices.HardwareOf(string hostId) => HardwareStore?.Find(hostId);
    ModelAbilities IThinkingPoolPageServices.SavedModelAbilities() => SavedModelAbilities();
    IReadOnlyList<string>? IThinkingPoolPageServices.OllamaModels => ollamaModels;
    MachineInfo IThinkingPoolPageServices.Machine => machine;
    bool IThinkingPoolPageServices.IsHost => Role == DeviceRole.Host;
    bool IThinkingPoolPageServices.ToolsRefused(SetupRoute route) =>
        mcpTools.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId));
    string? IThinkingPoolPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool IThinkingPoolPageServices.ChangesRolesOn(PairedHost host) => ChangesRolesOn(host);
    IReadOnlyList<ThinkingPoolCooling> IThinkingPoolPageServices.PoolCooling() => conversation?.PoolCooling() ?? [];
    FirstWordTimes? IThinkingPoolPageServices.FirstWords => conversation?.FirstWords;
    ThinkingPoolStatus? IThinkingPoolPageServices.PoolStatusNow() => conversation?.PoolStatusNow();

    bool IThinkingPoolPageServices.Closing => closing;
    CancellationToken IThinkingPoolPageServices.Lifetime => lifetime.Token;
    void IThinkingPoolPageServices.ShowStatus(string text) => ActionText.Text = text;
    void IThinkingPoolPageServices.Redraw() => RenderTab();
    void IThinkingPoolPageServices.RedrawIfOpen()
    {
        if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
    }
    void IThinkingPoolPageServices.MarkEdited() => tabEdited = true;
    void IThinkingPoolPageServices.UseAutoSave(AutoSave autoSave) => tabAutoSave = autoSave;
    void IThinkingPoolPageServices.ReloadThinkingPool() => conversation?.ReloadThinkingPool();
    Task<bool> IThinkingPoolPageServices.SaveRepliesAsync(Func<GenerationSettings?, GenerationSettings?> update, Action<GenerationSettings?> saved,
        string done) => SaveRepliesAsync(update, saved, done);
    Task<string?> IThinkingPoolPageServices.RunHostActionAsync(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers) =>
        RunHostActionAsync(host, action, answers);
    async Task<HostCheck> IThinkingPoolPageServices.CheckHostAsync(PairedHost host)
    {
        var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
        hostChecks[host.HostId] = check;
        return check;
    }
    void IThinkingPoolPageServices.RunNodeAction(NodeAction action) => RunNodeAction(action);
    Task IThinkingPoolPageServices.CheckOllamaAsync() => CheckOllamaAsync();
    bool IThinkingPoolPageServices.Confirm(string question, string title, string yes, string no, string questionId) =>
        ConfirmationDialog.Confirm(this, question, title, yes, no, questionId);
}

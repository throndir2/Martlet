using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What still works: each job's coverage from what this PC already knows (saved routes, the last host checks,
/// shared who-does-what and failover), shown at the top of Home and under the Devices map with the effect and a fix; and the
/// platform guardrails that keep impossible choices off the menus. Contacts nothing by itself.</summary>
public partial class MainWindow
{
    private IReadOnlyList<JobCoverage> coverage = [];
    private string? coverageSignature;

    /// <summary>Everything this PC knows about each job, in the order thinking, listening, speaking, lip-sync.</summary>
    private IReadOnlyList<JobSituation> JobSituations()
    {
        var hardware = HardwareStore?.Load() ?? [];
        var computers = OtherComputers();
        string Name(string hostId) => NetworkMap.ComputerName(hostId, computers, hardware);
        var jobs = HostJob.All.Select(job => Situation(job, hardware, Name)).ToList();
        jobs.Add(LipSyncSituation(hardware, Name));
        return jobs;
    }

    private JobSituation Situation(HostJob job, IReadOnlyList<HostHardware> hardware, Func<string, string> hostName)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == job.Role);
        if (route is null) return new() { Job = job.Job, Doer = JobDoer.NotChosen };
        var name = NetworkMap.ProviderName(route);
        var enabled = route.Enabled != false;
        var reviewed = route.Consent is not null && route.Consent == route.Selection();
        if (SelfHostSetup.IsGateway(route.RouteType) && route.Gateway is { } gateway)
        {
            var saved = store is null ? null : JobSavedRoute.Load(store.DataDirectory, job.SavedFile);
            return new()
            {
                Job = job.Job, Doer = JobDoer.Host, DoerName = hostName(gateway.HostId), Enabled = enabled, Reviewed = reviewed,
                Host = HostState(gateway.HostId, job.Job, job.RouteId, job.Engine, hardware, hostName,
                    paired: route.CredentialId is not null && route.GatewayDeviceId is not null),
                Fallback = saved is null ? null : SavedName(saved), FallbackIsCloud = saved is not null && IsCloud(saved),
                StandIn = job.Role == SetupRole.Stt && ListeningStandIn(route) is { } standIn ? ParakeetName(standIn) : null
            };
        }
        if (route.RouteType == SetupRouteType.LocalParakeet)
            return new()
            {
                Job = job.Job, Doer = JobDoer.ThisDevice, DoerName = name, Enabled = enabled, Reviewed = reviewed,
                NotConnected = store is not null && Martlet.Sherpa.ParakeetEngine.Installed(LocalVoices.SpeechRoot(store.DataDirectory), route.ModelId)
                    ? null
                    : $"{ParakeetName(route.ModelId)} isn't downloaded, so choose it again in Listening to download it"
            };
        if (route.RouteType is SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWhisper)
            return new()
            {
                Job = job.Job, Doer = JobDoer.ThisDevice, DoerName = name, Enabled = enabled, Reviewed = reviewed,
                NotConnected = "this option can be saved, but conversations don't use it yet, so choose OpenAI or one of your hosts"
            };
        var keyMissing = route.CredentialId is null && (route.RouteType is null or SetupRouteType.OpenAi or SetupRouteType.ElevenLabs ||
            route.RouteType == SetupRouteType.ChatCompletions && ChatCompletionsEndpointCatalog.Named(route.Origin) is not null);
        return new()
        {
            Job = job.Job, Doer = JobDoer.Provider, DoerName = name, Enabled = enabled, Reviewed = reviewed, KeyMissing = keyMissing
        };
    }
}

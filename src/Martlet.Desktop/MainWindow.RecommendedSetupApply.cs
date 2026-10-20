using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Cluster;
using Martlet.Core.Nodes;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Applies the recommended setup to all your computers (docs/CLUSTER.md, "Applying the recommended setup") and shows
/// the Configuring state everywhere ("Configuring"). <see cref="PrepareRecommendedSetupAsync"/> reads what the review must
/// show before Reconfigure (each role's terms, secrets, downloads, changes that need someone at a computer or can't be made);
/// <see cref="ReconfigureAsync"/> runs <see cref="ApplyRecommendedSetupAsync"/> as a background task (its run window, in
/// Background tasks), which makes the changes in order through Martlet's existing paths: host roles through
/// Martlet on that computer (node commands), this PC's own host service or SSH; who does each job through the shared cluster
/// plan with failover on; Devices › Sharing work; then one cluster check, so every companion PC follows within one check. The
/// run is published as this PC's setup-run entry in the shared settings, so every computer shows Configuring.</summary>
public partial class MainWindow
{
    /// <summary>The background task (run window) that applies the recommended setup. The engine runs on this PC name it to other
    /// runs that wait for the same step.</summary>
    internal const string SetupRunTitle = "Reconfigure your computers";
    /// <summary>How long Prepare waits for Martlet on a host to read a role there.</summary>
    private static readonly TimeSpan DescribePatience = TimeSpan.FromMinutes(2);
    /// <summary>How long a role change waits for Martlet on that host to take it.</summary>
    private static readonly TimeSpan TakePatience = TimeSpan.FromMinutes(10);
}

/// <summary>Sends one command to Martlet on a host (through its paired gateway) and follows it to its end without a window, for
/// the runs that apply the recommended setup. A command still waiting after <c>patience</c> (Martlet isn't running there) is
/// withdrawn; canceling withdraws it or asks Martlet there to stop it.</summary>
internal static class SetupHostCommands
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1.5);
    /// <summary>How long the host may stay unreachable while a command runs (its gateway restarts during some changes).</summary>
    private static readonly TimeSpan UnreachablePatience = TimeSpan.FromMinutes(15);

    internal static async Task<Martlet.Core.Nodes.NodeCommand> RunAsync(HostConnection connection, string host, string kind,
        IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string>? secrets, TimeSpan patience,
        Action<string> status, CancellationToken cancel)
    {
        // Sending the same command again while it waits or runs returns that one, so a retry never sends it twice.
        var command = await connection.SendCommandAsync(kind, arguments, secrets, cancel);
        var sent = DateTimeOffset.UtcNow;
        DateTimeOffset? unreachableSince = null;
        try
        {
            while (!command.Finished)
            {
                if (command.State == NodeCommandState.Queued && DateTimeOffset.UtcNow - sent > patience)
                {
                    await WithdrawAsync(connection, command.Id);
                    throw new InvalidOperationException($"Martlet on {host} didn't take it within {patience.TotalMinutes:0} minutes. Open Martlet " +
                        "there (or let it start with Windows), then try again.");
                }
                status(command.State == NodeCommandState.Queued ? $"waiting for Martlet on {host}"
                    : command.Summary is { } summary ? summary : $"running on {host}");
                await Task.Delay(Poll, cancel);
                try
                {
                    command = await connection.ReadCommandAsync(command.Id, cancel);
                    unreachableSince = null;
                }
                catch (Exception error) when (error is HostConnectionException { Code: "host.unreachable" or "auth.clock" or "gateway.internal" } ||
                    error is OperationCanceledException && !cancel.IsCancellationRequested)
                {
                    unreachableSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - unreachableSince > UnreachablePatience)
                        throw new InvalidOperationException($"{host}'s host service stopped answering for {UnreachablePatience.TotalMinutes:0} minutes.");
                }
                catch (HostConnectionException error) when (error.Code == "command.not_found")
                {
                    throw new InvalidOperationException($"{host} no longer has this command (its host service restarted).");
                }
            }
            return command;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await WithdrawAsync(connection, command.Id);
            throw;
        }
    }

    private static async Task WithdrawAsync(HostConnection connection, string id)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.CancelCommandAsync(id, limit.Token);
        }
        catch (Exception error) when (error is HostConnectionException or OperationCanceledException or System.Net.Http.HttpRequestException or IOException) { }
    }
}

/// <summary>The changes this PC makes on a host's service right now (a role it installs or removes there, an update), by host
/// ID, so Home and the Devices map show that computer as Configuring while it works.</summary>
internal static class HostActivity
{
    private static readonly Lock Gate = new();
    private static readonly List<Entry> Entries = [];

    /// <summary>Raised (on any thread) when an activity starts, changes or ends.</summary>
    internal static event Action? Changed;

    /// <summary>What each host is busy with, the newest activity first.</summary>
    internal static IReadOnlyDictionary<string, string> Now
    {
        get
        {
            lock (Gate)
                return Entries.AsEnumerable().Reverse().GroupBy(e => e.Host, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
        }
    }

    /// <summary>Marks <paramref name="host"/> as busy with <paramref name="text"/> until the result is disposed.</summary>
    internal static Entry Begin(string host, string text)
    {
        var entry = new Entry(host, text);
        lock (Gate) Entries.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    internal sealed class Entry(string host, string text) : IDisposable
    {
        private readonly string start = text;
        internal string Host { get; } = host;
        internal string Text { get; private set; } = text;

        /// <summary>Adds what the change does now ("Installing Chatterbox Turbo: waiting for Martlet on gpu-box").</summary>
        internal void Update(string now)
        {
            var next = string.IsNullOrWhiteSpace(now) ? start : $"{start}: {now}";
            if (next == Text) return;
            Text = next.Length <= 200 ? next : next[..197] + "...";
            Changed?.Invoke();
        }

        public void Dispose()
        {
            bool removed;
            lock (Gate) removed = Entries.Remove(this);
            if (removed) Changed?.Invoke();
        }
    }
}

/// <summary>A role as the executor takes it (<see cref="SetupRoleNeeds"/>), from the desktop's reading of martlet-host describe.</summary>
internal static class SetupRoleReading
{
    /// <summary>The desktop's reading of a role (<see cref="HostRoleInputs"/>) as the executor takes it.</summary>
    internal static SetupRoleNeeds Needs(HostRoleInputs inputs) => new(inputs.Title, inputs.Terms)
    {
        Installed = inputs.Installed, GpuOrCpu = inputs.GpuOrCpu,
        GpuWhen = inputs.GpuWhen is { } gpuWhen ? new(gpuWhen.Variable, gpuWhen.Value) : null,
        Choices = [.. inputs.Choices.Select(c => new SetupRoleChoice(c.Variable, c.Options, c.Default,
            c.When is { } when ? new(when.Variable, when.Value) : null) { Open = c.Open })],
        Secrets = [.. inputs.Secrets.Select(s => new SetupRoleSecret(s.Name, s.Prompt, s.Stored,
            inputs.SecretWhen.TryGetValue(s.Name, out var when) ? new(when.Variable, when.Value) : null))],
        TermsWhen = [.. inputs.TermsWhen.Select(t => new SetupRoleTerms(new(t.Variable, t.Value), t.Text))],
        Gpus = [.. inputs.Gpus.Select(g => new SetupRoleCard(g.Id, g.Name, g.MemoryMb))],
        Current = inputs.Current, Stops = inputs.Stops
    };
}

/// <summary>What this PC knows when it reads how it would do a job no host does next (<see cref="SetupRoutes.Read"/>): whether
/// it is a host PC (it uses no jobs), its route for the job now, whether Ollama is missing, which Parakeet models it has (null:
/// Parakeet can't run here), the download size of the Ollama model named, when known, and for a model one of the owner's model
/// apps serves, the app found running it (<paramref name="Served"/>; null when Martlet didn't find it this time).</summary>
internal sealed record SetupRouteFacts(bool HostPc, SetupRoute? Route, bool OllamaMissing, Func<string, bool>? ParakeetInstalled,
    string? OllamaModelSize = null, ServedModel? Served = null);

/// <summary>How this PC does a job that no host does next, for the recommended setup: the Companion page's own choices it
/// makes itself (a model in this PC's Ollama, Parakeet) and, for everything else (a hosted provider that
/// needs your key), where you choose it. Reads nothing itself.</summary>
internal static class SetupRoutes
{
    internal static SetupRole Role(string job) => job switch
    {
        ClusterJobs.Thinking => SetupRole.Llm,
        ClusterJobs.Listening => SetupRole.Stt,
        _ => SetupRole.Tts
    };

    internal static string Title(string job) => char.ToUpperInvariant(job[0]) + job[1..];

    private static string Tab(string job) => job switch
    {
        ClusterJobs.Thinking => "Companion › Thinking",
        ClusterJobs.Listening => "Companion › Listening",
        ClusterJobs.Speaking => "Companion › Voice",
        _ => "Companion › Lip-sync"
    };

    internal static SetupRouteReading Read(string job, string optionId, SetupRouteFacts facts)
    {
        var tab = Tab(job);
        var option = PlanningCatalog.Current.Find(optionId);
        var name = option?.DisplayName ?? optionId;
        if (facts.HostPc)
            return new(SetupStepVerdict.NeedsOwner, $"Choose {name} for {job} in {tab} on your companion PC: this PC is a host PC and uses no jobs.");
        var route = facts.Route;
        switch (job, option)
        {
            case (ClusterJobs.Thinking, { ModelId: { } served }) when ServedModels.IsServed(option):
                if (route is { RouteType: SetupRouteType.ChatCompletions } && route.ModelId == served &&
                    (facts.Served is null || route.Origin == facts.Served.BaseUrl))
                    return new(SetupStepVerdict.Ready, $"Thinking already uses {served} on this PC.") { InUse = true };
                if (facts.Served is not { BaseUrl.Length: > 0 } app)
                    return new(SetupStepVerdict.NeedsOwner, $"Martlet didn't find the app that runs {served} on this PC: " +
                        $"start it, then choose it in {tab} › This PC.");
                return new(SetupStepVerdict.Ready, $"Thinking uses {served} in {app.AppName} on this PC, which already runs it, so nothing downloads.");
            case (ClusterJobs.Thinking, { IsLocal: true, HostRoleKind: HostRoles.Ollama, ModelId: { } model }):
                if (route is { RouteType: SetupRouteType.ChatCompletions, Origin: MainWindow.LocalOllamaBaseUrl } && route.ModelId == model)
                    return new(SetupStepVerdict.Ready, $"Thinking already uses {model} in Ollama on this PC.") { InUse = true };
                if (facts.OllamaMissing)
                    return new(SetupStepVerdict.NeedsOwner, $"Ollama isn't installed on this PC: install it in {tab}, then check again to use {name}.");
                var size = facts.OllamaModelSize;
                return new(SetupStepVerdict.Ready, $"Thinking uses {name} in Ollama on this PC.")
                {
                    Terms = $"Ollama downloads {model}{(size is null ? "" : $" ({size})")} from " +
                        $"{(model.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) ? "Hugging Face" : "ollama.com")} when it isn't on this PC yet, " +
                        "and the model's own license applies."
                };
            case (ClusterJobs.Listening, { RunsInApp: true, ModelId: { } id }) when Parakeet(id) is { } parakeet:
                if (route is { RouteType: SetupRouteType.LocalParakeet } && route.ModelId == parakeet.Id)
                    return new(SetupStepVerdict.Ready, $"Listening already uses {parakeet} on this PC.") { InUse = true };
                if (facts.ParakeetInstalled is not { } installed)
                    return new(SetupStepVerdict.CannotApply, "Parakeet can't run without Martlet's data folder on this PC.");
                return new(SetupStepVerdict.Ready, $"Listening uses {parakeet} on this PC's processor, so speech stays on this PC.")
                {
                    Terms = installed(parakeet.Id) ? null
                        : $"{parakeet} (NVIDIA, CC BY 4.0) downloads from Hugging Face ({Martlet.Sherpa.SherpaComponents.Megabytes(parakeet.DownloadBytes)})."
                };
            case (_, { IsLocal: false, ProviderId: { } provider }):
                if (route is { RouteType: null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions } && Provider(route) == provider)
                    return new(SetupStepVerdict.Ready, $"{Title(job)} already uses {name} on this PC.") { InUse = true };
                return new(SetupStepVerdict.NeedsOwner, $"Choose {name} for {job} in {tab}; it needs your API key there.");
            default:
                return new(SetupStepVerdict.NeedsOwner, $"Choose {name} for {job} in {tab}.");
        }
    }

    /// <summary>The Parakeet model a catalog option names ("parakeet-tdt-0.6b-v3" is the int8 download Martlet uses).</summary>
    internal static Martlet.Sherpa.ParakeetModel? Parakeet(string id) =>
        Martlet.Sherpa.ParakeetModels.Find(id) ?? Martlet.Sherpa.ParakeetModels.All.FirstOrDefault(m => m.Id.StartsWith(id + "-", StringComparison.Ordinal));

    /// <summary>The hosted provider a cloud route goes to, as the footprint catalog names it, or null.</summary>
    internal static string? Provider(SetupRoute route)
    {
        var origin = route.Origin.ToLowerInvariant();
        return route.RouteType == SetupRouteType.OpenAi || origin.Contains("openai.com", StringComparison.Ordinal) ? "openai"
            : origin.Contains("nvidia", StringComparison.Ordinal) ? "nvidia-build"
            : origin.Contains("openrouter", StringComparison.Ordinal) ? "openrouter" : null;
    }
}
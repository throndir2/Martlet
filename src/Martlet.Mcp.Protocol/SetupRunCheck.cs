using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Sync;

namespace Martlet.Mcp;

/// <summary>setup_run_status and setup_run_check: applying the recommended setup to all your computers and the Configuring
/// state (docs/CLUSTER.md, "Applying the recommended setup" and "Configuring"). The status reads a data directory's
/// shared-settings.json (every computer's published run: who started it and when, each computer's state and step, when it
/// finished), cluster.json (who does each job, with failover) and work-sharing.json. The check runs the production executor
/// (<see cref="SetupExecutor"/>) on a fixture recommendation against simulated computers: the preflight (terms, a secret,
/// someone needed there, a computer without a host service), then the run, and checks the host commands sent (arguments,
/// graphics card UUIDs, the secret only to its role), the plan assignments with failover, Sharing work, a failed step that
/// doesn't stop the others, a change missing from the review skipped, and the run record's states as every computer reads it
/// from the shared settings. NOT real hosts: no network, model or credential is used.</summary>
internal static class SetupRunCheck
{
    internal static Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var hasSettings = File.Exists(Path.Combine(dataDirectory, SharedSettingsState.FileName));
        var (document, _) = hasSettings ? SharedSettingsState.Load(dataDirectory) : (SharedSettings.Empty, []);
        var entries = document.Settings.Where(s => SetupRun.IsKey(s.Key)).ToArray();
        ClusterPlan? plan = null;
        try { plan = ClusterPlan.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "cluster.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { }
        var sharing = WorkSharingSettings.Load(dataDirectory);
        return Task.FromResult<object>(new
        {
            state = !hasSettings ? "none" : entries.Length == 0 ? "no-runs" : "loaded",
            why = !hasSettings ? "No shared-settings.json in this data directory yet." : entries.Length == 0 ? "No computer published a run yet." : null,
            runs = entries.Select(entry => (Entry: entry, Run: SetupRun.Read(entry.Value))).Select(x => x.Run is not { } run
                ? (object)new { key = x.Entry.Key, readable = false }
                : new
                {
                    key = x.Entry.Key, readable = true, runId = run.RunId, startedBy = run.StartedBy, startedAt = run.StartedAt,
                    updatedAt = run.UpdatedAt, finishedAt = run.FinishedAt, active = run.Active(now), shown = run.Shown(now),
                    summary = run.Summary(now),
                    machines = run.Machines.Select(m => new { machineId = m.MachineId, state = m.State.ToString(), step = m.Step, done = m.Done, steps = m.Steps })
                }).ToArray(),
            plan = plan?.Assignments.Where(a => ClusterJobs.All.Contains(a.Job))
                .Select(a => new { job = a.Job, hostId = a.HostId, off = a.Off, failover = a.Failover, movedFrom = a.MovedFrom, updatedBy = a.UpdatedBy, updatedAt = a.UpdatedAt }),
            sharing = WorkSharingJobs.All.Select(job => sharing.Job(job))
                .Select(rules => new { job = rules.Job, shares = rules.Shares, order = rules.Order, never = rules.Never })
        });
    }

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object? detail = null)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        var targets = new FixtureTargets();
        var recommendation = Fixture();
        var preflight = await SetupExecutor.PrepareAsync(recommendation, targets, cancellation);
        SetupPreflightItem Item(int index) => preflight.Items[index];
        Step("preflight: one item per change", preflight.Items.Count == recommendation.Changes.Count && preflight.Fingerprint == recommendation.Fingerprint);
        Step("preflight: Chatterbox's terms shown, on the RTX 4090 by UUID",
            Item(2).Verdict == SetupStepVerdict.Ready && Item(2).Terms?.Contains("Perth watermark") == true &&
            Item(2).Arguments.GetValueOrDefault("choice.gpu") == "GPU-aaa" && Item(2).Arguments.GetValueOrDefault("choice.CHATTERBOX_MODEL") == "chatterbox-turbo",
            Item(2).Arguments);
        Step("preflight: Parakeet chosen as stt's variant, with that variant's terms",
            Item(1).Arguments.GetValueOrDefault("choice.STT_ENGINE") == "parakeet" && Item(1).Arguments.GetValueOrDefault("choice.STT_MODEL") == "parakeet-tdt-110m-en" &&
            Item(1).Terms?.Contains("sherpa-onnx") == true && Item(1).Terms?.Contains("whisper.cpp") != true, Item(1).Arguments);
        Step("preflight: moving Thinking keeps its model and names the RTX 3060",
            Item(0).Arguments.GetValueOrDefault("choice.OLLAMA_MODEL") == "gemma4:12b" && Item(0).Arguments.GetValueOrDefault("choice.gpu") == "GPU-bbb",
            Item(0).Arguments);
        Step("preflight: Audio2Face shows its default engine's terms and needs the owner's NGC key", Item(4).Verdict == SetupStepVerdict.NeedsOwner &&
            Item(4).Terms == "The local engine's models use the NVIDIA Open Model License." &&
            Item(4).Arguments.GetValueOrDefault("choice.A2F_ENGINE") == "local" &&
            preflight.Unanswered.SingleOrDefault()?.Name == "ngc_api_key", new { Item(4).Terms, unanswered = preflight.Unanswered.Select(s => s.Prompt) });
        Step("preflight: someone at old-box makes its change", Item(5).Verdict == SetupStepVerdict.NeedsSomeoneThere, Item(5).Text);
        Step("preflight: laptop runs no host service", Item(6).Verdict == SetupStepVerdict.CannotApply, Item(6).Text);
        Step("preflight: the Thinking pool joins by itself", Item(11).Verdict == SetupStepVerdict.Automatic, Item(11).Text);
        Step("preflight: thinking in each companion's own Ollama is ready, with the download's terms",
            Item(8).Verdict == SetupStepVerdict.Ready && Item(8).Terms?.Contains("ollama.com") == true, Item(8).Text);
        Step("preflight: a hosted provider the owner must choose says where", Item(14).Verdict == SetupStepVerdict.NeedsOwner && !Item(14).Applies &&
            Item(14).Text.Contains("Companion"), Item(14).Text);
        Step("preflight: the role still doing that job stays until it moves", Item(16).Verdict == SetupStepVerdict.NeedsOwner && !Item(16).Applies,
            Item(16).Text);
        Step("preflight: downloads add up", Math.Abs(preflight.DownloadGb - 12.5) < 0.01, preflight.DownloadGb);

        var need = preflight.Unanswered.Single();
        var answered = preflight.WithSecret(need, "nvapi-fixture-0000");
        Step("the secret stays out of text", !answered.ToString().Contains("nvapi") && answered.Unanswered.Count == 0);
        var runs = new List<SetupRun>();
        var outcome = await SetupExecutor.ApplyAsync(recommendation, answered, targets, new SyncProgress<SetupRun>(runs.Add), cancellation);

        string[] expected =
        [
            "Add ollama on gpu-box (choice.OLLAMA_MODEL=gemma4:12b, choice.gpu=GPU-bbb, choice.accelerator=gpu)",
            "Add stt on desk-host (choice.STT_ENGINE=parakeet, choice.STT_MODEL=parakeet-tdt-110m-en)",
            "Add chatterbox on gpu-box (choice.CHATTERBOX_MODEL=chatterbox-turbo, choice.gpu=GPU-aaa)",
            "Add deep-thinking on linux-box (choice.OLLAMA_MODEL=gemma4:e4b)",
            "Add audio2face on gpu-box (choice.A2F_ENGINE=local, 1 secret redacted)",
            "Remove f5 on desk-host",
            "Remove xtts on linux-box"
        ];
        Step("host commands sent in order, nothing for skipped changes", targets.Commands.Select(c => c.ToString()).SequenceEqual(expected),
            targets.Commands.Select(c => c.ToString()));
        Step("the NGC key went only to Audio2Face", targets.Commands.Count(c => c.Secrets.Count > 0) == 1 &&
            targets.Commands.Single(c => c.Secrets.Count > 0).Secrets.GetValueOrDefault("secret.ngc_api_key") == "nvapi-fixture-0000");
        Step("terms recorded as accepted for each install and download shown", targets.Accepted.SequenceEqual(["ollama@gpu-box", "stt@desk-host",
            "chatterbox@gpu-box", "deep-thinking@linux-box", "audio2face@gpu-box", "thinking@"]), targets.Accepted);
        Step("thinking switched on this PC through the Companion path (each companion's own Ollama)", targets.Routes.SequenceEqual(["thinking=gemma4:e2b"]) &&
            outcome.Steps[8].State == SetupMachineState.Done, targets.Routes);
        Step("a provider the owner must choose is never reported done, and its plan doesn't change",
            outcome.Steps[14].State == SetupMachineState.NeedsAttention && outcome.Steps[14].Text.Contains("Choose") &&
            targets.Plan.For(ClusterJobs.Listening) is null, outcome.Steps[14].Text);
        Step("loudness lip-sync: lip-sync off in the plan", targets.Plan.For(ClusterJobs.LipSync) is { HostId: null, Off: true });
        Step("the role whose job didn't move stays (make before break)", outcome.Steps[16].State == SetupMachineState.NeedsAttention &&
            targets.Commands.All(c => c.RoleKind != "stt" || c.Add), outcome.Steps[16].Text);
        Step("plan: speaking on gpu-box with failover, thinking back to each PC's choice",
            targets.Plan.For(ClusterJobs.Speaking) is { HostId: "gpu-box", Failover: true } && targets.Plan.For(ClusterJobs.Thinking) is { HostId: null, Failover: false },
            targets.Plan.Assignments.Select(a => $"{a.Job}={a.HostId ?? "(each PC)"}{(a.Failover ? " failover" : "")}"));
        Step("Sharing work: desk-host speaks, linux-box never listens",
            targets.Sharing.Job(WorkSharingJobs.Speaking) is { Shares: true } speaking && !speaking.Never.Contains("desk-host") &&
            targets.Sharing.Job(WorkSharingJobs.Listening).Never.Contains("linux-box"), targets.Sharing.Share());
        Step("one cluster check at the end", targets.Checks == 1);
        Step("the failed step didn't stop the others", outcome.Steps[13].State == SetupMachineState.Failed &&
            outcome.Steps.Count == recommendation.Changes.Count, outcome.Steps[13].Text);
        Step("a job this PC can't follow yet needs the owner", outcome.Steps[7].State == SetupMachineState.NeedsAttention &&
            outcome.Steps[7].Text.Contains("choose the voice"), outcome.Steps[7].Text);

        var final = outcome.Run;
        Step("run: first published with every computer waiting", runs.FirstOrDefault()?.Machines.All(m => m.State == SetupMachineState.Pending) == true);
        Step("run: Configuring with the step and its count while it works",
            runs.Any(r => r.Machine("gpu-box") is { State: SetupMachineState.Configuring, Step: "Installing Chatterbox Turbo on its NVIDIA GeForce RTX 4090 (2 of 5)" }),
            runs.Select(r => r.Machine("gpu-box")?.Step).Distinct());
        Step("run: each computer ends as its steps did", final.Finished &&
            final.Machine("desk-host")?.State == SetupMachineState.Done && final.Machine("gpu-box")?.State == SetupMachineState.NeedsAttention &&
            final.Machine("linux-box")?.State == SetupMachineState.Failed && final.Machine("old-box")?.State == SetupMachineState.NeedsAttention &&
            final.Machine("laptop")?.State == SetupMachineState.NeedsAttention,
            final.Machines.Select(m => $"{m.MachineId}: {m.State} ({m.Done} of {m.Steps}) {m.Step}"));

        // Every computer reads it from the shared settings: a per-computer entry that isn't counted as a setting.
        var shared = SharedSettings.Merge(SharedSettings.Empty,
            SharedSettings.Empty.Put(SetupRun.Key(targets.Device), targets.Published.Last().Write(), null, targets.Device, DateTimeOffset.UtcNow));
        var copy = SharedSettings.Parse(shared.Write());
        Step("published as setup-run.<device>, a per-computer entry", SharedSettings.IsDeviceKey(SetupRun.Key(targets.Device)) &&
            SetupRun.All(copy).SingleOrDefault()?.RunId == final.RunId, SetupRun.Key(targets.Device));
        Step("another computer's summary", SetupRun.All(copy).Single().Summary(DateTimeOffset.UtcNow).StartsWith("Your computers were reconfigured", StringComparison.Ordinal),
            SetupRun.All(copy).Single().Summary(DateTimeOffset.UtcNow));

        // Consent: a change the review didn't show is never made.
        var extra = recommendation with
        {
            Changes = [.. recommendation.Changes, new SetupChange(SetupChangeKind.AddRole, "gpu-box", "Install Dia on gpu-box.", "fixture") { RoleKind = "dia" }]
        };
        var before = targets.Commands.Count;
        var unreviewed = await SetupExecutor.ApplyAsync(extra, answered, targets, null, cancellation);
        Step("a change missing from the review is skipped", unreviewed.Steps[^1].State == SetupMachineState.NeedsAttention &&
            targets.Commands.Skip(before).All(c => c.RoleKind != "dia"), unreviewed.Steps[^1].Text);

        return new
        {
            passed = ok,
            note = "Simulated computers (FIXTURE): no host, network, model or credential was used.",
            summary = outcome.Summary,
            steps
        };
    }

    /// <summary>The fixture network: gpu-box (two NVIDIA cards, through Martlet there), desk-host (this PC's own host service),
    /// linux-box (SSH), old-box (Martlet can't reach it) and laptop (a companion PC without a host service).</summary>
    private static NetworkRecommendation Fixture()
    {
        SetupChange Role(SetupChangeKind kind, string machine, string role, string summary) => new(kind, machine, summary, "fixture") { RoleKind = role };
        var target = new NetworkSetup([], [new JobPlan(ClusterJobs.Speaking, "gpu-box"), new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b"),
            new JobPlan(ClusterJobs.Listening, null, OptionId: "hosted:openai-transcribe"), new JobPlan(ClusterJobs.LipSync, null, OptionId: "loudness-lipsync")]);
        SetupChange[] changes =
        [
            Role(SetupChangeKind.MoveToGpu, "gpu-box", "ollama", "Move Thinking to gpu-box's RTX 3060.") with { GpuIndex = 1 },
            Role(SetupChangeKind.ChangeModel, "desk-host", "stt", "Listen with Parakeet on this PC.") with
                { Model = "parakeet-tdt-110m-en", FromModel = "small", DownloadGb = 0.5 },
            Role(SetupChangeKind.AddRole, "gpu-box", "chatterbox", "Install Chatterbox Turbo on gpu-box's RTX 4090.") with
                { Model = "chatterbox-turbo", GpuIndex = 0, DownloadGb = 8 },
            Role(SetupChangeKind.AddRole, "linux-box", "deep-thinking", "Add linux-box to the Thinking pool.") with { Model = "gemma4:e4b", DownloadGb = 4 },
            Role(SetupChangeKind.AddRole, "gpu-box", "audio2face", "Install lip-sync on gpu-box."),
            Role(SetupChangeKind.AddRole, "old-box", "ollama", "Install Thinking on old-box.") with { NeedsSomeoneThere = true, DownloadGb = 3 },
            Role(SetupChangeKind.AddRole, "laptop", "stt", "Install Listening on laptop."),
            new(SetupChangeKind.AssignJob, "gpu-box", "Speak with gpu-box.", "fixture") { Job = ClusterJobs.Speaking, FromMachineId = "desk-host" },
            new(SetupChangeKind.AssignJob, "", "Think with Gemma 4 E2B in each companion PC's own Ollama.", "fixture") { Job = ClusterJobs.Thinking, FromMachineId = "desk-host" },
            new(SetupChangeKind.JoinPool, "desk-host", "Let desk-host speak when gpu-box is busy.", "fixture") { Job = ClusterJobs.Speaking },
            new(SetupChangeKind.LeavePool, "linux-box", "Keep listening off linux-box.", "fixture") { Job = ClusterJobs.Listening },
            new(SetupChangeKind.JoinPool, "linux-box", "linux-box joins the Thinking pool.", "fixture"),
            Role(SetupChangeKind.RemoveRole, "desk-host", "f5", "Remove F5-TTS from this PC."),
            Role(SetupChangeKind.RemoveRole, "linux-box", "xtts", "Remove XTTS-v2 from linux-box."),
            // A job no host does next, by a hosted provider the owner must choose; loudness lip-sync; a role kept while its job can't move.
            new(SetupChangeKind.AssignJob, "", "Listen with OpenAI transcription.", "fixture") { Job = ClusterJobs.Listening, FromMachineId = "linux-box" },
            new(SetupChangeKind.AssignJob, "", "Move the mouth with the voice's loudness.", "fixture") { Job = ClusterJobs.LipSync, FromMachineId = "gpu-box" },
            Role(SetupChangeKind.RemoveRole, "linux-box", "stt", "Remove Listening from linux-box.")
        ];
        return new(new NetworkSetup([], []), target, changes) { Fingerprint = "fixture-1" };
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FixtureTargets : ISetupTargets
    {
        internal List<SetupRoleCommand> Commands { get; } = [];
        internal List<string> Accepted { get; } = [];
        internal List<SetupRun> Published { get; } = [];
        internal List<string> Routes { get; } = [];
        internal ClusterPlan Plan { get; private set; } = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, "desk-host", false, false, null, "fixture", DateTimeOffset.UtcNow.AddDays(-1));
        internal WorkSharingSettings Sharing { get; private set; } = new WorkSharingSettings().With(new WorkSharingJob
            { Job = WorkSharingJobs.Speaking, Never = ["desk-host"] });
        internal int Checks { get; private set; }

        public string Device => "desk-fixture";

        public SetupReach Reach(string machineId) => machineId switch
        {
            "gpu-box" or "desk-host" or "linux-box" => SetupReach.Yes,
            "old-box" => new(false, "Set how Martlet reaches old-box on the Devices map.", SomeoneThere: true),
            _ => new(false, $"{machineId} runs no host service, so it can't run host roles.")
        };

        public MachineSpecs? Specs(string machineId) => machineId == "gpu-box"
            ? new MachineSpecs("gpu-box", "gpu-box")
            {
                Gpus = [new("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, 24), new("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia, 12)]
            }
            : null;

        public string RoleName(string kind) => kind switch
        {
            "chatterbox" => "Chatterbox Turbo", "ollama" => "Thinking", "stt" => "Listening", "audio2face" => "Lip-sync",
            "deep-thinking" => "Thinking pool", _ => kind
        };

        public Task<SetupRoleNeeds> DescribeAsync(string machineId, string roleKind, CancellationToken cancel)
        {
            SetupRoleCard[] cards = machineId == "gpu-box"
                ? [new("GPU-aaa", "NVIDIA GeForce RTX 4090", 24564), new("GPU-bbb", "NVIDIA GeForce RTX 3060", 12288)] : [];
            string[] models = ["gemma4:e2b", "gemma4:e4b", "gemma4:12b"];
            return Task.FromResult(roleKind switch
            {
                "chatterbox" => new SetupRoleNeeds("Chatterbox Turbo voice", "Every reply carries Resemble AI's imperceptible Perth watermark.")
                    { Choices = [new("CHATTERBOX_MODEL", ["chatterbox-turbo"], "chatterbox-turbo")], Gpus = cards, Stops = ["f5"] },
                "stt" => new SetupRoleNeeds("Speech-to-text", "Paired desktops send your recorded speech to this host.")
                {
                    Installed = true, Current = new Dictionary<string, string> { ["STT_ENGINE"] = "whisper", ["STT_MODEL"] = "small" },
                    Choices = [new("STT_ENGINE", ["whisper", "parakeet"], "whisper"),
                        new("STT_MODEL", ["base", "small", "large-v3-turbo"], "small", new("STT_ENGINE", "whisper")),
                        new("STT_MODEL", ["parakeet-tdt-110m-en", "parakeet-tdt-0.6b-v3-int8"], "parakeet-tdt-110m-en", new("STT_ENGINE", "parakeet"))],
                    TermsWhen = [new(new("STT_ENGINE", "whisper"), "whisper.cpp (MIT) runs as its official container."),
                        new(new("STT_ENGINE", "parakeet"), "Parakeet runs on this host's CPU with sherpa-onnx.")],
                    GpuOrCpu = true, GpuWhen = new("STT_ENGINE", "whisper")
                },
                "audio2face" => new SetupRoleNeeds("Audio2Face-3D lip-sync", "")
                {
                    // As its role.conf: terms only per engine; the change names no engine, so the default's are shown and sent.
                    Choices = [new("A2F_ENGINE", ["local", "nim"], "local")],
                    TermsWhen = [new(new("A2F_ENGINE", "local"), "The local engine's models use the NVIDIA Open Model License."),
                        new(new("A2F_ENGINE", "nim"), "The NIM engine uses NVIDIA AI Enterprise terms.")],
                    Secrets = [new("ngc_api_key", "your NGC API key", false)], Gpus = cards
                },
                _ => new SetupRoleNeeds("Ollama", "Each model has its own license.")
                {
                    Installed = machineId == "gpu-box", Current = machineId == "gpu-box" ? new Dictionary<string, string> { ["OLLAMA_MODEL"] = "gemma4:12b" }
                        : new Dictionary<string, string>(),
                    Choices = [new("OLLAMA_MODEL", models, "gemma4:e2b")], Gpus = cards, GpuOrCpu = machineId == "gpu-box"
                }
            });
        }

        public Task<SetupStepResult> ChangeRoleAsync(SetupRoleCommand command, IProgress<string> progress, CancellationToken cancel)
        {
            Commands.Add(command);
            return Task.FromResult(command.RoleKind == "xtts"
                ? SetupStepResult.Failed("martlet-host remove xtts stopped on linux-box (exit 1).")
                : SetupStepResult.Done($"{command.RoleKind} {(command.Add ? "runs" : "was removed")} on {command.MachineId}."));
        }

        public Task<SetupStepResult> AssignJobAsync(string job, string? hostId, bool off, CancellationToken cancel)
        {
            Plan = Plan.Assign(job, hostId, off, hostId is not null && !off, null, Device, DateTimeOffset.UtcNow);
            return Task.FromResult(SetupStepResult.Done($"{job} now goes to {hostId ?? "each PC's choice"}."));
        }

        public Task<SetupStepResult> ShareAsync(string job, string machineId, bool join, CancellationToken cancel)
        {
            var rules = Sharing.Job(job);
            Sharing = Sharing.With(join ? rules with { Share = true, Never = [.. rules.Never.Where(n => n != machineId)] }
                : rules with { Never = [.. rules.Never, machineId] });
            return Task.FromResult(SetupStepResult.Done($"{job} sharing changed for {machineId}."));
        }

        public Task<SetupRouteReading> ReadRouteAsync(string job, string optionId, CancellationToken cancel) => Task.FromResult(optionId switch
        {
            "gemma4:e2b" => new SetupRouteReading(SetupStepVerdict.Ready, "Thinking uses Gemma 4 E2B in Ollama on this PC.")
                { Terms = "Ollama downloads gemma4:e2b (7.2 GB) from ollama.com when it isn't on this PC yet. The model's own license applies." },
            _ => new SetupRouteReading(SetupStepVerdict.NeedsOwner, $"Choose {optionId} for {job} in Companion; it needs your API key there.")
        });

        public Task<SetupStepResult> UseRouteAsync(string job, string optionId, CancellationToken cancel)
        {
            Routes.Add($"{job}={optionId}");
            Plan = Plan.Assign(job, null, false, false, null, Device, DateTimeOffset.UtcNow);
            return Task.FromResult(SetupStepResult.Done($"{job} uses {optionId} on this PC."));
        }

        public Task<IReadOnlyDictionary<string, string>> CheckAsync(CancellationToken cancel)
        {
            Checks++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                [ClusterJobs.Speaking] = "choose the voice for gpu-box under Speaking."
            });
        }

        public Task PublishAsync(SetupRun run, CancellationToken cancel)
        {
            run.Write();
            Published.Add(run);
            return Task.CompletedTask;
        }

        void ISetupTargets.Accepted(SetupPreflightItem item) => Accepted.Add($"{item.Change.RoleKind ?? item.Change.Job}@{item.Change.MachineId}");
    }
}

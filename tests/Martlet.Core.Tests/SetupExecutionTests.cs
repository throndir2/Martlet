using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

public sealed class SetupExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_run_goes_from_pending_through_configuring_to_how_each_computer_ended()
    {
        var run = SetupRun.Start("run-1", "desk-a", Now, [("gpu-box", 2), ("desk-host", 1)]);
        Assert.All(run.Machines, m => Assert.Equal(SetupMachineState.Pending, m.State));
        Assert.True(run.Active(Now));

        run = run.With("gpu-box", SetupMachineState.Configuring, "Installing Chatterbox Turbo (1 of 2)", 0, Now.AddSeconds(5));
        Assert.Equal("Installing Chatterbox Turbo (1 of 2)", run.Configuring.Single().Step);
        Assert.Contains("gpu-box: Installing Chatterbox Turbo (1 of 2).", run.Summary(Now.AddSeconds(6)));
        Assert.StartsWith("Configuring your computers: 0 of 2 finished.", run.Summary(Now.AddSeconds(6)), StringComparison.Ordinal);

        run = run.With("gpu-box", SetupMachineState.Failed, "martlet-host add chatterbox stopped (exit 1).", 1, Now.AddMinutes(1))
            .Finish(Now.AddMinutes(2));
        Assert.True(run.Finished);
        Assert.False(run.Active(Now.AddMinutes(2)));
        Assert.True(run.Shown(Now.AddMinutes(5)));
        Assert.False(run.Shown(Now.AddMinutes(2) + SetupRun.ShownAfterFinish));
        Assert.Equal(SetupMachineState.Failed, run.Machine("gpu-box")!.State);
        // desk-host never started: one change left, so it needs the owner.
        Assert.Equal(SetupMachineState.NeedsAttention, run.Machine("desk-host")!.State);
        Assert.Contains("1 failed, 1 needs you", run.Summary(Now.AddMinutes(3)));
    }

    [Fact]
    public void A_run_that_stops_changing_is_no_longer_active()
    {
        var run = SetupRun.Start("run-2", "desk-a", Now, [("gpu-box", 1)]);
        Assert.True(run.Active(Now + SetupRun.StaleAfter - TimeSpan.FromMinutes(1)));
        Assert.False(run.Active(Now + SetupRun.StaleAfter));
        Assert.StartsWith("Configuring your computers stopped", run.Summary(Now + SetupRun.StaleAfter), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_is_a_per_computer_shared_entry_every_computer_reads()
    {
        Assert.Equal("setup-run.desk-a-b", SetupRun.Key("DESK-A_b"));
        Assert.True(SharedSettings.IsDeviceKey(SetupRun.Key("desk-a")));
        Assert.True(SharedSettings.IsKey(SetupRun.Key("desk-a")));

        var run = SetupRun.Start("run-3", "desk-a", Now, [("gpu-box", 1)])
            .With("gpu-box", SetupMachineState.Configuring, "Installing Thinking (1 of 1)", 0, Now);
        var shared = SharedSettings.Empty.Put(SetupRun.Key("desk-a"), run.Write(), null, "desk-a", Now)
            .Put("talk", "{}", null, "desk-a", Now);
        var copy = SharedSettings.Parse(shared.Write());
        var read = Assert.Single(SetupRun.All(copy));
        Assert.Equal(run.RunId, read.RunId);
        Assert.Equal("Installing Thinking (1 of 1)", read.Machine("gpu-box")!.Step);
        Assert.Equal(SetupMachineState.Configuring, read.Machine("gpu-box")!.State);
    }

    [Fact]
    public void An_unreadable_run_is_left_out()
    {
        Assert.Null(SetupRun.Read("{\"schema_version\":2,\"run_id\":\"r\",\"started_by\":\"d\",\"started_at\":\"2026-10-07T18:00:00Z\",\"updated_at\":\"2026-10-07T18:00:00Z\"}"));
        Assert.Null(SetupRun.Read("not json"));
        var twice = SetupRun.Start("run-4", "desk-a", Now, [("gpu-box", 1)]) with
        {
            Machines = [new() { MachineId = "gpu-box" }, new() { MachineId = "gpu-box" }]
        };
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => twice.Write());
    }

    [Fact]
    public void Arguments_pick_the_models_variant_and_keep_what_an_installed_role_runs_with()
    {
        var stt = new SetupRoleNeeds("stt", "")
        {
            Installed = true, Current = new Dictionary<string, string> { ["STT_ENGINE"] = "whisper", ["STT_MODEL"] = "small" },
            Choices = [new("STT_ENGINE", ["whisper", "parakeet"], "whisper"),
                new("STT_MODEL", ["small", "large-v3-turbo"], "small", new("STT_ENGINE", "whisper")),
                new("STT_MODEL", ["parakeet-tdt-110m-en"], "parakeet-tdt-110m-en", new("STT_ENGINE", "parakeet"))],
            GpuOrCpu = true, GpuWhen = new("STT_ENGINE", "whisper")
        };
        var parakeet = new SetupChange(SetupChangeKind.ChangeModel, "desk-host", "s", "w") { RoleKind = "stt", Model = "parakeet-tdt-110m-en" };
        var (arguments, problem) = SetupExecutor.Arguments(parakeet, stt, null);
        Assert.Null(problem);
        Assert.Equal("parakeet", arguments["choice.STT_ENGINE"]);
        Assert.Equal("parakeet-tdt-110m-en", arguments["choice.STT_MODEL"]);

        var turbo = parakeet with { Model = "large-v3-turbo" };
        (arguments, problem) = SetupExecutor.Arguments(turbo, stt, null);
        Assert.Equal(("whisper", "large-v3-turbo"), (arguments["choice.STT_ENGINE"], arguments["choice.STT_MODEL"]));

        (_, problem) = SetupExecutor.Arguments(parakeet with { Model = "whisper-xxl" }, stt, null);
        Assert.Contains("doesn't offer whisper-xxl", problem);
        (_, problem) = SetupExecutor.Arguments(parakeet with { Model = null }, stt, null);
        Assert.Contains("names no model", problem);
    }

    [Fact]
    public async Task A_role_installed_with_its_default_variant_shows_and_sends_that_variant()
    {
        // Audio2Face: its terms are only per engine, and the change names no engine.
        var targets = new Targets
        {
            Needs = new SetupRoleNeeds("Audio2Face", "")
            {
                Choices = [new("A2F_ENGINE", ["local", "nim"], "local"), new("A2F_MODEL", ["claire", "mark"], "claire")],
                TermsWhen = [new(new("A2F_ENGINE", "local"), "NVIDIA Open Model License."), new(new("A2F_ENGINE", "nim"), "NVIDIA AI Enterprise.")],
                Secrets = [new("ngc_api_key", "your NGC API key", false, new("A2F_ENGINE", "nim"))]
            }
        };
        SetupChange[] changes = [new(SetupChangeKind.AddRole, "gpu-box", "Install lip-sync on gpu-box.", "w") { RoleKind = "audio2face" }];
        var recommendation = new NetworkRecommendation(new([], []), new([], []), changes);
        var preflight = await SetupExecutor.PrepareAsync(recommendation, targets, default);
        var item = preflight.Items.Single();
        Assert.Equal(SetupStepVerdict.Ready, item.Verdict);
        Assert.Equal("NVIDIA Open Model License.", item.Terms);
        Assert.Equal("local", item.Arguments["choice.A2F_ENGINE"]);
        Assert.False(item.Arguments.ContainsKey("choice.A2F_MODEL"));
        Assert.Empty(item.Secrets);

        await SetupExecutor.ApplyAsync(recommendation, preflight, targets, null, default);
        Assert.Equal("Add audio2face on gpu-box (choice.A2F_ENGINE=local)", targets.Commands.Single().ToString());
        Assert.Equal(["audio2face@gpu-box"], targets.Accepted);
    }

    [Fact]
    public void A_card_is_found_by_name_then_by_its_place_among_the_NVIDIA_cards()
    {
        var specs = new MachineSpecs("gpu-box", "gpu-box")
        {
            Gpus = [new("AMD Radeon RX 7900", GpuVendor.Amd, 24), new("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, 24),
                new("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, 24), new("NVIDIA RTX A4000", GpuVendor.Nvidia, 16)]
        };
        SetupRoleCard[] cards = [new("GPU-1", "NVIDIA GeForce RTX 4090", 24564), new("GPU-2", "NVIDIA GeForce RTX 4090", 24564),
            new("GPU-3", "RTX A4000 (renamed)", 16376)];
        Assert.Equal("GPU-1", SetupExecutor.Card(specs, 1, cards).Card!.Id);
        Assert.Equal("GPU-2", SetupExecutor.Card(specs, 2, cards).Card!.Id);
        // No name matches: the third NVIDIA card.
        Assert.Equal("GPU-3", SetupExecutor.Card(specs, 3, cards).Card!.Id);
        Assert.Contains("isn't an NVIDIA card", SetupExecutor.Card(specs, 0, cards).Problem);
        Assert.Contains("no graphics card 9", SetupExecutor.Card(specs, 8, cards).Problem);
        // One card or none: nothing to choose.
        Assert.Equal((null, null), SetupExecutor.Card(specs, 1, []));

        // Windows and nvidia-smi may list cards in different orders: a 4070 never takes the 4070 Ti.
        var mixed = new MachineSpecs("pc", "pc")
        {
            Gpus = [new("NVIDIA GeForce RTX 4070", GpuVendor.Nvidia, 12), new("NVIDIA GeForce RTX 4070 Ti", GpuVendor.Nvidia, 12)]
        };
        SetupRoleCard[] smi = [new("GPU-ti", "NVIDIA GeForce RTX 4070 Ti", 12282), new("GPU-plain", "NVIDIA GeForce RTX 4070", 12282)];
        Assert.Equal("GPU-plain", SetupExecutor.Card(mixed, 0, smi).Card!.Id);
        Assert.Equal("GPU-ti", SetupExecutor.Card(mixed, 1, smi).Card!.Id);
    }

    [Fact]
    public void A_run_left_by_a_closed_Martlet_ends_with_what_still_needs_doing()
    {
        var run = SetupRun.Start("run-5", "desk-a", Now, [("gpu-box", 2), ("desk-host", 1), ("old-box", 1)])
            .With("gpu-box", SetupMachineState.Configuring, "Installing Thinking (2 of 2)", 1, Now)
            .With("desk-host", SetupMachineState.Done, "All 1 change made", 1, Now)
            .With("old-box", SetupMachineState.Failed, "exit 1", 0, Now);
        var ended = run.Interrupted(Now.AddMinutes(1), "Martlet closed first.");
        Assert.True(ended.Finished);
        Assert.Equal((SetupMachineState.NeedsAttention, "Martlet closed first."), (ended.Machine("gpu-box")!.State, ended.Machine("gpu-box")!.Step));
        Assert.Equal(SetupMachineState.Done, ended.Machine("desk-host")!.State);
        Assert.Equal(SetupMachineState.Failed, ended.Machine("old-box")!.State);
    }

    [Fact]
    public async Task Apply_makes_the_reviewed_changes_in_order_and_continues_past_a_failed_one()
    {
        var targets = new Targets();
        var target = new NetworkSetup([], [new JobPlan(ClusterJobs.Speaking, "gpu-box"), new JobPlan(ClusterJobs.Thinking, null)]);
        SetupChange[] changes =
        [
            new(SetupChangeKind.AddRole, "gpu-box", "Install Chatterbox Turbo on gpu-box.", "w") { RoleKind = "chatterbox", Model = "chatterbox-turbo" },
            new(SetupChangeKind.AddRole, "gpu-box", "Install lip-sync on gpu-box.", "w") { RoleKind = "audio2face" },
            new(SetupChangeKind.AssignJob, "gpu-box", "Speak with gpu-box.", "w") { Job = ClusterJobs.Speaking },
            new(SetupChangeKind.AssignJob, "", "Think with a hosted provider.", "w") { Job = ClusterJobs.Thinking, FromMachineId = "desk-host" },
            new(SetupChangeKind.JoinPool, "desk-host", "Let desk-host speak too.", "w") { Job = ClusterJobs.Speaking },
            new(SetupChangeKind.JoinPool, "gpu-box", "gpu-box joins the Thinking pool.", "w"),
            new(SetupChangeKind.RemoveRole, "desk-host", "Remove XTTS-v2.", "w") { RoleKind = "xtts" },
            new(SetupChangeKind.RemoveRole, "desk-host", "Remove F5-TTS.", "w") { RoleKind = "f5" }
        ];
        var recommendation = new NetworkRecommendation(new([], []), target, changes) { Fingerprint = "f1" };
        var preflight = await SetupExecutor.PrepareAsync(recommendation, targets, default);
        Assert.Equal([SetupStepVerdict.Ready, SetupStepVerdict.NeedsOwner, SetupStepVerdict.Ready, SetupStepVerdict.Ready, SetupStepVerdict.Ready,
            SetupStepVerdict.Automatic, SetupStepVerdict.Ready, SetupStepVerdict.Ready], preflight.Items.Select(i => i.Verdict));
        Assert.Equal("Chatterbox terms.", preflight.Items[0].Terms);
        Assert.Contains("F5-TTS stops there first", preflight.Items[0].Text);

        var runs = new List<SetupRun>();
        var outcome = await SetupExecutor.ApplyAsync(recommendation, preflight, targets, new Collect(runs), default);

        // The key wasn't entered: lip-sync is skipped and says how to finish it; the failing XTTS removal doesn't stop F5's.
        Assert.Equal(["Add chatterbox on gpu-box (choice.CHATTERBOX_MODEL=chatterbox-turbo)", "Remove xtts on desk-host", "Remove f5 on desk-host"],
            targets.Commands.Select(c => c.ToString()));
        Assert.Equal(SetupMachineState.NeedsAttention, outcome.Steps[1].State);
        Assert.Contains("your NGC API key", outcome.Steps[1].Text);
        Assert.Equal(SetupMachineState.Failed, outcome.Steps[6].State);
        Assert.Equal(SetupMachineState.Done, outcome.Steps[7].State);
        Assert.Equal(["speaking=gpu-box", "thinking=(each PC)"], targets.Assigned);
        Assert.Equal(["speaking+desk-host"], targets.Shared);
        Assert.Equal(["chatterbox@gpu-box"], targets.Accepted);
        // Speaking is recorded, but this PC can't follow it yet (no voice chosen): that needs the owner too.
        Assert.Equal(SetupMachineState.NeedsAttention, outcome.Steps[2].State);
        Assert.Contains("This PC follows it as soon as it can: choose the voice.", outcome.Steps[2].Text);
        Assert.False(outcome.Succeeded);
        Assert.Equal("Reconfigured your computers: 5 of 8 changes made, 1 failed, 2 need you.", outcome.Summary);

        // A job with no host next shows on the host it leaves.
        Assert.Equal(4, runs[0].Machine("desk-host")!.Steps);
        Assert.Contains(runs, r => r.Machine("desk-host") is { State: SetupMachineState.Configuring, Step: "Handing thinking back to each companion PC's own choice (1 of 4)" });
        Assert.Equal(SetupMachineState.NeedsAttention, outcome.Run.Machine("gpu-box")!.State);
        Assert.Equal(SetupMachineState.Failed, outcome.Run.Machine("desk-host")!.State);
        Assert.True(outcome.Run.Finished);
        Assert.Same(targets.Published[^1], outcome.Run);
        Assert.Equal(1, targets.Checks);

        // With the key entered, lip-sync is installed with it, and nothing else carries it.
        var answered = preflight.WithSecret(preflight.Unanswered.Single(), "nvapi-test");
        Assert.DoesNotContain("nvapi", answered.ToString());
        targets.Commands.Clear();
        await SetupExecutor.ApplyAsync(recommendation, answered, targets, null, default);
        var lipSync = Assert.Single(targets.Commands, c => c.RoleKind == "audio2face");
        Assert.Equal("nvapi-test", lipSync.Secrets["secret.ngc_api_key"]);
        Assert.All(targets.Commands.Where(c => c.RoleKind != "audio2face"), c => Assert.Empty(c.Secrets));
    }

    [Fact]
    public async Task Apply_never_makes_a_change_the_review_did_not_show_or_one_it_could_not_make()
    {
        var targets = new Targets();
        SetupChange[] changes =
        [
            new(SetupChangeKind.AddRole, "old-box", "Install Thinking on old-box.", "w") { RoleKind = "ollama", NeedsSomeoneThere = true },
            new(SetupChangeKind.AddRole, "laptop", "Install Listening on laptop.", "w") { RoleKind = "stt" }
        ];
        var recommendation = new NetworkRecommendation(new([], []), new([], []), changes) { Fingerprint = "f2" };
        var preflight = await SetupExecutor.PrepareAsync(recommendation, targets, default);
        Assert.Equal(SetupStepVerdict.NeedsSomeoneThere, preflight.Items[0].Verdict);
        Assert.Equal(SetupStepVerdict.CannotApply, preflight.Items[1].Verdict);
        Assert.False(preflight.CanApply);

        var more = recommendation with
        {
            Changes = [.. changes, new SetupChange(SetupChangeKind.AddRole, "gpu-box", "Install Dia.", "w") { RoleKind = "dia" }]
        };
        var outcome = await SetupExecutor.ApplyAsync(more, preflight, targets, null, default);
        Assert.Empty(targets.Commands);
        Assert.All(outcome.Steps, s => Assert.Equal(SetupMachineState.NeedsAttention, s.State));
        Assert.Contains("wasn't in the review", outcome.Steps[2].Text);
    }

    [Fact]
    public async Task Canceling_stops_before_the_next_change_and_still_publishes_the_end()
    {
        var targets = new Targets();
        using var cancel = new CancellationTokenSource();
        targets.OnCommand = cancel.Cancel;
        SetupChange[] changes =
        [
            new(SetupChangeKind.RemoveRole, "gpu-box", "Remove XTTS-v2.", "w") { RoleKind = "f5" },
            new(SetupChangeKind.RemoveRole, "gpu-box", "Remove Dia.", "w") { RoleKind = "dia" }
        ];
        var recommendation = new NetworkRecommendation(new([], []), new([], []), changes);
        var preflight = await SetupExecutor.PrepareAsync(recommendation, targets, default);
        var outcome = await SetupExecutor.ApplyAsync(recommendation, preflight, targets, null, cancel.Token);
        Assert.Single(targets.Commands);
        Assert.Equal("Stopped before it ran.", outcome.Steps[1].Text);
        Assert.True(targets.Published[^1].Finished);
        Assert.Equal(0, targets.Checks);
    }

    private sealed class Collect(List<SetupRun> runs) : IProgress<SetupRun>
    {
        public void Report(SetupRun value) => runs.Add(value);
    }

    private sealed class Targets : ISetupTargets
    {
        internal List<SetupRoleCommand> Commands { get; } = [];
        internal List<string> Assigned { get; } = [];
        internal List<string> Shared { get; } = [];
        internal List<string> Accepted { get; } = [];
        internal List<SetupRun> Published { get; } = [];
        internal int Checks { get; private set; }
        internal Action? OnCommand { get; set; }
        /// <summary>What every role needs, instead of the defaults below.</summary>
        internal SetupRoleNeeds? Needs { get; init; }

        public string Device => "desk-test";

        public SetupReach Reach(string machineId) => machineId switch
        {
            "gpu-box" or "desk-host" => SetupReach.Yes,
            "old-box" => new(false, "Set how Martlet reaches old-box.", SomeoneThere: true),
            _ => new(false, $"{machineId} runs no host service.")
        };

        public MachineSpecs? Specs(string machineId) => null;

        public string RoleName(string kind) => kind switch { "f5" => "F5-TTS", "chatterbox" => "Chatterbox Turbo", _ => kind };

        public Task<SetupRoleNeeds> DescribeAsync(string machineId, string roleKind, CancellationToken cancel) => Task.FromResult(Needs ?? roleKind switch
        {
            "chatterbox" => new SetupRoleNeeds("Chatterbox", "Chatterbox terms.")
                { Choices = [new("CHATTERBOX_MODEL", ["chatterbox-turbo"], "chatterbox-turbo")], Stops = ["f5"] },
            "audio2face" => new SetupRoleNeeds("Audio2Face", "NVIDIA terms.") { Secrets = [new("ngc_api_key", "your NGC API key", false)] },
            _ => new SetupRoleNeeds(roleKind, "")
        });

        public Task<SetupStepResult> ChangeRoleAsync(SetupRoleCommand command, IProgress<string> progress, CancellationToken cancel)
        {
            Commands.Add(command);
            OnCommand?.Invoke();
            return Task.FromResult(command.RoleKind == "xtts" ? SetupStepResult.Failed("exit 1") : SetupStepResult.Done("ok"));
        }

        public Task<SetupStepResult> AssignJobAsync(string job, string? hostId, bool off, CancellationToken cancel)
        {
            Assigned.Add($"{job}={hostId ?? "(each PC)"}");
            return Task.FromResult(SetupStepResult.Done("assigned"));
        }

        public Task<SetupStepResult> ShareAsync(string job, string machineId, bool join, CancellationToken cancel)
        {
            Shared.Add($"{job}{(join ? "+" : "-")}{machineId}");
            return Task.FromResult(SetupStepResult.Done("shared"));
        }

        public Task<IReadOnlyDictionary<string, string>> CheckAsync(CancellationToken cancel)
        {
            Checks++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [ClusterJobs.Speaking] = "choose the voice." });
        }

        public Task PublishAsync(SetupRun run, CancellationToken cancel)
        {
            run.Write();
            Published.Add(run);
            return Task.CompletedTask;
        }

        void ISetupTargets.Accepted(SetupPreflightItem item) => Accepted.Add($"{item.Change.RoleKind}@{item.Change.MachineId}");
    }
}

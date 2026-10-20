using Martlet.HostClient;
using Martlet.Core.Nodes;
using static Martlet.NodeLinkCheck.CheckHelpers;

namespace Martlet.NodeLinkCheck;

/// <summary>The node link check's live mode (<c>live &lt;pairing-code&gt; &lt;container&gt;</c>).</summary>
internal static class LiveGatewayCheck
{
    /// <summary>Against a real Linux gateway in a Docker container (a disposable one, built from this checkout): pairs with the one-use code
    /// it printed, reads its agent token the way Martlet on a host PC does (docker exec ... cat), and checks the mailbox, the saved
    /// commands.json and a new token after a restart.</summary>
    internal static async Task RunAsync(CheckSteps check, string code, string container)
    {
        void Step(string name, bool ok, string detail) => check.Step(name, ok, detail);
        const string tokenPath = "/var/lib/martlet/config/gateway/agent.token";
        var gateway = new GatewayContainer(container);
        var (pairing, pairingSecret) = await HostPairingCode.Parse(code).PairAsync("check-live");
        Step("pair-live", true, $"paired as check-live with {pairing.HostId} at {pairing.Origin}");
        string? token = null;
        for (var attempt = 0; attempt < 90 && token is null; attempt++)
        {
            token = await gateway.ReadAsync(tokenPath);
            if (token is null) await Task.Delay(1000);
        }
        Step("token-readable-locally", token is { Length: 43 }, token is null ? "docker exec could not read agent.token" : "read with docker exec (43 characters)");
        using var connection = new HostConnection(pairing, pairingSecret);
        var runner = new FixtureRunner();
        var agent = new NodeCommandAgent(_ => gateway.ReadAsync(tokenPath), "9.9.9", runner);
        var update = await connection.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = "9.9.9" });
        var pass = await agent.RunOnceAsync(connection, CancellationToken.None);
        var done = await connection.ReadCommandAsync(update.Id);
        Step("live-agent-runs-it", pass.Kind == NodeAgentPassKind.Ran && done.State == NodeCommandState.Succeeded &&
            done.Output.SequenceEqual(FixtureRunner.UpdateLines), $"{done.State}: {done.Summary}");
        const string secret = "fixture-secret-live-7d21";
        var add = await connection.SendCommandAsync(NodeCommandKinds.AddRole, new Dictionary<string, string> { ["role"] = "fixture-role" },
            new Dictionary<string, string> { ["secret.api-key"] = secret });
        var saved = await gateway.ReadAsync("/var/lib/martlet/config/gateway/commands.json") ?? "";
        pass = await agent.RunOnceAsync(connection, CancellationToken.None);
        Step("live-saved-without-secrets", saved.Contains(update.Id) && saved.Contains(add.Id) && !saved.Contains(secret) &&
            runner.SecretsSeen.Contains(secret) && pass.Outcome?.Succeeded == true,
            $"commands.json holds both commands ({saved.Length} bytes) and no secret; the agent received it");
        await gateway.RestartAsync();
        HostCommandList? after = null;
        for (var attempt = 0; attempt < 30 && after is null; attempt++)
        {
            await Task.Delay(1000);
            try { after = await connection.ReadCommandsAsync(); }
            catch (HostConnectionException) { }
        }
        var newToken = await gateway.ReadAsync(tokenPath);
        var oldRefused = await Expect(() => connection.TakeCommandAsync(token ?? "", null, NodeCommandKinds.All), "command.agent");
        pass = await agent.RunOnceAsync(connection, CancellationToken.None);
        Step("live-restart", after?.Commands.FirstOrDefault(c => c.Id == update.Id)?.State == NodeCommandState.Succeeded &&
            newToken is { Length: 43 } && newToken != token && oldRefused && pass.Kind == NodeAgentPassKind.Idle,
            $"commands kept: {after?.Commands.Count}; new token {(newToken != token ? "differs" : "same")}; old refused {oldRefused}; agent {pass.Kind}");
    }
}

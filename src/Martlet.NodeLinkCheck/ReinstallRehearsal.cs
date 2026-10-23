using System.Net;
using Martlet.Core.Hardware;
using Martlet.HostClient;
using LabHost = Martlet.NodeLinkCheck.MoveRehearsal.LabHost;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses a host whose computer was reinstalled (docs/NETWORK.md, "When a host is reinstalled") with the production code:
/// real gateways (Kestrel, TLS) on 127.0.0.6 with a simulated TPM (<see cref="SimulatedTpm"/>), and a desktop's real proof
/// client (<see cref="HostComputerProof"/>) and decision (<see cref="ReinstallCheck"/>). The desktop saves the host's TPM keys
/// over its pinned TLS key; the host comes back with a new TLS key and the same chip (proved: the same computer), then with
/// another chip (not trusted), then without a TPM (its hardware facts decide, only at home or over Tailscale). Also signs and
/// checks a proof with this PC's own TPM when it has one. Loopback only; writes nothing.
/// </summary>
internal static class ReinstallRehearsal
{
    private static readonly IPAddress At = IPAddress.Parse("127.0.0.6");
    private const string HostId = "lab-reinstalled-host";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add((name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add((name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        var chip = new SimulatedTpm();
        var timeout = TimeSpan.FromSeconds(10);
        IReadOnlyList<HardwareKey> saved = [];
        string pinned = "", origin = "";
        int port;
        var facts = MachineFacts.Hashes(HostId, "product_uuid=4c4c4544-0042-3510-8051-b4c04f4e4b32\nmac=d8:bb:c1:12:34:56\ngpu=GPU-1b2c3d4e");

        await using (var before = await LabHost.StartAsync(HostId, At, [At]))
        {
            before.Server.Hardware = HardwareKeys.From(chip.Open);
            port = before.Port;
            origin = before.Origin;
            pinned = before.Identity.SpkiFingerprint;
            await Run("While the host answers with its pinned TLS key, the desktop saves its TPM keys", async () =>
            {
                var answer = await HostComputerProof.AskAsync(origin, null, timeout, token);
                saved = answer.Spki == pinned ? HostComputerProof.Keys(answer, HostId) : [];
                return (saved.Count == 2, $"the host showed {answer.Spki?[..15]}... (pinned {pinned[..15]}...); saved " +
                    $"{string.Join(", ", saved.Select(k => k.Kind))}{(answer.Problem is null ? "" : "; " + answer.Problem)}");
            });
        }

        await using (var reinstalled = await LabHost.StartAsync(HostId, At, [At], port))
        {
            reinstalled.Server.Hardware = HardwareKeys.From(chip.Open);
            await Run("After the reinstall the pinned-key check sees another TLS key at the host's address", async () =>
            {
                var probe = await HostRoutes.ProbeAsync(origin, pinned, null, TimeSpan.FromSeconds(4), token);
                return (!probe.Reachable && probe.Problem == "another key", $"reachable {probe.Reachable}, {probe.Problem}");
            });
            await Run("The reinstalled computer proves with its TPM that it is the same one, on any route", async () =>
            {
                var answer = await HostComputerProof.AskAsync(origin, null, timeout, token);
                var check = HostComputerProof.Check(answer, HostId, saved);
                var (verdict, why) = ReinstallCheck.Decide(new(check, null, null, RouteTrust.Other));
                return (check == HardwareCheck.Proven && answer.Spki == reinstalled.Identity.SpkiFingerprint && answer.Spki != pinned &&
                        verdict == ReinstallVerdict.Same,
                    $"new TLS key {answer.Spki?[..15]}...; proof {check}; verdict {verdict} ({why})");
            });
        }

        await using (var stranger = await LabHost.StartAsync(HostId, At, [At], port))
        {
            stranger.Server.Hardware = HardwareKeys.From(new SimulatedTpm().Open);
            await Run("Another computer that answers for the host's ID with its own TPM is not trusted", async () =>
            {
                var answer = await HostComputerProof.AskAsync(origin, null, timeout, token);
                var check = HostComputerProof.Check(answer, HostId, saved);
                var (verdict, why) = ReinstallCheck.Decide(new(check, facts, [], RouteTrust.Home));
                return (check == HardwareCheck.OtherKeys && verdict != ReinstallVerdict.Same, $"proof {check}; verdict {verdict} ({why})");
            });
        }

        await using (var noTpm = await LabHost.StartAsync(HostId, At, [At], port))
        {
            noTpm.Server.Hardware = HardwareKeys.None("This computer has no TPM 2.0 (lab).");
            await Run("Without a TPM the hardware facts decide, only on the home network or over Tailscale", async () =>
            {
                var answer = await HostComputerProof.AskAsync(origin, null, timeout, token);
                var check = HostComputerProof.Check(answer, HostId, saved);
                var home = ReinstallCheck.Decide(new(check, facts, facts, RouteTrust.Home));
                var tailnet = ReinstallCheck.Decide(new(check, facts, facts, RouteTrust.Tailnet));
                var outside = ReinstallCheck.Decide(new(check, facts, facts, RouteTrust.Other));
                var otherBoard = ReinstallCheck.Decide(new(check, facts,
                    MachineFacts.Hashes(HostId, "product_uuid=11111111-2222-3333-4444-555555555555\nmac=d8:bb:c1:12:34:56"), RouteTrust.Home));
                return (check == HardwareCheck.NoKeys && answer.Proof?.Unavailable is not null && home.Verdict == ReinstallVerdict.Same &&
                        tailnet.Verdict == ReinstallVerdict.Same && outside.Verdict == ReinstallVerdict.Unsure &&
                        otherBoard.Verdict == ReinstallVerdict.NotSame,
                    $"proof {check} ({answer.Proof?.Unavailable}); home {home.Verdict}, Tailscale {tailnet.Verdict}, outside {outside.Verdict}, " +
                    $"another board {otherBoard.Verdict}");
            });
        }

        string? notRunTpm = null;
        await Run("This PC's own TPM makes the same keys each time and signs proofs that verify", () =>
        {
            var keys = HardwareKeys.ForThisComputer();
            if (keys.Keys.Count == 0)
            {
                notRunTpm = "This PC's own TPM: " + keys.Unavailable;
                return Task.FromResult((true, "no TPM here: " + keys.Unavailable));
            }
            var again = HardwareKeys.ForThisComputer();
            var nonce = HardwareProof.NewNonce();
            var proof = HardwareProof.Make(keys, HostId, pinned, nonce);
            var check = HardwareProof.Check(proof, HostId, pinned, nonce, again.Keys);
            return Task.FromResult((check == HardwareCheck.Proven && keys.Keys.SequenceEqual(again.Keys),
                $"keys {string.Join(", ", keys.Keys.Select(k => k.Kind))}; proof {check}"));
        });

        var passed = steps.All(s => s.Ok);
        return (passed, new
        {
            passed,
            steps = steps.Select(s => new { name = s.Name, ok = s.Ok, detail = s.Detail }),
            notRun = new[]
            {
                "A real OS reinstall of a Linux host and Martlet setting it up again over SSH (HostReinstall in the desktop) need a " +
                "spare Linux computer with a TPM; covered by the steps above with a simulated TPM.",
                notRunTpm
            }.Where(n => n is not null)
        });
    }
}

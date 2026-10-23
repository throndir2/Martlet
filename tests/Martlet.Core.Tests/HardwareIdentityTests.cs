using System.Net;
using System.Security.Cryptography;
using Martlet.Core.Hardware;

namespace Martlet.Core.Tests;

public sealed class HardwareIdentityTests
{
    private const string HostId = "aihost1-host";
    private static readonly string Spki = "sha256:" + new string('a', 64);
    private static readonly string OtherSpki = "sha256:" + new string('b', 64);

    [Fact]
    public void A_chip_makes_the_same_keys_each_time_and_another_chip_makes_others()
    {
        var chip = new SimulatedTpm();
        var first = HardwareKeys.From(chip.Open);
        var again = HardwareKeys.From(chip.Open);
        var other = HardwareKeys.From(new SimulatedTpm().Open);

        Assert.Null(first.Unavailable);
        Assert.Equal([HardwareKey.Endorsement, HardwareKey.Owner], first.Keys.Select(k => k.Kind));
        Assert.Equal(first.Keys, again.Keys);
        Assert.Empty(first.Keys.Intersect(other.Keys));
    }

    [Fact]
    public void A_hierarchy_that_refuses_leaves_the_other_key_and_both_refusing_means_no_key()
    {
        var chip = new SimulatedTpm();
        chip.Refuse.Add(Tpm2.Endorsement);
        var owner = HardwareKeys.From(chip.Open);
        Assert.Equal([HardwareKey.Owner], owner.Keys.Select(k => k.Kind));

        chip.Refuse.Add(Tpm2.Owner);
        var none = HardwareKeys.From(chip.Open);
        Assert.Empty(none.Keys);
        Assert.Contains("refused", none.Unavailable);
        Assert.Empty(none.Sign(new byte[32]));
    }

    [Fact]
    public void A_proof_from_the_saved_chip_proves_the_same_computer_after_a_reinstall()
    {
        var chip = new SimulatedTpm();
        var saved = HardwareKeys.From(chip.Open).Keys;
        // The reinstalled host has a new TLS key; the chip is the same.
        var nonce = HardwareProof.NewNonce();
        var proof = HardwareProof.Make(HardwareKeys.From(chip.Open), HostId, OtherSpki, nonce);

        Assert.True(HardwareProof.IsNonce(nonce));
        Assert.Equal(HardwareCheck.Proven, HardwareProof.Check(proof, HostId, OtherSpki, nonce, saved));
        Assert.Equal(saved, HardwareProof.Verified(proof, HostId, OtherSpki, nonce));
    }

    [Fact]
    public void A_proof_from_another_chip_or_for_another_key_host_or_question_never_proves_it()
    {
        var saved = HardwareKeys.From(new SimulatedTpm().Open).Keys;
        var stranger = HardwareKeys.From(new SimulatedTpm().Open);
        var nonce = HardwareProof.NewNonce();
        var proof = HardwareProof.Make(stranger, HostId, Spki, nonce);

        Assert.Equal(HardwareCheck.OtherKeys, HardwareProof.Check(proof, HostId, Spki, nonce, saved));
        // The proof binds the TLS key of the connection, the host ID and this PC's nonce.
        Assert.Equal(HardwareCheck.Invalid, HardwareProof.Check(proof, HostId, OtherSpki, nonce, saved));
        Assert.Equal(HardwareCheck.Invalid, HardwareProof.Check(proof, "other-host", Spki, nonce, saved));
        Assert.Equal(HardwareCheck.Invalid, HardwareProof.Check(proof, HostId, Spki, HardwareProof.NewNonce(), saved));
        // Copying the saved public keys into a proof doesn't help: the signatures don't verify.
        var forged = proof with { Keys = saved };
        Assert.Equal(HardwareCheck.Invalid, HardwareProof.Check(forged, HostId, Spki, nonce, saved));
        Assert.Equal(HardwareCheck.NoKeys, HardwareProof.Check(proof with { Keys = [], Signatures = [] }, HostId, Spki, nonce, saved));
    }

    [Fact]
    public void Proofs_travel_as_snake_case_json()
    {
        var nonce = HardwareProof.NewNonce();
        var proof = HardwareProof.Make(HardwareKeys.From(new SimulatedTpm().Open), HostId, Spki, nonce);
        var json = System.Text.Json.JsonSerializer.Serialize(proof, HardwareProof.Json);
        Assert.Contains("\"host_id\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<HardwareProofDocument>(json, HardwareProof.Json)!;
        Assert.Equal(HardwareCheck.Proven, HardwareProof.Check(back, HostId, Spki, nonce, proof.Keys));
        Assert.Equal("/martlet/v1/hardware?nonce=" + nonce, HardwareProof.Target(nonce));
    }

    [Fact]
    public void This_PCs_own_TPM_signs_proofs_that_verify_when_it_has_one()
    {
        // Real hardware: Windows' TPM service here, /dev/tpmrm0 on Linux. A computer without a TPM says why.
        var keys = HardwareKeys.ForThisComputer();
        if (keys.Keys.Count == 0)
        {
            Assert.False(string.IsNullOrWhiteSpace(keys.Unavailable));
            return;
        }
        var again = HardwareKeys.ForThisComputer();
        Assert.Equal(keys.Keys, again.Keys);
        var nonce = HardwareProof.NewNonce();
        var proof = HardwareProof.Make(keys, HostId, Spki, nonce);
        Assert.Equal(HardwareCheck.Proven, HardwareProof.Check(proof, HostId, Spki, nonce, again.Keys));
    }

    private static IReadOnlyList<string> Facts(string text, string host = HostId) => MachineFacts.Hashes(host, text);

    [Fact]
    public void Facts_leave_out_placeholders_and_random_addresses_and_depend_on_the_host()
    {
        var facts = Facts("""
            product_uuid=4C4C4544-0042-3510-8051-B4C04F4E4B32
            board_serial=To be filled by O.E.M.
            mac=d8:bb:c1:12:34:56
            mac=da:bb:c1:12:34:56
            mac=00:00:00:00:00:00
            gpu=GPU-1b2c3d4e-0000-1111-2222-333344445555
            noise=1
            """);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.DoesNotContain("d8:bb", f));
        Assert.Empty(Facts("product_uuid=03000200-0400-0500-0006-000700080009"));
        Assert.Empty(Facts("mac=d8:bb:c1:12:34:56").Intersect(Facts("mac=d8:bb:c1:12:34:56", "other-host")));
        Assert.Equal(Facts("mac=d8:bb:c1:12:34:56"), [MachineFacts.HashOf(HostId, MachineFacts.Mac, "D8:BB:C1:12:34:56")!]);
    }

    [Fact]
    public void Facts_match_on_the_board_ID_or_two_other_facts_and_a_different_board_ID_conflicts()
    {
        var saved = Facts("product_uuid=4c4c4544-0042\nmac=d8:bb:c1:12:34:56\ngpu=GPU-1\ngpu=GPU-2");
        Assert.True(MachineFacts.Match(saved, Facts("product_uuid=4c4c4544-0042")));
        Assert.True(MachineFacts.Match(saved, Facts("mac=d8:bb:c1:12:34:56\ngpu=GPU-2")));
        Assert.False(MachineFacts.Match(saved, Facts("gpu=GPU-2")));
        var other = Facts("product_uuid=ffff0000-1111\nmac=d8:bb:c1:12:34:56\ngpu=GPU-1");
        Assert.True(MachineFacts.Conflict(saved, other));
        Assert.False(MachineFacts.Match(saved, other));
        Assert.False(MachineFacts.Match([], Facts("gpu=GPU-1")));
    }

    [Fact]
    public void The_reinstall_check_trusts_a_TPM_proof_on_any_route_and_facts_only_at_home_or_over_Tailscale()
    {
        var saved = Facts("product_uuid=4c4c4544-0042\nmac=d8:bb:c1:12:34:56\ngpu=GPU-1");
        var same = Facts("product_uuid=4c4c4544-0042\nmac=d8:bb:c1:12:34:56\ngpu=GPU-1");
        var mac = MachineFacts.HashOf(HostId, MachineFacts.Mac, "d8:bb:c1:12:34:56");
        var strangerMac = MachineFacts.HashOf(HostId, MachineFacts.Mac, "00:11:22:33:44:55");

        Assert.Equal(ReinstallVerdict.Same, ReinstallCheck.Decide(new(HardwareCheck.Proven, null, null, RouteTrust.Other)).Verdict);
        Assert.Equal(ReinstallVerdict.NotSame, ReinstallCheck.Decide(new(HardwareCheck.Invalid, saved, same, RouteTrust.Home)).Verdict);
        // No TPM: the facts decide, at home (with the network card that answers there) or over Tailscale.
        Assert.Equal(ReinstallVerdict.Same, ReinstallCheck.Decide(new(HardwareCheck.NoKeys, saved, same, RouteTrust.Home, mac)).Verdict);
        Assert.Equal(ReinstallVerdict.Same, ReinstallCheck.Decide(new(null, saved, same, RouteTrust.Tailnet)).Verdict);
        Assert.Equal(ReinstallVerdict.Unsure, ReinstallCheck.Decide(new(HardwareCheck.NoKeys, saved, same, RouteTrust.Other)).Verdict);
        Assert.Equal(ReinstallVerdict.NotSame, ReinstallCheck.Decide(new(HardwareCheck.NoKeys, saved, same, RouteTrust.Home, strangerMac)).Verdict);
        Assert.Equal(ReinstallVerdict.NotSame, ReinstallCheck.Decide(new(HardwareCheck.OtherKeys, saved,
            Facts("product_uuid=99999999-0000\nmac=d8:bb:c1:12:34:56\ngpu=GPU-1"), RouteTrust.Home)).Verdict);
        Assert.Equal(ReinstallVerdict.Unsure, ReinstallCheck.Decide(new(HardwareCheck.NoKeys, [], same, RouteTrust.Home)).Verdict);
        // A TPM that was reset (another key) still passes on matching facts at home: the fallback the owner asked for.
        var (verdict, why) = ReinstallCheck.Decide(new(HardwareCheck.OtherKeys, saved, same, RouteTrust.Home));
        Assert.Equal(ReinstallVerdict.Same, verdict);
        Assert.Contains("TPM key changed", why);
    }

    [Fact]
    public void Routes_are_home_on_this_PCs_subnet_and_tailnet_only_while_this_PC_is_on_Tailscale()
    {
        IReadOnlyList<(IPAddress, int)> home = [(IPAddress.Parse("192.168.1.10"), 24)];
        IReadOnlyList<(IPAddress, int)> both = [.. home, (IPAddress.Parse("100.101.102.103"), 32)];
        Assert.Equal(RouteTrust.Home, RouteTrusts.Of(IPAddress.Parse("192.168.1.37"), home));
        Assert.Equal(RouteTrust.Other, RouteTrusts.Of(IPAddress.Parse("192.168.2.37"), home));
        Assert.Equal(RouteTrust.Other, RouteTrusts.Of(IPAddress.Parse("8.8.8.8"), home));
        Assert.Equal(RouteTrust.Other, RouteTrusts.Of(IPAddress.Parse("100.64.1.2"), home));
        Assert.Equal(RouteTrust.Tailnet, RouteTrusts.Of(IPAddress.Parse("100.64.1.2"), both));
        Assert.Equal(RouteTrust.Other, RouteTrusts.Of(IPAddress.Parse("100.128.0.1"), both));
    }

    [Fact]
    public void The_digest_binds_host_key_and_nonce()
    {
        var a = HardwareProof.Digest(HostId, Spki, "n");
        Assert.Equal(32, a.Length);
        Assert.False(a.AsSpan().SequenceEqual(HardwareProof.Digest(HostId, OtherSpki, "n")));
        Assert.True(a.AsSpan().SequenceEqual(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"martlet-hardware-proof-v1\n{HostId}\n{Spki}\nn"))));
    }
}

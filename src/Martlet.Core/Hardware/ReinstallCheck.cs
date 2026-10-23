namespace Martlet.Core.Hardware;

public enum ReinstallVerdict
{
    /// <summary>The computer proved it is the same one: Martlet sets it up again and gives it back its jobs.</summary>
    Same,
    /// <summary>It is another computer (or proved nothing it should): Martlet stops and gives it nothing.</summary>
    NotSame,
    /// <summary>Not enough proof: Martlet stops and asks the owner.</summary>
    Unsure,
}

/// <summary>What Martlet knows about a computer that answers for a host after a reinstall.</summary>
/// <param name="Proof">The TPM proof check (null when the host couldn't be asked).</param>
/// <param name="SavedFacts">The fact hashes saved for the host (<see cref="MachineFacts"/>).</param>
/// <param name="FactsNow">The fact hashes read from the computer now (null when they couldn't be read).</param>
/// <param name="Route">How this PC reaches the computer.</param>
/// <param name="MacNow">The fact hash of the network card address that answered at the computer's address on the home network
/// (this PC's ARP table), or null when unknown (over Tailscale, or no answer).</param>
public sealed record ReinstallEvidence(HardwareCheck? Proof, IReadOnlyList<string>? SavedFacts, IReadOnlyList<string>? FactsNow,
    RouteTrust Route, string? MacNow = null);

/// <summary>Decides whether a reinstalled host is the same computer (docs/NETWORK.md, "When a host is reinstalled"):
/// <list type="number">
/// <item>A TPM key saved before signed the new TLS key: the same computer, on any route (home, outside, Tailscale).</item>
/// <item>A proof that doesn't verify, a different board ID, or another network card at its home address: another computer.</item>
/// <item>Without a TPM proof, the hardware facts must match (<see cref="MachineFacts.Match"/>), and only over a route the
/// owner's network vouches for (the home subnet or Tailscale).</item>
/// </list></summary>
public static class ReinstallCheck
{
    public static (ReinstallVerdict Verdict, string Why) Decide(ReinstallEvidence e)
    {
        if (e.Proof == HardwareCheck.Proven)
            return (ReinstallVerdict.Same, "its TPM signed its new TLS key with the key Martlet saved before");
        if (e.Proof == HardwareCheck.Invalid)
            return (ReinstallVerdict.NotSame, "its proof doesn't verify (it isn't for this host or its TLS key)");
        if (e.FactsNow is { } now && MachineFacts.Conflict(e.SavedFacts, now))
            return (ReinstallVerdict.NotSame, "its board ID is not the one Martlet saved");
        if (e.MacNow is { } mac && e.SavedFacts is { Count: > 0 } saved && saved.Any(f => f.StartsWith(MachineFacts.Mac + ":", StringComparison.Ordinal)) &&
            !saved.Contains(mac))
            return (ReinstallVerdict.NotSame, "another network card answers at its address");
        var tpm = e.Proof switch
        {
            HardwareCheck.OtherKeys => "its TPM key changed (a TPM reset, or another computer)",
            HardwareCheck.NoKeys => "it has no usable TPM",
            _ => "it couldn't show a TPM proof",
        };
        if (e.Route == RouteTrust.Other)
            return (ReinstallVerdict.Unsure, tpm + ", and without a TPM proof Martlet trusts hardware facts only on your home network or Tailscale");
        if (e.FactsNow is null || e.SavedFacts is not { Count: > 0 })
            return (ReinstallVerdict.Unsure, tpm + ", and Martlet has no hardware facts to compare");
        return MachineFacts.Match(e.SavedFacts, e.FactsNow)
            ? (ReinstallVerdict.Same, tpm + ", but its hardware facts match the ones Martlet saved" +
                (e.MacNow is not null ? " and its network card answers at its address" : ""))
            : (ReinstallVerdict.Unsure, tpm + ", and too few of its hardware facts match");
    }
}

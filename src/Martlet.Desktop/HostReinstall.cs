using System.Net;
using System.Runtime.InteropServices;
using Martlet.Core.Hardware;

namespace Martlet.Desktop;

/// <summary>What this PC saved to recognize a host's computer after an OS reinstall (docs/NETWORK.md, "When a host is
/// reinstalled"). It lives with the pairing in hosts.json: it describes that computer as this PC saw it, so each PC keeps its
/// own (the roster's format stays the same for older Martlet versions).</summary>
internal sealed record HostRecognition
{
    /// <summary>The host's TPM keys (<see cref="HardwareKeys"/>), proved over the pinned TLS connection.</summary>
    public IReadOnlyList<HardwareKey> Keys { get; init; } = [];
    /// <summary>Why the host has no TPM key (it said so, or it couldn't be asked); null while it has one or before the first check.</summary>
    public string? NoKey { get; init; }
    /// <summary>Salted hashes of its hardware facts (<see cref="MachineFacts"/>), read over SSH, for a host without a TPM.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];
    /// <summary>The roles it ran when this PC last saw it, put back after a reinstall.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
    public DateTimeOffset? KeysCheckedAt { get; init; }
    public DateTimeOffset? FactsCheckedAt { get; init; }
    /// <summary>What happened the last time Martlet found the host reinstalled, for people and MCP.</summary>
    public string? LastReinstall { get; init; }
    public DateTimeOffset? LastReinstallAt { get; init; }
}

/// <summary>Timings and small helpers for <see cref="MainWindow"/>'s reinstall check.</summary>
internal static class HostReinstall
{
    /// <summary>How often each host is checked.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(2);
    /// <summary>How often a reachable host's TPM keys are checked again.</summary>
    internal static readonly TimeSpan KeysEvery = TimeSpan.FromDays(1);
    /// <summary>How often a host's hardware facts are read again over SSH.</summary>
    internal static readonly TimeSpan FactsEvery = TimeSpan.FromDays(7);
    /// <summary>How long an SSH host may stay unreachable before Martlet looks whether it was reinstalled.</summary>
    internal static readonly TimeSpan GoneFor = TimeSpan.FromMinutes(5);
    /// <summary>How long Martlet waits before it tries to set up the same host again after a try that stopped.</summary>
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);

    /// <summary>The fact hash of the network card address that answers at <paramref name="address"/> on this PC's home
    /// network (ARP), or null when unknown.</summary>
    internal static string? MacAt(string hostId, IPAddress address)
    {
        if (!OperatingSystem.IsWindows() || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        try
        {
            var mac = new byte[6];
            var length = (uint)mac.Length;
            if (SendARP(BitConverter.ToUInt32(address.GetAddressBytes()), 0, mac, ref length) != 0 || length != 6) return null;
            return MachineFacts.HashOf(hostId, MachineFacts.Mac, string.Join(':', mac.Select(b => b.ToString("x2"))));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SendARP(uint destination, uint source, byte[] mac, ref uint length);

    /// <summary>Answers the questions of an SSH run that sets up a reinstalled host again. It trusts one new SSH key for this
    /// run only (pinned after the computer proved it is the same one): <paramref name="expected"/>, the key seen when Martlet
    /// found the reinstall, when the route checks passed (<paramref name="approved"/>), otherwise only when the owner trusts it
    /// through <paramref name="ask"/>. Every later connection of the run must show that same key. It signs in with the
    /// remembered password; with none (or a wrong one) it asks the owner.</summary>
    internal sealed class Prompts(string? password, IHostShellPrompts? ask = null, string? expected = null, bool approved = false)
        : IHostShellPrompts
    {
        internal string? OfferedKey { get; private set; }
        public bool TrustHostKey(HostShellTarget target, string hostKey)
        {
            if (OfferedKey is not null) return OfferedKey == hostKey;
            if (!(approved && hostKey == expected) && ask?.TrustHostKey(target, hostKey) != true) return false;
            OfferedKey = hostKey;
            return true;
        }
        public string? LoginPassword(HostShellTarget target, bool retry) =>
            !retry && password is not null ? password : ask?.LoginPassword(target, retry && password is null);
        public void LoginAccepted(HostShellTarget target, string password) => ask?.LoginAccepted(target, password);
        public HostShellSudo? SudoPassword(HostShellTarget target, bool retry) =>
            !retry && password is not null ? new(password, false) : ask?.SudoPassword(target, retry && password is null);
    }

    /// <summary>The address this PC dials for <paramref name="host"/> ("192.168.1.20" or a name it resolves here), never
    /// one the remote computer reports.</summary>
    internal static async Task<IPAddress?> DialedAddressAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return ip;
        try
        {
            return (await Dns.GetHostAddressesAsync(host, token)).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    /// <summary>Before Martlet sends a password to a computer with a new SSH key: <c>Go</c> when the route is your home subnet
    /// (and, when Martlet saved the host's network card addresses, one of them answers there) or Tailscale; <c>Ask</c> when
    /// at home without saved card addresses or an answer (the owner trusts the new key); <c>Stop</c> with why otherwise.</summary>
    internal static (string Decision, string Why) BeforePassword(RouteTrust route, IReadOnlyList<string>? savedFacts, string? macNow)
    {
        if (route == RouteTrust.Tailnet) return ("Go", "reached over Tailscale");
        if (route != RouteTrust.Home) return ("Stop", "it isn't on your home network or Tailscale, so Martlet sends it no password");
        var savedMacs = (savedFacts ?? []).Where(f => f.StartsWith(MachineFacts.Mac + ":", StringComparison.Ordinal)).ToList();
        if (macNow is not null && savedMacs.Count > 0)
            return savedMacs.Contains(macNow) ? ("Go", "its network card answers at its address")
                : ("Stop", "another network card answers at its address");
        return ("Ask", "Martlet can't tell from its network card that it is the same computer");
    }

    /// <summary>Only watches: records a changed SSH key and refuses it (to see whether a computer was reinstalled).</summary>
    internal sealed class Watch : IHostShellPrompts
    {
        internal string? OfferedKey { get; private set; }
        public bool TrustHostKey(HostShellTarget target, string hostKey) { OfferedKey = hostKey; return false; }
        public string? LoginPassword(HostShellTarget target, bool retry) => null;
        public HostShellSudo? SudoPassword(HostShellTarget target, bool retry) => null;
    }

    /// <summary>Runs <see cref="MachineFacts.Script"/> with sudo when the account has it (the board's ID needs root).</summary>
    internal static string FactsCommand => "if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null || [ -n \"$SUDO_ASKPASS\" ]; then sudo sh -c " +
        HostShell.Quote(MachineFacts.Script) + "; else sh -c " + HostShell.Quote(MachineFacts.Script) + "; fi";
}

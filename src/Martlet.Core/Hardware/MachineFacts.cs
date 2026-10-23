using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Hardware;

/// <summary>The fallback for a computer without a usable TPM: hardware facts that stay the same after an OS reinstall and
/// that only the owner's paired computers saw (the board's ID and serial number, the physical network cards' addresses
/// and the NVIDIA GPUs' IDs). They aren't secret like a TPM key, so a match is weaker proof; Martlet keeps only salted
/// hashes of them.</summary>
public static class MachineFacts
{
    public const string ProductUuid = "product_uuid", BoardSerial = "board_serial", Mac = "mac", Gpu = "gpu";

    /// <summary>Prints one <c>kind=value</c> line for each fact (POSIX sh; run with sudo to read the board's ID and serial).</summary>
    public const string Script =
        "for f in product_uuid board_serial; do v=$(cat /sys/class/dmi/id/$f 2>/dev/null) && printf '%s=%s\\n' \"$f\" \"$v\"; done; " +
        "for n in /sys/class/net/*; do [ -e \"$n/device\" ] || continue; a=$(cat \"$n/address\" 2>/dev/null) && printf 'mac=%s\\n' \"$a\"; done; " +
        "command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi --query-gpu=uuid --format=csv,noheader 2>/dev/null | sed 's/^/gpu=/'; true";

    /// <summary>Values that boards ship with when the maker filled in nothing; they say nothing about one computer.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "none", "default string", "to be filled by o.e.m.", "not specified", "not applicable", "system serial number",
        "chassis serial number", "base board serial number", "123456789", "0123456789", "n/a", "na", "unknown",
        "03000200-0400-0500-0006-000700080009", "00000000-0000-0000-0000-000000000000", "ffffffff-ffff-ffff-ffff-ffffffffffff",
        "00:00:00:00:00:00", "ff:ff:ff:ff:ff:ff",
    };

    /// <summary>The facts in <see cref="Script"/>'s output, as salted hashes ("kind:hex"), placeholders left out.</summary>
    public static IReadOnlyList<string> Hashes(string hostId, string output)
    {
        var hashes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf('=');
            if (at <= 0) continue;
            var kind = line[..at];
            if (kind is not (ProductUuid or BoardSerial or Mac or Gpu)) continue;
            var value = line[(at + 1)..].Trim().ToLowerInvariant();
            if (Placeholders.Contains(value) || value.Length > 128) continue;
            if (kind == Mac && (value.Length != 17 ||
                !int.TryParse(value.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var first) ||
                (first & 2) != 0)) continue; // a locally administered address is random, not the card's own
            hashes.Add(kind + ":" + Hash(hostId, kind, value));
            if (hashes.Count >= 32) break;
        }
        return [.. hashes];
    }

    /// <summary>Whether the facts read now (<paramref name="now"/>) show the computer whose facts were saved
    /// (<paramref name="saved"/>): the same board ID, or at least two other facts.</summary>
    public static bool Match(IReadOnlyList<string>? saved, IReadOnlyList<string> now)
    {
        if (saved is not { Count: > 0 } || now.Count == 0 || Conflict(saved, now)) return false;
        var same = now.Where(saved.Contains).ToList();
        return same.Any(h => h.StartsWith(ProductUuid + ":", StringComparison.Ordinal)) || same.Count >= 2;
    }

    /// <summary>Whether both have a board ID and they differ: another computer.</summary>
    public static bool Conflict(IReadOnlyList<string>? saved, IReadOnlyList<string> now)
    {
        if (saved is null) return false;
        var before = saved.Where(h => h.StartsWith(ProductUuid + ":", StringComparison.Ordinal)).ToList();
        var after = now.Where(h => h.StartsWith(ProductUuid + ":", StringComparison.Ordinal)).ToList();
        return before.Count > 0 && after.Count > 0 && !before.Intersect(after).Any();
    }

    /// <summary>One fact's hash, as <see cref="Hashes"/> makes it (null for a placeholder).</summary>
    public static string? HashOf(string hostId, string kind, string value) => Hashes(hostId, $"{kind}={value}") is [var one] ? one : null;

    private static string Hash(string hostId, string kind, string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"martlet-machine-fact-v1\n{hostId}\n{kind}\n{value}")))[..32];
}

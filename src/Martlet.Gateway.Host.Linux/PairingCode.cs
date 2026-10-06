using System.Buffers.Text;
using System.Text.Json;

namespace Martlet.Gateway.Host.Linux;

/// <summary>
/// How the host shows an invitation. <see cref="Describe"/> is for people: this host's address and a short one-use code
/// to type on a desktop; it works until a desktop uses it or it is withdrawn. <see cref="Format"/> is one machine-readable
/// line carrying the whole card (origin, host ID, pin, pairing ID, token) that Martlet reads by itself when it pairs over
/// SSH or on this PC, one use, five minutes. Both are shown only on the owner's terminal or run.
/// </summary>
internal static class PairingCode
{
    internal const string Prefix = "martlet-pair-v1.";
    internal const int DefaultPort = 9443;

    internal static string Format(GatewayPairingCard card) => Prefix + System.Buffers.Text.Base64Url.EncodeToString(
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["o"] = card.Origin, ["h"] = card.HostId, ["s"] = card.SpkiFingerprint,
            ["i"] = card.PairingId, ["t"] = card.Token.Reveal()
        }));

    /// <summary>The address as people type it: the IP alone on the default port, otherwise IP:port.</summary>
    internal static string Address(string origin)
    {
        var uri = new Uri(origin);
        return uri.Port == DefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    internal static string Describe(GatewayCodePairingCard card) =>
        $"""

        Pair a Martlet desktop with {card.HostId}
          In Martlet on the desktop: Devices > Add a computer > Enter a pairing code, then type
            Address:  {Address(card.Origin)}
            Code:     {card.Code.Reveal()}
          The code works once and doesn't expire: it stays valid until a desktop uses it or you type cancel
          (or press Ctrl+C). Until then this host's jobs are paused.

        Waiting for the desktop...
        """;
}

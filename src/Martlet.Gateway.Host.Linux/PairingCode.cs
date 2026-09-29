using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Martlet.Gateway.Host.Linux;

/// <summary>
/// One copyable line carrying the whole invitation (origin, host ID, pin, pairing ID, token) so a desktop can pair
/// with a single paste. Same secrecy as the displayed fields: shown only on the owned terminal, one use, five minutes.
/// </summary>
internal static class PairingCode
{
    internal const string Prefix = "martlet-pair-v1.";

    internal static string Format(GatewayPairingCard card) => Prefix + Base64Url.EncodeToString(
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["o"] = card.Origin, ["h"] = card.HostId, ["s"] = card.SpkiFingerprint,
            ["i"] = card.PairingId, ["t"] = card.Token.Reveal()
        }));
}

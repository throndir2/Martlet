using System.Buffers.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Core.Network;

/// <summary>
/// What the owner hands a computer that is away from home so it can sign in to one of the network's hosts: the host's ID,
/// its TLS key fingerprint (the pin, checked before anything is sent), its home address and the addresses it answers on
/// from outside (a DNS name, public address or overlay address with its port), and the network ID. It holds no secret: the
/// sign-in itself (owner password and authenticator code, or an allowed identity at a provider) is the authority, so an invite
/// may be reused and shared like an address. Written as one line, <c>martlet-invite-v1.&lt;base64url JSON&gt;</c>.
/// </summary>
public sealed record NetworkInvite
{
    public const string Prefix = "martlet-invite-v1.";
    public const int MaximumAddresses = NetworkRoster.MaximumAddresses;
    private const int MaximumLength = 4096;

    public required string HostId { get; init; }
    public required string SpkiFingerprint { get; init; }
    /// <summary>The host's home (LAN) origin, https://&lt;private IP&gt;:&lt;port&gt;; the pairing is kept under it.</summary>
    public required string Origin { get; init; }
    /// <summary>Where the host answers from outside home, each "name:port", "1.2.3.4:port" or "[v6]:port".</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public string? NetworkId { get; init; }
    /// <summary>A label for people ("Home"), shown when the invite is opened.</summary>
    public string? Label { get; init; }

    /// <summary>Every https origin to try, outside addresses first (the invite is meant for a computer away from home),
    /// then the home origin.</summary>
    public IReadOnlyList<string> Origins() => Addresses.Select(a => "https://" + a).Append(Origin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public string Write()
    {
        Validate();
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["v"] = 1, ["h"] = HostId, ["s"] = SpkiFingerprint, ["o"] = Origin, ["a"] = Addresses,
            ["n"] = NetworkId, ["l"] = Label
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
        return Prefix + Base64Url.EncodeToString(json);
    }

    public static NetworkInvite Parse(string? text)
    {
        var value = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        try
        {
            if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length > MaximumLength) throw new FormatException();
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value.AsSpan(Prefix.Length)), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.GetProperty("v").GetInt32() != 1) throw new FormatException();
            string? Optional(string name) => root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            var invite = new NetworkInvite
            {
                HostId = root.GetProperty("h").GetString()!, SpkiFingerprint = root.GetProperty("s").GetString()!,
                Origin = root.GetProperty("o").GetString()!,
                Addresses = root.TryGetProperty("a", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(a => a.GetString()!).ToArray() : [],
                NetworkId = Optional("n"), Label = Optional("l")
            };
            invite.Validate();
            return invite;
        }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or
            ArgumentException or ContractException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "That isn't a whole Martlet invite. Copy the entire line, starting with martlet-invite-v1.");
        }
    }

    public void Validate()
    {
        ContractRules.Identifier(HostId);
        ContractRules.Require(SpkiFingerprint is { Length: 71 } && SpkiFingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
            SpkiFingerprint[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "The invite's host fingerprint is invalid.");
        ContractRules.Require(Uri.TryCreate(Origin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps &&
            origin.AbsolutePath == "/" && !Origin.EndsWith('/'), "The invite's home address is invalid.");
        ContractRules.Require(Addresses.Count <= MaximumAddresses && Addresses.All(IsAddress), "The invite's outside addresses are invalid.");
        ContractRules.Require(Label is null || Label.Length <= 64 && Label.All(c => c >= ' '), "The invite's label is invalid.");
        ContractRules.Require(NetworkId is null || NetworkId.Length <= 64 && NetworkId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "The invite's network ID is invalid.");
    }

    /// <summary>A host and port someone outside can reach, in <see cref="NetworkRoster.NormalizeAddress"/>'s canonical form
    /// ("name:port", "1.2.3.4:port" or "[v6]:port"), as the roster keeps a host's outside addresses.</summary>
    public static bool IsAddress(string? address) => address is not null && NetworkRoster.NormalizeAddress(address) == address;

    /// <summary>"name:port" from what a person types: like <see cref="NetworkRoster.NormalizeAddress"/>, but a bare name or
    /// address gets <paramref name="defaultPort"/>. Null when it can't be an address.</summary>
    public static string? NormalizeAddress(string? text, int defaultPort = 9443)
    {
        if (NetworkRoster.NormalizeAddress(text) is { } exact) return exact;
        var value = (text ?? "").Trim().TrimEnd('/');
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value[8..].TrimEnd('/');
        if (value.Length == 0) return null;
        var bracketed = value.StartsWith('[') && value.EndsWith(']');
        var bareV6 = !value.StartsWith('[') && value.Count(c => c == ':') > 1;
        if (!bracketed && !bareV6 && value.Contains(':')) return null;
        return NetworkRoster.NormalizeAddress((bareV6 ? "[" + value + "]" : value) + ":" + defaultPort);
    }

    public override string ToString() => $"Invite to {HostId}";
}

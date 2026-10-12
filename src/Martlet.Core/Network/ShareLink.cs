using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Core.Network;

/// <summary>
/// A share link: what the owner sends a friend so the friend's Martlet can use some or all of the owner's hosts for their
/// engines (docs/NETWORK.md, "Sharing with a link"). It names the link (<see cref="Id"/>), carries its secret
/// (<see cref="Secret"/>, 32 random bytes: whoever has the link may sign in with it, as a friend only) and, for each host it
/// shared when it was made, that host's invite (ID, TLS pin, home address and outside addresses). A host keeps only the
/// secret's verifier (<see cref="Verifier"/>). Hosts added to the share later are found through a host the friend already
/// signed in to. Written as <c>martlet://share/&lt;base64url JSON&gt;</c> (opens Martlet when clicked) or, pasted,
/// <c>martlet-share-v1.&lt;base64url JSON&gt;</c>.
/// </summary>
public sealed record ShareLink
{
    public const string Scheme = "martlet";
    public const string UrlPrefix = "martlet://share/";
    public const string TextPrefix = "martlet-share-v1.";
    public const int MaximumHosts = 8;
    private const int MaximumLength = 12_288;

    /// <summary>The link's ID: 16 random bytes, base64url. Hosts name the friend's sign-in by it (provider "link").</summary>
    public required string Id { get; init; }
    /// <summary>The link's secret: 32 random bytes, base64url. Never stored on a host.</summary>
    public required string Secret { get; init; }
    /// <summary>The owner's Martlet network ID, when the owner has one.</summary>
    public string? NetworkId { get; init; }
    /// <summary>Who shares (shown to the friend, never trusted), for example "Sam".</summary>
    public string? From { get; init; }
    /// <summary>The name the owner gave this friend (shown on both sides), for example "Ana".</summary>
    public string? Label { get; init; }
    /// <summary>The hosts shared when the link was made, each as an invite (no secret in it).</summary>
    public required IReadOnlyList<NetworkInvite> Hosts { get; init; }

    /// <summary>A new link with a fresh ID and secret.</summary>
    public static ShareLink Create(IReadOnlyList<NetworkInvite> hosts, string? networkId, string? from, string? label) => new()
    {
        Id = NewId(), Secret = NewSecret(), NetworkId = networkId, From = Clean(from), Label = Clean(label), Hosts = hosts
    };

    /// <summary>A fresh link ID: 16 random bytes, base64url.</summary>
    public static string NewId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>A fresh link secret: 32 random bytes, base64url.</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>What a host keeps to check the secret: base64url SHA-256 of "martlet-share-link-v1\n&lt;id&gt;\n&lt;secret&gt;".
    /// The secret has 256 random bits, so a fast hash is enough.</summary>
    public static string Verifier(string id, string secret) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes($"martlet-share-link-v1\n{id}\n{secret}")));

    /// <summary>Whether <paramref name="secret"/> matches <paramref name="verifier"/>, in fixed time.</summary>
    public static bool Matches(string id, string? secret, string verifier)
    {
        if (secret is null || !IsToken(secret, 32)) return false;
        var expected = Encoding.ASCII.GetBytes(Verifier(id, secret));
        var actual = Encoding.ASCII.GetBytes(verifier);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Whether <paramref name="value"/> is base64url of exactly <paramref name="bytes"/> bytes.</summary>
    public static bool IsToken(string? value, int bytes)
    {
        if (value is null || value.Length != Base64Url.GetEncodedLength(bytes)) return false;
        try { return Base64Url.DecodeFromChars(value).Length == bytes; }
        catch (FormatException) { return false; }
    }

    /// <summary>The link people click: <c>martlet://share/&lt;payload&gt;</c>.</summary>
    public string Url() => UrlPrefix + Payload();

    /// <summary>The same link as one line of text, for chats that don't make martlet:// links clickable.</summary>
    public string Text() => TextPrefix + Payload();

    private string Payload()
    {
        Validate();
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["v"] = 1, ["i"] = Id, ["k"] = Secret, ["n"] = NetworkId, ["f"] = From, ["l"] = Label,
            ["h"] = Hosts.Select(h => new Dictionary<string, object?>
            {
                ["h"] = h.HostId, ["s"] = h.SpkiFingerprint, ["o"] = h.Origin, ["a"] = h.Addresses.Count > 0 ? h.Addresses : null, ["l"] = h.Label
            }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value)).ToArray()
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
        return Base64Url.EncodeToString(json);
    }

    /// <summary>Whether <paramref name="text"/> looks like a share link (either form), so a pasted invite box can take both.</summary>
    public static bool LooksLike(string? text)
    {
        var value = (text ?? "").Trim();
        return value.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(TextPrefix, StringComparison.Ordinal);
    }

    public static ShareLink Parse(string? text)
    {
        var value = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Trim('<', '>', '"', '\'');
        try
        {
            if (value.Length > MaximumLength) throw new FormatException();
            string payload;
            if (value.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase)) payload = value[UrlPrefix.Length..].TrimEnd('/');
            else if (value.StartsWith(TextPrefix, StringComparison.Ordinal)) payload = value[TextPrefix.Length..];
            else throw new FormatException();
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(payload.AsSpan()), new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            if (root.GetProperty("v").GetInt32() != 1) throw new FormatException();
            static string? Optional(JsonElement item, string name) =>
                item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var networkId = Optional(root, "n");
            var link = new ShareLink
            {
                Id = root.GetProperty("i").GetString()!, Secret = root.GetProperty("k").GetString()!, NetworkId = networkId,
                From = Optional(root, "f"), Label = Optional(root, "l"),
                Hosts = root.GetProperty("h").EnumerateArray().Take(MaximumHosts + 1).Select(h => new NetworkInvite
                {
                    HostId = h.GetProperty("h").GetString()!, SpkiFingerprint = h.GetProperty("s").GetString()!,
                    Origin = h.GetProperty("o").GetString()!,
                    Addresses = h.TryGetProperty("a", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Select(a => a.GetString()!).ToArray() : [],
                    NetworkId = networkId, Label = Optional(h, "l")
                }).ToArray()
            };
            link.Validate();
            return link;
        }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or
            ArgumentException or ContractException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "That isn't a whole Martlet share link. Copy the entire link, starting with martlet://share/ or martlet-share-v1.");
        }
    }

    public void Validate()
    {
        ContractRules.Require(IsToken(Id, 16), "The share link's ID is invalid.");
        ContractRules.Require(IsToken(Secret, 32), "The share link's secret is invalid.");
        ContractRules.Require(Hosts.Count is > 0 and <= MaximumHosts, "The share link must name one to eight hosts.");
        ContractRules.Require(Hosts.Select(h => h.HostId).Distinct(StringComparer.Ordinal).Count() == Hosts.Count, "The share link names a host twice.");
        foreach (var host in Hosts) host.Validate();
        ContractRules.Require(From is null || From.Length <= 64 && From.All(c => c >= ' '), "The share link's sender is invalid.");
        ContractRules.Require(Label is null || Label.Length <= 64 && Label.All(c => c >= ' '), "The share link's name is invalid.");
        ContractRules.Require(NetworkId is null || NetworkId.Length <= 64 && NetworkId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "The share link's network ID is invalid.");
    }

    /// <summary>A name for people: at most 64 printable characters, or null when empty.</summary>
    public static string? Clean(string? value)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    public override string ToString() => $"Share link {Id} ({Hosts.Count} host(s))";
}

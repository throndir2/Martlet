using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux;

internal sealed class HostInputException : Exception;
internal sealed class HostApprovalException : Exception;
internal sealed class HostTerminalException : Exception;
internal sealed class HostEofException : Exception;

/// <summary>A host role service the gateway relays to: a loopback service on this machine, installed by martlet-host.
/// <paramref name="Slots"/> is how many requests it runs at once (only a deep-thinking role runs more than one).
/// <paramref name="Gpus"/> is where it runs, from gpus.json (the role's card, or "cpu"); null when that doesn't say, which
/// counts as the whole host.</summary>
internal sealed record HostRole(string Kind, Uri Endpoint, string Model, int Slots = 1, IReadOnlyList<string>? Gpus = null);

/// <summary>gpus.json beside host.json, written by martlet-host: which graphics cards each route role runs on, for the
/// gateway's GPU priority (live turn first): {"schemaVersion":1,"roles":{"ollama":["GPU-..."],"stt":["cpu"]}}. A role
/// pinned to one card names its UUID; a role added to run on the processor says "cpu"; a role on every card, or one the host
/// doesn't know, is left out. It is not part of the approved configuration: moving a role to another card needs no new
/// approval, and a wrong file only changes which work waits, never what is reachable.</summary>
internal static class HostGpus
{
    internal const int MaximumBytes = 4_096;

    /// <summary>The cards each role kind runs on (one to eight devices: a GPU or MIG UUID, a CUDA index, or "cpu" alone).</summary>
    internal static IReadOnlyDictionary<string, string[]> Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new HostInputException();
        using var document = StrictJson.Parse(bytes);
        var root = document.RootElement;
        StrictJson.Properties(root, "schemaVersion", "roles");
        var roles = root.GetProperty("roles");
        if (StrictJson.Number(root, "schemaVersion") != 1 || roles.ValueKind != JsonValueKind.Object) throw new HostInputException();
        var placed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var role in roles.EnumerateObject())
        {
            if (!HostConfiguration.RoleKinds.Contains(role.Name) || placed.ContainsKey(role.Name) ||
                role.Value.ValueKind != JsonValueKind.Array || role.Value.GetArrayLength() is < 1 or > GatewayGpus.MaximumDevices)
                throw new HostInputException();
            var devices = role.Value.EnumerateArray()
                .Select(device => device.ValueKind == JsonValueKind.String ? device.GetString()! : throw new HostInputException()).ToArray();
            try { placed[role.Name] = GatewayGpus.Validate(devices); }
            catch (GatewayProtocolException) { throw new HostInputException(); }
        }
        return placed;
    }
}

internal sealed record HostConfiguration(string HostId, string StateDirectory,
    GatewayHostBinding Binding, uint ServiceUid, uint ServiceGid, string Digest,
    IReadOnlyList<HostRole> Roles)
{
    internal const int MaximumBytes = 8192;
    internal const int MaximumRoles = 8;
    internal const string Backend = "linuxServicePermissions";
    /// <summary>Role kinds with a gateway relay worker. Every role is declared the same way in host.json.</summary>
    internal static readonly IReadOnlySet<string> RoleKinds = new HashSet<string>(StringComparer.Ordinal) { "audio2face", "ollama", "deep-thinking", "stt", "f5", "xtts", "gpt-sovits", "chatterbox", "dia", "singing", "pictures", "ocr" };

    internal static HostConfiguration Parse(byte[] bytes)
    {
        using var document = StrictJson.Parse(bytes);
        var root = document.RootElement;
        var hasRoles = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("roles", out _);
        StrictJson.Properties(root, hasRoles
            ? ["schemaVersion", "hostId", "stateDirectory", "storageBackend", "binding", "serviceUid", "serviceGid", "roles"]
            : ["schemaVersion", "hostId", "stateDirectory", "storageBackend", "binding", "serviceUid", "serviceGid"]);
        if (StrictJson.Number(root, "schemaVersion") != 1 || StrictJson.Text(root, "storageBackend") != Backend)
            throw new HostInputException();
        var id = StrictJson.Text(root, "hostId");
        var state = StrictJson.Text(root, "stateDirectory");
        if (!Identifier(id) || !LinuxControlDirectory.ValidPath(state) || state.Split('/').Length < 3)
            throw new HostInputException();
        var uid = StrictJson.Number(root, "serviceUid");
        var gid = StrictJson.Number(root, "serviceGid");
        if (uid == 0 || gid == 0 || uid == uint.MaxValue || gid == uint.MaxValue) throw new HostInputException();
        var binding = root.GetProperty("binding");
        StrictJson.Properties(binding, "mode", "origin");
        GatewayHostBinding selected;
        try
        {
            var origin = new GatewayOrigin(StrictJson.Text(binding, "origin"));
            selected = StrictJson.Text(binding, "mode") switch
            {
                "loopback" => GatewayHostBinding.Loopback(origin),
                "privateIp" => GatewayHostBinding.ExactPrivateAddress(origin),
                "published" => GatewayHostBinding.PublishedPrivateAddress(origin, InsideContainer()),
                _ => throw new HostInputException()
            };
        }
        catch (GatewayProtocolException) { throw new HostInputException(); }
        catch (GatewayPersistenceException) { throw new HostInputException(); }
        var roles = new List<HostRole>();
        if (hasRoles)
        {
            var list = root.GetProperty("roles");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumRoles) throw new HostInputException();
            foreach (var item in list.EnumerateArray())
            {
                // A deep-thinking role may say how many thinks its Ollama runs at once (OLLAMA_NUM_PARALLEL); one otherwise.
                var hasSlots = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("slots", out _);
                if (hasSlots) StrictJson.Properties(item, "kind", "endpoint", "model", "slots");
                else StrictJson.Properties(item, "kind", "endpoint", "model");
                var kind = StrictJson.Text(item, "kind");
                var model = StrictJson.Text(item, "model");
                var slots = hasSlots ? StrictJson.Number(item, "slots") : 1;
                if (!RoleKinds.Contains(kind) || roles.Any(role => role.Kind == kind) || !ModelToken(model) ||
                    !Uri.TryCreate(StrictJson.Text(item, "endpoint"), UriKind.Absolute, out var endpoint) ||
                    !LoopbackRoot(endpoint) || slots < 1 || slots > Martlet.Core.Settings.SelfHostSetup.DeepThinkingMaximumSlots ||
                    hasSlots && kind != "deep-thinking")
                    throw new HostInputException();
                roles.Add(new(kind, endpoint, model, (int)slots));
            }
        }
        return new(id, state, selected, uid, gid, Convert.ToHexStringLower(SHA256.HashData(bytes)), roles);
    }

    /// <summary>This configuration with each role placed on the graphics cards <paramref name="gpus"/> names for its kind
    /// (from gpus.json); a role it doesn't name keeps an unknown placement, which counts as the whole host.</summary>
    internal HostConfiguration WithGpus(IReadOnlyDictionary<string, string[]> gpus) =>
        gpus.Count == 0 ? this : this with { Roles = [.. Roles.Select(role => role with { Gpus = gpus.GetValueOrDefault(role.Kind) })] };

    // Role services listen only on this host's numeric loopback; the gateway is the single LAN entry point.
    private static bool LoopbackRoot(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttp && System.Net.IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) &&
        System.Net.IPAddress.IsLoopback(address) && endpoint.AbsolutePath == "/" && endpoint.Query.Length == 0 &&
        endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0 && endpoint.OriginalString.EndsWith('/');

    internal static bool ModelToken(string text) => text is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/');

    /// <summary>Docker/Podman markers; "published" binding is only valid in a container's own network namespace.</summary>
    internal static Func<bool> InsideContainer { get; set; } =
        () => File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");

    internal static bool Identifier(string text) => text is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    internal void CheckIdentity(LinuxControlDirectory directory)
    {
        if (ServiceUid != directory.UserId || ServiceGid != directory.GroupId)
            throw new HostApprovalException();
    }

    internal void CheckPlacement(string configPath)
    {
        var parent = configPath[..^"/host.json".Length];
        if (StateDirectory == parent || configPath.StartsWith(StateDirectory + "/", StringComparison.Ordinal) ||
            new[] { LinuxControlDirectory.Config, LinuxControlDirectory.Approval, LinuxControlDirectory.Staging, LinuxControlDirectory.Machine }
                .Select(name => parent + "/" + name)
                .Any(path => StateDirectory == path || StateDirectory.StartsWith(path + "/", StringComparison.Ordinal)))
            throw new HostInputException();
    }

    internal void Recheck(LinuxControlDirectory directory)
    {
        CheckIdentity(directory);
        var current = directory.Read(LinuxControlDirectory.Config, MaximumBytes) ?? throw new HostInputException();
        if (Convert.ToHexStringLower(SHA256.HashData(current)) != Digest)
            throw new HostApprovalException();
    }
}

internal sealed record ServiceApproval(string ConfigSha256, string HostId, string SpkiFingerprint,
    uint ServiceUid, uint ServiceGid)
{
    internal GatewayHostIdentity Identity => new() { HostId = HostId, SpkiFingerprint = SpkiFingerprint };

    internal static ServiceApproval Parse(byte[] bytes)
    {
        using var document = StrictJson.Parse(bytes);
        var root = document.RootElement;
        StrictJson.Properties(root, "schemaVersion", "scope", "configSha256", "hostId",
            "spkiFingerprint", "serviceUid", "serviceGid");
        var digest = StrictJson.Text(root, "configSha256");
        var pin = StrictJson.Text(root, "spkiFingerprint");
        var id = StrictJson.Text(root, "hostId");
        if (StrictJson.Number(root, "schemaVersion") != 1 ||
            StrictJson.Text(root, "scope") != "gateway-listener-start" ||
            !Hash(digest) || !HostConfiguration.Identifier(id) ||
            !pin.StartsWith("sha256:", StringComparison.Ordinal) || !Hash(pin[7..]))
            throw new HostInputException();
        return new(digest, id, pin, StrictJson.Number(root, "serviceUid"), StrictJson.Number(root, "serviceGid"));
    }

    private static bool Hash(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal void Check(HostConfiguration config, LinuxControlDirectory directory, bool requireDigest = true)
    {
        config.Recheck(directory);
        if (HostId != config.HostId || ServiceUid != directory.UserId || ServiceGid != directory.GroupId ||
            requireDigest && ConfigSha256 != config.Digest)
            throw new HostApprovalException();
    }

    internal static byte[] Create(HostConfiguration config, GatewayHostIdentity identity) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, scope = "gateway-listener-start", configSha256 = config.Digest,
            hostId = identity.HostId, spkiFingerprint = identity.SpkiFingerprint,
            serviceUid = config.ServiceUid, serviceGid = config.ServiceGid
        });
}

internal static class StrictJson
{
    internal static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > HostConfiguration.MaximumBytes) throw new HostInputException();
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException) { throw new HostInputException(); }
    }

    internal static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new HostInputException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                throw new HostInputException();
        if (names.Count != expected.Length) throw new HostInputException();
    }

    internal static string Text(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String
            ? value.GetProperty(name).GetString()! : throw new HostInputException();

    internal static uint Number(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Number && value.GetProperty(name).TryGetUInt32(out var number)
            ? number : throw new HostInputException();
}

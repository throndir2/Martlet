using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps the shared Home Assistant connection between restarts (home-assistant.json beside
/// host.json on Linux hosts). The saved copy contains the HA access token and must be private to the gateway owner.</summary>
public interface IGatewayHomeAssistantStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>The Home Assistant connection this host shares with every paired desktop. The host never uses the token; it
/// only keeps the newest owner-written register and returns it to paired devices over the signed, pinned connection.</summary>
internal sealed class GatewayHomeAssistantStore
{
    internal const int MaximumBytes = 16 * 1024;

    private static readonly JsonSerializerOptions StorageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    private readonly object gate = new();
    private SharedHomeAssistantConnection? current;
    private IGatewayHomeAssistantStorage? storage;

    internal SharedHomeAssistantConnection? Current { get { lock (gate) return current; } }

    internal void Attach(IGatewayHomeAssistantStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SharedHomeAssistantConnection? saved = null;
        try
        {
            if (value.Load() is { Length: > 0 and <= MaximumBytes } bytes)
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                InspectStorageJson(document.RootElement);
                saved = document.Deserialize<SharedHomeAssistantConnection>(StorageJson);
                ValidateStored(saved);
            }
        }
        // An unreadable or malformed copy starts empty; the next accepted desktop write replaces it.
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or GatewayProtocolException or
            IOException or UnauthorizedAccessException) { saved = null; }
        lock (gate)
        {
            storage = value;
            if (saved is not null) current = Winner(current, saved);
        }
    }

    internal SharedHomeAssistantConnection Upsert(HomeAssistantShareRequest request, GatewayPrincipal principal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        var incoming = new SharedHomeAssistantConnection
        {
            Revision = request.Revision,
            Address = request.Address,
            Token = request.Token,
            LocationName = request.LocationName,
            Version = request.Version,
            UpdatedBy = principal.DeviceId,
            UpdatedAt = now
        };
        ValidateStored(incoming);
        lock (gate)
        {
            var next = Winner(current, incoming);
            if (ReferenceEquals(next, current)) return current!;
            current = next;
            SaveLocked(next);
            return next;
        }
    }

    private void SaveLocked(SharedHomeAssistantConnection value)
    {
        if (storage is null) return;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, StorageJson);
            if (bytes.Length <= MaximumBytes) storage.Save(bytes);
        }
        // If saving fails, keep serving the in-memory copy; desktops will send it again.
        catch (Exception) { }
    }

    private static SharedHomeAssistantConnection Winner(SharedHomeAssistantConnection? stored, SharedHomeAssistantConnection incoming)
    {
        if (stored is null || incoming.Revision > stored.Revision ||
            incoming.Revision == stored.Revision && string.CompareOrdinal(incoming.UpdatedBy, stored.UpdatedBy) > 0)
            return incoming;
        return stored;
    }

    internal static void ValidateRequest(HomeAssistantShareRequest request)
    {
        ValidateCore(request.Revision, request.Address, request.Token, request.LocationName, request.Version);
    }

    private static void ValidateStored(SharedHomeAssistantConnection? value)
    {
        GatewayRules.Require(value is not null, "request.invalid");
        ValidateCore(value!.Revision, value.Address, value.Token, value.LocationName, value.Version);
        GatewayRules.Identifier(value.UpdatedBy);
        GatewayRules.Require(value.UpdatedAt != default, "request.invalid");
    }

    private static void ValidateCore(long revision, string? address, string? token, string? locationName, string? version)
    {
        GatewayRules.Require(revision > 0, "request.invalid");
        GatewayRules.Require((address is null) == (token is null), "request.invalid");
        if (address is null)
        {
            GatewayRules.Require(locationName is null && version is null, "request.invalid");
            return;
        }
        GatewayRules.Require(address.Length is > 0 and <= 512 && PrintableAscii(address), "request.invalid");
        GatewayRules.Require(Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0, "request.invalid");
        GatewayRules.Require(token is { Length: >= 16 and <= 4096 } && PrintableAscii(token) && !token.Any(char.IsWhiteSpace),
            "request.invalid");
        GatewayRules.Require(locationName is null || locationName.Length <= 64 && !locationName.Any(char.IsControl), "request.invalid");
        GatewayRules.Require(version is null || version.Length <= 32 && PrintableAscii(version), "request.invalid");
    }

    private static bool PrintableAscii(string value) => value.All(c => c is >= ' ' and <= '~');

    private static void InspectStorageJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                GatewayRules.Require(names.Add(property.Name), "request.invalid");
                InspectStorageJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) InspectStorageJson(item);
        }
        else if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HomeAssistantShareRequest
{
    public required long Revision { get; init; }
    public string? Address { get; init; }
    public string? Token { get; init; }
    public string? LocationName { get; init; }
    public string? Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SharedHomeAssistantConnection
{
    public required long Revision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Address { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Token { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? LocationName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Version { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string HomeAssistantPath = "/martlet/v1/home-assistant";
    private const int MaximumHomeAssistantResponseBytes = 32 * 1024;

    internal GatewayHomeAssistantStore HomeAssistant { get; } = new();

    /// <summary>GET returns this host's shared Home Assistant connection; POST replaces it when the incoming revision wins.
    /// Any paired device may read or write it, including the token, over its signed and pinned connection.</summary>
    private async ValueTask InvokeHomeAssistantAsync(HttpContext context)
    {
        SharedHomeAssistantConnection? result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            result = HomeAssistant.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, GatewayHomeAssistantStore.MaximumBytes, context.RequestAborted)
                .ConfigureAwait(false);
            var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
            var request = ReadHomeAssistantBody(bytes);
            result = HomeAssistant.Upsert(request, principal, clock.GetUtcNow());
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new HomeAssistantDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            HomeAssistant = result
        }, MaximumHomeAssistantResponseBytes).ConfigureAwait(false);
    }

    private static HomeAssistantShareRequest ReadHomeAssistantBody(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            InspectJson(document.RootElement);
            var request = document.Deserialize<HomeAssistantShareRequest>(Json) ?? throw new GatewayProtocolException("request.invalid");
            GatewayHomeAssistantStore.ValidateRequest(request);
            return request;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    private sealed record HomeAssistantDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required SharedHomeAssistantConnection? HomeAssistant { get; init; }
    }
}

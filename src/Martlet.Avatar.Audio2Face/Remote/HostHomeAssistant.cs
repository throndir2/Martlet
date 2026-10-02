using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A Home Assistant connection shared through a host. Address/Token null = sharing stopped (tombstone).</summary>
public sealed record SharedHomeAssistant(long Revision, string? Address, string? Token, string? LocationName, string? Version,
    string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null);

public sealed partial class Audio2FaceHostConnection
{
    private const string HomeAssistantPath = "/martlet/v1/home-assistant";
    private const int MaximumHomeAssistantResponseBytes = 32 * 1024;

    private static readonly JsonSerializerOptions HomeAssistantJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    /// <summary>null when the host keeps none. Hosts older than this endpoint throw Audio2FaceHostException with Code
    /// "request.invalid" (let it propagate).</summary>
    public async Task<SharedHomeAssistant?> ReadHomeAssistantAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + HomeAssistantPath);
        Sign(request, []);
        return await HomeAssistantAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends value (UpdatedBy/UpdatedAt ignored) and returns what the host keeps afterwards (newer value from
    /// another device may win).</summary>
    public async Task<SharedHomeAssistant?> ShareHomeAssistantAsync(SharedHomeAssistant value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        try { ValidateHomeAssistant(value, requireStamp: false); }
        catch (FormatException error) { throw new ArgumentException("The shared Home Assistant connection is invalid.", nameof(value), error); }
        var body = JsonSerializer.SerializeToUtf8Bytes(new HomeAssistantRequest
        {
            Revision = value.Revision,
            Address = value.Address,
            Token = value.Token,
            LocationName = value.LocationName,
            Version = value.Version
        }, HomeAssistantJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + HomeAssistantPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await HomeAssistantAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SharedHomeAssistant?> HomeAssistantAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumHomeAssistantResponseBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            if (!root.TryGetProperty("home_assistant", out var item)) throw new FormatException();
            if (item.ValueKind == JsonValueKind.Null) return null;
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException();
            var value = new SharedHomeAssistant(
                item.GetProperty("revision").GetInt64(),
                NullableString(item, "address"),
                NullableString(item, "token"),
                NullableString(item, "location_name"),
                NullableString(item, "version"),
                RequiredString(item, "updated_by"),
                item.GetProperty("updated_at").GetDateTimeOffset());
            ValidateHomeAssistant(value, requireStamp: true);
            return value;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's shared Home Assistant connection was invalid.");
        }
    }

    private static string? NullableString(JsonElement item, string name)
    {
        var value = item.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new FormatException()
        };
    }

    private static string RequiredString(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String ? item.GetProperty(name).GetString() ?? throw new FormatException() : throw new FormatException();

    private static void ValidateHomeAssistant(SharedHomeAssistant value, bool requireStamp)
    {
        if (value.Revision <= 0) throw new FormatException();
        if ((value.Address is null) != (value.Token is null)) throw new FormatException();
        if (value.Address is null)
        {
            if (value.LocationName is not null || value.Version is not null) throw new FormatException();
        }
        else
        {
            var address = value.Address;
            var token = value.Token ?? throw new FormatException();
            if (address.Length is 0 or > 512 || !PrintableAscii(address) ||
                !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https" ||
                uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new FormatException();
            if (token.Length is < 16 or > 4096 || !PrintableAscii(token) || token.Any(char.IsWhiteSpace))
                throw new FormatException();
            if (value.LocationName is { Length: > 64 } || value.LocationName?.Any(char.IsControl) == true) throw new FormatException();
            if (value.Version is { Length: > 32 } || value.Version is not null && !PrintableAscii(value.Version)) throw new FormatException();
        }
        if (!requireStamp) return;
        if (!IsDeviceId(value.UpdatedBy) || value.UpdatedAt is null || value.UpdatedAt == default(DateTimeOffset)) throw new FormatException();
    }

    private static bool PrintableAscii(string value) => value.All(c => c is >= ' ' and <= '~');

    private static bool IsDeviceId(string? value) => value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private sealed record HomeAssistantRequest
    {
        public required long Revision { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Address { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Token { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? LocationName { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Version { get; init; }
    }
}

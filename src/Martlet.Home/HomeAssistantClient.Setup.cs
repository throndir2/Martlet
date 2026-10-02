using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;

namespace Martlet.Home;

/// <summary>Where a Home Assistant is in its first-run setup (<c>GET /api/onboarding</c>, which needs no sign-in).</summary>
public sealed record HomeOnboarding(bool Owner, bool CoreConfig, bool Analytics, bool Integration)
{
    /// <summary>Nobody has created the owner account yet: whoever does becomes its administrator.</summary>
    public bool NeedsOwner => !Owner;
    public bool Done => Owner && CoreConfig && Analytics && Integration;
}

/// <summary>Regional settings Martlet gives a new Home Assistant, taken from Windows.</summary>
public sealed record HomeRegion(string Language, string? TimeZone, string? Country, string? Currency, bool Metric)
{
    public static HomeRegion FromSystem()
    {
        var culture = CultureInfo.CurrentUICulture;
        var language = culture.TwoLetterISOLanguageName is { Length: 2 } two && two != "iv" ? two : "en";
        string? zone = TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : null;
        RegionInfo? region = null;
        try { region = RegionInfo.CurrentRegion; }
        catch (ArgumentException) { }
        var country = region?.TwoLetterISORegionName is { Length: 2 } code && code.All(char.IsAsciiLetterUpper) ? code : null;
        var currency = region?.ISOCurrencySymbol is { Length: 3 } symbol && symbol.All(char.IsAsciiLetterUpper) ? symbol : null;
        return new(language, zone, country, currency, region?.IsMetric ?? true);
    }
}

/// <summary>The short-lived sign-in Home Assistant hands out after its owner is created or someone signs in: an access token
/// (about 30 minutes) and the refresh token behind it. Martlet only uses it to mint its own long-lived token, then revokes it.</summary>
public sealed record HomeSession(string AccessToken, string RefreshToken);

public sealed partial class HomeAssistantClient
{
    /// <summary>Reads how far first-run setup got. Throws <see cref="HomeAssistantException"/> when the address isn't a
    /// Home Assistant. A Home Assistant that started after its setup was finished no longer serves the onboarding API; it
    /// is recognized by its sign-in providers instead (<c>/auth/providers</c>, which also needs no sign-in).</summary>
    public async Task<HomeOnboarding> OnboardingAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await SendAsync(HttpMethod.Get, baseUri, "api/onboarding", null, (HttpContent?)null, MaximumSmallResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HomeAssistantException error) when (error.Failure == HomeAssistantFailure.NotHomeAssistant)
        {
            using var providers = await SendAsync(HttpMethod.Get, baseUri, "auth/providers", null, (HttpContent?)null, MaximumSmallResponse,
                cancellationToken).ConfigureAwait(false);
            var root = providers.RootElement;
            var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("providers", out var named) ? named : root;
            if (list.ValueKind != JsonValueKind.Array || !list.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.Object && Text(p, "type") is { Length: > 0 }))
                throw NotHomeAssistant();
            return new(true, true, true, true);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw NotHomeAssistant();
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in document.RootElement.EnumerateArray())
                if (step.ValueKind == JsonValueKind.Object && Text(step, "step") is { } name &&
                    step.TryGetProperty("done", out var flag) && flag.ValueKind == JsonValueKind.True)
                    done.Add(name);
            return new(done.Contains("user"), done.Contains("core_config"), done.Contains("analytics"), done.Contains("integration"));
        }
    }

    /// <summary>Creates the owner (administrator) account of a Home Assistant nobody has set up yet and signs in as it.
    /// The password is sent once, to this address only, and never stored.</summary>
    public async Task<HomeSession> CreateOwnerAsync(Uri baseUri, string name, string username, ReadOnlyMemory<char> password,
        string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (password.Length is 0 or > 256) throw new ArgumentException("Enter a password of up to 256 characters.", nameof(password));
        var clientId = ClientId(baseUri);
        var body = new JsonObject
        {
            ["client_id"] = clientId, ["name"] = name.Trim(), ["username"] = username.Trim(),
            ["password"] = new string(password.Span), ["language"] = language
        };
        string code;
        using (var document = await SendAsync(HttpMethod.Post, baseUri, "api/onboarding/users", null, Json(body), MaximumSmallResponse,
            cancellationToken, forbidden: "Someone already created this Home Assistant's owner account. Sign in with Home Assistant instead.")
            .ConfigureAwait(false))
            code = Text(document.RootElement, "auth_code") ?? throw NotHomeAssistant();
        return await ExchangeCodeAsync(baseUri, clientId, code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finishes first-run setup as the new owner: Windows' time zone, country, currency, units and language;
    /// usage analytics stay off (Home Assistant's default); then marks the remaining steps done. Regional settings Home
    /// Assistant refuses are left at its defaults rather than failing setup.</summary>
    public async Task FinishOnboardingAsync(Uri baseUri, HomeSession session, HomeRegion region, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(region);
        var state = await OnboardingAsync(baseUri, cancellationToken).ConfigureAwait(false);
        await using (var socket = await HomeAssistantSocket.ConnectAsync(baseUri, session.AccessToken, cancellationToken).ConfigureAwait(false))
        {
            var update = new JsonObject { ["type"] = "config/core/update", ["unit_system"] = region.Metric ? "metric" : "us_customary" };
            if (region.TimeZone is { } zone) update["time_zone"] = zone;
            if (region.Country is { } country) update["country"] = country;
            if (region.Currency is { } currency) update["currency"] = currency;
            update["language"] = region.Language;
            foreach (var attempt in new[] { update, Without(update, "language", "currency"), Without(update, "language", "currency", "country") })
            {
                try
                {
                    await socket.CommandAsync(attempt, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (HomeAssistantException error) when (error.Failure == HomeAssistantFailure.BadResponse) { }
            }
        }
        if (!state.CoreConfig) await StepAsync(baseUri, session, "api/onboarding/core_config", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!state.Analytics) await StepAsync(baseUri, session, "api/onboarding/analytics", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!state.Integration)
            await StepAsync(baseUri, session, "api/onboarding/integration", new JsonObject
            {
                ["client_id"] = ClientId(baseUri), ["redirect_uri"] = ClientId(baseUri) + "?auth_callback=1"
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mints Martlet's own long-lived access token (shown in the user's Home Assistant profile under Security as
    /// <paramref name="clientName"/>, where it can be revoked) from a short-lived session, then revokes that session.</summary>
    public async Task<SecretLease> MintTokenAsync(Uri baseUri, HomeSession session, string clientName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        string? minted;
        await using (var socket = await HomeAssistantSocket.ConnectAsync(baseUri, session.AccessToken, cancellationToken).ConfigureAwait(false))
        {
            var result = await socket.CommandAsync(new JsonObject
            {
                ["type"] = "auth/long_lived_access_token", ["client_name"] = Clean(clientName, 64), ["lifespan"] = 3650
            }, cancellationToken).ConfigureAwait(false);
            minted = result.ValueKind == JsonValueKind.String ? result.GetString() : null;
        }
        if (string.IsNullOrEmpty(minted) || minted.Length > SecretLease.MaximumLength) throw NotHomeAssistant();
        var lease = new SecretLease(minted);
        await RevokeAsync(baseUri, session.RefreshToken, cancellationToken).ConfigureAwait(false);
        return lease;
    }

    /// <summary>Exchanges a sign-in code for a short-lived session (<c>POST /auth/token</c>).</summary>
    public async Task<HomeSession> ExchangeCodeAsync(Uri baseUri, string clientId, string code, CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId
        });
        using var document = await SendAsync(HttpMethod.Post, baseUri, "auth/token", null, form, MaximumSmallResponse, cancellationToken,
            forbidden: "Home Assistant didn't accept the sign-in. Try again.").ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "access_token") is not { Length: > 0 } access ||
            Text(root, "refresh_token") is not { Length: > 0 } refresh)
            throw NotHomeAssistant();
        return new(access, refresh);
    }

    // Revoking is best effort: the session expires on its own, and the minted token is what Martlet keeps.
    private async Task RevokeAsync(Uri baseUri, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "auth/revoke"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken })
            };
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException) { }
    }

    private async Task StepAsync(Uri baseUri, HomeSession session, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Post, baseUri, path, null, Json(body), MaximumSmallResponse, cancellationToken,
            forbidden: "Home Assistant's setup was finished by someone else in the meantime. Sign in with Home Assistant instead.",
            bearer: session.AccessToken).ConfigureAwait(false);
    }

    /// <summary>The OAuth client ID Martlet uses while setting up: Home Assistant's own address, as its web page does.</summary>
    internal static string ClientId(Uri baseUri) => new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/").AbsoluteUri;

    private static ByteArrayContent Json(JsonObject body) => Json(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()));

    private static JsonObject Without(JsonObject source, params string[] names)
    {
        var copy = (JsonObject)source.DeepClone();
        foreach (var name in names) copy.Remove(name);
        return copy;
    }
}

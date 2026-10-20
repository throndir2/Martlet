using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>Martlet's VRoid Hub app: its client ID and secret. Release builds carry them (the build reads the
/// <c>VROID_HUB_CLIENT_ID</c> and <c>VROID_HUB_CLIENT_SECRET</c> environment variables into assembly metadata); a developer's
/// PC can set the same variables instead. Without them, linking VRoid Hub is off and searching still works.</summary>
public static class VRoidHubApp
{
    public const string ClientIdVariable = "VROID_HUB_CLIENT_ID", ClientSecretVariable = "VROID_HUB_CLIENT_SECRET";

    public static string? ClientId => Value(ClientIdVariable);
    public static string? ClientSecret => Value(ClientSecretVariable);
    public static bool Configured => ClientId is not null && ClientSecret is not null;

    private static string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            value = typeof(VRoidHubApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value;
        value = value?.Trim();
        return value is { Length: > 0 and <= 256 } && value.All(c => c is > ' ' and < (char)127) ? value : null;
    }
}

/// <summary>A linked VRoid Hub account's tokens and who it is. Kept only in Windows Credential Manager, never in settings.</summary>
public sealed record VRoidHubTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId, string UserName)
{
    public bool Expired(DateTimeOffset now) => now >= ExpiresAt - TimeSpan.FromMinutes(2);

    public string Write() => JsonSerializer.Serialize(this, VRoidHub.Json);

    public static VRoidHubTokens? Read(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var tokens = JsonSerializer.Deserialize<VRoidHubTokens>(text, VRoidHub.Json);
            return tokens is { AccessToken.Length: > 0, RefreshToken.Length: > 0 } ? tokens : null;
        }
        catch (JsonException) { return null; }
    }
}

public sealed class VRoidHubException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    /// <summary>VRoid Hub refused the account's tokens: link the account again.</summary>
    public bool SignedOut => Status is HttpStatusCode.Unauthorized;
}

/// <summary>VRoid Hub's public API (https://hub.vroid.com, API version 11): searching character models (no account needed),
/// the linked account's own and hearted models, and downloading a model with a download license into memory. Linking uses
/// OAuth 2.0 with PKCE and a loopback return address on this PC (<see cref="SignInAsync"/>). The rules for linked apps:
/// show each model's conditions of use and credit, never save its file, and never pass it on.</summary>
public sealed class VRoidHub(HttpClient http)
{
    public const string Origin = "https://hub.vroid.com";
    public const string ApiVersion = "11";
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private const int MaximumRequestBytes = 8192;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 32
    };

    /// <summary>The shared client: no cookies, bounded time, an honest user agent (some file hosts refuse requests without one),
    /// and redirects followed only where Martlet checks them (see <see cref="DownloadAsync"/>).</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Martlet/{typeof(VRoidHub).Assembly.GetName().Version?.ToString(3) ?? "0"} (+https://github.com/throndir2/Martlet)");
        return client;
    }

    // ---------- searching ----------

    /// <summary>Models whose name, character or tags match <paramref name="keyword"/> that their creators let anyone download
    /// and use as an avatar, all ages, at most <paramref name="count"/> (1-100).</summary>
    public async Task<IReadOnlyList<CharacterModelCandidate>> SearchAsync(string keyword, int count, string? accessToken, CancellationToken token)
    {
        ContractRules.Require(!string.IsNullOrWhiteSpace(keyword) && keyword.Length <= 100, "Search for 1 to 100 characters.");
        var path = "/api/search/character_models?keyword=" + Uri.EscapeDataString(keyword.Trim()) +
            $"&count={Math.Clamp(count, 1, 100)}&is_downloadable=true&characterization_allowed_user=everyone";
        using var document = await GetJsonAsync(path, accessToken, token);
        return Models(document.RootElement, ownerId: null).Where(c => c.Usable).ToArray();
    }

    /// <summary>The linked account's own models and the models it hearted that it may use.</summary>
    public async Task<IReadOnlyList<CharacterModelCandidate>> AccountModelsAsync(VRoidHubTokens tokens, CancellationToken token)
    {
        var found = new List<CharacterModelCandidate>();
        using (var own = await GetJsonAsync("/api/account/character_models?count=50", tokens.AccessToken, token))
            found.AddRange(Models(own.RootElement, tokens.UserId));
        using (var hearts = await GetJsonAsync("/api/hearts?count=50", tokens.AccessToken, token))
            found.AddRange(Models(hearts.RootElement, tokens.UserId).Where(c => c.Usable));
        return found.DistinctBy(c => c.Id).ToArray();
    }

    /// <summary>One model's details (public, no account needed); null when VRoid Hub doesn't show it.</summary>
    public async Task<CharacterModelCandidate?> ModelAsync(string modelId, string? ownerId, CancellationToken token)
    {
        ContractRules.Require(RemoteCharacterModels.IsVRoidHubId(modelId), "A VRoid Hub model ID is 1 to 32 digits.");
        try
        {
            using var document = await GetJsonAsync("/api/character_models/" + modelId, null, token);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            // The details wrap the model (character_model) with its description; older answers gave the model itself.
            var model = data.TryGetProperty("character_model", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : data;
            return Candidate(model, ownerId) is { } candidate
                ? candidate with { Description = Clip(Text(data, "description"), 400) } : null;
        }
        catch (VRoidHubException error) when (error.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) { return null; }
    }

    // ---------- downloading ----------

    /// <summary>Downloads a model's VRM file into memory with a download license for the linked account. Nothing is written to
    /// disk. Throws <see cref="VRoidHubException"/> when VRoid Hub refuses it.</summary>
    public async Task<byte[]> DownloadAsync(string modelId, VRoidHubTokens tokens, CancellationToken token)
    {
        ContractRules.Require(RemoteCharacterModels.IsVRoidHubId(modelId), "A VRoid Hub model ID is 1 to 32 digits.");
        string licenseId;
        using (var license = await SendJsonAsync(HttpMethod.Post, "/api/download_licenses",
                   new StringContent(JsonSerializer.Serialize(new { character_model_id = modelId }), Encoding.UTF8, "application/json"),
                   tokens.AccessToken, token))
        {
            licenseId = license.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id) ? Text(id) ?? "" : "";
        }
        ContractRules.Require(licenseId is { Length: > 0 and <= 64 } && licenseId.All(char.IsAsciiLetterOrDigit),
            "VRoid Hub didn't give a download license for this model.");
        using var request = Request(HttpMethod.Get, $"/api/download_licenses/{licenseId}/download", tokens.AccessToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        Uri? file = response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.MovedPermanently
            ? response.Headers.Location : null;
        if (file is null)
        {
            if (response.IsSuccessStatusCode) return await ReadBoundedAsync(response, LocalAvatarFiles.MaximumVrmBytes, token);
            throw await FailureAsync(response, token);
        }
        if (!file.IsAbsoluteUri) file = new Uri(new Uri(Origin), file);
        ContractRules.Require(file.Scheme == Uri.UriSchemeHttps, "VRoid Hub sent the model from an address that isn't secure.");
        // The file is on VRoid Hub's storage with a signed one-time address: no account token goes there.
        using var download = new HttpRequestMessage(HttpMethod.Get, file);
        using var stored = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, token);
        if (!stored.IsSuccessStatusCode) throw new VRoidHubException($"VRoid Hub's storage didn't send the model ({(int)stored.StatusCode}).", stored.StatusCode);
        return await ReadBoundedAsync(stored, LocalAvatarFiles.MaximumVrmBytes, token);
    }

    // ---------- the linked account ----------

    /// <summary>The address that asks the owner to link Martlet in the browser.</summary>
    public static Uri AuthorizeUri(string clientId, string redirect, string state, string challenge) => new(Origin +
        "/oauth/authorize?response_type=code&client_id=" + Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(redirect) +
        "&scope=default&state=" + state + "&code_challenge=" + challenge + "&code_challenge_method=S256");

    /// <summary>PKCE: a random verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>Links an account: opens VRoid Hub's sign-in page in the browser (<paramref name="openBrowser"/>), receives the
    /// one-time code on a loopback port only this PC can reach, and trades it for the account's tokens. The owner signs in on
    /// VRoid Hub's own page; no password passes through Martlet.</summary>
    public async Task<VRoidHubTokens> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        var clientId = VRoidHubApp.ClientId ?? throw new VRoidHubException("This Martlet build can't link VRoid Hub: it has no VRoid Hub app keys.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/vroid-hub/callback";
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var (verifier, challenge) = Pkce();
        openBrowser(AuthorizeUri(clientId, redirect, state, challenge));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        string code;
        try { code = await ReceiveCodeAsync(listener, state, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VRoidHubException("The VRoid Hub sign-in wasn't finished within 5 minutes. Try again.");
        }
        var tokens = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier
        }, cancellationToken);
        return await WithAccountAsync(tokens, cancellationToken);
    }

    /// <summary>New tokens for an account whose access token ran out.</summary>
    public async Task<VRoidHubTokens> RefreshAsync(VRoidHubTokens tokens, CancellationToken token)
    {
        var fresh = await TokenAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken }, token);
        return fresh with { UserId = tokens.UserId, UserName = tokens.UserName };
    }

    /// <summary>Tells VRoid Hub the tokens are no longer used (unlinking). Failures are ignored: the tokens are deleted anyway.</summary>
    public async Task RevokeAsync(VRoidHubTokens tokens, CancellationToken token)
    {
        if (VRoidHubApp.ClientId is not { } id || VRoidHubApp.ClientSecret is not { } secret) return;
        try
        {
            using var request = Request(HttpMethod.Post, "/oauth/revoke", null);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = tokens.RefreshToken, ["client_id"] = id, ["client_secret"] = secret
            });
            using var response = await http.SendAsync(request, token);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
    }

    private async Task<VRoidHubTokens> WithAccountAsync(VRoidHubTokens tokens, CancellationToken token)
    {
        using var account = await GetJsonAsync("/api/account", tokens.AccessToken, token);
        var user = account.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("user_detail", out var detail) &&
            detail.TryGetProperty("user", out var found) ? found : default;
        var id = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var userId) ? Text(userId) : null;
        var name = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("name", out var userName) ? Text(userName) : null;
        return tokens with { UserId = id ?? "", UserName = Clip(name, 80) ?? "VRoid Hub user" };
    }

    private async Task<VRoidHubTokens> TokenAsync(Dictionary<string, string> form, CancellationToken token)
    {
        var id = VRoidHubApp.ClientId ?? throw new VRoidHubException("This Martlet build has no VRoid Hub app keys.");
        var secret = VRoidHubApp.ClientSecret ?? throw new VRoidHubException("This Martlet build has no VRoid Hub app keys.");
        form["client_id"] = id;
        form["client_secret"] = secret;
        using var request = Request(HttpMethod.Post, "/oauth/token", null);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await FailureAsync(response, token);
            throw response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                ? new VRoidHubException("VRoid Hub didn't accept the sign-in. Link your account again.", HttpStatusCode.Unauthorized, failure)
                : failure;
        }
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, MaximumJsonBytes, token));
        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? Text(a) : null;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? Text(r) : null;
        var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 3600;
        if (access is null || refresh is null) throw new VRoidHubException("VRoid Hub's answer had no tokens.");
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 60, 365 * 86400)), "", "");
    }

    // ---------- reading answers ----------

    internal static IEnumerable<CharacterModelCandidate> Models(JsonElement root, string? ownerId)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in data.EnumerateArray().Take(200))
        {
            // Hearts wrap the model; lists and searches give it directly.
            var model = item.TryGetProperty("character_model", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : item;
            if (Candidate(model, ownerId) is { } candidate) yield return candidate;
        }
    }

    /// <summary>A search result from one VRoid Hub model, with its conditions of use in words. Null when it isn't a public model
    /// Martlet could show (private, age-restricted, or missing its ID).</summary>
    internal static CharacterModelCandidate? Candidate(JsonElement model, string? ownerId)
    {
        if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var idElement) || Text(idElement) is not { } id ||
            !RemoteCharacterModels.IsVRoidHubId(id))
            return null;
        if (Bool(model, "is_private") == true) return null;
        if (model.TryGetProperty("age_limit", out var age) && (Bool(age, "is_r18") == true || Bool(age, "is_r15") == true || Bool(age, "is_adult") == true))
            return null;
        var character = model.TryGetProperty("character", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var user = character.ValueKind == JsonValueKind.Object && character.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var characterName = character.ValueKind == JsonValueKind.Object && character.TryGetProperty("name", out var cn) ? Clip(Text(cn), 80) : null;
        var characterId = character.ValueKind == JsonValueKind.Object && character.TryGetProperty("id", out var ci) ? Text(ci) : null;
        var authorId = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var ui) ? Text(ui) : null;
        var author = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("name", out var un) ? Clip(Text(un), 80) : null;
        var name = Clip(model.TryGetProperty("name", out var n) ? Text(n) : null, 80) ?? characterName ?? "VRoid Hub model";
        var version = model.TryGetProperty("latest_character_model_version", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        var spec = version.ValueKind == JsonValueKind.Object && version.TryGetProperty("spec_version", out var s) ? Text(s) : null;
        var bytes = version.ValueKind == JsonValueKind.Object && version.TryGetProperty("original_file_size", out var size) &&
            size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var b) ? b : (long?)null;
        var meta = version.ValueKind == JsonValueKind.Object && version.TryGetProperty("vrm_meta", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var own = ownerId is { Length: > 0 } && authorId == ownerId;
        var conditions = new List<string>();
        bool avatar, credit;
        if (model.TryGetProperty("license", out var license) && license.ValueKind == JsonValueKind.Object)
        {
            var who = Text(license, "characterization_allowed_user");
            avatar = who == "everyone";
            credit = Text(license, "credit") == "necessary";
            conditions.Add("Use as an avatar or character: " + (avatar ? "anyone" : "only the creator"));
            conditions.Add("Credit: " + (credit ? "required" : "not required"));
            conditions.Add("Changes: " + Allowed(Text(license, "modification")));
            conditions.Add("Redistribution: " + Allowed(Text(license, "redistribution")));
            conditions.Add("Personal commercial use: " + Text(license, "personal_commercial_use") switch
            {
                "profit" => "allowed, also for profit", "nonprofit" => "only non-profit", _ => "not allowed"
            });
            conditions.Add("Corporate commercial use: " + Allowed(Text(license, "corporate_commercial_use")));
            conditions.Add("Sexual expression: " + Allowed(Text(license, "sexual_expression")));
            conditions.Add("Violent expression: " + Allowed(Text(license, "violent_expression")));
        }
        else if (meta.ValueKind == JsonValueKind.Object)
        {
            var permission = Text(meta, "avatarPermission");
            avatar = permission == "everyone";
            credit = Text(meta, "creditNotation") == "required";
            conditions.Add("Use as an avatar or character: " + permission switch
            {
                "everyone" => "anyone", "onlySeparatelyLicensedPerson" => "only people with a separate license", _ => "only the creator"
            });
            conditions.Add("Credit: " + (credit ? "required" : "not required"));
            conditions.Add("Changes: " + Text(meta, "modification") switch
            {
                "allowModification" => "allowed", "allowModificationRedistribution" => "allowed, also to share changed copies", _ => "not allowed"
            });
            conditions.Add("Redistribution: " + (Bool(meta, "allowRedistribution") == true ? "allowed" : "not allowed"));
            conditions.Add("Commercial use: " + Text(meta, "commercialUsage") switch
            {
                "personalProfit" => "personal, also for profit", "corporation" => "personal and corporate", _ => "only personal non-profit"
            });
            conditions.Add("Excessively sexual use: " + (Bool(meta, "allowExcessivelySexualUsage") == true ? "allowed" : "not allowed"));
            conditions.Add("Excessively violent use: " + (Bool(meta, "allowExcessivelyViolentUsage") == true ? "allowed" : "not allowed"));
            conditions.Add("Political or religious use: " + (Bool(meta, "allowPoliticalOrReligiousUsage") == true ? "allowed" : "not allowed"));
            conditions.Add("Antisocial or hateful use: " + (Bool(meta, "allowAntisocialOrHateUsage") == true ? "allowed" : "not allowed"));
            if (Text(meta, "licenseUrl") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps)
                conditions.Add("License: " + url);
        }
        else return null;
        var downloadable = Bool(model, "is_downloadable") == true;
        var others = Bool(model, "is_other_users_available") == true;
        var usable = own || (downloadable && avatar);
        var thumbnail = Picture(model, "portrait_image") ?? Picture(model, "full_body_image");
        var page = characterId is not null && RemoteCharacterModels.IsVRoidHubId(characterId)
            ? $"{Origin}/en/characters/{characterId}/models/{id}" : $"{Origin}/en/models/{id}";
        return new CharacterModelCandidate
        {
            Source = CharacterModelCandidate.VRoidHubSource, Id = id, Name = name, Character = characterName, Author = author ?? "a VRoid Hub creator",
            AuthorUrl = authorId is not null && authorId.All(char.IsAsciiDigit) ? $"{Origin}/en/users/{authorId}" : null,
            Description = null, ThumbnailUrl = thumbnail, PageUrl = page,
            License = "VRoid Hub conditions of use", Conditions = conditions, CreditRequired = credit,
            VrmVersion = spec, Bytes = bytes, Own = own, Usable = usable && (others || downloadable || own),
            Look = RemoteCharacterModels.VRoidHubPath(id)
        };

        static string Allowed(string? value) => value is "allow" or "allowed" ? "allowed" : "not allowed";
    }

    private static string? Picture(JsonElement model, string name) =>
        model.TryGetProperty(name, out var image) && image.ValueKind == JsonValueKind.Object && Bool(image, "is_default_image") != true &&
        image.TryGetProperty("w300", out var size) && size.ValueKind == JsonValueKind.Object && Text(size, "url") is { } url &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? Text(value) : null;

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    internal static string? Clip(string? text, int maximum)
    {
        if (text is null) return null;
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (text.Length > maximum) text = text[..(maximum - 1)].TrimEnd() + "…";
        return text.Length == 0 ? null : text;
    }

    // ---------- HTTP ----------

    private static HttpRequestMessage Request(HttpMethod method, string path, string? accessToken)
    {
        var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.Add("X-Api-Version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Task<JsonDocument> GetJsonAsync(string path, string? accessToken, CancellationToken token) =>
        SendJsonAsync(HttpMethod.Get, path, null, accessToken, token);

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, HttpContent? content, string? accessToken, CancellationToken token)
    {
        using var request = Request(method, path, accessToken);
        request.Content = content;
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException error) { throw new VRoidHubException("VRoid Hub can't be reached: " + error.Message, inner: error); }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, token);
            try { return JsonDocument.Parse(await ReadBoundedAsync(response, MaximumJsonBytes, token), new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException error) { throw new VRoidHubException("VRoid Hub's answer couldn't be read.", inner: error); }
        }
    }

    private static async Task<VRoidHubException> FailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response, 64 * 1024, token));
            var root = document.RootElement;
            message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? Text(error, "message")
                : Text(root, "error_description") ?? Text(root, "message");
        }
        catch (Exception error) when (error is JsonException or ContractException or HttpRequestException) { }
        var status = response.StatusCode;
        return new VRoidHubException(status switch
        {
            HttpStatusCode.Unauthorized => "VRoid Hub signed Martlet out; link your VRoid Hub account again.",
            HttpStatusCode.Forbidden => "VRoid Hub doesn't let Martlet use this model" + (message is null ? "." : $": {Clip(message, 200)}"),
            HttpStatusCode.NotFound => "VRoid Hub doesn't have this model any more.",
            HttpStatusCode.TooManyRequests => "VRoid Hub asked Martlet to slow down; try again in a minute.",
            _ => $"VRoid Hub answered {(int)status}" + (message is null ? "." : $": {Clip(message, 200)}")
        }, status);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximum)
            throw new ContractException(ErrorCode.PayloadTooLarge, $"The answer is larger than Martlet's {Martlet.Core.Characters.CharacterModelLibrary.SizeText(maximum)} limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maximum)
                throw new ContractException(ErrorCode.PayloadTooLarge, $"The answer is larger than Martlet's {Martlet.Core.Characters.CharacterModelLibrary.SizeText(maximum)} limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------- the loopback return ----------

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = connection.GetStream();
            var target = await ReadTargetAsync(stream, cancellationToken);
            var query = target is null ? null : Query(target);
            string? code = null;
            string message;
            if (query is null || !target!.StartsWith("/vroid-hub/callback", StringComparison.Ordinal))
                message = "This address belongs to Martlet's VRoid Hub sign-in.";
            else if (query.GetValueOrDefault("state") != state)
                message = "This sign-in doesn't match the one Martlet started; start it again from Martlet.";
            else if (query.GetValueOrDefault("code") is { Length: > 0 and <= 512 } found)
            {
                code = found;
                message = "VRoid Hub is linked; you can close this tab and go back to Martlet.";
            }
            else message = "VRoid Hub didn't link Martlet; go back to Martlet and try again.";
            var html = "<!doctype html><meta charset=\"utf-8\"><title>Martlet</title>" +
                "<body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><h2>Martlet</h2><p>" + WebUtility.HtmlEncode(message) + "</p>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(head, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
            }
            catch (IOException) { }
            if (code is not null) return code;
        }
    }

    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumRequestBytes];
        var length = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token);
                if (read == 0) break;
                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n"u8) >= 0) break;
            }
        }
        catch (Exception error) when (error is IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, length);
        var end = line.IndexOf("\r\n", StringComparison.Ordinal);
        if (end < 0) return null;
        var parts = line[..end].Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static Dictionary<string, string>? Query(string target)
    {
        var mark = target.IndexOf('?');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mark < 0) return values;
        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            try { values[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')); }
            catch (UriFormatException) { return null; }
        }
        return values;
    }
}

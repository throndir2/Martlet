using System.Net;
using System.Text.Json;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>One character model found online that Martlet could use as a look: where it is from, who made it, a picture, its
/// license and conditions of use in words, and how Martlet uses it (<see cref="Look"/>).</summary>
public sealed record CharacterModelCandidate
{
    public const string VRoidHubSource = "vroidhub", OpenSourceAvatarsSource = "osa";

    /// <summary><see cref="VRoidHubSource"/> or <see cref="OpenSourceAvatarsSource"/>.</summary>
    public required string Source { get; init; }
    /// <summary>The model's ID at its source (a VRoid Hub model ID; an Open Source Avatars project and avatar ID).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The character the model is of, when the source says (VRoid Hub).</summary>
    public string? Character { get; init; }
    public required string Author { get; init; }
    public string? AuthorUrl { get; init; }
    public string? Description { get; init; }
    public string? ThumbnailUrl { get; init; }
    public required string PageUrl { get; init; }
    /// <summary>The license's short name: CC0, CC-BY, or VRoid Hub's conditions of use.</summary>
    public required string License { get; init; }
    /// <summary>The conditions of use, one per line, as the source sets them.</summary>
    public required IReadOnlyList<string> Conditions { get; init; }
    /// <summary>Martlet must show the creator's name with the character.</summary>
    public bool CreditRequired { get; init; }
    /// <summary>"0.0" or "1.0" when known.</summary>
    public string? VrmVersion { get; init; }
    public long? Bytes { get; init; }
    /// <summary>A model of the linked VRoid Hub account itself.</summary>
    public bool Own { get; init; }
    /// <summary>Its conditions let Martlet show it as a character for anyone (or it is the owner's own).</summary>
    public bool Usable { get; init; } = true;
    /// <summary>How a character profile names this look: <c>vroidhub:&lt;ID&gt;</c> (shown from memory, needs a linked VRoid
    /// Hub account) or <c>osa:&lt;project&gt;/&lt;avatar&gt;</c> (downloaded once and kept with your characters).</summary>
    public required string Look { get; init; }
    /// <summary>The address of the model file (Open Source Avatars only; VRoid Hub gives one per download).</summary>
    public string? ModelUrl { get; init; }

    /// <summary>Martlet keeps a copy (it joins your characters) rather than showing it from memory.</summary>
    public bool Kept => Source == OpenSourceAvatarsSource;

    /// <summary>The credit line to show with the character: who made it and where it is from.</summary>
    public string Credit => $"{Name} by {Author} ({(Source == VRoidHubSource ? "VRoid Hub" : "Open Source Avatars")}, {License})";
}

/// <summary>The Open Source Avatars registry (https://github.com/ToxSam/open-source-avatars): thousands of free VRM avatars,
/// mostly CC0 (public domain) and some CC-BY, listed in JSON files on GitHub. Martlet reads the list once per run and searches
/// it by name, description and traits. Its models may be kept and shared, so a chosen one joins your characters.</summary>
public sealed class OpenSourceAvatars(HttpClient http)
{
    public const string DataRoot = "https://raw.githubusercontent.com/ToxSam/open-source-avatars/main/data/";
    public const string LookPrefix = "osa:";
    private const int MaximumListBytes = 32 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<CharacterModelCandidate>? cached;
    private static DateTimeOffset cachedAt;

    /// <summary>Avatars whose name, description or traits contain every word of <paramref name="query"/>, best first.</summary>
    public async Task<IReadOnlyList<CharacterModelCandidate>> SearchAsync(string query, int count, CancellationToken token)
    {
        var words = Words(query);
        if (words.Length == 0) return [];
        var all = await AllAsync(token);
        return all.Select(c => (Candidate: c, Score: Score(c, words))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(count, 1, 100)).Select(x => x.Candidate).ToArray();
    }

    /// <summary>One avatar by its look (<c>osa:&lt;project&gt;/&lt;avatar&gt;</c>), or null.</summary>
    public async Task<CharacterModelCandidate?> FindAsync(string look, CancellationToken token) =>
        (await AllAsync(token)).FirstOrDefault(c => string.Equals(c.Look, look, StringComparison.Ordinal));

    /// <summary>Downloads an avatar's VRM file into memory (at most the VRM limit).</summary>
    public async Task<byte[]> DownloadAsync(CharacterModelCandidate candidate, CancellationToken token)
    {
        ContractRules.Require(candidate.Source == CharacterModelCandidate.OpenSourceAvatarsSource && candidate.ModelUrl is not null,
            "This isn't an Open Source Avatars model.");
        var uri = new Uri(candidate.ModelUrl!);
        for (var hop = 0; hop < 5; hop++)
        {
            ContractRules.Require(uri.Scheme == Uri.UriSchemeHttps, "The model's address isn't secure (https).");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect && response.Headers.Location is { } next)
            {
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The model's host answered {(int)response.StatusCode}.", null, response.StatusCode);
            var bytes = await ReadAsync(response, LocalAvatarFiles.MaximumVrmBytes, token);
            ContractRules.Require(bytes.Length >= 20 && bytes[0] == (byte)'g' && bytes[1] == (byte)'l' && bytes[2] == (byte)'T' && bytes[3] == (byte)'F',
                "The downloaded file isn't a VRM model.");
            return bytes;
        }
        throw new HttpRequestException("The model's address redirected too many times.");
    }

    private async Task<IReadOnlyList<CharacterModelCandidate>> AllAsync(CancellationToken token)
    {
        if (cached is { } ready && DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromHours(6)) return ready;
        await Gate.WaitAsync(token);
        try
        {
            if (cached is { } again && DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromHours(6)) return again;
            var found = new List<CharacterModelCandidate>();
            using var projects = JsonDocument.Parse(await GetAsync("projects.json", token));
            if (projects.RootElement.ValueKind != JsonValueKind.Array) return [];
            var lists = projects.RootElement.EnumerateArray().Take(64).Where(p => p.ValueKind == JsonValueKind.Object).Select(p => (
                Id: Text(p, "id"), Name: Text(p, "name"), License: Text(p, "license"), Creator: Text(p, "creator_id"),
                File: Text(p, "avatar_data_file"), Public: !p.TryGetProperty("is_public", out var visible) || visible.ValueKind != JsonValueKind.False))
                .Where(p => p.Public && p.Id is not null && p.File is not null && IsFreeLicense(p.License) && SafeRelative(p.File!)).ToArray();
            var files = await Task.WhenAll(lists.Select(async p =>
            {
                try { return (Project: p, Bytes: (byte[]?)await GetAsync(p.File!, token)); }
                catch (Exception error) when (error is HttpRequestException or ContractException or TaskCanceledException && !token.IsCancellationRequested)
                {
                    return (p, null);
                }
            }));
            foreach (var (project, bytes) in files)
            {
                if (bytes is null) continue;
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var avatar in document.RootElement.EnumerateArray().Take(10_000))
                    if (Candidate(avatar, project.Id!, project.Name ?? project.Id!, project.License!, project.Creator) is { } candidate)
                        found.Add(candidate);
            }
            cached = found;
            cachedAt = DateTimeOffset.UtcNow;
            return found;
        }
        finally { Gate.Release(); }
    }

    internal static CharacterModelCandidate? Candidate(JsonElement avatar, string projectId, string projectName, string license, string? creator)
    {
        if (avatar.ValueKind != JsonValueKind.Object || Text(avatar, "id") is not { Length: <= 100 } id ||
            avatar.TryGetProperty("is_public", out var visible) && visible.ValueKind == JsonValueKind.False ||
            avatar.TryGetProperty("is_draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return null;
        if (Text(avatar, "format") is { } format && !format.Equals("VRM", StringComparison.OrdinalIgnoreCase)) return null;
        if (Text(avatar, "model_file_url") is not { } model || !Uri.TryCreate(model, UriKind.Absolute, out var modelUri) || modelUri.Scheme != Uri.UriSchemeHttps)
            return null;
        var look = LookPrefix + projectId + "/" + id;
        if (look.Length > 128 || look.Any(char.IsWhiteSpace) || look.Any(char.IsControl)) return null;
        var name = VRoidHub.Clip(Text(avatar, "name"), 80) ?? "Avatar";
        var traits = new List<string>();
        if (avatar.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (Text(metadata, "series") is { } series) traits.Add("Series " + series);
            if (metadata.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
                foreach (var attribute in attributes.EnumerateArray().Take(16))
                    if (Text(attribute, "trait_type") is { } kind && Text(attribute, "value") is { } value && attribute.GetProperty("value").ValueKind == JsonValueKind.String)
                        traits.Add($"{kind}: {value}");
        }
        var description = VRoidHub.Clip(string.Join(". ", new[] { Text(avatar, "description") }.Concat(traits).OfType<string>()), 400);
        var thumbnail = Text(avatar, "thumbnail_url") is { } picture && Uri.TryCreate(picture, UriKind.Absolute, out var pictureUri) &&
            pictureUri.Scheme == Uri.UriSchemeHttps ? picture : null;
        var cc0 = license.Equals("CC0", StringComparison.OrdinalIgnoreCase);
        var author = creator ?? projectName;
        return new CharacterModelCandidate
        {
            Source = CharacterModelCandidate.OpenSourceAvatarsSource, Id = projectId + "/" + id, Name = name, Author = author,
            AuthorUrl = "https://www.opensourceavatars.com/", Description = description, ThumbnailUrl = thumbnail,
            PageUrl = "https://www.opensourceavatars.com/en/gallery", License = license.ToUpperInvariant(),
            Conditions = cc0
                ? ["Public domain (CC0): anyone may use, change and share it, also commercially.", "Credit: not required.", $"Collection: {projectName}."]
                : ["Creative Commons Attribution (CC-BY): anyone may use, change and share it, also commercially.",
                    $"Credit: required ({author}).", $"Collection: {projectName}."],
            CreditRequired = !cc0, Usable = true, Look = look, ModelUrl = model
        };
    }

    internal static bool IsFreeLicense(string? license) => license is not null &&
        (license.Equals("CC0", StringComparison.OrdinalIgnoreCase) || license.Equals("CC-BY", StringComparison.OrdinalIgnoreCase) ||
         license.Equals("CC BY", StringComparison.OrdinalIgnoreCase) || license.StartsWith("CC-BY-4", StringComparison.OrdinalIgnoreCase));

    private static bool SafeRelative(string path) => path.Length <= 200 && !path.Contains("..", StringComparison.Ordinal) &&
        !path.StartsWith('/') && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');

    internal static string[] Words(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(w => w.Trim('"', '\'', ',', '.', '!', '?')).Where(w => w.Length > 0).Take(8).ToArray();

    internal static int Score(CharacterModelCandidate candidate, string[] words)
    {
        var score = 0;
        foreach (var word in words)
        {
            if (candidate.Name.Equals(word, StringComparison.OrdinalIgnoreCase)) score += 6;
            else if (candidate.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 4;
            else if (candidate.Description?.Contains(word, StringComparison.OrdinalIgnoreCase) == true) score += 1;
            else return 0;
        }
        return score;
    }

    private async Task<byte[]> GetAsync(string relative, CancellationToken token)
    {
        using var response = await http.GetAsync(DataRoot + relative, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, MaximumListBytes, token);
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximum)
            throw new ContractException(ErrorCode.PayloadTooLarge, $"The file is larger than Martlet's {CharacterModelLibrary.SizeText(maximum)} limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maximum)
                throw new ContractException(ErrorCode.PayloadTooLarge, $"The file is larger than Martlet's {CharacterModelLibrary.SizeText(maximum)} limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : null;
}

/// <summary>Finds character models for a character the owner names: the linked VRoid Hub account's own and hearted models that
/// match first, then VRoid Hub's search, then the Open Source Avatars registry. A source that can't be reached is skipped and
/// named in <see cref="CharacterModelSearchResult.Notes"/>.</summary>
public sealed class CharacterModelSearch(HttpClient http)
{
    public VRoidHub VRoidHub { get; } = new(http);
    public OpenSourceAvatars OpenSourceAvatars { get; } = new(http);

    public static HttpClient CreateHttpClient() => Hosting.VRoidHub.CreateHttpClient();

    public async Task<CharacterModelSearchResult> SearchAsync(string query, int count, VRoidHubTokens? linked, IReadOnlyCollection<string>? sources,
        CancellationToken token)
    {
        ContractRules.Require(!string.IsNullOrWhiteSpace(query) && query.Trim().Length <= 100, "Name the character in 1 to 100 characters.");
        query = query.Trim();
        count = Math.Clamp(count, 1, 50);
        bool Wanted(string source) => sources is null || sources.Count == 0 || sources.Contains(source, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var found = new List<CharacterModelCandidate>();
        async Task<IReadOnlyList<CharacterModelCandidate>> Try(string source, Func<Task<IReadOnlyList<CharacterModelCandidate>>> search)
        {
            try { return await search(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or VRoidHubException or ContractException or JsonException or OperationCanceledException)
            {
                notes.Add($"{source} couldn't be searched: {error.Message}");
                return [];
            }
        }
        var vroid = Wanted(CharacterModelCandidate.VRoidHubSource);
        var own = vroid && linked is not null
            ? Try("Your VRoid Hub characters", () => VRoidHub.AccountModelsAsync(linked, token)) : Task.FromResult<IReadOnlyList<CharacterModelCandidate>>([]);
        var hub = vroid ? Try("VRoid Hub", () => VRoidHub.SearchAsync(query, count, linked?.AccessToken, token))
            : Task.FromResult<IReadOnlyList<CharacterModelCandidate>>([]);
        var osa = Wanted(CharacterModelCandidate.OpenSourceAvatarsSource) ? Try("Open Source Avatars", () => OpenSourceAvatars.SearchAsync(query, count, token))
            : Task.FromResult<IReadOnlyList<CharacterModelCandidate>>([]);
        await Task.WhenAll(own, hub, osa);
        var words = OpenSourceAvatars.Words(query);
        found.AddRange((await own).Where(c => words.All(w => (c.Name + " " + c.Character).Contains(w, StringComparison.OrdinalIgnoreCase))));
        found.AddRange(await hub);
        found.AddRange(await osa);
        if (vroid && linked is null && !VRoidHubApp.Configured) notes.Add("This Martlet build can't link VRoid Hub, so VRoid Hub models can be found but not shown.");
        else if (vroid && linked is null) notes.Add("Link your VRoid Hub account (Account › Linked services) to show VRoid Hub models.");
        return new(found.DistinctBy(c => c.Look).Take(count * 2).ToArray(), notes);
    }
}

public sealed record CharacterModelSearchResult(IReadOnlyList<CharacterModelCandidate> Candidates, IReadOnlyList<string> Notes);

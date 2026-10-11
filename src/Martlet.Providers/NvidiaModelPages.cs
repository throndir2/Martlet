using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Providers;

/// <summary>What NVIDIA Build's page for one model says (docs/MODEL_CATALOG.md, NVIDIA Build): <see cref="ModelId"/> from its
/// <c>canonical</c> link, <see cref="Endpoint"/> when its header says <c>type: "endpoint"</c> (a hosted chat model), the inputs
/// of its Specifications (<see cref="Hears"/> audio, <see cref="Sees"/> image, <see cref="Video"/> video; null when the page has
/// no input line), <see cref="Tools"/> from its Capabilities' Function Calling, and <see cref="ContextTokens"/> its context
/// length.</summary>
public sealed record NvidiaModelPage(string ModelId, bool Endpoint, bool? Hears, bool? Sees, bool? Video, bool? Tools, int? ContextTokens);

/// <summary>Reads NVIDIA Build's model pages, which are made for machines to read (https://build.nvidia.com/llms.txt) and need no
/// key: <c>models.md</c> links one page for each model, and each page's header and Specifications say what the model takes.
/// NVIDIA's <c>/v1/models</c> gives only IDs, so <see cref="ModelContextProbe"/> reads the page when a model on NVIDIA Build is
/// chosen or checked. Only links on the same site are followed; no key and nothing anyone said is sent.</summary>
public static partial class NvidiaModelPages
{
    public static Uri Site { get; } = new("https://build.nvidia.com/");
    private const int MaximumBytes = 2_097_152;
    private const int MaximumPages = 3;

    /// <summary>The model pages <c>models.md</c> links, each once, in order: paths on the same site ending in <c>.md</c>.</summary>
    public static IReadOnlyList<(string Title, string Path)> Links(string modelsMarkdown)
    {
        ArgumentNullException.ThrowIfNull(modelsMarkdown);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<(string, string)>();
        foreach (Match match in Link().Matches(modelsMarkdown))
            if (seen.Add(match.Groups["path"].Value)) links.Add((match.Groups["title"].Value.Trim(), match.Groups["path"].Value));
        return links;
    }

    /// <summary>The linked pages that may be <paramref name="modelId"/>'s: the link's title or file name is the model's name
    /// (the part after the publisher), ignoring case, dots and underscores (NVIDIA writes <c>llama-3_1-...</c> for
    /// <c>llama-3.1-...</c>).</summary>
    public static IReadOnlyList<string> Candidates(string modelsMarkdown, string modelId)
    {
        var name = Same(modelId[(modelId.LastIndexOf('/') + 1)..]);
        return [.. Links(modelsMarkdown).Where(l => Same(l.Title) == name || Same(Path.GetFileNameWithoutExtension(l.Path)) == name)
            .Select(l => l.Path).Take(MaximumPages)];
    }

    /// <summary>A model page's facts; null when it has no <c>canonical</c> model link.</summary>
    public static NvidiaModelPage? Parse(string page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Header(page, "canonical") is not { } canonical || !Uri.TryCreate(canonical, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath.Trim('/') is not { Length: > 0 } id)
            return null;
        bool? hears = null, sees = null, video = null;
        if ((Field(page, SpecificationInput()) ?? Field(page, InputTypes())) is { } inputs)
        {
            var words = inputs.ToLowerInvariant();
            (hears, sees, video) = (words.Contains("audio", StringComparison.Ordinal), words.Contains("image", StringComparison.Ordinal),
                words.Contains("video", StringComparison.Ordinal));
        }
        bool? tools = Field(page, FunctionCalling())?.ToLowerInvariant() switch
        {
            { } said when said.StartsWith("supported", StringComparison.Ordinal) => true,
            { } said when said.StartsWith("not supported", StringComparison.Ordinal) => false,
            _ => null
        };
        int? context = Field(page, ContextLength()) is { } length &&
            int.TryParse(length.Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) &&
            tokens is >= 256 and <= 100_000_000 ? tokens : null;
        return new(id, Header(page, "type") == "endpoint", hears, sees, video, tools, context);
    }

    /// <summary>Reads <paramref name="modelId"/>'s page on <paramref name="site"/> (<see cref="Site"/>; MCP gives a fixture):
    /// <c>models.md</c>, then at most three pages whose name matches, and the one whose canonical model ID is
    /// <paramref name="modelId"/> and that is a hosted chat model. Null when none is, or the site doesn't answer.</summary>
    public static async Task<NvidiaModelPage?> ReadAsync(HttpClient client, Uri site, string modelId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(site);
        if (await GetAsync(client, new Uri(site, "models.md"), token).ConfigureAwait(false) is not { } list) return null;
        foreach (var path in Candidates(list, modelId))
            if (await GetAsync(client, new Uri(site, path.TrimStart('/')), token).ConfigureAwait(false) is { } text &&
                Parse(text) is { Endpoint: true } page && Same(page.ModelId) == Same(modelId))
                return page;
        return null;
    }

    private static async Task<string?> GetAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.9));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var chunk = new byte[16_384];
            int read;
            while ((read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaximumBytes) return null;
                body.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or IOException) { return null; }
    }

    // Lower case, with dots and underscores as hyphens: "llama-3_1-nemoguard" and "llama-3.1-nemoguard" are the same name.
    private static string Same(string text) => text.Trim().ToLowerInvariant().Replace('_', '-').Replace('.', '-');

    // A line of the page's front matter: `canonical: "https://build.nvidia.com/google/gemma-4-31b-it"`.
    private static string? Header(string page, string name)
    {
        if (!page.StartsWith("---", StringComparison.Ordinal)) return null;
        var end = page.IndexOf("\n---", 3, StringComparison.Ordinal);
        foreach (var line in page[..(end < 0 ? Math.Min(page.Length, 4096) : end)].Split('\n'))
            if (line.StartsWith(name + ":", StringComparison.Ordinal))
                return line[(name.Length + 1)..].Trim().Trim('"');
        return null;
    }

    private static string? Field(string page, Regex pattern) =>
        pattern.Match(page) is { Success: true } match ? match.Groups["value"].Value.Trim() : null;

    [GeneratedRegex(@"\[(?<title>[^\]\r\n]+)\]\((?<path>/[A-Za-z0-9._~/-]+\.md)\)")]
    private static partial Regex Link();

    // The Specifications list: "- **Input:** Text, Image, Video". ("Data Modality" is the training data, not the inputs.)
    [GeneratedRegex(@"(?m)^- \*\*Input:\*\*[ \t]*(?<value>[^\r\n]+)")]
    private static partial Regex SpecificationInput();

    [GeneratedRegex(@"(?m)^\*\*Input Type(?:s|\(s\))?:\*\*[ \t]*(?<value>[^\r\n]+)")]
    private static partial Regex InputTypes();

    [GeneratedRegex(@"(?m)^- \*\*Function Calling:\*\*[ \t]*(?<value>[^\r\n]+)")]
    private static partial Regex FunctionCalling();

    [GeneratedRegex(@"(?m)^- \*\*Context Length:\*\*[ \t]*(?<value>[\d,]+)")]
    private static partial Regex ContextLength();
}

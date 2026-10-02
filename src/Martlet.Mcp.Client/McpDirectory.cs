using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Martlet.Mcp.Client;

/// <summary>A public MCP server directory that speaks the MCP Registry API (v0.1): GET /v0.1/servers with search, limit and
/// cursor. Entries use the registry's server.json format (packages to run on this PC, remotes to connect to).</summary>
public sealed record McpDirectorySource(string Id, string Name, Uri BaseAddress, string About)
{
    public static McpDirectorySource GitHub { get; } = new("github", "GitHub MCP Registry", new("https://api.mcp.github.com/"),
        "Popular servers GitHub lists, most-starred first.");
    public static McpDirectorySource Official { get; } = new("official", "Official MCP Registry",
        new("https://registry.modelcontextprotocol.io/"),
        "Every server published to the Model Context Protocol's own registry, A to Z; search matches server names.");
    public static IReadOnlyList<McpDirectorySource> All { get; } = [GitHub, Official];
    public override string ToString() => Name;
}

/// <summary>The directory couldn't be reached or answered with something Martlet can't read; the message says which.</summary>
public sealed class McpDirectoryException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record McpDirectoryPage(IReadOnlyList<McpDirectoryEntry> Servers, string? NextCursor);

/// <summary>Searches an MCP directory. Each request is one GET of public, read-only data; what you search for is sent to it.</summary>
public sealed class McpDirectoryClient(HttpClient http)
{
    public const int PageSize = 20;
    public const int MaxSearchLength = 100;
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(60);

    public async Task<McpDirectoryPage> SearchAsync(McpDirectorySource source, string? search, string? cursor, CancellationToken token)
    {
        var query = new StringBuilder($"v0.1/servers?limit={PageSize}&version=latest");
        var text = search?.Trim() ?? "";
        if (text.Length > MaxSearchLength) text = text[..MaxSearchLength];
        if (text.Length > 0) query.Append("&search=").Append(Uri.EscapeDataString(text));
        if (!string.IsNullOrEmpty(cursor)) query.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(source.BaseAddress, query.ToString()));
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new McpDirectoryException($"{source.Name} answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new McpDirectoryException($"{source.Name} sent more than Martlet reads at once.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) throw new McpDirectoryException($"{source.Name} sent more than Martlet reads at once.");
                buffer.Write(chunk, 0, read);
            }
            return ParsePage(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), source);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new McpDirectoryException($"{source.Name} didn't answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException error)
        {
            throw new McpDirectoryException($"Couldn't reach {source.Name}: {error.Message}", error);
        }
    }

    /// <summary>Reads one page of a v0.1 server list. Entries that aren't active or can't be read are left out.</summary>
    public static McpDirectoryPage ParsePage(ReadOnlySpan<byte> json, McpDirectorySource? source = null)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new McpDirectoryException($"{source?.Name ?? "The directory"} sent something that isn't JSON."); }
        if (root is not JsonObject page || page["servers"] is not JsonArray items)
            throw new McpDirectoryException($"{source?.Name ?? "The directory"} sent a list Martlet doesn't understand.");
        var servers = new List<McpDirectoryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OfType<JsonObject>())
        {
            var server = item["server"] as JsonObject ?? item;
            var official = (item["_meta"] ?? server["_meta"])?["io.modelcontextprotocol.registry/official"];
            var status = DirectoryJson.Text(official?["status"]) ?? "active";
            if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)) continue;
            McpDirectoryEntry? entry;
            try { entry = McpDirectoryEntry.Parse(server); }
            catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException or JsonException)
            {
                entry = null;
            }
            if (entry is not null && seen.Add(entry.Name)) servers.Add(entry);
        }
        var next = DirectoryJson.Text(page["metadata"]?["nextCursor"]);
        return new(servers, servers.Count == 0 || string.IsNullOrEmpty(next) ? null : next);
    }
}

/// <summary>One server in an MCP directory, with the ways Martlet can install it and why any others aren't offered.</summary>
public sealed record McpDirectoryEntry
{
    public const int MaxDescriptionLength = 1000;
    public required string Name { get; init; }
    public string? Title { get; init; }
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public Uri? Repository { get; init; }
    public Uri? Website { get; init; }
    public long? Stars { get; init; }
    public IReadOnlyList<McpInstallOption> Options { get; init; } = [];
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    public string DisplayName => Title is { Length: > 0 } title ? title : Name[(Name.LastIndexOf('/') + 1)..];

    /// <summary>A short mcp.json name: the part after the publisher without "mcp"/"server" decorations (or the publisher's own
    /// name when nothing else is left), limited to letters, digits, '.', '_' and '-'.</summary>
    public string SuggestedName
    {
        get
        {
            static string Clean(string text)
            {
                var clean = Regex.Replace(text, "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');
                return clean.Length > McpServerDefinition.MaxNameLength ? clean[..McpServerDefinition.MaxNameLength].TrimEnd('-', '.', '_') : clean;
            }
            var slash = Name.LastIndexOf('/');
            var tail = Clean(Name[(slash + 1)..]);
            var trimmed = Regex.Replace(tail, @"^(mcp[-_.]server[-_.]|mcp[-_.])|([-_.]mcp[-_.]server|[-_.]mcp|[-_.]server)$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (trimmed.Length > 0 && !Regex.IsMatch(trimmed, "^(mcp|server|mcp-server)$", RegexOptions.IgnoreCase)) return trimmed;
            var publisher = slash > 0 ? Clean(Name[..slash].Split('.').Last()) : "";
            return publisher.Length > 0 ? publisher : tail.Length > 0 ? tail : "server";
        }
    }

    public static McpDirectoryEntry Parse(JsonObject server)
    {
        var name = DirectoryJson.Text(server["name"]);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new FormatException("The entry has no usable name.");
        var github = server["_meta"]?["io.modelcontextprotocol.registry/publisher-provided"]?["github"] as JsonObject;
        var version = DirectoryJson.Text(server["version"]) ?? "";
        var options = new List<McpInstallOption>();
        var unsupported = new List<string>();
        foreach (var package in (server["packages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            try
            {
                if (McpInstallOption.FromPackage(package, name, version, out var reason) is { } option) options.Add(option);
                else if (reason is not null) unsupported.Add(reason);
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
            {
                unsupported.Add($"One of its packages couldn't be read ({error.Message}).");
            }
        }
        foreach (var remote in (server["remotes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            try
            {
                if (McpInstallOption.FromRemote(remote, name, version, out var reason) is { } option) options.Add(option);
                else if (reason is not null) unsupported.Add(reason);
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
            {
                unsupported.Add($"One of its hosted addresses couldn't be read ({error.Message}).");
            }
        }
        // An SSE-listed address is only a fallback when the publisher lists a streamable HTTP one too.
        if (options.Any(o => o.Kind == McpInstallKind.Remote && !o.ListedAsSse))
            options.RemoveAll(o => o.ListedAsSse);
        var description = (DirectoryJson.Text(server["description"]) ?? "").Trim();
        if (description.Length > MaxDescriptionLength) description = description[..MaxDescriptionLength] + "…";
        var title = DirectoryJson.Text(server["title"]) ?? DirectoryJson.Text(github?["displayName"]);
        return new()
        {
            Name = name.Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Description = description,
            Version = version,
            Repository = DirectoryJson.Web(server["repository"]?["url"]),
            Website = DirectoryJson.Web(server["websiteUrl"]) ?? DirectoryJson.Web(github?["homepageUrl"]),
            Stars = DirectoryJson.Number(github?["stargazerCount"]),
            Options = options.OrderBy(o => o.Kind).ToArray(),
            Unsupported = unsupported.Distinct().ToArray()
        };
    }
}

public enum McpInstallKind { Npm, PyPI, Docker, NuGet, Remote }

/// <summary>Something the user fills in before installing: an environment variable, header, argument or a placeholder in one.
/// Secret values never go into mcp.json: Martlet keeps them separately and mcp.json refers to them as ${secret:NAME}.</summary>
public sealed record McpInstallInput
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public bool Secret { get; init; }
    /// <summary>An on/off switch (a flag argument) rather than text; its value is "true" or "false".</summary>
    public bool Flag { get; init; }
    public bool IsPath { get; init; }
    public string? Default { get; init; }
    public string? Placeholder { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    /// <summary>The part of the ${secret:NAME} name after the server name.</summary>
    internal string SecretLabel { get; init; } = "";
    /// <summary>What a text box starts with: the default for required values (optional ones are left out unless typed).</summary>
    public string? Initial => Flag ? (Default == "true" ? "true" : "false") : Required ? Default : null;
}

/// <summary>What installing writes: the mcp.json entry, the secrets to keep outside it and a one-line preview.</summary>
public sealed record McpInstallPlan(JsonObject Entry, IReadOnlyDictionary<string, string> Secrets, string Preview);

/// <summary>One way to install a directory entry: a package Martlet runs with npx, uvx, docker or dnx, or a hosted streamable
/// HTTP address.</summary>
public sealed partial class McpInstallOption
{
    public const int MaxValueLength = 4096;
    /// <summary>The longest secret value (in UTF-8 bytes) Martlet keeps.</summary>
    public const int MaxSecretBytes = 768;
    private const int MaxInputs = 160;
    private readonly List<McpInstallInput> inputs = [];
    private readonly List<Argument> runtimeArguments = [];
    private readonly List<Argument> packageArguments = [];
    private readonly List<(string Name, Template Value)> environment = [];
    private readonly List<(string Name, Template Value)> headers = [];
    private Template? url;
    private string registryName = "", registryVersion = "";
    private string? identifier, packageVersion;

    private McpInstallOption(McpInstallKind kind) => Kind = kind;

    public McpInstallKind Kind { get; }
    /// <summary>The program it runs with (npx, uvx, docker or dnx), or null for a hosted address.</summary>
    public string? Runtime => Kind switch
    {
        McpInstallKind.Npm => "npx", McpInstallKind.PyPI => "uvx", McpInstallKind.Docker => "docker", McpInstallKind.NuGet => "dnx", _ => null
    };
    public string Summary { get; private set; } = "";
    public string? Host { get; private set; }
    public IReadOnlyList<McpInstallInput> Inputs => inputs;

    /// <summary>A short name for choosing between ways to install.</summary>
    public string ShortName => Kind switch
    {
        McpInstallKind.Npm => "npm package (Node.js)", McpInstallKind.PyPI => "Python package (uv)",
        McpInstallKind.Docker => "Docker image", McpInstallKind.NuGet => ".NET tool (dnx)", _ => "Hosted by the publisher"
    };

    /// <summary>What provides the runtime program, and where to get it.</summary>
    public string? RuntimeHelp => Kind switch
    {
        McpInstallKind.Npm => "Node.js (nodejs.org)",
        McpInstallKind.PyPI => "uv (docs.astral.sh/uv)",
        McpInstallKind.Docker => "Docker Desktop (it has to be running)",
        McpInstallKind.NuGet => "the .NET 10 SDK",
        _ => null
    };

    /// <summary>Whether the runtime program is on this PC's PATH (always true for a hosted address).</summary>
    public bool RuntimeAvailable() =>
        Runtime is not { } command || !OperatingSystem.IsWindows() || McpProcessStart.Resolve(command, Environment.GetEnvironmentVariable("PATH"), null) is not null;

    public override string ToString() => ShortName;

    internal static McpInstallOption? FromPackage(JsonObject package, string name, string version, out string? unsupported)
    {
        unsupported = null;
        var type = (DirectoryJson.Text(package["registryType"]) ?? "").Trim().ToLowerInvariant();
        var id = DirectoryJson.Text(package["identifier"])?.Trim();
        var transport = (DirectoryJson.Text(package["transport"]?["type"]) ?? "stdio").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) throw new FormatException("a package has no identifier");
        _ = Literal(id + (DirectoryJson.Text(package["version"]) ?? ""));
        McpInstallKind? kind = type switch
        {
            "npm" => McpInstallKind.Npm, "pypi" => McpInstallKind.PyPI, "oci" or "docker" => McpInstallKind.Docker,
            "nuget" => McpInstallKind.NuGet, _ => null
        };
        if (type == "mcpb")
        {
            unsupported = "It comes as an MCP bundle (.mcpb) file, which Martlet can't install yet.";
            return null;
        }
        if (kind is null)
        {
            unsupported = $"Its {(type.Length == 0 ? "unnamed" : type)} package isn't a kind Martlet can install.";
            return null;
        }
        if (transport != "stdio")
        {
            unsupported = $"Its {ShortNameOf(kind.Value)} runs as a local web server, which Martlet can't start yet.";
            return null;
        }
        var option = new McpInstallOption(kind.Value)
        {
            registryName = name, registryVersion = version, identifier = id,
            packageVersion = DirectoryJson.Text(package["version"])?.Trim() is { Length: > 0 } v ? v : null
        };
        foreach (var argument in (package["runtimeArguments"] as JsonArray ?? []).OfType<JsonObject>())
            option.runtimeArguments.Add(option.ParseArgument(argument));
        foreach (var argument in (package["packageArguments"] as JsonArray ?? []).OfType<JsonObject>())
            option.packageArguments.Add(option.ParseArgument(argument));
        foreach (var variable in (package["environmentVariables"] as JsonArray ?? []).OfType<JsonObject>())
            option.environment.Add(option.ParseKeyValue(variable, "env"));
        option.Summary = kind switch
        {
            McpInstallKind.Npm => $"npm package {id}{Suffix(option.packageVersion)}, run with npx",
            McpInstallKind.PyPI => $"Python package {id}{Suffix(option.packageVersion)}, run with uvx",
            McpInstallKind.Docker => $"Docker image {option.Image()}",
            _ => $".NET tool {id}{Suffix(option.packageVersion)}, run with dnx"
        };
        return option;
    }

    internal static McpInstallOption? FromRemote(JsonObject remote, string name, string version, out string? unsupported)
    {
        unsupported = null;
        var type = (DirectoryJson.Text(remote["type"]) ?? "").Trim().ToLowerInvariant();
        var address = DirectoryJson.Text(remote["url"])?.Trim();
        // Many servers listed as SSE answer streamable HTTP at the same address; one whose address ends in /sse doesn't.
        if (type == "sse" && (address is null || address.Split('?', 2)[0].TrimEnd('/').EndsWith("/sse", StringComparison.OrdinalIgnoreCase)))
        {
            unsupported = "Its hosted address uses the older SSE transport, which Martlet doesn't support.";
            return null;
        }
        if (type is not ("streamable-http" or "http" or "sse"))
        {
            unsupported = $"Its hosted address uses a {type} transport Martlet doesn't know.";
            return null;
        }
        if (string.IsNullOrEmpty(address)) throw new FormatException("a hosted address has no url");
        var scheme = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        if (scheme == 0 && !address.StartsWith('{')) throw new FormatException("a hosted address isn't an http(s) address");
        var option = new McpInstallOption(McpInstallKind.Remote) { registryName = name, registryVersion = version };
        option.url = option.ParseTemplate(address, remote["variables"] as JsonObject, parentSecret: false, urlPart: true);
        foreach (var header in (remote["headers"] as JsonArray ?? []).OfType<JsonObject>())
            option.headers.Add(option.ParseKeyValue(header, "header"));
        option.Host = scheme == 0 || address[scheme..].Split('/', 2)[0] is var authority && authority.Contains('{')
            ? "an address you fill in" : address[scheme..].Split('/', 2)[0];
        option.ListedAsSse = type == "sse";
        option.Summary = option.ListedAsSse
            ? $"Hosted at {option.Host} (listed as SSE; Martlet connects with streamable HTTP, which most such servers also accept)"
            : $"Hosted at {option.Host} (streamable HTTP)";
        return option;
    }

    /// <summary>The directory lists this address as SSE; Martlet tries streamable HTTP there.</summary>
    internal bool ListedAsSse { get; private set; }

    private static string ShortNameOf(McpInstallKind kind) => kind switch
    {
        McpInstallKind.Npm => "npm package", McpInstallKind.PyPI => "Python package", McpInstallKind.Docker => "Docker image",
        McpInstallKind.NuGet => ".NET tool", _ => "hosted address"
    };

    private static string Suffix(string? version) => version is null ? "" : $" {version}";

    private string Image()
    {
        var image = identifier!;
        var lastSegment = image[(image.LastIndexOf('/') + 1)..];
        return lastSegment.Contains(':') || image.Contains('@') || packageVersion is null ? image : $"{image}:{packageVersion}";
    }

    // ---- Parsing the registry's inputs ----

    private sealed record Template(IReadOnlyList<Part> Parts);
    private readonly record struct Part(string? Text, string? Input);
    /// <summary>One argument: its tokens, an optional on/off switch deciding whether it is passed at all.</summary>
    private sealed record Argument(IReadOnlyList<Template> Tokens, string? Flag = null);

    private McpInstallInput AddInput(McpInstallInput input)
    {
        if (inputs.FirstOrDefault(i => i.Key == input.Key) is { } existing) return existing;
        if (inputs.Count >= MaxInputs) throw new FormatException("it asks for too many settings");
        inputs.Add(input);
        return input;
    }

    private static string? Describe(JsonObject node) => DirectoryJson.Text(node["description"])?.Trim() is { Length: > 0 } text
        ? text.Length > 600 ? text[..600] + "…" : text : null;

    private McpInstallInput InputFrom(JsonObject node, string key, string label, string secretLabel, bool parentSecret, bool required) => new()
    {
        Key = key, Label = label, Description = Describe(node), SecretLabel = secretLabel,
        Required = required, Secret = parentSecret || DirectoryJson.Bool(node["isSecret"]) == true,
        Flag = DirectoryJson.Text(node["format"]) == "boolean" && node["choices"] is null,
        IsPath = DirectoryJson.Text(node["format"]) == "filepath",
        Default = DirectoryJson.Text(node["default"]), Placeholder = DirectoryJson.Text(node["placeholder"]),
        Choices = (node["choices"] as JsonArray ?? []).Select(DirectoryJson.Text).OfType<string>().ToArray()
    };

    /// <summary>A value with {name} placeholders for the variables it declares; each variable becomes an input.</summary>
    private Template ParseTemplate(string value, JsonObject? variables, bool parentSecret, bool urlPart = false)
    {
        var parts = new List<Part>();
        var at = 0;
        foreach (Match match in Regex.Matches(value, @"\{([^{}\s]{1,64})\}"))
        {
            var variableName = match.Groups[1].Value;
            if (variables?[variableName] is not JsonObject variable) continue;
            if (match.Index > at) parts.Add(new(Literal(value[at..match.Index]), null));
            var required = DirectoryJson.Bool(variable["isRequired"]) == true || urlPart;
            var input = AddInput(InputFrom(variable, "var:" + variableName, variableName, variableName, parentSecret, required));
            parts.Add(new(null, input.Key));
            at = match.Index + match.Length;
        }
        if (at < value.Length) parts.Add(new(Literal(value[at..]), null));
        return new(parts);
    }

    /// <summary>Text from the directory goes into mcp.json as is, so it must not hold a ${...} reference Martlet would expand
    /// (an entry could otherwise send one of your environment variables or saved secrets to its server).</summary>
    private static string Literal(string text) =>
        text.Contains("${", StringComparison.Ordinal) ? throw new FormatException("it contains ${...} text Martlet would expand") : text;

    private (string Name, Template Value) ParseKeyValue(JsonObject node, string kind)
    {
        var name = DirectoryJson.Text(node["name"])?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl)) throw new FormatException($"a {kind} has no usable name");
        var secret = DirectoryJson.Bool(node["isSecret"]) == true;
        if (DirectoryJson.Text(node["value"]) is { } value)
            return (name, ParseTemplate(value, node["variables"] as JsonObject, secret));
        var label = kind == "header" ? $"{name} header" : name;
        var input = AddInput(InputFrom(node, $"{kind}:{name}", label, name, false, DirectoryJson.Bool(node["isRequired"]) == true));
        return (name, new([new(null, input.Key)]));
    }

    private Argument ParseArgument(JsonObject node)
    {
        var type = DirectoryJson.Text(node["type"]);
        var required = DirectoryJson.Bool(node["isRequired"]) == true;
        var value = DirectoryJson.Text(node["value"]);
        var variables = node["variables"] as JsonObject;
        if (type == "named")
        {
            var name = DirectoryJson.Text(node["name"])?.Trim();
            if (string.IsNullOrEmpty(name) || name.Any(char.IsControl)) throw new FormatException("a named argument has no name");
            var flag = new Template([new(Literal(name), null)]);
            if (value is not null) return new([flag, ParseTemplate(value, variables, false)]);
            if (DirectoryJson.Text(node["format"]) == "boolean")
            {
                if (required) return new([flag]);
                var toggle = AddInput(InputFrom(node, "arg:" + name, name, name.TrimStart('-'), false, false));
                return new([flag], toggle.Key);
            }
            // A required flag with nothing to describe or fill in is passed as is (its value, if any, is the next argument).
            if (required && Describe(node) is null && node["default"] is null) return new([flag]);
            var input = AddInput(InputFrom(node, "arg:" + name, name, name.TrimStart('-'), false, required));
            return new([flag, new([new(null, input.Key)])]);
        }
        if (type != "positional") throw new FormatException($"an argument has an unknown type {type}");
        if (value is not null) return new([ParseTemplate(value, variables, false)]);
        var hint = DirectoryJson.Text(node["valueHint"])?.Trim();
        if (string.IsNullOrEmpty(hint)) throw new FormatException("a positional argument has neither a value nor a hint");
        var positional = AddInput(InputFrom(node, "arg:" + hint, hint, hint, false, required));
        return new([new([new(null, positional.Key)])]);
    }

    // ---- Building the mcp.json entry ----

    /// <summary>The mcp.json entry for <paramref name="serverName"/> with <paramref name="values"/> (by input key) filled in.
    /// Optional settings left empty are left out. Throws <see cref="McpConfigurationException"/> saying what to fix.</summary>
    public McpInstallPlan Build(string serverName, IReadOnlyDictionary<string, string?> values)
    {
        if (serverName.Trim().Length == 0 || serverName.Length > McpServerDefinition.MaxNameLength || serverName.Any(char.IsControl))
            throw new McpConfigurationException($"Give the server a name of 1-{McpServerDefinition.MaxNameLength} characters.");
        string? Entered(McpInstallInput input) =>
            values.TryGetValue(input.Key, out var value) && value?.Trim() is { Length: > 0 } text ? text : null;
        var missing = inputs.Where(i => i.Required && !i.Flag && Entered(i) is null).Select(i => i.Label).ToArray();
        if (missing.Length > 0) throw new McpConfigurationException($"Fill in {string.Join(", ", missing)} first.");
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var value = Entered(input);
            if (input.Flag) { resolved[input.Key] = value == "true" ? "true" : input.Required ? "false" : null; continue; }
            if (value is null) { resolved[input.Key] = null; continue; }
            if (value.Length > MaxValueLength) throw new McpConfigurationException($"{input.Label} is too long.");
            if (value.Any(char.IsControl)) throw new McpConfigurationException($"{input.Label} can't contain line breaks or tabs.");
            if (input.Choices.Count > 0 && !input.Choices.Contains(value, StringComparer.Ordinal))
                throw new McpConfigurationException($"Choose one of {string.Join(", ", input.Choices)} for {input.Label}.");
            if (input.Secret && !EnvironmentReference().IsMatch(value))
            {
                if (Encoding.UTF8.GetByteCount(value) > MaxSecretBytes)
                    throw new McpConfigurationException($"{input.Label} is too long to keep as a secret; put it in an environment " +
                        "variable and type ${env:NAME} here instead.");
                var secretName = SecretName(serverName, input.SecretLabel);
                secrets[secretName] = value;
                value = $"${{secret:{secretName}}}";
            }
            resolved[input.Key] = value;
        }
        string? Render(Template template)
        {
            var text = new StringBuilder();
            foreach (var part in template.Parts)
            {
                if (part.Text is not null) { text.Append(part.Text); continue; }
                if (resolved[part.Input!] is not { } value) return null;
                text.Append(value);
            }
            return text.ToString();
        }
        IEnumerable<string> Arguments(IEnumerable<Argument> arguments)
        {
            foreach (var argument in arguments)
            {
                if (argument.Flag is { } flag && resolved[flag] is null) continue;
                var tokens = argument.Tokens.Select(Render).ToArray();
                if (tokens.Any(t => t is null)) continue;
                foreach (var token in tokens) yield return token!;
            }
        }
        var env = new JsonObject();
        foreach (var (name, template) in environment)
            if (Render(template) is { } value) env[name] = value;
        var entry = new JsonObject();
        string preview;
        if (Kind == McpInstallKind.Remote)
        {
            var address = Render(url!) ?? throw new McpConfigurationException("Fill in the server's address first.");
            var check = Regex.Replace(address, @"\$\{[^}]*\}", "x");
            if (!Uri.TryCreate(check, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                throw new McpConfigurationException($"{address} isn't a valid http:// or https:// address.");
            entry["type"] = "http";
            entry["url"] = address;
            var headerValues = new JsonObject();
            foreach (var (name, template) in headers)
                if (Render(template) is { } value) headerValues[name] = value;
            if (headerValues.Count > 0) entry["headers"] = headerValues;
            preview = address + (headerValues.Count > 0 ? $"  (headers: {string.Join(", ", headerValues.Select(h => h.Key))})" : "");
        }
        else
        {
            var args = new List<string>();
            var runtime = Arguments(runtimeArguments).ToList();
            var package = Arguments(packageArguments).ToList();
            var runtimeNamesCommand = runtimeArguments.Any(a => a.Flag is null && a.Tokens.Count == 1 &&
                a.Tokens[0].Parts is [{ Text: { } literal }] && literal.Length > 0 && !literal.StartsWith('-'));
            switch (Kind)
            {
                case McpInstallKind.Npm:
                    // Some entries list the server's own flags as runtime arguments; npx would take them, so they go after the package.
                    var own = runtimeArguments.Where(a => !MisplacedForNpx(a)).ToArray();
                    var npx = Arguments(own).ToList();
                    if (!npx.Contains("-y") && !npx.Contains("--yes")) args.Add("-y");
                    args.AddRange(npx);
                    if (!runtimeNamesCommand) args.Add(packageVersion is null ? identifier! : $"{identifier}@{packageVersion}");
                    args.AddRange(Arguments(runtimeArguments.Except(own)));
                    args.AddRange(package);
                    break;
                case McpInstallKind.PyPI:
                    args.AddRange(runtime);
                    if (!runtimeNamesCommand)
                        args.Add(runtime.Contains("--from") || packageVersion is null ? identifier! : $"{identifier}@{packageVersion}");
                    args.AddRange(package);
                    break;
                case McpInstallKind.Docker:
                    if (runtime.FirstOrDefault() != "run") args.AddRange(["run", "-i", "--rm"]);
                    args.AddRange(runtime);
                    foreach (var (name, _) in env) args.AddRange(["-e", name]);
                    args.Add(Image());
                    args.AddRange(package);
                    break;
                default:
                    args.AddRange(runtime);
                    args.Add(packageVersion is null ? identifier! : $"{identifier}@{packageVersion}");
                    args.Add("--yes");
                    if (package.Count > 0) args.Add("--");
                    args.AddRange(package);
                    break;
            }
            entry["command"] = Runtime;
            entry["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            if (env.Count > 0) entry["env"] = env;
            preview = string.Join(' ', new[] { Runtime! }.Concat(args).Select(a => a.Contains(' ') ? $"\"{a}\"" : a)) +
                (env.Count > 0 ? $"  (environment: {string.Join(", ", env.Select(e => e.Key))})" : "");
        }
        entry["registry"] = registryName;
        if (registryVersion.Length > 0) entry["version"] = registryVersion;
        return new(entry, secrets, preview);
    }

    private static readonly HashSet<string> NpxOptions = new(StringComparer.Ordinal)
    {
        "-y", "--yes", "--no", "-p", "--package", "-c", "--call", "--registry", "-q", "--quiet", "--silent", "--prefix", "--cache",
        "--prefer-offline", "--prefer-online", "--offline", "--ignore-scripts", "--node-options", "--loglevel"
    };

    private static bool MisplacedForNpx(Argument argument) =>
        argument.Tokens.Count > 0 && argument.Tokens[0].Parts is [{ Text: { } flag }] && flag.StartsWith('-') &&
        !NpxOptions.Contains(flag.Split('=', 2)[0]);

    /// <summary>The ${secret:NAME} name for one value of one server: "server.LABEL" in letters, digits, '.', '_' and '-'.</summary>
    public static string SecretName(string serverName, string label)
    {
        static string Clean(string text) => Regex.Replace(text, "[^A-Za-z0-9._-]+", "-").Trim('-');
        var server = Clean(serverName);
        var name = $"{(server.Length == 0 ? "server" : server)}.{(Clean(label) is { Length: > 0 } l ? l : "value")}";
        return name.Length <= 128 ? name : name[..128];
    }

    [GeneratedRegex(@"^\$\{env:[A-Za-z_][A-Za-z0-9_]*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentReference();
}

internal static class DirectoryJson
{
    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    internal static long? Number(JsonNode? node) => node is not JsonValue value ? null
        : value.TryGetValue<long>(out var number) ? number
        : value.TryGetValue<double>(out var real) ? (long)real
        : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;

    /// <summary>An https (or http) web address from the entry, for opening in a browser.</summary>
    internal static Uri? Web(JsonNode? node) =>
        Text(node)?.Trim() is { Length: > 0 and <= 2048 } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" ? uri : null;
}

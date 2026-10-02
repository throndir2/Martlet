using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Martlet.Mcp.Client;

public enum McpTransportKind { Stdio, Http }

/// <summary>The user's MCP configuration could not be read; the message says what to fix.</summary>
public sealed class McpConfigurationException(string message) : Exception(message);

/// <summary>One MCP server Martlet may start on this PC (stdio) or reach over streamable HTTP. Env and headers can hold
/// secrets, so <see cref="ToString"/> never includes them.</summary>
public sealed record McpServerDefinition
{
    public const int MaxNameLength = 64;
    public required string Name { get; init; }
    public McpTransportKind Transport { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public string? WorkingDirectory { get; init; }
    public Uri? Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public bool Disabled { get; init; }
    /// <summary>Every tool of this server runs without asking first.</summary>
    public bool AutoApproveAll { get; init; }
    /// <summary>Tools of this server that run without asking first.</summary>
    public IReadOnlyList<string> AutoApprove { get; init; } = [];
    /// <summary>Why this server cannot start as configured (for example a missing environment variable).</summary>
    public string? Problem { get; init; }
    /// <summary>The Martlet feature that manages this server (for example Smart home), or null for one from mcp.json.</summary>
    public string? ManagedBy { get; init; }
    /// <summary>The MCP Registry name it was installed from (for example io.github.upstash/context7), if any.</summary>
    public string? Registry { get; init; }
    /// <summary>The registry version it was installed at, if any.</summary>
    public string? RegistryVersion { get; init; }
    /// <summary>The ${secret:NAME} values it uses, which Martlet keeps outside mcp.json.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];

    public bool AutoApproves(string tool) => AutoApproveAll || AutoApprove.Contains(tool, StringComparer.Ordinal);

    /// <summary>What decides whether a running server must restart after a change; approval lists do not.</summary>
    public string LaunchFingerprint => JsonSerializer.Serialize(new
    {
        Transport, Command, Args, Env, WorkingDirectory, Url = Url?.AbsoluteUri, Headers, Problem
    });

    /// <summary>The command line or address, for the user's own settings page (never env values or headers).</summary>
    public string Describe() => Transport == McpTransportKind.Http
        ? Url?.GetLeftPart(UriPartial.Path) ?? "(no address)"
        : string.Join(' ', new[] { Command ?? "" }.Concat(Args).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    public override string ToString() => $"MCP server {Name}";
}

/// <summary>The MCP servers in Martlet's mcp.json. It accepts the common client formats: an "mcpServers" object
/// (Claude Desktop, Cursor, Cline) or a "servers" object (VS Code), with comments and trailing commas.</summary>
public sealed partial class McpConfiguration
{
    public const int MaxFileBytes = 1_048_576;
    public const int MaxServers = 32;
    public static McpConfiguration Empty { get; } = new([]);
    public IReadOnlyList<McpServerDefinition> Servers { get; }

    public McpConfiguration(IEnumerable<McpServerDefinition> servers)
    {
        var list = servers.ToList();
        if (list.Count > MaxServers) throw new McpConfigurationException($"Martlet supports at most {MaxServers} MCP servers.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in list)
            if (!names.Add(server.Name)) throw new McpConfigurationException($"Two servers are named \"{server.Name}\".");
        Servers = list.AsReadOnly();
    }

    /// <summary>An example the settings page can insert: the reference filesystem server, limited to your Documents.</summary>
    public const string Example = """
        {
          "mcpServers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "${userHome}\\Documents"]
            }
          }
        }
        """;

    // The file is the user's own; keep it readable (no \u0027 escapes for quotes or non-ASCII letters).
    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions Reading = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32
    };

    /// <summary>Reads mcp.json. <paramref name="secrets"/> looks up ${secret:NAME} values (Martlet keeps them in Windows
    /// Credential Manager); without it, or for a name it doesn't know, that server can't start until the secret is saved.</summary>
    public static McpConfiguration Parse(string? json, Func<string, string?>? environment = null, Func<string, string?>? secrets = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        secrets ??= _ => null;
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        if (json.Length > MaxFileBytes) throw new McpConfigurationException("mcp.json is larger than 1 MB.");
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: Reading); }
        catch (JsonException error)
        {
            throw new McpConfigurationException($"This isn't valid JSON (line {(error.LineNumber ?? 0) + 1}, " +
                $"position {(error.BytePositionInLine ?? 0) + 1}). Check for a missing comma, quote or brace.");
        }
        if (root is not JsonObject document)
            throw new McpConfigurationException("The file must be a JSON object with an \"mcpServers\" section.");
        var section = Section(document);
        if (section is null)
        {
            if (document.Count == 0) return Empty;
            throw new McpConfigurationException("Put your servers inside an \"mcpServers\" object, the format Claude Desktop, " +
                "Cursor and Cline use (VS Code's \"servers\" works too).");
        }
        var servers = new List<McpServerDefinition>();
        foreach (var (name, value) in section)
            servers.Add(ParseServer(name, value, environment, secrets));
        return new(servers);
    }

    private static JsonObject? Section(JsonObject document) =>
        (document["mcpServers"] ?? document["servers"]) switch
        {
            JsonObject servers => servers,
            null => null,
            _ => throw new McpConfigurationException("\"mcpServers\" must be an object of named servers.")
        };

    private static McpServerDefinition ParseServer(string name, JsonNode? value, Func<string, string?> environment,
        Func<string, string?> secrets)
    {
        if (name.Trim().Length == 0 || name.Length > McpServerDefinition.MaxNameLength || name.Any(char.IsControl))
            throw new McpConfigurationException($"Server names must be 1-{McpServerDefinition.MaxNameLength} characters.");
        if (value is not JsonObject server) throw new McpConfigurationException($"Server \"{name}\" must be an object.");
        var problems = new List<string>();
        var used = new List<string>();
        string Expand(string text) => ExpandVariables(text, environment, secrets, problems, used);
        var type = String(server, name, "type")?.Trim().ToLowerInvariant();
        var command = String(server, name, "command");
        var url = String(server, name, "url") ?? String(server, name, "serverUrl");
        var transport = type switch
        {
            "stdio" => McpTransportKind.Stdio,
            "http" or "streamable-http" or "streamablehttp" or "streamable_http" => McpTransportKind.Http,
            "sse" => throw new McpConfigurationException($"Server \"{name}\" uses the older SSE transport, which Martlet doesn't " +
                "support. Use the server's streamable HTTP address (\"type\": \"http\") or run it with a command (stdio)."),
            null when command is not null => McpTransportKind.Stdio,
            null when url is not null => McpTransportKind.Http,
            null => throw new McpConfigurationException($"Server \"{name}\" needs a \"command\" (a program on this PC) or a \"url\"."),
            _ => throw new McpConfigurationException($"Server \"{name}\" has an unknown \"type\": {type}. Use \"stdio\" or \"http\".")
        };
        Uri? address = null;
        if (transport == McpTransportKind.Stdio)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new McpConfigurationException($"Server \"{name}\" needs a \"command\" to run.");
            command = Expand(command.Trim());
        }
        else
        {
            if (string.IsNullOrWhiteSpace(url)) throw new McpConfigurationException($"Server \"{name}\" needs a \"url\".");
            var expanded = Expand(url.Trim());
            if (!Uri.TryCreate(expanded, UriKind.Absolute, out address) ||
                address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            {
                if (problems.Count == 0)
                    throw new McpConfigurationException($"Server \"{name}\" has an invalid \"url\"; use an http:// or https:// address.");
                address = null;
            }
        }
        var (autoAll, autoTools) = Approvals(server, name);
        var cwd = String(server, name, "cwd");
        return new()
        {
            Name = name,
            Transport = transport,
            Command = transport == McpTransportKind.Stdio ? command : null,
            Args = Strings(server, name, "args").Select(Expand).ToArray(),
            Env = Map(server, name, "env", Expand),
            WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? null : Expand(cwd.Trim()),
            Url = address,
            Headers = Map(server, name, "headers", Expand),
            Disabled = Bool(server, name, "disabled") == true || Bool(server, name, "enabled") == false,
            AutoApproveAll = autoAll,
            AutoApprove = autoTools,
            Registry = String(server, name, "registry") is { Length: > 0 } registry ? registry.Trim() : null,
            RegistryVersion = String(server, name, "version") is { Length: > 0 } version ? version.Trim() : null,
            Secrets = used.Distinct(StringComparer.Ordinal).ToArray(),
            Problem = problems.Count == 0 ? null : string.Join(" ", problems.Distinct())
        };
    }

    private static (bool All, IReadOnlyList<string> Tools) Approvals(JsonObject server, string name)
    {
        var node = server["autoApprove"] ?? server["alwaysAllow"];
        if (node is null) return (false, []);
        if (node is JsonValue flag && flag.TryGetValue<bool>(out var all)) return (all, []);
        if (node is not JsonArray items)
            throw new McpConfigurationException($"Server \"{name}\": \"autoApprove\" must be a list of tool names or true.");
        var tools = new List<string>();
        foreach (var item in items)
        {
            if (item is not JsonValue text || !text.TryGetValue<string>(out var tool) || string.IsNullOrWhiteSpace(tool))
                throw new McpConfigurationException($"Server \"{name}\": \"autoApprove\" must list tool names.");
            if (tool == "*") return (true, []);
            tools.Add(tool);
        }
        return (false, tools.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string ExpandVariables(string text, Func<string, string?> environment, Func<string, string?> secrets,
        List<string> problems, List<string> used) =>
        VariablePattern().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            if (key == "userHome") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (key.StartsWith("env:", StringComparison.Ordinal))
            {
                var variable = key[4..];
                var found = environment(variable);
                if (found is null) problems.Add($"The environment variable {variable} isn't set for Martlet.");
                return found ?? "";
            }
            if (key.StartsWith("secret:", StringComparison.Ordinal))
            {
                var secret = key[7..];
                used.Add(secret);
                var found = secrets(secret);
                if (found is null)
                    problems.Add($"The secret {secret} isn't saved on this PC. Install the server again from the MCP directory to enter it.");
                return found ?? "";
            }
            return match.Value;
        });

    /// <summary>Every ${secret:NAME} name used anywhere in <paramref name="json"/> (none if it can't be read).</summary>
    public static IReadOnlyList<string> SecretNames(string json)
    {
        try { return SecretNames(ReadDocument(json)); }
        catch (McpConfigurationException) { return []; }
    }

    /// <summary>The ${secret:NAME} names used anywhere in one server's entry.</summary>
    public static IReadOnlyList<string> SecretNames(JsonNode? entry)
    {
        var names = new List<string>();
        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject values: foreach (var (_, child) in values) Visit(child); break;
                case JsonArray items: foreach (var child in items) Visit(child); break;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    foreach (Match match in VariablePattern().Matches(text))
                        if (match.Groups[1].Value.StartsWith("secret:", StringComparison.Ordinal)) names.Add(match.Groups[1].Value[7..]);
                    break;
            }
        }
        Visit(entry);
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    [GeneratedRegex(@"\$\{(userHome|env:[A-Za-z_][A-Za-z0-9_]*|secret:[A-Za-z0-9._-]{1,128})\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    private static string? String(JsonObject server, string name, string property) => server[property] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new McpConfigurationException($"Server \"{name}\": \"{property}\" must be text.")
    };

    private static bool? Bool(JsonObject server, string name, string property) => server[property] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw new McpConfigurationException($"Server \"{name}\": \"{property}\" must be true or false.")
    };

    private static IReadOnlyList<string> Strings(JsonObject server, string name, string property)
    {
        if (server[property] is null) return [];
        if (server[property] is not JsonArray items)
            throw new McpConfigurationException($"Server \"{name}\": \"{property}\" must be a list of text values.");
        return items.Select(item => item switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<double>(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new McpConfigurationException($"Server \"{name}\": every \"{property}\" value must be text.")
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, string> Map(JsonObject server, string name, string property, Func<string, string> expand)
    {
        if (server[property] is null) return new Dictionary<string, string>();
        if (server[property] is not JsonObject values)
            throw new McpConfigurationException($"Server \"{name}\": \"{property}\" must be an object of names and text values.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (key.Length == 0) throw new McpConfigurationException($"Server \"{name}\": \"{property}\" has an empty name.");
            result[key] = value switch
            {
                JsonValue text when text.TryGetValue<string>(out var s) => expand(s),
                JsonValue number when number.TryGetValue<double>(out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValue flag when flag.TryGetValue<bool>(out var b) => b ? "true" : "false",
                _ => throw new McpConfigurationException($"Server \"{name}\": \"{property}\".{key} must be text.")
            };
        }
        return result;
    }

    /// <summary>Returns <paramref name="json"/> with <paramref name="tool"/> added to the server's "autoApprove" list
    /// (comments in the file are not preserved).</summary>
    public static string AddAutoApprove(string json, string server, string tool) =>
        EditServer(json, server, entry => AddAutoApproveEntry(entry, tool));

    /// <summary>Returns <paramref name="json"/> after <paramref name="edit"/> changed one server's object (comments in the file
    /// are not preserved).</summary>
    public static string EditServer(string json, string server, Action<JsonObject> edit)
    {
        JsonObject? document;
        try { document = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, documentOptions: Reading) as JsonObject; }
        catch (JsonException) { throw new McpConfigurationException("mcp.json isn't valid JSON; fix it in the editor first."); }
        if (document is null) throw new McpConfigurationException("The file must be a JSON object.");
        if (Section(document)?[server] is not JsonObject entry)
            throw new McpConfigurationException($"Server \"{server}\" isn't in mcp.json any more.");
        edit(entry);
        return document.ToJsonString(Writing);
    }

    private static JsonObject ReadDocument(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, documentOptions: Reading) as JsonObject
                ?? throw new McpConfigurationException("The file must be a JSON object.");
        }
        catch (JsonException) { throw new McpConfigurationException("mcp.json isn't valid JSON; fix it in the editor first."); }
    }

    /// <summary>Returns <paramref name="json"/> with <paramref name="entry"/> saved as server <paramref name="server"/>,
    /// replacing one of that name only when <paramref name="replace"/> is set (comments in the file are not preserved).</summary>
    public static string AddServer(string json, string server, JsonObject entry, bool replace = false)
    {
        if (server.Trim().Length == 0 || server.Length > McpServerDefinition.MaxNameLength || server.Any(char.IsControl))
            throw new McpConfigurationException($"Server names must be 1-{McpServerDefinition.MaxNameLength} characters.");
        var document = ReadDocument(json);
        var section = Section(document);
        if (section is null) document["mcpServers"] = section = new JsonObject();
        var existing = section.Select(pair => pair.Key).FirstOrDefault(name => string.Equals(name, server, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !replace) throw new McpConfigurationException($"A server named \"{existing}\" is already in mcp.json.");
        if (existing is null && section.Count >= MaxServers)
            throw new McpConfigurationException($"Martlet supports at most {MaxServers} MCP servers; remove one first.");
        if (existing is not null) section.Remove(existing);
        section[server] = entry.DeepClone();
        return document.ToJsonString(Writing);
    }

    /// <summary>A copy of the entry named <paramref name="server"/> (ignoring case), or null.</summary>
    public static JsonObject? FindServer(string json, string server)
    {
        try
        {
            return Section(ReadDocument(json))?.FirstOrDefault(pair => string.Equals(pair.Key, server, StringComparison.OrdinalIgnoreCase))
                .Value?.DeepClone() as JsonObject;
        }
        catch (McpConfigurationException) { return null; }
    }

    /// <summary>Returns <paramref name="json"/> without server <paramref name="server"/> and the entry it removed.</summary>
    public static (string Json, JsonObject Removed) RemoveServer(string json, string server)
    {
        var document = ReadDocument(json);
        if (Section(document) is not { } section || section[server] is not JsonObject entry)
            throw new McpConfigurationException($"Server \"{server}\" isn't in mcp.json any more.");
        section.Remove(server);
        return (document.ToJsonString(Writing), entry);
    }

    /// <summary>The first of <paramref name="name"/>, name-2, name-3... that no server in <paramref name="json"/> uses.</summary>
    public static string UniqueName(string json, string name)
    {
        HashSet<string> taken;
        try { taken = Section(ReadDocument(json))?.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []; }
        catch (McpConfigurationException) { taken = []; }
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = $"-{n}";
            candidate = name[..Math.Min(name.Length, McpServerDefinition.MaxNameLength - suffix.Length)] + suffix;
        }
        return candidate;
    }

    public static void AddAutoApproveEntry(JsonObject entry, string tool)
    {
        var key = entry.ContainsKey("alwaysAllow") && !entry.ContainsKey("autoApprove") ? "alwaysAllow" : "autoApprove";
        switch (entry[key])
        {
            case JsonValue flag when flag.TryGetValue<bool>(out var all) && all:
                break;
            case JsonArray items:
                if (!items.Any(item => item is JsonValue value && value.TryGetValue<string>(out var name) && (name == "*" || name == tool)))
                    items.Add(tool);
                break;
            default:
                entry[key] = new JsonArray(tool);
                break;
        }
    }

    /// <summary>Removes <paramref name="tool"/> from the server's always-allowed tools.</summary>
    public static void RemoveAutoApproveEntry(JsonObject entry, string tool)
    {
        foreach (var key in new[] { "autoApprove", "alwaysAllow" })
            if (entry[key] is JsonArray items)
                foreach (var item in items.Where(i => i is JsonValue v && v.TryGetValue<string>(out var n) && n == tool).ToArray())
                    items.Remove(item);
    }

    /// <summary>Turns "run every tool without asking" on or off for one server.</summary>
    public static void SetAutoApproveAll(JsonObject entry, bool all)
    {
        var key = entry.ContainsKey("alwaysAllow") && !entry.ContainsKey("autoApprove") ? "alwaysAllow" : "autoApprove";
        if (all) entry[key] = true;
        else if (entry[key] is JsonValue) entry.Remove(key);
        else if (entry[key] is JsonArray items)
            foreach (var item in items.Where(i => i is JsonValue v && v.TryGetValue<string>(out var n) && n == "*").ToArray())
                items.Remove(item);
    }

    /// <summary>Turns one server on or off (the "disabled" flag Cline and Roo use).</summary>
    public static void SetDisabled(JsonObject entry, bool disabled)
    {
        entry.Remove("enabled");
        if (disabled) entry["disabled"] = true;
        else entry.Remove("disabled");
    }
}

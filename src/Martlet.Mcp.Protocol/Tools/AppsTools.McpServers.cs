using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>mcp.json in a data directory as the desktop's McpToolService parses it (the file name matches). Arguments are
    /// the raw ones from the file, so ${env:...} and ${secret:...} stay references; a literal value that looks secret is
    /// hidden (<see cref="HideSecretArguments"/>). No server starts and no credential is read.</summary>
    private static object McpServersStatus(JsonElement arguments)
    {
        var path = Path.Combine(DataDirectory(arguments), "mcp.json");
        if (!File.Exists(path)) return new { state = "none" };
        string text;
        try
        {
            if (new FileInfo(path).Length > McpConfiguration.MaxFileBytes) return new { state = "invalid", problem = "mcp.json is larger than 1 MB." };
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new { state = "unreadable", problem = error.Message };
        }
        McpConfiguration configuration;
        try { configuration = McpConfiguration.Parse(text, secrets: _ => ""); }
        catch (McpConfigurationException error) { return new { state = "invalid", problem = error.Message }; }
        return new
        {
            state = "loaded",
            servers = configuration.Servers.Select(server =>
            {
                var raw = McpConfiguration.FindServer(text, server.Name);
                return new
                {
                    name = server.Name,
                    transport = server.Transport.ToString().ToLowerInvariant(),
                    command = (raw?["command"] as JsonValue)?.ToString(),
                    args = HideSecretArguments((raw?["args"] as JsonArray)?.Select(a => a?.ToString()).ToArray() ?? []),
                    host = server.Url?.Host,
                    env = server.Env.Keys.ToArray(),
                    headers = server.Headers.Keys.ToArray(),
                    disabled = server.Disabled,
                    autoApproveAll = server.AutoApproveAll,
                    autoApprove = server.AutoApprove,
                    registry = server.Registry,
                    registryVersion = server.RegistryVersion,
                    secrets = server.Secrets,
                    // How a reply offers its tools: always <prefix>_<tool> (McpToolNames), whatever else runs.
                    toolNames = McpToolNames.Safe(server.Name) + "_<tool>",
                    problem = server.Problem
                };
            }).ToArray()
        };
    }

    private const string Hidden = "[hidden]";

    /// <summary>mcp.json arguments with every literal value that looks secret replaced by [hidden]: the value after a flag with a
    /// secret name (--token, --api-key, --password...), the value of a key=value or --key=value with such a name, the user and
    /// password in a web address, and values shaped like well-known tokens (sk-..., ghp_..., xoxb-..., long random strings).
    /// ${env:...} and ${secret:...} references stay as they are.</summary>
    internal static string?[] HideSecretArguments(string?[] arguments)
    {
        var hidden = new string?[arguments.Length];
        var afterSecretFlag = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument is null) { hidden[i] = null; afterSecretFlag = false; continue; }
            if (afterSecretFlag && !argument.StartsWith('-'))
            {
                hidden[i] = OnlyReferences(argument) ? argument : Hidden;
                afterSecretFlag = false;
                continue;
            }
            afterSecretFlag = false;
            var equals = argument.IndexOf('=');
            if (equals > 0 && SecretName(argument[..equals]))
            {
                var value = argument[(equals + 1)..];
                hidden[i] = OnlyReferences(value) ? argument : argument[..(equals + 1)] + Hidden;
                continue;
            }
            if (argument.StartsWith('-') && equals < 0 && SecretName(argument)) afterSecretFlag = true;
            hidden[i] = HideInValue(argument);
        }
        return hidden;
    }

    private static readonly string[] SecretWords =
        ["token", "secret", "password", "passwd", "pwd", "apikey", "api-key", "api_key", "auth", "credential", "bearer", "private-key",
         "private_key", "access-key", "access_key", "client-secret", "client_secret", "pat", "key"];

    // A flag or setting name that names a secret: --token, --api-key, API_KEY, password, -p is not one (too short to tell).
    private static bool SecretName(string name)
    {
        var words = name.TrimStart('-').ToLowerInvariant();
        if (words.Length < 3) return false;
        return SecretWords.Any(word => words == word || words.EndsWith("-" + word, StringComparison.Ordinal) ||
            words.EndsWith("_" + word, StringComparison.Ordinal) || words.StartsWith(word + "-", StringComparison.Ordinal) ||
            words.StartsWith(word + "_", StringComparison.Ordinal) || (word.Length >= 5 && words.Contains(word, StringComparison.Ordinal)));
    }

    private static bool OnlyReferences(string value) => SecretReference().Replace(value, "").Length == 0 && value.Length > 0;

    // A value that is not after a flag: hides a web address's user and password and values shaped like tokens.
    private static string HideInValue(string value)
    {
        if (OnlyReferences(value)) return value;
        var hidden = UrlUserInfo().Replace(value, match => match.Groups[1].Value + Hidden + "@");
        return TokenShape().IsMatch(hidden) ? Hidden : hidden;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{(?:env|secret):[^}]+\}")]
    private static partial System.Text.RegularExpressions.Regex SecretReference();

    [System.Text.RegularExpressions.GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.-]*://)[^/@\s]+@")]
    private static partial System.Text.RegularExpressions.Regex UrlUserInfo();

    // Well-known token prefixes, a JWT, or a long run of letters and digits that holds both (a key, not a word or a path).
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?:sk-[A-Za-z0-9_-]{16,}|sk_(?:live|test)_[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|" +
        @"xox[abposr]-[A-Za-z0-9-]{10,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{30,}|glpat-[A-Za-z0-9_-]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]*|" +
        @"(?=[A-Za-z0-9_\-]*[0-9])(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{32,})$")]
    private static partial System.Text.RegularExpressions.Regex TokenShape();

    /// <summary>How the desktop's MCP directory would install one registry entry. Secret values are never returned, only the
    /// ${secret:...} names the entry would use.</summary>
    private static object McpDirectoryPlan(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("server", out var given) ||
            given.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Missing object 'server'.");
        var node = JsonNode.Parse(given.GetRawText()) as JsonObject ?? throw new ArgumentException("Missing object 'server'.");
        if (node["server"] is JsonObject wrapped) node = wrapped;
        McpDirectoryEntry entry;
        try { entry = McpDirectoryEntry.Parse(node); }
        catch (FormatException error) { throw new ArgumentException(error.Message); }
        var name = OptionalString(arguments, "name") ?? entry.SuggestedName;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("values", out var given2) && given2.ValueKind == JsonValueKind.Object)
            foreach (var value in given2.EnumerateObject())
                values[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()
                    : throw new ArgumentException($"values.{value.Name} must be a string.");
        return new
        {
            name = entry.Name, displayName = entry.DisplayName, suggestedName = entry.SuggestedName, version = entry.Version,
            unsupported = entry.Unsupported,
            options = entry.Options.Select(option =>
            {
                object? plan = null;
                string? problem = null;
                try
                {
                    var built = option.Build(name, values);
                    plan = new { entry = built.Entry, secrets = built.Secrets.Keys.ToArray(), preview = built.Preview };
                }
                catch (McpConfigurationException error) { problem = error.Message; }
                return new
                {
                    kind = option.Kind.ToString(), summary = option.Summary, runtime = option.Runtime,
                    runtimeAvailable = option.RuntimeAvailable(), host = option.Host,
                    inputs = option.Inputs.Select(input => new
                    {
                        key = input.Key, label = input.Label, required = input.Required, secret = input.Secret, flag = input.Flag,
                        isPath = input.IsPath, @default = input.Default, placeholder = input.Placeholder, choices = input.Choices
                    }).ToArray(),
                    plan, problem
                };
            }).ToArray()
        };
    }
}

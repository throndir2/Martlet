using System.Net;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public static class ChatCompletionsSetup
{
    public const string Alias = "chat-completions";
    /// <summary>The model alias of the Thinking fallback, so its one-use permission can never stand in for the Thinking route's
    /// (the two may name the same upstream model).</summary>
    public const string FallbackAlias = "chat-completions-fallback";

    public static Uri BaseUri(string value)
    {
        ContractRules.Require(value is { Length: > 0 and <= 2048 } &&
            Uri.TryCreate(value, UriKind.Absolute, out _),
            "Enter an absolute HTTPS API base URL, or an explicit loopback HTTP API base URL.");
        var uri = new Uri(value);
        var loopback = IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) &&
            IPAddress.IsLoopback(address);
        ContractRules.Require((uri.Scheme == "https" || uri.Scheme == "http" && loopback) &&
            uri.Port is > 0 and <= 65535 && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            !value.Contains('\\') && !value.Contains('%') &&
            uri.AbsolutePath.Split('/').All(part => part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) &&
            value == uri.GetLeftPart(UriPartial.Path).TrimEnd('/') &&
            !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal),
            "Use the canonical API base URL without a trailing slash, credentials, query or fragment (for example https://host/v1 or http://127.0.0.1:8080/v1). HTTP requires a literal loopback IP.");
        return uri;
    }

    public static void ModelId(string value) =>
        ContractRules.Require(value is { Length: > 0 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':'),
            "Enter an explicit model ID of 1-128 ASCII letters, digits, dots, underscores, hyphens, slashes or colons.");

    public static AppSettings SelectRoute(AppSettings settings, string baseUrl, string modelId)
    {
        settings.Validate();
        ContractRules.Require(settings.Setup is not null, "Open setup before selecting a Chat Completions route.");
        _ = BaseUri(baseUrl);
        ModelId(modelId);
        var old = settings.Setup?.Routes.SingleOrDefault(route => route.Role == SetupRole.Llm);
        if (old?.RouteType == SetupRouteType.ChatCompletions && old.Origin == baseUrl && old.ModelId == modelId)
            return settings;
        return SetupSettings.ReplaceRoute(settings, new()
        {
            RouteSchemaVersion = 1, RouteType = SetupRouteType.ChatCompletions, Enabled = true,
            Role = SetupRole.Llm, ProviderAlias = Alias, Origin = baseUrl, ModelId = modelId,
            CredentialId = old?.RouteType == SetupRouteType.ChatCompletions && old.Origin == baseUrl
                ? old.CredentialId : null,
            ConfigurationRevision = Guid.NewGuid()
        });
    }
}

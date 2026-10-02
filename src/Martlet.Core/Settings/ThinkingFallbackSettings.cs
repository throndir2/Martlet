using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>A second Thinking destination (Companion › Thinking › If Thinking fails): an OpenAI-compatible Chat Completions
/// endpoint and model that answers a reply when the Thinking route fails before it says anything (an error, a rate limit or no
/// answer in time). Saving it is the owner's explicit choice to send replies there when that happens. Its optional key is its
/// own Windows Credential Manager entry, bound to this exact base URL; with no key of its own and the same base URL as a
/// Chat Completions Thinking route, it uses that route's key.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThinkingFallbackSettings : IContract
{
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }

    public void Validate()
    {
        _ = ChatCompletionsSetup.BaseUri(Origin);
        ChatCompletionsSetup.ModelId(ModelId);
        ContractRules.Require(CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID.");
        ContractRules.Require(ConfigurationRevision != Guid.Empty, "The Thinking fallback requires a configuration revision.");
    }

    /// <summary>The fallback's own key in Windows Credential Manager, scoped like a Chat Completions Thinking key.</summary>
    public CredentialBinding Binding(Guid profileId, Guid credentialId) =>
        new(profileId, credentialId, SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsSetup.Alias, Origin);

    /// <summary>Whether this is exactly the Thinking route already (same endpoint and model), so it adds nothing.</summary>
    public bool Same(SetupRoute? thinking) =>
        thinking?.RouteType == SetupRouteType.ChatCompletions && thinking.Origin == Origin && thinking.ModelId == ModelId;

    /// <summary>Whether it borrows the Thinking route's key: no key of its own, and the same Chat Completions base URL as a
    /// Thinking route that has one.</summary>
    public bool UsesThinkingKey(SetupRoute? thinking) =>
        CredentialId is null && thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } &&
        thinking.Origin == Origin;
}

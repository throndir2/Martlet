using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Avatar.Audio2Face.Tests;

public sealed class HostSignInSettingsTests
{
    private static HostSignInSettings Settings(string? owner, params (string Provider, string Subject)[] allowed) => new("home-host", owner, 10,
        [new HostSignInProviderSettings("idp", "oidc", "IdP", "https://idp.example", "c", null, true)],
        allowed.Select(a => new HostSignInAllowed(a.Provider, a.Subject, null)).ToArray(), [], null);

    [Fact]
    public void A_change_that_leaves_nobody_able_to_sign_in_is_recognized_before_it_is_sent()
    {
        var both = Settings("owner", ("idp", "user-1"));
        Assert.True(both.UsableAfter(new JsonObject { ["action"] = "remove-owner" }));
        Assert.True(both.UsableAfter(new JsonObject { ["action"] = "disallow", ["provider"] = "idp", ["subject"] = "user-1" }));

        var providerOnly = Settings(null, ("idp", "user-1"));
        Assert.False(providerOnly.UsableAfter(new JsonObject { ["action"] = "disallow", ["provider"] = "idp", ["subject"] = "user-1" }));
        Assert.False(providerOnly.UsableAfter(new JsonObject { ["action"] = "remove-provider", ["id"] = "idp" }));
        Assert.True(providerOnly.UsableAfter(new JsonObject { ["action"] = "disallow", ["provider"] = "idp", ["subject"] = "someone-else" }));

        var ownerOnly = Settings("owner");
        Assert.False(ownerOnly.UsableAfter(new JsonObject { ["action"] = "remove-owner" }));
        Assert.True(Settings(null).UsableAfter(new JsonObject { ["action"] = "allow", ["provider"] = "idp", ["subject"] = "user-2" }));
        Assert.True(Settings(null) with { BlockedReason = "signin.no_allowed_identity" } is { Usable: false });
    }
}

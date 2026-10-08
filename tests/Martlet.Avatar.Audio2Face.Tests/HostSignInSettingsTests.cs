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

    [Fact]
    public void Allowing_one_of_your_computers_sends_no_access_so_hosts_older_than_friend_sharing_take_it()
    {
        var member = HostSignInAccess.AllowChange("authentik", "me-1", "me@example.net", friend: false);
        Assert.False(member.ContainsKey("access"));
        Assert.Equal("allow", (string?)member["action"]);
        Assert.Equal("me-1", (string?)member["subject"]);
        var friend = HostSignInAccess.AllowChange("authentik", "ana-7", null, friend: true);
        Assert.Equal("friend", (string?)friend["access"]);
    }

    [Fact]
    public void The_settings_answer_says_which_identities_and_computers_are_a_friends_and_which_are_yours()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "protocol_version": { "major": 2, "minor": 0 }, "host_id": "home-host", "attached": true, "blocked_reason": null,
              "owner": { "user": "owner", "recovery_codes_left": 9 },
              "providers": [ { "id": "authentik", "kind": "oidc", "name": "Authentik", "issuer": "https://idp.example", "has_client_secret": true } ],
              "allowed": [
                { "provider": "authentik", "subject": "me-1", "label": "me@example.net", "access": "member" },
                { "provider": "authentik", "subject": "ana-7", "label": "ana@example.net", "access": "friend" },
                { "provider": "authentik", "subject": "old-2" },
                { "provider": "authentik", "subject": "new-3", "access": "something-newer" }
              ],
              "enrolled": [
                { "device_id": "laptop", "provider": "authentik", "subject": "me-1", "enrolled_at": "2026-10-08T09:00:00Z", "access": "member" },
                { "device_id": "friend-pc", "provider": "authentik", "subject": "ana-7", "label": "ana@example.net", "enrolled_at": "2026-10-08T09:05:00Z", "access": "friend" }
              ],
              "refused": [ { "device_id": "bob-pc", "provider": "authentik", "subject": "bob-9", "enrolled_at": "2026-10-08T09:10:00Z" } ],
              "removed_from_network": []
            }
            """);
        var settings = HostSignInSettings.Parse("home-host", document.RootElement);
        Assert.Equal(["member", "friend", "member", "member"], settings.Allowed.Select(a => a.Access));
        Assert.Equal(["ana-7"], settings.Friends.Select(f => f.Subject));
        Assert.False(settings.Enrolled.Single(e => e.DeviceId == "laptop").Friend);
        Assert.True(settings.Enrolled.Single(e => e.DeviceId == "friend-pc").Friend);
        // A refused identity has no access yet; the host decides it when the owner allows it.
        Assert.Null(settings.Refused.Single().Access);
        Assert.Throws<FormatException>(() => HostSignInSettings.Parse("another-host", document.RootElement));
        Assert.Equal(HostSignInAccess.Member, HostSignInAccess.Normalize(null));
        Assert.Equal(HostSignInAccess.Friend, HostSignInAccess.Normalize("friend"));
        Assert.True(new HostSignInIdentity("authentik", "ana-7", "ana@example.net") { Access = "friend" }.Friend);
        Assert.False(new HostPairedDevice("home-host", "laptop", "LAPTOP", DateTimeOffset.UnixEpoch, null).Friend);
        Assert.True(new HostPairedDevice("home-host", "friend-pc", "FRIEND-PC", DateTimeOffset.UnixEpoch, null) { Access = "friend" }.Friend);
    }
}

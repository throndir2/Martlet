using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Avatar.Audio2Face.Tests;

public sealed class HouseholdSignInTests
{
    private static HostSignInProviderSettings Google(bool secret = true, string client = "client-1") =>
        new("google", "oidc", "Google", "https://accounts.google.com", client, "openid email profile", secret);

    private static HostSignInSettings Host(string hostId, params HostSignInProviderSettings[] providers) =>
        new(hostId, null, 0, providers, [new HostSignInAllowed("google", "friend-7", "ana@example.net") { Access = "friend" }], [], null);

    [Fact]
    public void The_household_view_says_which_hosts_have_each_provider_and_which_miss_it()
    {
        var read = new Dictionary<string, HostSignInSettings>
        {
            ["gpu-box"] = Host("gpu-box", Google()),
            ["this-pc"] = Host("this-pc", Google(), new("steam", "steam", "Steam", null, null, null, false)),
            ["linux-box"] = Host("linux-box"),
            ["old-box"] = Host("old-box", Google(client: "client-0")),
            ["new-box"] = Host("new-box", Google(secret: false))
        };
        var states = HouseholdSignIn.Providers(read);
        var google = states.Single(s => s.Provider.Id == "google");
        Assert.Equal("client-1", google.Provider.ClientId);
        Assert.Equal(["gpu-box", "new-box", "this-pc"], google.On);
        Assert.Equal(["linux-box"], google.Missing);
        Assert.Equal(["old-box"], google.Different);
        // A confidential client: the host without a secret can't finish a sign-in.
        Assert.Equal(["new-box"], google.WithoutSecret);
        Assert.False(google.Everywhere);
        var steam = states.Single(s => s.Provider.Id == "steam");
        Assert.Equal(["this-pc"], steam.On);
        Assert.Empty(steam.WithoutSecret);
        var text = HouseholdSignIn.Coverage(states, new Dictionary<string, string> { ["off-box"] = "didn't answer in time" });
        Assert.Contains("Google (google): on gpu-box, new-box and this-pc; missing on linux-box; other settings on old-box; no client secret on new-box.", text);
        Assert.Contains("Couldn't read off-box (didn't answer in time).", text);
        Assert.Equal("No sign-in provider is set up on your hosts yet.", HouseholdSignIn.Coverage([]));
    }

    [Fact]
    public void A_provider_goes_only_to_hosts_that_miss_it_or_have_other_settings_and_its_change_never_touches_friends()
    {
        var provider = HouseholdProvider.From(Google());
        var read = new Dictionary<string, HostSignInSettings>
        {
            ["gpu-box"] = Host("gpu-box", Google()),
            ["linux-box"] = Host("linux-box"),
            ["old-box"] = Host("old-box", Google(client: "client-0")),
            ["new-box"] = Host("new-box", Google(secret: false))
        };
        Assert.Equal(["linux-box", "old-box", "unread-box"], HouseholdSignIn.Targets(provider, read, ["gpu-box", "linux-box", "old-box", "new-box", "unread-box"], withSecret: false));
        Assert.Equal(["linux-box", "old-box", "new-box"], HouseholdSignIn.Targets(provider, read, ["gpu-box", "linux-box", "old-box", "new-box"], withSecret: true));

        var change = provider.Change("s3cret");
        Assert.Equal("provider", (string?)change["action"]);
        var config = change["provider_config"]!.AsObject();
        Assert.Equal("google", (string?)config["id"]);
        Assert.Equal("https://accounts.google.com", (string?)config["issuer"]);
        Assert.Equal("s3cret", (string?)config["client_secret"]);
        // Without a secret the host keeps its own; nothing here names an allowed identity, so friends stay as they are.
        Assert.False(provider.Change(null)["provider_config"]!.AsObject().ContainsKey("client_secret"));
        Assert.DoesNotContain("subject", change.ToJsonString());
        Assert.Equal(new JsonObject { ["action"] = "remove-provider", ["id"] = "google" }.ToJsonString(), HouseholdProvider.RemoveChange("google").ToJsonString());
        Assert.Null(new HouseholdProvider("steam", "steam", "Steam", "https://ignored.example", null, null, null).Change(null)["provider_config"]!["issuer"]);
    }

    [Fact]
    public void Only_providers_that_need_it_require_a_client_secret()
    {
        Assert.False(new HouseholdProvider("steam", "steam", "Steam", null, null, null, null).NeedsSecret(householdHasSecret: true));
        Assert.True(new HouseholdProvider("discord", "discord", "Discord", null, "c", "identify", 53682).NeedsSecret(householdHasSecret: false));
        Assert.True(HouseholdProvider.From(Google()).NeedsSecret(householdHasSecret: true));
        Assert.False(HouseholdProvider.From(Google(secret: false)).NeedsSecret(householdHasSecret: false));
    }

    [Fact]
    public void The_result_line_names_where_it_was_saved_and_why_not_elsewhere()
    {
        var saved = Host("gpu-box", Google());
        var text = HouseholdSignIn.Describe(
            [new("gpu-box", saved, null), new("this-pc", Host("this-pc", Google()), null), new("linux-box", null, "didn't answer in time")],
            "Saved Google");
        Assert.StartsWith("Saved Google on gpu-box and this-pc. Not changed on linux-box (didn't answer in time).", text);
        Assert.Equal("No host to change.", HouseholdSignIn.Describe([], "Saved Google"));
    }

    private static readonly Guid Owner = new("8a58da66-fddc-5c5b-9282-fb119c84915f");
    private static readonly Guid Sam = new("5a6e0000-0000-4000-8000-00000000005a");

    private static HostSignInSettings Household(string hostId, bool samPassword, params HostSignInAllowed[] allowed) =>
        new(hostId, "owner", 10, [Google()], allowed, [], null)
        {
            OwnerAccountId = Owner,
            Accounts = samPassword ? [new HostSignInAccount(Sam, "Sam", true, 10)] : []
        };

    [Fact]
    public void An_accounts_provider_logins_are_the_member_identities_linked_to_it_on_any_host_never_a_friend()
    {
        var read = new Dictionary<string, HostSignInSettings>
        {
            ["gpu-box"] = Household("gpu-box", samPassword: false,
                new HostSignInAllowed("google", "sam-1", "sam@example.net") { AccountId = Sam },
                new HostSignInAllowed("google", "me-1", "me@example.net"),
                new HostSignInAllowed("google", "ana-7", "ana@example.net") { Access = "friend" }),
            ["this-pc"] = Household("this-pc", samPassword: false, new HostSignInAllowed("google", "sam-1", null) { AccountId = Sam })
        };
        var sams = Assert.Single(HouseholdSignIn.Logins(read, Sam));
        Assert.Equal(("google", "sam-1", "sam@example.net"), (sams.Provider, sams.Subject, sams.Label));
        Assert.Equal(["gpu-box", "this-pc"], sams.Hosts);
        Assert.Equal("sam@example.net (google)", sams.ToString());
        // A member identity linked to no account proves the owner's; a friend is nobody's login.
        Assert.Equal(["me-1"], HouseholdSignIn.Logins(read, Owner).Select(l => l.Subject));

        // Sam's only login that proves Sam elsewhere can't go; with a Martlet password (or another login) it can.
        Assert.Contains("only sign-in", HouseholdSignIn.UnlinkRefusal(read, Sam, sams));
        Assert.False(HouseholdSignIn.HasPassword(read, Sam));
        Assert.True(HouseholdSignIn.HasPassword(read, Owner));
        read["linux-box"] = Household("linux-box", samPassword: true);
        Assert.Null(HouseholdSignIn.UnlinkRefusal(read, Sam, sams));

        var link = HouseholdSignIn.LinkChange("google", "sam-1", "sam@example.net", Sam);
        Assert.Equal(("allow", "google", "sam-1", Sam.ToString()), ((string?)link["action"], (string?)link["provider"], (string?)link["subject"], (string?)link["account_id"]));
        Assert.False(link.ContainsKey("access"));
        Assert.Equal("disallow", (string?)HouseholdSignIn.UnlinkChange("google", "sam-1")["action"]);
    }
}

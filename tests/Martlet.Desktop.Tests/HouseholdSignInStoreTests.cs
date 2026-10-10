using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class HouseholdSignInStoreTests
{
    private static HostSignInProviderSettings Google(bool secret = true, string client = "client-1") =>
        new("google", "oidc", "Google", "https://accounts.google.com", client, "openid email profile", secret);

    private static HostSignInSettings Host(string hostId, params HostSignInProviderSettings[] providers) => new(hostId, null, 0, providers, [], [], null);

    private static HouseholdSignInStore.Document Kept(params KeptHouseholdProvider[] kept) => new() { Kept = [.. kept] };

    [Fact]
    public void A_new_host_gets_the_households_providers_with_the_secret_only_from_the_pc_that_kept_it()
    {
        var read = new Dictionary<string, HostSignInSettings>
        {
            ["gpu-box"] = Host("gpu-box", Google(), new("steam", "steam", "Steam", null, null, null, false)),
            ["new-box"] = Host("new-box")
        };
        var keptHere = KeptHouseholdProvider.From(HouseholdProvider.From(Google()), secret: true, DateTimeOffset.UnixEpoch);
        var withSecret = HouseholdSignInStore.Missing(Kept(keptHere), read);
        Assert.Equal(["google", "steam"], withSecret.Select(m => m.Provider.Id).Order());
        Assert.True(withSecret.Single(m => m.Provider.Id == "google").WithSecret);
        Assert.Equal(["new-box"], withSecret.Single(m => m.Provider.Id == "google").Hosts);
        // Another PC adds what needs no secret (Steam), never a confidential client it has no secret for.
        Assert.Equal(["steam"], HouseholdSignInStore.Missing(Kept(), read).Select(m => m.Provider.Id));
        // A secret kept for another client of the same ID is not given out.
        var oldClient = KeptHouseholdProvider.From(HouseholdProvider.From(Google(client: "client-0")), secret: true, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(HouseholdSignInStore.Missing(Kept(oldClient), read), m => m.Provider.Id == "google");
    }

    [Fact]
    public void A_provider_removed_from_every_host_is_never_added_back()
    {
        var read = new Dictionary<string, HostSignInSettings> { ["gpu-box"] = Host("gpu-box"), ["new-box"] = Host("new-box") };
        var keptHere = KeptHouseholdProvider.From(HouseholdProvider.From(Google()), secret: true, DateTimeOffset.UnixEpoch);
        Assert.Empty(HouseholdSignInStore.Missing(Kept(keptHere), read));
    }

    [Fact]
    public void A_host_that_has_the_provider_without_its_secret_gets_the_kept_secret()
    {
        var read = new Dictionary<string, HostSignInSettings>
        {
            ["gpu-box"] = Host("gpu-box", Google()),
            ["new-box"] = Host("new-box", Google(secret: false))
        };
        var keptHere = KeptHouseholdProvider.From(HouseholdProvider.From(Google()), secret: true, DateTimeOffset.UnixEpoch);
        var missing = Assert.Single(HouseholdSignInStore.Missing(Kept(keptHere), read));
        Assert.Equal(["new-box"], missing.Hosts);
        Assert.True(missing.WithSecret);
    }
}

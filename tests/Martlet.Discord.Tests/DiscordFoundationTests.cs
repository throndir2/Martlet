namespace Martlet.Discord.Tests;

public sealed class DiscordFoundationTests
{
    [Fact]
    public void ServerInviteAsksForChatAndVoicePermissions()
    {
        var url = DiscordInvite.ServerUrl(1234567890123456789);

        Assert.StartsWith("https://discord.com/oauth2/authorize?client_id=1234567890123456789&scope=bot%20applications.commands&permissions=", url);
        var permissions = (DiscordPermission)ulong.Parse(url.Split("permissions=")[1].Split('&')[0]);
        Assert.True(permissions.HasFlag(DiscordPermission.Connect | DiscordPermission.Speak | DiscordPermission.SendMessages |
            DiscordPermission.ReadMessageHistory | DiscordPermission.ViewChannel | DiscordPermission.UseVoiceActivity));
        Assert.False(permissions.HasFlag(DiscordPermission.ManageChannels));
        Assert.True(DiscordInvite.HomeServerPermissions.HasFlag(DiscordPermission.ManageChannels));
    }

    [Fact]
    public void UserInstallUsesUserIntegrationType() =>
        Assert.Equal("https://discord.com/oauth2/authorize?client_id=42&scope=applications.commands&integration_type=1",
            DiscordInvite.UserUrl(42));

    [Fact]
    public void ApplicationIdComesFromTheTokensFirstPart()
    {
        var first = Convert.ToBase64String("1234567890123456789"u8.ToArray()).TrimEnd('=');
        Assert.Equal(1234567890123456789UL, DiscordInvite.ApplicationIdFromToken($"{first}.GabcDe.xyz_-123"));
        Assert.Equal(0UL, DiscordInvite.ApplicationIdFromToken("not-a-token"));
    }

    [Theory]
    [InlineData(DiscordChatMode.Off, true, false)]
    [InlineData(DiscordChatMode.Mentions, true, true)]
    [InlineData(DiscordChatMode.Mentions, false, false)]
    [InlineData(DiscordChatMode.Sometimes, false, true)]
    [InlineData(DiscordChatMode.Always, false, true)]
    public void ChatModesDecideWhatIsConsidered(DiscordChatMode mode, bool addressed, bool considered) =>
        Assert.Equal(considered, DiscordChatRules.Considers(mode, addressed));

    [Fact]
    public void OnlySometimesMayPassOnAmbientTurns()
    {
        Assert.True(DiscordChatRules.MayPass(DiscordChatMode.Sometimes, addressed: false));
        Assert.False(DiscordChatRules.MayPass(DiscordChatMode.Sometimes, addressed: true));
        Assert.False(DiscordChatRules.MayPass(DiscordChatMode.Always, addressed: false));
    }

    [Fact]
    public void TextModeFollowsPeopleChannelsAndDefaults()
    {
        var saved = new DiscordPreferences
        {
            OwnerUserId = 1, ServerChat = DiscordChatMode.Off, DirectChat = DiscordChatMode.Always,
            People = [new(2, "Ana")], Channels = [new(10, 11, "general", DiscordChatMode.Sometimes)]
        };

        Assert.Equal(DiscordChatMode.Always, saved.TextMode(new(5, null, "dm", true), 1));
        Assert.Equal(DiscordChatMode.Always, saved.TextMode(new(5, null, "dm", true), 2));
        Assert.Equal(DiscordChatMode.Off, saved.TextMode(new(5, null, "dm", true), 3));
        Assert.Equal(DiscordChatMode.Sometimes, saved.TextMode(new(11, 10, "general", false), 3));
        Assert.Equal(DiscordChatMode.Off, saved.TextMode(new(12, 10, "other", false), 3));
    }

    [Fact]
    public void PreferencesRoundTripWithoutTheToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-discord-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = new DiscordPreferences
            {
                ApplicationId = 7, CredentialId = Guid.NewGuid(), Enabled = true, People = [new(2, "Ana", MayCall: false)],
                Channels = [new(10, 11, "general", DiscordChatMode.Mentions)]
            };
            Assert.True(saved.Save(directory));

            var loaded = DiscordPreferences.Load(directory);

            Assert.Equal(saved.ApplicationId, loaded.ApplicationId);
            Assert.Equal(saved.CredentialId, loaded.CredentialId);
            Assert.Equal(saved.People, loaded.People);
            Assert.Equal(saved.Channels, loaded.Channels);
            Assert.True(loaded.Configured);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

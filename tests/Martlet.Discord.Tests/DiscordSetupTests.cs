using Martlet.Discord;

namespace Martlet.Discord.Tests;

public sealed class DiscordSetupTests
{
    private static readonly DiscordPreferences Configured = new() { ApplicationId = 123456789012345678, CredentialId = Guid.NewGuid() };

    [Fact]
    public void NextStepFollowsTheSetupOrder()
    {
        Assert.Equal(DiscordSetupStep.CreateApplication, DiscordSetup.Next(new(), new(DiscordBotState.Off)));
        Assert.Equal(DiscordSetupStep.TurnOn, DiscordSetup.Next(Configured, new(DiscordBotState.Off)));
        Assert.Equal(DiscordSetupStep.Connecting, DiscordSetup.Next(Configured, new(DiscordBotState.Connecting)));
        Assert.Equal(DiscordSetupStep.TurnOnMessageContent,
            DiscordSetup.Next(Configured, new(DiscordBotState.Failed, Problem: DiscordBot.DescribeClose(4014, "Disallowed intent(s)."))));
        Assert.Equal(DiscordSetupStep.FixToken,
            DiscordSetup.Next(Configured, new(DiscordBotState.Failed, Problem: DiscordBot.DescribeClose(4004, null))));
        Assert.Equal(DiscordSetupStep.FixConnection,
            DiscordSetup.Next(Configured, new(DiscordBotState.Failed, Problem: DiscordBot.DescribeClose(4000, "Unknown error"))));
        Assert.Equal(DiscordSetupStep.AddToServer, DiscordSetup.Next(Configured, new(DiscordBotState.Online, "Martlet", 1, 0)));
        Assert.Equal(DiscordSetupStep.SetOwner, DiscordSetup.Next(Configured, new(DiscordBotState.Online, "Martlet", 1, 2)));
        Assert.Equal(DiscordSetupStep.Ready, DiscordSetup.Next(Configured with { OwnerUserId = 42 }, new(DiscordBotState.Online, "Martlet", 1, 2)));
    }

    [Fact]
    public void CloseCodesNameTheirFix()
    {
        Assert.True(DiscordSetup.IsIntentProblem(DiscordBot.DescribeClose(4014, null)));
        Assert.True(DiscordSetup.IsTokenProblem(DiscordBot.DescribeClose(4004, null)));
        Assert.Equal("Discord closed the connection.", DiscordBot.DescribeClose(null, null));
        Assert.Equal("Discord closed the connection (4000: Unknown error).", DiscordBot.DescribeClose(4000, "Unknown error"));
        Assert.False(DiscordSetup.IsIntentProblem(DiscordBot.DescribeClose(4000, null)));
    }

    [Theory]
    [InlineData("123456789012345678", 123456789012345678UL)]
    [InlineData("  <@123456789012345678> ", 123456789012345678UL)]
    [InlineData("<@!123456789012345678>", 123456789012345678UL)]
    [InlineData("<#123456789012345678>", 123456789012345678UL)]
    [InlineData("https://discord.com/channels/111111111111111111/123456789012345678/", 123456789012345678UL)]
    [InlineData("12345", 0UL)]
    [InlineData("not an id", 0UL)]
    [InlineData("", 0UL)]
    [InlineData(null, 0UL)]
    public void ReadsIdsAsTypedOrPasted(string? text, ulong expected)
    {
        Assert.Equal(expected != 0, DiscordSetup.TryParseId(text, out var id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void SummariesCountWithoutNamesOrIds()
    {
        var saved = Configured with
        {
            OwnerUserId = 987654321098765432, HomeGuildId = 555555555555555555, ServerChat = DiscordChatMode.Mentions,
            Channels = [new(1, 2, "Home › #secret-plans", DiscordChatMode.Always)],
            People = [new(11, "Alice"), new(12, "Bob", MayCall: false)]
        };
        var chat = DiscordSetup.ChatSummary(saved);
        Assert.Equal("Servers: Only when mentioned. DMs: Always (people Martlet knows). Voice: Sometimes. 1 channel rule.", chat);
        var people = DiscordSetup.PeopleSummary(saved);
        Assert.Equal("Your account is set. 2 people (1 may be called). Home server set.", people);
        foreach (var secret in new[] { "Alice", "Bob", "secret-plans", "987654321098765432", "555555555555555555" })
            Assert.DoesNotContain(secret, chat + people, StringComparison.Ordinal);
        Assert.Equal("Online as Martlet in 1 server.", DiscordSetup.StatusLine(new(DiscordBotState.Online, "Martlet", 1, 1)));
    }
}

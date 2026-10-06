using System.Globalization;

namespace Martlet.Discord;

/// <summary>The next thing the owner does to get Martlet onto Discord, in setup order.</summary>
public enum DiscordSetupStep { CreateApplication, TurnOn, Connecting, TurnOnMessageContent, FixToken, FixConnection, AddToServer, SetOwner, Ready }

/// <summary>Setup progress, chat mode wording and ID parsing shared by Companion › Discord, its MCP status and Doctor-style
/// checks. Nothing here reads or returns the token.</summary>
public static class DiscordSetup
{
    /// <summary>The setup step to show next, from the saved setup and the bot's live status.</summary>
    public static DiscordSetupStep Next(DiscordPreferences saved, DiscordBotStatus status)
    {
        if (!saved.Configured) return DiscordSetupStep.CreateApplication;
        return status.State switch
        {
            DiscordBotState.Failed when IsIntentProblem(status.Problem) => DiscordSetupStep.TurnOnMessageContent,
            DiscordBotState.Failed when IsTokenProblem(status.Problem) => DiscordSetupStep.FixToken,
            DiscordBotState.Failed => DiscordSetupStep.FixConnection,
            DiscordBotState.Connecting => DiscordSetupStep.Connecting,
            DiscordBotState.Off => DiscordSetupStep.TurnOn,
            _ when status.Servers == 0 => DiscordSetupStep.AddToServer,
            _ when saved.OwnerUserId == 0 => DiscordSetupStep.SetOwner,
            _ => DiscordSetupStep.Ready
        };
    }

    public static string Describe(DiscordSetupStep step) => step switch
    {
        DiscordSetupStep.CreateApplication => "Next: create Martlet's Discord application and paste its bot token below.",
        DiscordSetupStep.TurnOn => "Next: turn on Connect Martlet to Discord.",
        DiscordSetupStep.Connecting => "Connecting to Discord...",
        DiscordSetupStep.TurnOnMessageContent => "Next: turn on Message Content Intent on the Bot page in the Developer Portal, then Reconnect.",
        DiscordSetupStep.FixToken => "Next: reset the bot token in the Developer Portal and paste the new one below.",
        DiscordSetupStep.FixConnection => "Next: check the problem below, then Reconnect.",
        DiscordSetupStep.AddToServer => "Next: add Martlet to a server with Add to a server.",
        DiscordSetupStep.SetOwner => "Next: tell Martlet which Discord account is yours under People on Discord.",
        _ => "Martlet is on Discord."
    };

    /// <summary>Discord refused the privileged Message Content intent (close code 4014).</summary>
    public static bool IsIntentProblem(string? problem) =>
        problem is not null && problem.Contains("Message Content Intent", StringComparison.Ordinal);

    /// <summary>Discord rejected the token (close code 4004).</summary>
    public static bool IsTokenProblem(string? problem) =>
        problem is not null && problem.Contains("rejected the bot token", StringComparison.Ordinal);

    public static string ModeName(DiscordChatMode mode) => mode switch
    {
        DiscordChatMode.Off => "Off",
        DiscordChatMode.Mentions => "Only when mentioned",
        DiscordChatMode.Sometimes => "Sometimes",
        _ => "Always"
    };

    /// <summary>The bot's status in one line, for the page and MCP.</summary>
    public static string StatusLine(DiscordBotStatus status) => status.State switch
    {
        DiscordBotState.Online => $"Online as {status.BotName} in {Servers(status.Servers)}.",
        DiscordBotState.Connecting => "Connecting to Discord...",
        DiscordBotState.Failed => "Not connected: " + (status.Problem ?? "Discord closed the connection."),
        _ => "Off."
    };

    public static string Servers(int count) => count == 1 ? "1 server" : $"{count.ToString(CultureInfo.InvariantCulture)} servers";

    /// <summary>The chat modes in one line ("Servers: Off. DMs: Always (people Martlet knows). Voice: Sometimes. 2 channel rules.").</summary>
    public static string ChatSummary(DiscordPreferences saved) =>
        $"Servers: {ModeName(saved.ServerChat)}. DMs: {ModeName(saved.DirectChat)} " +
        $"({(saved.DirectFromAnyone ? "anyone" : "people Martlet knows")}). Voice: {ModeName(saved.VoiceChat)}. " +
        (saved.Channels.Count == 1 ? "1 channel rule." : $"{saved.Channels.Count.ToString(CultureInfo.InvariantCulture)} channel rules.");

    /// <summary>Who Martlet knows in one line, without names or IDs.</summary>
    public static string PeopleSummary(DiscordPreferences saved)
    {
        var callable = saved.People.Count(person => person.MayCall);
        return (saved.OwnerUserId != 0 ? "Your account is set. " : "Your account isn't set. ") +
            (saved.People.Count == 1 ? "1 person" : $"{saved.People.Count.ToString(CultureInfo.InvariantCulture)} people") +
            $" ({callable.ToString(CultureInfo.InvariantCulture)} may be called). " +
            (saved.HomeGuildId != 0 ? "Home server set." : "No home server.");
    }

    /// <summary>Reads a Discord ID (a snowflake) as typed or pasted: digits, a mention like &lt;@123&gt;, &lt;@!123&gt; or
    /// &lt;#123&gt;, or the last number of a discord.com link.</summary>
    public static bool TryParseId(string? text, out ulong id)
    {
        id = 0;
        var value = (text ?? "").Trim();
        if (value.StartsWith('<') && value.EndsWith('>')) value = value[1..^1].TrimStart('@', '#', '!', '&');
        else if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) value = value.TrimEnd('/').Split('/')[^1];
        return value.Length is >= 15 and <= 20 &&
            ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}

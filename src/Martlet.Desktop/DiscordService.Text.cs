using Martlet.Discord;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Martlet.Desktop;

/// <summary>Discord text chat: server channels, threads and DMs (each place's chat mode, mentions, replies and names), typing
/// while Martlet thinks, and the /martlet and /chatmode slash commands. The decisions live in <see cref="DiscordTextChat"/>
/// (Martlet.Discord); this file adapts NetCord's messages and interactions to it and sends through NetCord's REST client.</summary>
internal sealed partial class DiscordService
{
    private DiscordTextChat? text;

    /// <summary>The companion's own names (the personas'), so a message that names Martlet counts as addressed. Set by the
    /// main window; the bot's own Discord names always count.</summary>
    internal Func<IEnumerable<string>>? CompanionNames { get; set; }

    internal DiscordTextChat Text => text!;
    internal DiscordTextStats TextStats => Text.Stats;

    /// <summary>One line for the Discord page (DiscordTextStatus), MCP and the log: counts only, no message text, names or IDs.</summary>
    internal string TextStatusLine => Describe(TextStats);

    internal static string Describe(DiscordTextStats stats) =>
        $"Text chat: {stats.Seen} seen, {stats.Considered} considered, {stats.Answered} answered, {stats.Passed} passed, " +
        $"{stats.Dropped} dropped, {stats.Failed} failed. Last reply: {stats.LastPlaceKind ?? "none"}. " +
        $"Last problem: {stats.LastError ?? "none"}.";

    private void AttachText()
    {
        text = new(new NetCordTextTransport(Bot), () => Replies, message => Preferences.TextMode(message.Place, message.Speaker.UserId),
            Names);
        text.Changed += _ => Changed?.Invoke();
        Bot.Message += message =>
        {
            // Return at once: the gateway waits for its handlers, and a reply takes seconds.
            if (Incoming(message) is { } incoming) Task.Run(() => HandleTextAsync(incoming)).Forget();
            return default;
        };
        Bot.Commands.Add(DiscordCommands.Slash("martlet", "Say something to Martlet.",
            new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "message", "What you say to Martlet")
            {
                Required = true, MaxLength = 1500
            }), MartletCommandAsync);
        Bot.Commands.Add(new SlashCommandProperties("chatmode", "How readily Martlet answers in this channel.")
        {
            Options =
            [
                new(ApplicationCommandOptionType.String, "mode", "Off, only when addressed, sometimes on its own, or always")
                {
                    Required = true,
                    Choices =
                    [
                        new("Off", "off"), new("Mentions (when addressed)", "mentions"), new("Sometimes (Martlet decides)", "sometimes"),
                        new("Always", "always"), new("Server default", "default")
                    ]
                }
            ],
            IntegrationTypes = [ApplicationIntegrationType.GuildInstall],
            Contexts = [InteractionContextType.Guild],
            DefaultGuildPermissions = Permissions.ManageChannels
        }, ChatModeCommandAsync);
    }

    private async Task HandleTextAsync(DiscordIncoming incoming)
    {
        var result = await Text.HandleAsync(incoming).ConfigureAwait(false);
        if (result.Outcome is not (DiscordTextOutcome.NotConsidered or DiscordTextOutcome.IgnoredBot or DiscordTextOutcome.Empty))
            ErrorLog.Info(DiscordTextChat.Describe(result));
    }

    private IEnumerable<string> Names()
    {
        if (Bot.Status.BotName is { } botName) yield return botName;
        if (Bot.Client?.Cache.User?.GlobalName is { } globalName) yield return globalName;
        if (CompanionNames is { } companion)
            foreach (var name in companion()) yield return name;
    }

    private DiscordIncoming? Incoming(Message message)
    {
        if (message.Type is not (MessageType.Default or MessageType.Reply)) return null;
        var botId = Bot.Status.BotId;
        Guild? guild = null;
        if (message.GuildId is { } guildId) Bot.Client?.Cache.Guilds.TryGetValue(guildId, out guild);
        var author = message.Author;
        var speaker = new DiscordSpeaker(author.Id, (author as GuildUser)?.Nickname ?? author.GlobalName ?? author.Username,
            author.Id != 0 && author.Id == Preferences.OwnerUserId);
        var place = new DiscordPlace(message.ChannelId, message.GuildId, PlaceName(message, guild, speaker.Name), message.GuildId is null);
        var mentions = message.MentionedUsers.Any(user => user.Id == botId) || message.MentionedRoleIds.Any(roleId =>
            guild is not null && guild.Roles.TryGetValue(roleId, out var role) && role.Tags?.BotId == botId);
        // The bot's nickname in this server counts as one of its names.
        if (!mentions && guild is not null && guild.Users.TryGetValue(botId, out var me) && me.Nickname is { } nickname)
            mentions = DiscordAddressing.NameSaid(message.Content, [nickname]);
        return new(message.Id, place, speaker, DiscordAddressing.WithoutMention(message.Content, botId), mentions,
            message.ReferencedMessage?.Author.Id is { } repliedTo && repliedTo == botId,
            author.IsBot || author.IsSystemUser == true || message.WebhookId is not null);
    }

    private static string PlaceName(Message message, Guild? guild, string speaker)
    {
        if (guild is null) return message.GuildId is null ? $"DM with {speaker}" : "a server channel";
        var channel = guild.Channels.TryGetValue(message.ChannelId, out var found) ? found.Name
            : guild.ActiveThreads.TryGetValue(message.ChannelId, out var thread) ? thread.Name
            : (message.Channel as INamedChannel)?.Name;
        return $"#{channel ?? "channel"} in {guild.Name}";
    }

    private async ValueTask MartletCommandAsync(SlashCommandInteraction interaction)
    {
        var said = DiscordCommands.Option(interaction, "message")?.Trim() ?? "";
        var user = interaction.User;
        var speaker = new DiscordSpeaker(user.Id, (user as GuildUser)?.Nickname ?? user.GlobalName ?? user.Username,
            user.Id == Preferences.OwnerUserId);
        // In a server Martlet is in, that channel's chat mode applies; anywhere else (a DM, a group DM or a server where only the
        // person installed Martlet's app) it is their own app, so the DM allowance applies.
        var inServer = interaction.GuildId is not null &&
            interaction.AuthorizingIntegrationOwners.ContainsKey(ApplicationIntegrationType.GuildInstall);
        var place = new DiscordPlace(interaction.Channel?.Id ?? interaction.Id, inServer ? interaction.GuildId : null,
            CommandPlaceName(interaction, speaker.Name), !inServer);
        if (Preferences.TextMode(place, user.Id) == DiscordChatMode.Off)
        {
            await interaction.SendResponseAsync(InteractionCallback.Message(new()
            {
                Content = inServer ? "Martlet isn't chatting in this channel." : "Martlet only chats with people it knows.",
                Flags = MessageFlags.Ephemeral, AllowedMentions = NoMentions()
            })).ConfigureAwait(false);
            return;
        }
        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage()).ConfigureAwait(false);
        Task.Run(async () =>
        {
            var incoming = new DiscordIncoming(interaction.Id, place, speaker, said);
            var result = await Text.CommandAsync(incoming, async pieces =>
            {
                for (var index = 0; index < pieces.Count; index++)
                {
                    var quoted = index == 0 ? Quote(said, pieces[0]) : pieces[index];
                    await interaction.SendFollowupMessageAsync(new() { Content = quoted, AllowedMentions = NoMentions() })
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            ErrorLog.Info(DiscordTextChat.Describe(result) + " (/martlet)");
            if (result.Outcome != DiscordTextOutcome.Answered)
                await interaction.SendFollowupMessageAsync(new()
                {
                    Content = result.Outcome == DiscordTextOutcome.Passed ? "…" : $"Martlet couldn't answer: {result.Problem ?? result.Outcome.ToString()}",
                    AllowedMentions = NoMentions()
                }).ConfigureAwait(false);
        }).Forget();
    }

    /// <summary>The person's words quoted above the reply, so a group DM sees what was asked, when both fit in one message.</summary>
    internal static string Quote(string said, string reply)
    {
        var quoted = "> " + DiscordTextFormat.Sanitize(said).ReplaceLineEndings(" ") + "\n" + reply;
        return quoted.Length <= DiscordTextFormat.Limit ? quoted : reply;
    }

    private static string CommandPlaceName(SlashCommandInteraction interaction, string speaker) => interaction.Context switch
    {
        InteractionContextType.BotDMChannel => $"DM with {speaker}",
        InteractionContextType.DMChannel => (interaction.Channel as INamedChannel)?.Name is { Length: > 0 } name
            ? $"group DM {name}" : $"a DM or group DM with {speaker}",
        _ => interaction.Guild is { } guild
            ? $"#{(interaction.Channel as INamedChannel)?.Name ?? "channel"} in {guild.Name}"
            : $"#{(interaction.Channel as INamedChannel)?.Name ?? "channel"}"
    };

    private async ValueTask ChatModeCommandAsync(SlashCommandInteraction interaction)
    {
        string answer;
        var chosen = DiscordCommands.Option(interaction, "mode");
        if (interaction.GuildId is not { } guildId || interaction.Channel is null) answer = "Use /chatmode in a server channel.";
        else if (interaction.User.Id != Preferences.OwnerUserId && !(interaction.User is GuildInteractionUser member &&
                     (member.Permissions & (Permissions.ManageChannels | Permissions.Administrator)) != 0))
            answer = "Only Martlet's owner or someone who can manage channels can change this.";
        else
        {
            var channelId = interaction.Channel.Id;
            var name = (interaction.Channel as INamedChannel)?.Name ?? "channel";
            DiscordChatMode? mode = chosen switch
            {
                "off" => DiscordChatMode.Off,
                "mentions" => DiscordChatMode.Mentions,
                "sometimes" => DiscordChatMode.Sometimes,
                "always" => DiscordChatMode.Always,
                _ => null
            };
            var saved = Save(preferences => preferences with
            {
                Channels = [.. preferences.Channels.Where(rule => rule.ChannelId != channelId),
                    .. mode is { } set ? [new DiscordChannelRule(guildId, channelId, name, set)] : Array.Empty<DiscordChannelRule>()]
            });
            answer = !saved ? "Martlet couldn't save that."
                : mode is { } now ? $"Martlet's chat mode in #{name} is now {now}."
                : $"#{name} follows the server default ({Preferences.ServerChat}) again.";
        }
        await interaction.SendResponseAsync(InteractionCallback.Message(new()
        {
            Content = answer, Flags = MessageFlags.Ephemeral, AllowedMentions = NoMentions()
        })).ConfigureAwait(false);
    }

    /// <summary>No @everyone, @here, role or user pings, except (when <paramref name="replied"/>) the person replied to.</summary>
    internal static AllowedMentionsProperties NoMentions(bool replied = false) => new()
    {
        Everyone = false, AllowedRoles = [], AllowedUsers = [], ReplyMention = replied
    };

    private sealed class NetCordTextTransport(DiscordBot bot) : IDiscordTextTransport
    {
        public IDisposable Typing(DiscordPlace place) =>
            bot.Client is { } client ? client.Rest.EnterTypingScope(place.ChannelId) : new Nothing();

        public async Task SendAsync(DiscordPlace place, string text, ulong? replyTo, CancellationToken token)
        {
            var client = bot.Client ?? throw new InvalidOperationException("The Discord bot is offline.");
            MessageProperties message = new() { Content = text, AllowedMentions = NoMentions(replyTo is not null) };
            if (replyTo is { } id) message.MessageReference = MessageReferenceProperties.Reply(id, false);
            await client.Rest.SendMessageAsync(place.ChannelId, message, cancellationToken: token).ConfigureAwait(false);
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}

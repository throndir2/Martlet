using System.Security.Cryptography;
using System.Text;
using Martlet.Conversation;
using Martlet.Discord;
using NetCord.Rest;

namespace Martlet.Desktop;

/// <summary>Discord in the record of conversations: each answered text message (a channel, thread or DM; /martlet too) is
/// recorded as an exchange of that place's conversation, with the Discord IDs of the person's message and of Martlet's reply,
/// while the owner keeps a record (memory on). While the bot is online, deleting or editing those here deletes or edits them in
/// Discord through <see cref="DiscordPlatform"/>, at most one change a second (NetCord also waits out Discord's 429s).</summary>
internal sealed partial class DiscordService
{
    private DiscordPlatform? platform;

    /// <summary>The record of conversations; null keeps Discord out of it.</summary>
    internal DesktopConversationHistory? History { get; set; }
    /// <summary>Whether exchanges are recorded now (memory on and the owner keeps a record).</summary>
    internal Func<bool>? Recording { get; set; }

    /// <summary>The conversation one Discord place's exchanges belong to: always the same for the same place.</summary>
    internal static Guid PlaceConversation(DiscordPlace place) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("martlet/discord/" + place.Key)));

    private void RecordText(DiscordIncoming incoming, DiscordTextResult result, bool command)
    {
        if (result.Outcome != DiscordTextOutcome.Answered || result.Reply is not { Length: > 0 } reply) return;
        if (History is not { } history || Recording?.Invoke() != true) return;
        var place = incoming.Place;
        // A /martlet answer goes as the interaction's follow-up: Martlet can't find it in the channel again.
        var source = new HistorySource(HistoryApps.Discord, place.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            place.GuildId?.ToString(System.Globalization.CultureInfo.InvariantCulture), place.Name,
            command ? null : [incoming.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            command ? null : result.SentIds?.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        history.Record(PlaceConversation(place), HistoryInputKind.Typed, incoming.Text, reply, incoming.Speaker.Name, source);
    }

    /// <summary>Keeps the record's Discord changes going while the bot is online.</summary>
    private void FollowPlatform()
    {
        if (History is not { } history) return;
        var online = Bot.Client is not null && Bot.Status.State == DiscordBotState.Online;
        lock (gate)
        {
            if (online && platform is null)
            {
                platform = new(Bot);
                history.Platforms.Connect(platform);
            }
            else if (!online && platform is { } gone)
            {
                platform = null;
                history.Platforms.Disconnect(gone);
            }
        }
    }

    internal sealed class DiscordPlatform(DiscordBot bot) : IPlatformMessages
    {
        public string App => HistoryApps.Discord;
        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public async Task ApplyAsync(PlatformChange change, CancellationToken token)
        {
            var client = bot.Client ?? throw new PlatformChangeException(PlatformFailure.Unavailable, "The Discord bot is offline.");
            if (!ulong.TryParse(change.Chat, out var channel) || !ulong.TryParse(change.Message, out var message))
                throw new PlatformChangeException(PlatformFailure.Refused, "That isn't a Discord message.");
            try
            {
                if (change.Kind == PlatformChangeKind.Edit)
                    await client.Rest.ModifyMessageAsync(channel, message, options =>
                    {
                        options.Content = DiscordTextFormat.Sanitize(change.Text ?? "");
                        options.AllowedMentions = NoMentions();
                    }, cancellationToken: token).ConfigureAwait(false);
                else await client.Rest.DeleteMessageAsync(channel, message, cancellationToken: token).ConfigureAwait(false);
            }
            catch (RestRateLimitedException error)
            {
                throw new PlatformChangeException(PlatformFailure.RateLimited, "Discord asked Martlet to slow down.",
                    TimeSpan.FromMilliseconds(Math.Max(500, error.ResetAfter)));
            }
            catch (RestException error)
            {
                var why = error.Error?.Message is { Length: > 0 } said ? said : error.ReasonPhrase ?? error.StatusCode.ToString();
                throw (int)error.StatusCode switch
                {
                    429 => new PlatformChangeException(PlatformFailure.RateLimited, "Discord asked Martlet to slow down."),
                    400 or 403 or 404 => new PlatformChangeException(PlatformFailure.Refused, why),
                    _ => new PlatformChangeException(PlatformFailure.Unavailable, $"Discord couldn't do it right now ({why}).")
                };
            }
            catch (System.Net.Http.HttpRequestException error)
            {
                throw new PlatformChangeException(PlatformFailure.Unavailable, $"Couldn't reach Discord ({error.HttpRequestError}).");
            }
        }
    }
}

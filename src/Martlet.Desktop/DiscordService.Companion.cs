using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Discord;
using Martlet.Providers;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Martlet.Desktop;

/// <summary>Calls someone on Discord for the local conversation's call_on_discord tool.</summary>
internal interface IDiscordCaller
{
    /// <summary>Whether Martlet can call anyone at all (set up, a home server and someone who takes calls). Depends only on saved
    /// choices, so the tool list (and the start of every Thinking request) stays the same while the bot reconnects.</summary>
    bool CanCall { get; }
    Task<string> CallAsync(string person, CancellationToken token);
}

/// <summary>call_on_discord: the owner says "call Ana" and Martlet rings that Discord friend through its home server.</summary>
internal static class DiscordCallTool
{
    internal const string Name = "call_on_discord";
    internal static TextToolDefinition Definition { get; } = new(Name,
        "Call one of your Discord friends: you open a private voice channel in your Discord server, DM them a link and join it. " +
        "Use it only when the user asks you to call someone on Discord. Tell the user what the result says.",
        """{"type":"object","properties":{"person":{"type":"string","description":"The friend's name as the user said it."}},"required":["person"],"additionalProperties":false}""");

    internal static string? Person(string argumentsJson)
    {
        try { return (JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject)?["person"]?.GetValue<string>(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>Companion presence on Discord: the People list as Martlet's friends (<c>/friend</c>, approvals, removal), calls through
/// private voice channels in the home server (<c>/call</c>, the Call button and call_on_discord), the bot's status following what
/// Martlet is doing and its picture following the character (<c>/selfie</c> posts one). Discord's own rules are in docs/DISCORD.md.</summary>
internal sealed partial class DiscordService : IDiscordCaller
{
    private DiscordCompanion? companion;
    private Func<bool, CancellationToken, Task<byte[]?>>? characterPicture;
    private Func<string?>? avatarSource;
    private Func<CancellationToken, Task<byte[]?>>? avatarPicture;
    private Func<string?>? characterName;
    private Timer? sweeper;

    internal DiscordCompanion Companion
    {
        get
        {
            lock (gate) return companion ??= new(directory, () => Preferences, Save);
        }
    }

    /// <summary>Joins a call channel once Martlet can talk in Discord voice; null until then (the ring then says it joins later).</summary>
    internal Func<ulong, ulong, CancellationToken, Task>? JoinCall { get; set; }

    internal string Character => characterName?.Invoke() is { Length: > 0 } name ? name : Status.BotName ?? "Martlet";

    /// <summary>Wires the companion features: commands, the transport on each connection and the cleanup of call channels.
    /// <paramref name="picture"/> is a PNG of the showing character (portrait or whole); <paramref name="avatarSource"/> names the
    /// current character (null for none) and <paramref name="avatar"/> takes its bot picture.</summary>
    internal void AttachCompanion(Func<bool, CancellationToken, Task<byte[]?>> picture,
        Func<string?> avatarSource, Func<CancellationToken, Task<byte[]?>> avatar, Func<string?> character)
    {
        characterPicture = picture;
        this.avatarSource = avatarSource;
        avatarPicture = avatar;
        characterName = character;
        var friends = Companion;
        friends.Changed += () => Changed?.Invoke();
        Bot.Commands.Add(DiscordCommands.Slash("friend", "Be friends with Martlet, or stop being friends",
            new ApplicationCommandOptionProperties(ApplicationCommandOptionType.SubCommand, "ask", "Ask to be Martlet's friend"),
            new ApplicationCommandOptionProperties(ApplicationCommandOptionType.SubCommand, "remove", "Stop being Martlet's friend (no more DMs or calls)")),
            FriendCommandAsync);
        Bot.Commands.Add(DiscordCommands.Slash("selfie", "A picture of Martlet's character as it looks right now"), SelfieCommandAsync);
        Bot.Commands.Add(DiscordCommands.Slash("call", "Martlet calls one of its friends (owner only)",
            new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "person", "The friend's name") { Required = true, MaxLength = 64 }),
            CallCommandAsync);
        Bot.Ready += client =>
        {
            friends.Transport = new NetCordCompanionTransport(client);
            friends.PresenceReset();
            _ = Task.Run(async () =>
            {
                try
                {
                    await friends.SweepAsync(CancellationToken.None).ConfigureAwait(false);
                    await UpdateAvatarAsync(manual: false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (DiscordCompanion.IsDiscordFailure(error)) { ErrorLog.Warn($"Discord companion: {error.Message}"); }
            });
            return default;
        };
        sweeper = new Timer(_ => friends.SweepAsync(CancellationToken.None).ContinueWith(task =>
        {
            if (task.Exception is { } failed) ErrorLog.Warn($"Discord call cleanup failed: {failed.GetBaseException().Message}");
        }, TaskScheduler.Default), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ---------- the owner's actions (Martlet's window) ----------

    internal Task<DiscordCallResult> CallAsync(DiscordPerson person, CancellationToken token) =>
        Companion.CallAsync(person.UserId, Character, JoinCall, token);

    public bool CanCall
    {
        get
        {
            var saved = Preferences;
            return saved.Configured && saved.Enabled && saved.HomeGuildId != 0 && saved.People.Any(person => person.MayCall);
        }
    }

    public async Task<string> CallAsync(string person, CancellationToken token)
    {
        var saved = Preferences;
        if (DiscordFriends.Find(saved.People, person) is not { } found)
            return $"No Discord friend is called \"{person}\". Friends: {string.Join(", ", saved.People.Select(p => p.Name))}.";
        return (await CallAsync(found, token).ConfigureAwait(false)).Message;
    }

    /// <summary>Changes the bot's picture to the current character when it changed (or now, for <paramref name="manual"/>, within
    /// Discord's limits). Returns what happened.</summary>
    internal async Task<string> UpdateAvatarAsync(bool manual, CancellationToken token)
    {
        if (avatarPicture is not { } picture || avatarSource?.Invoke() is not { } source) return "No character to take a picture of.";
        return await Companion.AvatarAsync(picture, source, manual, token).ConfigureAwait(false);
    }

    /// <summary>Shows what Martlet is doing as the bot's status (rate limited).</summary>
    internal Task UpdatePresenceAsync(DiscordPresenceInputs inputs, CancellationToken token) => Companion.PresenceAsync(inputs, token);

    // ---------- slash commands ----------

    private async ValueTask FriendCommandAsync(SlashCommandInteraction interaction)
    {
        var user = interaction.User;
        var subcommand = DiscordCommands.Subcommand(interaction) ?? "ask";
        string reply;
        if (subcommand == "remove")
        {
            var known = Preferences.People.Any(person => person.UserId == user.Id) || Companion.State.Requests.Any(r => r.UserId == user.Id);
            reply = !known ? "We weren't friends on Discord, so there's nothing to remove."
                : Companion.Remove(user.Id) ? "Okay, we're not friends anymore. I won't DM or call you. You can /friend ask again any time."
                : "I couldn't save that just now. Try again in a moment.";
        }
        else reply = DiscordCompanion.AskReply(Companion.Ask(user.Id, user.GlobalName ?? user.Username), Character);
        await RespondAsync(interaction, reply).ConfigureAwait(false);
    }

    private async ValueTask CallCommandAsync(SlashCommandInteraction interaction)
    {
        if (interaction.User.Id != Preferences.OwnerUserId)
        {
            await RespondAsync(interaction, "Only my person can ask me to call someone.").ConfigureAwait(false);
            return;
        }
        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral)).ConfigureAwait(false);
        var message = await CallAsync(DiscordCommands.Option(interaction, "person") ?? "", CancellationToken.None).ConfigureAwait(false);
        await interaction.SendFollowupMessageAsync(new InteractionMessageProperties { Content = message, Flags = MessageFlags.Ephemeral })
            .ConfigureAwait(false);
    }

    private async ValueTask SelfieCommandAsync(SlashCommandInteraction interaction)
    {
        // In DMs and group DMs (the user-installed app) only friends get one; in a server, anyone there.
        if (interaction.GuildId is null && !Preferences.Knows(interaction.User.Id))
        {
            await RespondAsync(interaction, "I only share selfies with friends. Try /friend ask.").ConfigureAwait(false);
            return;
        }
        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage()).ConfigureAwait(false);
        var png = characterPicture is { } take ? await take(false, CancellationToken.None).ConfigureAwait(false) : null;
        if (png is null)
        {
            await interaction.SendFollowupMessageAsync(new InteractionMessageProperties
            {
                Content = $"{Character} isn't showing on screen right now, so no selfie this time."
            }).ConfigureAwait(false);
            return;
        }
        using var stream = new MemoryStream(png, writable: false);
        await interaction.SendFollowupMessageAsync(new InteractionMessageProperties
        {
            Content = "📸",
            Attachments = [new AttachmentProperties("selfie.png", stream)]
        }).ConfigureAwait(false);
    }

    private static async Task RespondAsync(SlashCommandInteraction interaction, string text)
    {
        try
        {
            await interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
            {
                Content = text, Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None
            })).ConfigureAwait(false);
        }
        catch (Exception error) when (DiscordCompanion.IsDiscordFailure(error)) { ErrorLog.Warn($"Discord command reply failed: {error.Message}"); }
    }

    private void DisposeCompanion() => sweeper?.Dispose();
}

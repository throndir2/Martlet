using System.IO;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Discord;

namespace Martlet.Mcp;

/// <summary>discord_status reads Companion › Discord's saved setup (discord.json) and whether its bot token is readable in
/// Windows Credential Manager (the token itself is never returned); discord_check connects the saved bot once with the
/// production <see cref="DiscordBot"/> and reports whether Discord accepted it: online with the bot's name and servers, a
/// rejected token, or Message Content Intent left off. The check sends no messages and registers no commands' replies.</summary>
internal static class DiscordCheck
{
    internal static object Status(string directory)
    {
        var path = Path.Combine(directory, DiscordPreferences.FileName);
        if (!File.Exists(path))
            return new { state = "none", configured = false, token = "none", next = DiscordSetup.Describe(DiscordSetupStep.CreateApplication) };
        try { using var _ = JsonDocument.Parse(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", configured = false, token = "none", problem = error.GetType().Name };
        }
        var saved = DiscordPreferences.Load(directory);
        var token = Token(saved);
        return new
        {
            state = "loaded",
            configured = saved.Configured,
            applicationId = saved.ApplicationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            token,
            enabled = saved.Enabled,
            ownerSet = saved.OwnerUserId != 0,
            homeServerSet = saved.HomeGuildId != 0,
            serverChat = saved.ServerChat.ToString(),
            directChat = saved.DirectChat.ToString(),
            voiceChat = saved.VoiceChat.ToString(),
            directFromAnyone = saved.DirectFromAnyone,
            channelRules = saved.Channels.Count,
            people = saved.People.Count,
            peopleMayCall = saved.People.Count(person => person.MayCall),
            chat = DiscordSetup.ChatSummary(saved),
            next = !saved.Configured ? DiscordSetup.Describe(DiscordSetupStep.CreateApplication)
                : token != "readable" ? DiscordSetup.Describe(DiscordSetupStep.FixToken)
                : !saved.Enabled ? DiscordSetup.Describe(DiscordSetupStep.TurnOn)
                : "Martlet connects to Discord whenever it runs. discord_check tests the connection; the desktop's DiscordState shows it live."
        };
    }

    internal static async Task<object> RunAsync(string directory, int? seconds, CancellationToken cancellation)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 15, 3, 30));
        var saved = DiscordPreferences.Load(directory);
        if (!saved.Configured)
            return new { state = "notConfigured", next = DiscordSetup.Describe(DiscordSetupStep.CreateApplication) };
        var vault = new WindowsCredentialStore();
        using var read = vault.ReadDiscordBotToken(saved.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            return new { state = "tokenUnreadable", problem = CredentialMessages.Describe(read.Error), next = DiscordSetup.Describe(DiscordSetupStep.FixToken) };
        string? token = null;
        read.Secret.Use(value => token = value.ToString());

        await using var bot = new DiscordBot();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bot.Changed += status =>
        {
            if (status.State is DiscordBotState.Online or DiscordBotState.Failed) settled.TrySetResult();
        };
        var started = DateTimeOffset.UtcNow;
        try
        {
            await bot.StartAsync(token!, cancellation);
            if (bot.Status.State is not (DiscordBotState.Online or DiscordBotState.Failed))
                await settled.Task.WaitAsync(wait, cancellation);
            // Servers arrive one by one after Ready.
            if (bot.Status.State == DiscordBotState.Online) await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
        }
        catch (TimeoutException) { }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        {
            return new { state = "Failed", problem = "Discord couldn't use the saved bot token.", next = DiscordSetup.Describe(DiscordSetupStep.FixToken) };
        }
        var status = bot.Status;
        var elapsed = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        await bot.StopAsync();
        var step = DiscordSetup.Next(saved with { Enabled = true }, status);
        return new
        {
            state = status.State.ToString(),
            botName = status.BotName,
            servers = status.Servers,
            problem = status.State == DiscordBotState.Connecting ? $"Discord didn't answer within {wait.TotalSeconds:0} seconds." : status.Problem,
            messageContentIntentOff = DiscordSetup.IsIntentProblem(status.Problem),
            tokenRejected = DiscordSetup.IsTokenProblem(status.Problem),
            milliseconds = elapsed,
            next = DiscordSetup.Describe(step)
        };
    }

    /// <summary>Whether the saved token can be read ("readable", "none" or the credential error), never the token.</summary>
    private static string Token(DiscordPreferences saved)
    {
        if (saved.CredentialId == Guid.Empty) return "none";
        using var read = new WindowsCredentialStore().ReadDiscordBotToken(saved.CredentialId);
        return read.Error == CredentialError.None && read.Secret is not null ? "readable" : read.Error.ToString();
    }
}

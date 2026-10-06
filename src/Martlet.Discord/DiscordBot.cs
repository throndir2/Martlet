using NetCord;
using NetCord.Gateway;

namespace Martlet.Discord;

public enum DiscordBotState { Off, Connecting, Online, Failed }

/// <summary>What the bot is doing, for the UI, Doctor and MCP (no secrets).</summary>
public sealed record DiscordBotStatus(DiscordBotState State, string? BotName = null, ulong BotId = 0, int Servers = 0,
    string? Problem = null);

/// <summary>Martlet's Discord bot, hosted in the Martlet process: one gateway connection with the intents chat and voice need.
/// Text, voice and companion features attach to <see cref="Client"/> and <see cref="Message"/>; this class only owns the
/// connection's lifetime and status.</summary>
public sealed class DiscordBot : IAsyncDisposable
{
    /// <summary>Guilds, server and DM messages, their content (privileged: turn on Message Content Intent in the Developer
    /// Portal) and voice states (to join and follow voice channels).</summary>
    public const GatewayIntents Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages |
        GatewayIntents.MessageContent | GatewayIntents.GuildVoiceStates;

    private readonly Lock gate = new();
    private GatewayClient? client;
    private DiscordBotStatus status = new(DiscordBotState.Off);

    public DiscordBotStatus Status { get { lock (gate) return status; } }
    public GatewayClient? Client { get { lock (gate) return client; } }

    /// <summary>Raised off the UI thread whenever <see cref="Status"/> changes.</summary>
    public event Action<DiscordBotStatus>? Changed;
    /// <summary>Every message the bot can see, except its own.</summary>
    public event Func<Message, ValueTask>? Message;
    /// <summary>Raised once a fresh connection is ready (after each start), so features can register commands.</summary>
    public event Func<GatewayClient, ValueTask>? Ready;

    public async Task StartAsync(string token, CancellationToken cancellation = default)
    {
        await StopAsync().ConfigureAwait(false);
        GatewayClient started = new(new BotToken(token), new GatewayClientConfiguration { Intents = Intents });
        started.Ready += async ready =>
        {
            Publish(new(DiscordBotState.Online, ready.User.Username, ready.User.Id, started.Cache.Guilds.Count));
            if (Ready is { } handler) await handler(started).ConfigureAwait(false);
        };
        started.GuildCreate += _ => { Recount(started); return default; };
        started.GuildDelete += _ => { Recount(started); return default; };
        started.MessageCreate += async message =>
        {
            if (message.Author.Id == Status.BotId || Message is not { } handler) return;
            await handler(message).ConfigureAwait(false);
        };
        started.Disconnect += args =>
        {
            if (!args.Reconnect) Publish(new(DiscordBotState.Failed, Status.BotName, Status.BotId, 0, "Discord closed the connection."));
            return default;
        };
        lock (gate) client = started;
        Publish(new(DiscordBotState.Connecting));
        try
        {
            await started.StartAsync(cancellationToken: cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lock (gate) if (client == started) client = null;
            started.Dispose();
            Publish(new(DiscordBotState.Failed, Problem: Describe(error)));
        }
    }

    public async Task StopAsync()
    {
        GatewayClient? stopping;
        lock (gate) { stopping = client; client = null; }
        if (stopping is null) return;
        try { await stopping.CloseAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
        stopping.Dispose();
        Publish(new(DiscordBotState.Off));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Recount(GatewayClient source)
    {
        lock (gate)
        {
            if (client != source || status.State != DiscordBotState.Online) return;
            status = status with { Servers = source.Cache.Guilds.Count };
        }
        Changed?.Invoke(Status);
    }

    private void Publish(DiscordBotStatus next)
    {
        lock (gate) status = next;
        Changed?.Invoke(next);
    }

    internal static string Describe(Exception error) => error.Message.Contains("4004", StringComparison.Ordinal)
        ? "Discord rejected the bot token. Reset it in the Developer Portal and paste the new one."
        : error.Message.Contains("4014", StringComparison.Ordinal)
            ? "Turn on Message Content Intent for the bot in the Developer Portal (Bot > Privileged Gateway Intents)."
            : $"Couldn't connect to Discord: {error.Message}";
}

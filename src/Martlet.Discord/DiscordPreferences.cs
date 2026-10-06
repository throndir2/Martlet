using System.Text.Json;

namespace Martlet.Discord;

/// <summary>A server channel whose chat mode differs from the server default.</summary>
public sealed record DiscordChannelRule(ulong GuildId, ulong ChannelId, string Name, DiscordChatMode Mode);

/// <summary>Someone Martlet knows on Discord (its stand-in for a friends list: bots cannot have friends). They may DM Martlet,
/// and with <paramref name="MayCall"/> Martlet may call them (open a private voice channel in its home server and invite them).</summary>
public sealed record DiscordPerson(ulong UserId, string Name, bool MayCall = true);

/// <summary>Martlet's saved Discord setup. The bot token lives in Windows Credential Manager under
/// <see cref="CredentialId"/>, never here.</summary>
public sealed record DiscordPreferences
{
    public const string FileName = "discord.json";

    public ulong ApplicationId { get; init; }
    public Guid CredentialId { get; init; }
    /// <summary>Connect the bot whenever Martlet runs.</summary>
    public bool Enabled { get; init; }
    /// <summary>The owner's own Discord account: always allowed, and Martlet's "you".</summary>
    public ulong OwnerUserId { get; init; }
    /// <summary>The server Martlet treats as home (private call channels are made there); 0 for none.</summary>
    public ulong HomeGuildId { get; init; }
    public DiscordChatMode ServerChat { get; init; } = DiscordChatMode.Off;
    public DiscordChatMode DirectChat { get; init; } = DiscordChatMode.Always;
    public DiscordChatMode VoiceChat { get; init; } = DiscordChatMode.Sometimes;
    /// <summary>Anyone may DM Martlet, not only <see cref="People"/> and the owner.</summary>
    public bool DirectFromAnyone { get; init; }
    public IReadOnlyList<DiscordChannelRule> Channels { get; init; } = [];
    public IReadOnlyList<DiscordPerson> People { get; init; } = [];

    public bool Configured => ApplicationId != 0 && CredentialId != Guid.Empty;

    public bool Knows(ulong userId) => userId == OwnerUserId || People.Any(person => person.UserId == userId);

    /// <summary>The chat mode for text in <paramref name="place"/>, from the person allowance, channel rules and defaults.</summary>
    public DiscordChatMode TextMode(DiscordPlace place, ulong authorId)
    {
        if (place.GuildId is null)
            return DirectFromAnyone || Knows(authorId) ? DirectChat : DiscordChatMode.Off;
        return Channels.FirstOrDefault(rule => rule.ChannelId == place.ChannelId)?.Mode ?? ServerChat;
    }

    public static DiscordPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<DiscordPreferences>(File.ReadAllText(path)) ?? new();
            return loaded with { Channels = loaded.Channels ?? [], People = loaded.People ?? [] };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"discord.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

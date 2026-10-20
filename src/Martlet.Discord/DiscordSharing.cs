using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Sync;
using Martlet.Discord.Calls;

namespace Martlet.Discord;

/// <summary>Where a computer keeps the bot token (Windows Credential Manager on the desktop): read, write and delete by the
/// credential ID that discord.json names.</summary>
public interface IDiscordTokenVault
{
    string? Read(Guid credentialId);
    bool Write(Guid credentialId, string token);
    void Delete(Guid credentialId);
}

/// <summary>What of Martlet's Discord setup is the same on all the owner's computers (the household setting
/// <see cref="DiscordSharing.SharedKey"/>): the bot (its application, and its token as the setting's secret), the owner's own
/// account, the home server, the chat modes, the channel rules and the people Martlet knows, and in the owner's own calls the
/// owner's display name, seeing who speaks, also playing on the usual output, stopping when talked over and the camera view's
/// framing. Each computer keeps for itself whether the bot connects there (only one computer should run it), where the token is
/// in its own credential store, and in calls whether the call mode is on there, what it hears, its output device and the camera
/// background (a picture is a file on that computer).</summary>
public sealed record DiscordShared
{
    public const int SchemaVersion1 = 1;
    public int Version { get; init; } = SchemaVersion1;
    public ulong ApplicationId { get; init; }
    public ulong OwnerUserId { get; init; }
    public ulong HomeGuildId { get; init; }
    public DiscordChatMode ServerChat { get; init; } = DiscordChatMode.Off;
    public DiscordChatMode DirectChat { get; init; } = DiscordChatMode.Always;
    public DiscordChatMode VoiceChat { get; init; } = DiscordChatMode.Sometimes;
    public bool VoiceFollowOwner { get; init; }
    public int VoiceLeaveAloneMinutes { get; init; } = 2;
    public bool DirectFromAnyone { get; init; }
    public IReadOnlyList<DiscordChannelRule> Channels { get; init; } = [];
    public IReadOnlyList<DiscordPerson> People { get; init; } = [];
    public bool CallSeeSpeakers { get; init; } = true;
    public string? CallOwnerName { get; init; }
    public bool CallAlsoSpeakers { get; init; } = true;
    public bool CallBargeIn { get; init; } = true;
    public double CameraZoom { get; init; } = 1;
    public double CameraX { get; init; }
    public double CameraY { get; init; }
}

/// <summary>Carrying the shared part of discord.json and discord-calls.json (<see cref="DiscordShared"/>) to the owner's other
/// computers and taking it there.</summary>
public static class DiscordSharing
{
    public const string SharedKey = "discord";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<DiscordChatMode>() },
        MaxDepth = 8
    };

    public static DiscordShared From(DiscordPreferences preferences, DiscordCallPreferences calls) => new()
    {
        ApplicationId = preferences.ApplicationId, OwnerUserId = preferences.OwnerUserId, HomeGuildId = preferences.HomeGuildId,
        ServerChat = preferences.ServerChat, DirectChat = preferences.DirectChat, VoiceChat = preferences.VoiceChat,
        VoiceFollowOwner = preferences.VoiceFollowOwner, VoiceLeaveAloneMinutes = preferences.VoiceLeaveAloneMinutes,
        DirectFromAnyone = preferences.DirectFromAnyone, Channels = preferences.Channels, People = preferences.People,
        CallSeeSpeakers = calls.SeeSpeakers, CallOwnerName = calls.OwnerName, CallAlsoSpeakers = calls.AlsoSpeakers, CallBargeIn = calls.BargeIn,
        CameraZoom = calls.CameraZoom, CameraX = calls.CameraX, CameraY = calls.CameraY
    };

    /// <summary>This computer's setup with the shared part taken from <paramref name="shared"/>; its own choices stay.</summary>
    public static DiscordPreferences Follow(DiscordPreferences local, DiscordShared shared, Guid credentialId) => local with
    {
        ApplicationId = shared.ApplicationId, CredentialId = shared.ApplicationId == 0 ? Guid.Empty : credentialId,
        Enabled = shared.ApplicationId != 0 && local.Enabled, OwnerUserId = shared.OwnerUserId, HomeGuildId = shared.HomeGuildId,
        ServerChat = shared.ServerChat, DirectChat = shared.DirectChat, VoiceChat = shared.VoiceChat, VoiceFollowOwner = shared.VoiceFollowOwner,
        VoiceLeaveAloneMinutes = shared.VoiceLeaveAloneMinutes, DirectFromAnyone = shared.DirectFromAnyone, Channels = shared.Channels,
        People = shared.People
    };

    public static DiscordCallPreferences Follow(DiscordCallPreferences local, DiscordShared shared) => (local with
    {
        SeeSpeakers = shared.CallSeeSpeakers, OwnerName = shared.CallOwnerName, AlsoSpeakers = shared.CallAlsoSpeakers, BargeIn = shared.CallBargeIn,
        CameraZoom = shared.CameraZoom, CameraX = shared.CameraX, CameraY = shared.CameraY
    }).Normalized();

    public static string Share(DiscordShared shared) => JsonSerializer.Serialize(shared, Json);

    /// <summary>A travelled setup, or null when it is a newer Martlet's or can't be read.</summary>
    public static DiscordShared? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var shared = JsonSerializer.Deserialize<DiscordShared>(value, Json);
            if (shared is not { Version: DiscordShared.SchemaVersion1 } || shared.Channels is null || shared.People is null ||
                shared.Channels.Any(c => c is null || c.Name is null || !Enum.IsDefined(c.Mode)) || shared.People.Any(p => p is null || p.Name is null) ||
                !Enum.IsDefined(shared.ServerChat) || !Enum.IsDefined(shared.DirectChat) || !Enum.IsDefined(shared.VoiceChat))
                return null;
            return shared;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>The bot token as a shared setting's secret may carry it (base64url, so any character fits), and back.</summary>
    public static string? PackToken(string? token) =>
        string.IsNullOrEmpty(token) ? null : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token)) is { Length: <= SharedSettings.MaximumSecretLength } packed
            ? packed : null;

    public static string? UnpackToken(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        try { return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(secret)); }
        catch (FormatException) { return null; }
    }

    /// <summary>The setting backed by discord.json and discord-calls.json in <paramref name="directory"/>, with the token in
    /// <paramref name="vault"/>. <paramref name="save"/> and <paramref name="saveCalls"/> save a change the way this computer
    /// does (the desktop's services, so what runs follows at once); without them the files are written.</summary>
    public static ISharedSection Section(string directory, IDiscordTokenVault vault,
        Func<Func<DiscordPreferences, DiscordPreferences>, bool>? save = null,
        Func<Func<DiscordCallPreferences, DiscordCallPreferences>, bool>? saveCalls = null)
    {
        save ??= change => change(DiscordPreferences.Load(directory)).Save(directory);
        saveCalls ??= change => change(DiscordCallPreferences.Load(directory)).Save(directory);
        var paths = new[] { Path.Combine(directory, DiscordPreferences.FileName), Path.Combine(directory, DiscordCallPreferences.FileName) };
        return new SecretSection(SharedKey, "Discord", _ =>
        {
            var preferences = DiscordPreferences.Load(directory);
            var token = preferences.Configured ? vault.Read(preferences.CredentialId) : null;
            var times = paths.Select(path => File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : (DateTimeOffset?)null).ToArray();
            // A bot whose token this computer can't read isn't shared from here (the token would be lost elsewhere).
            if (preferences.Configured && PackToken(token) is null) return Task.FromResult<SharedLocal?>(null);
            return Task.FromResult<SharedLocal?>(new(Share(From(preferences, DiscordCallPreferences.Load(directory))), PackToken(token),
                times.All(t => t is null), times.Max()));
        }, (setting, secret, _) =>
        {
            if (Parse(setting.Value) is not { } shared)
                return Task.FromResult(SharedApply.Waiting("It was set up on a newer Martlet. Update this PC to use it."));
            var token = UnpackToken(secret);
            if (shared.ApplicationId != 0 && token is null) return Task.FromResult(SharedApply.Waiting("Its bot token hasn't reached this PC yet."));
            var local = DiscordPreferences.Load(directory);
            var credential = local.CredentialId;
            Guid? written = null;
            if (shared.ApplicationId != 0 && (credential == Guid.Empty || vault.Read(credential) != token))
            {
                credential = Guid.NewGuid();
                if (!vault.Write(credential, token!)) return Task.FromResult(SharedApply.Waiting("Its bot token couldn't be saved on this PC."));
                written = credential;
            }
            if (!save(current => Follow(current, shared, credential)))
            {
                if (written is { } made) vault.Delete(made);
                return Task.FromResult(SharedApply.Waiting("It couldn't be saved on this PC."));
            }
            if (local.CredentialId != Guid.Empty && (written is not null || shared.ApplicationId == 0)) vault.Delete(local.CredentialId);
            if (!saveCalls(current => Follow(current, shared))) return Task.FromResult(SharedApply.Waiting("Its call choices couldn't be saved on this PC."));
            return Task.FromResult(SharedApply.Done);
        });
    }
}


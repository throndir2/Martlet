using System.Text.Json;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Core.Singing;

/// <summary>
/// The cloud providers Singing's lists can hold (docs/SINGING.md#cloud-providers), as <see cref="PoolMemberKind.Cloud"/> members of
/// the music list (<see cref="PoolAreas.Singing"/>) or the voice match list (<see cref="PoolAreas.VoiceMatch"/>):
/// <list type="bullet">
/// <item><see cref="ElevenLabs"/> writes music (Eleven Music, model <see cref="ElevenLabsMusicModel"/>) and changes a song's
/// vocals into the voice (its Voice Changer, model <see cref="ElevenLabsVoiceChangerModel"/>, with an instant voice clone of the
/// voice's recording).</item>
/// <item><see cref="Mureka"/> writes music; with its member setting <see cref="VoiceSetting"/> = <see cref="OwnVoice"/> it sings
/// in the voice itself (its vocal clone), so the song needs no voice match after.</item>
/// <item><see cref="Fal"/> (fal.ai) runs a hosted music model: <see cref="FalAceStep"/> or <see cref="FalMiniMax"/>.</item>
/// </list>
/// Each request may cost money: a member takes work only with the owner's agreement (<see cref="PoolMember.Consented"/>). Keys are
/// kept by provider (<see cref="SingingKeys"/>) and travel to the owner's other computers with the shared setting
/// <see cref="SingingKeys.SharedKey"/>.
/// </summary>
public static class SingingCloud
{
    public const string ElevenLabs = "elevenlabs", Mureka = "mureka", Fal = "fal";

    public const string ElevenLabsMusicModel = "music_v1";
    public const string ElevenLabsVoiceChangerModel = "eleven_multilingual_sts_v2";
    public const string MurekaModel = "auto";
    public const string FalAceStep = "fal-ai/ace-step";
    public const string FalMiniMax = "fal-ai/minimax-music/v1.5";

    /// <summary>A Mureka member's setting: <see cref="OwnVoice"/> makes it sing in the voice Martlet speaks with.</summary>
    public const string VoiceSetting = PoolSettingKeys.Voice;
    public const string OwnVoice = "martlet";

    public static readonly string ElevenLabsOrigin = ElevenLabsSetup.Origin;
    public const string MurekaOrigin = "https://api.mureka.ai";
    public const string FalOrigin = "https://queue.fal.run";

    /// <summary>The providers that write music, in the order the Add form offers them.</summary>
    public static IReadOnlyList<string> MusicProviders { get; } = [ElevenLabs, Mureka, Fal];

    /// <summary>The providers that match a song's voice.</summary>
    public static IReadOnlyList<string> VoiceMatchProviders { get; } = [ElevenLabs];

    /// <summary>Every provider Singing knows.</summary>
    public static IReadOnlyList<string> Providers { get; } = [ElevenLabs, Mureka, Fal];

    /// <summary>The provider's name in the app.</summary>
    public static string Name(string? provider) => provider switch
    {
        ElevenLabs => "ElevenLabs",
        Mureka => "Mureka",
        Fal => "fal.ai",
        _ => provider ?? "a cloud provider"
    };

    /// <summary>The member's name in the app: "ElevenLabs Music", "ElevenLabs Voice Changer", "Mureka", "fal.ai (MiniMax Music)".</summary>
    public static string Describe(PoolMember member, PoolArea area)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Provider switch
        {
            ElevenLabs => area == PoolAreas.VoiceMatch ? "ElevenLabs Voice Changer" : "ElevenLabs Music",
            Mureka => SingsInVoice(member) ? "Mureka (in Martlet's voice)" : "Mureka",
            Fal => $"fal.ai ({(member.Model == FalMiniMax ? "MiniMax Music" : member.Model == FalAceStep ? "ACE-Step" : member.Model)})",
            _ => member.Name
        };
    }

    /// <summary>The model a new member of <paramref name="area"/> gets for <paramref name="provider"/>.</summary>
    public static string DefaultModel(string provider, PoolArea area) => provider switch
    {
        ElevenLabs => area == PoolAreas.VoiceMatch ? ElevenLabsVoiceChangerModel : ElevenLabsMusicModel,
        Mureka => MurekaModel,
        Fal => FalMiniMax,
        _ => throw new ArgumentException("Singing doesn't know that provider.", nameof(provider))
    };

    /// <summary>Whether <paramref name="member"/> is a cloud provider that writes music.</summary>
    public static bool WritesMusic(PoolMember member) =>
        member.Kind == PoolMemberKind.Cloud && MusicProviders.Contains(member.Provider) && Model(member.Model);

    /// <summary>Whether <paramref name="member"/> is a cloud provider that matches a song's voice.</summary>
    public static bool MatchesVoice(PoolMember member) =>
        member.Kind == PoolMemberKind.Cloud && VoiceMatchProviders.Contains(member.Provider) && Model(member.Model);

    /// <summary>Whether a Mureka member sings in the voice Martlet speaks with (its vocal clone).</summary>
    public static bool SingsInVoice(PoolMember member) =>
        member.Kind == PoolMemberKind.Cloud && member.Provider == Mureka && member.Setting(VoiceSetting) == OwnVoice;

    /// <summary>The model ID rule for a cloud member: 1-128 letters, digits and <c>-_./:</c>.</summary>
    public static bool Model(string? model) =>
        model is { Length: > 0 and <= 128 } && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':');

    /// <summary>The credential scope of <paramref name="provider"/>'s key: ElevenLabs keeps its own scope (the same as a
    /// Speaking route's, so Speaking's ElevenLabs key can be used), the others a key bound to their exact API origin.</summary>
    public static CredentialBinding Binding(string provider, Guid profile, Guid credential) => provider switch
    {
        ElevenLabs => new(profile, credential, SetupRole.Tts, SetupRouteType.ElevenLabs, ElevenLabsSetup.Alias, ElevenLabsSetup.Origin),
        Mureka => new(profile, credential, SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsSetup.Alias, MurekaOrigin),
        Fal => new(profile, credential, SetupRole.Llm, SetupRouteType.ChatCompletions, ChatCompletionsSetup.Alias, FalOrigin),
        _ => throw new ArgumentException("Singing doesn't know that provider.", nameof(provider))
    };

    /// <summary>What goes to <paramref name="provider"/> and what it may cost, for the Add form's agreement.</summary>
    public static string Disclosure(string provider, PoolArea area) => area == PoolAreas.VoiceMatch
        ? $"Each song's sung vocals and the recording of the voice Martlet speaks with go to {Name(provider)}, which keeps a voice made " +
          "from that recording on your account; each song may cost money."
        : $"Each song's lyrics and style go to {Name(provider)}" +
          (provider == Mureka ? " (and, when it sings in Martlet's voice, the voice's recording)" : "") + "; each song may cost money.";
}

/// <summary>This PC's keys for Singing's cloud providers (singing-keys.json; the keys themselves are in Windows Credential
/// Manager): the credential ID of each provider's key. The keys travel to the owner's other computers with the shared setting
/// <see cref="SharedKey"/> (their names in its value, the keys in its secret), and each computer saves them in its own vault.</summary>
public sealed record SingingKeys
{
    public const string FileName = "singing-keys.json";
    public const string SharedKey = "singing-keys";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    /// <summary>Provider to credential ID.</summary>
    public IReadOnlyDictionary<string, Guid> Keys { get; init; } = new Dictionary<string, Guid>(StringComparer.Ordinal);

    public Guid? For(string provider) => Keys.TryGetValue(provider, out var id) ? id : null;

    public SingingKeys With(string provider, Guid? id)
    {
        var next = new Dictionary<string, Guid>(Keys, StringComparer.Ordinal);
        if (id is { } value && value != Guid.Empty) next[provider] = value;
        else next.Remove(provider);
        return this with { Keys = next };
    }

    public static SingingKeys Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 16_384) return new();
            var keys = JsonSerializer.Deserialize<SingingKeys>(File.ReadAllText(path), Json);
            return keys is { SchemaVersion: 1, Keys: not null }
                ? keys with { Keys = new Dictionary<string, Guid>(keys.Keys.Where(k => SingingCloud.Providers.Contains(k.Key)), StringComparer.Ordinal) }
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            SettingsFiles.NotifySaved(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The shared setting's value: the providers whose keys travel, in order (their keys are the secret, in the same
    /// order, <see cref="Bundle"/>).</summary>
    public static string Share(IReadOnlyList<string> providers) => JsonSerializer.Serialize(new { providers }, Json);

    /// <summary>The providers in a shared value, or null when it was written by a newer Martlet (a provider this one doesn't know).</summary>
    public static IReadOnlyList<string>? Parse(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (!document.RootElement.TryGetProperty("providers", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var providers = list.EnumerateArray().Select(p => p.GetString() ?? "").ToArray();
            return providers.All(p => SingingCloud.Providers.Contains(p)) && providers.Distinct().Count() == providers.Length ? providers : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The keys of the shared value's providers as one secret (null for none).</summary>
    public static string? Bundle(IReadOnlyList<string> keys) => keys.Count == 0 ? null : ThinkingPoolShare.Bundle(keys);

    /// <summary>The keys in a shared secret, in its providers' order.</summary>
    public static IReadOnlyList<string> Unbundle(string? secret) => ThinkingPoolShare.Unbundle(secret);
}

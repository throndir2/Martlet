using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Companion.Platform;
using Martlet.Providers;

namespace Martlet.Companion;

/// <summary>The companion's settings (settings.json in the data folder). Engine fields hold platform catalog ids;
/// secrets never live here (see <see cref="ICredentialStore"/>).</summary>
public sealed record CompanionSettings
{
    public const string Silent = "none";
    public const string OpenAiKey = "openai-api-key";
    public const string ChatCompletionsKey = "chat-completions-api-key";

    public int Version { get; init; } = 1;
    public string CharacterName { get; init; } = "Martlet";
    public string Persona { get; init; } =
        "You are Martlet, a warm and playful companion who keeps the user company while they play and work. " +
        "Answer in one to three short spoken sentences.";

    public string Thinking { get; init; } = "openai-llm";
    public string OpenAiModel { get; init; } = OpenAiTextGenerationCatalog.DefaultModelId;
    /// <summary>Chat Completions base URL: HTTPS, or loopback HTTP for Ollama (11434), LM Studio (1234) or Docker Model
    /// Runner (12434/engines/v1).</summary>
    public string ChatBaseUrl { get; init; } = "http://127.0.0.1:11434/v1";
    public string ChatModel { get; init; } = "llama3.2";

    public string Listening { get; init; } = "openai-stt";
    public string ListeningModel { get; init; } = OpenAiTranscriptionCatalog.DefaultModelId;

    /// <summary>A speaking engine id, or <see cref="Silent"/>.</summary>
    public string Speaking { get; init; } = "openai-tts";
    public string Voice { get; init; } = OpenAiSpeechSynthesisCatalog.DefaultVoice;

    public string LipSync { get; init; } = "loudness-lipsync";

    /// <summary>A .vrm/.glb file or a Live2D .model3.json; null uses the bundled Live2D character when present.</summary>
    public string? CharacterModel { get; init; }
    public bool ShowCharacter { get; init; } = true;
    public string PushToTalkKey { get; init; } = HotkeyGesture.Default.ToString();

    /// <summary>The push-to-talk default for a platform: F8 is the play/pause media key on Mac keyboards, so Macs use
    /// Control+Alt+T.</summary>
    public static string DefaultPushToTalk(Core.Platforms.DevicePlatform platform) =>
        platform == Core.Platforms.DevicePlatform.MacOs ? "Control+Alt+T" : HotkeyGesture.Default.ToString();

    /// <summary>"Control+Shift+Space" → a gesture; null when it names no key.</summary>
    public static HotkeyGesture? ParseGesture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            if (!Enum.TryParse<HotkeyModifiers>(part switch { "Ctrl" => "Control", "Option" or "Opt" => "Alt", "Cmd" or "Command" or "Super" or "Win" => "Meta", _ => part },
                    true, out var modifier) || modifier == HotkeyModifiers.None) return null;
            modifiers |= modifier;
        }
        return parts.Length == 0 ? null : new HotkeyGesture(parts[^1], modifiers);
    }

    /// <summary>The user agreed that what they type and say is sent to the chosen cloud provider, which may charge them.
    /// Loopback servers on this computer don't need it.</summary>
    public bool CloudConsent { get; init; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Reads and writes settings.json. Loading checks the file against this computer's guardrails, so a file
/// copied from another device keeps only what this one can run and reports the rest.</summary>
public sealed class CompanionSettingsStore(string folder)
{
    public string Folder { get; } = folder;
    public string FilePath => Path.Combine(Folder, "settings.json");

    public static string DefaultFolder()
    {
        var overridden = Environment.GetEnvironmentVariable("MARTLET_COMPANION_DATA");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg
            : OperatingSystem.IsLinux() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "Martlet");
    }

    public (CompanionSettings Settings, IReadOnlyList<string> Refusals) Load(CompanionGuardrails guardrails)
    {
        var defaults = Defaults(guardrails);
        if (!File.Exists(FilePath)) return (defaults, []);
        CompanionSettings? read;
        try { read = JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(FilePath), CompanionSettings.Json); }
        catch (JsonException error) { return (defaults, [$"settings.json couldn't be read ({error.Message}); defaults are used."]); }
        return read is null ? (defaults, []) : guardrails.Admit(read, defaults);
    }

    /// <summary>Settings carried from another device: admitted against <paramref name="current"/>.</summary>
    public static (CompanionSettings Settings, IReadOnlyList<string> Refusals) Import(string json, CompanionGuardrails guardrails,
        CompanionSettings current)
    {
        var incoming = JsonSerializer.Deserialize<CompanionSettings>(json, CompanionSettings.Json)
            ?? throw new JsonException("The settings file is empty.");
        return guardrails.Admit(incoming, current);
    }

    public void Save(CompanionSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, CompanionSettings.Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>The first offered engine for each job when the built-in default isn't offered here.</summary>
    public static CompanionSettings Defaults(CompanionGuardrails guardrails)
    {
        var settings = new CompanionSettings();
        string First(string job, string wanted) =>
            guardrails.IsOffered(wanted) ? wanted : guardrails.Offered(job).FirstOrDefault()?.Id ?? wanted;
        return settings with
        {
            Thinking = First(Core.Cluster.ClusterJobs.Thinking, settings.Thinking),
            Listening = First(Core.Cluster.ClusterJobs.Listening, settings.Listening),
            Speaking = guardrails.IsOffered(settings.Speaking) ? settings.Speaking : CompanionSettings.Silent,
            LipSync = First(Core.Cluster.ClusterJobs.LipSync, settings.LipSync),
            PushToTalkKey = CompanionSettings.DefaultPushToTalk(guardrails.Info.Platform)
        };
    }
}

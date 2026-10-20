using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;
using Martlet.Mcp.Shared;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>Companion › Personality and Character as saved in a data directory: settings.json's personas, avatar.json (read as
    /// JSON; the field names match Martlet.Avatar.Hosting's AvatarProfile) and lorebooks.json. Persona instructions, model paths
    /// and lorebook text are personal and never returned.</summary>
    private static async Task<object> CharacterStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var loaded = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync(cancellation);
        var companion = loaded.Settings?.Companion;
        object personality = new
        {
            state = loaded.State switch
            {
                Martlet.Core.Settings.SettingsLoadState.Loaded => "loaded",
                Martlet.Core.Settings.SettingsLoadState.FirstRun => "none",
                _ => "unreadable"
            },
            problem = loaded.Error?.Summary,
            active = companion?.Personas.FirstOrDefault(p => p.Id == companion.ActivePersonaId)?.Name,
            personas = companion?.Personas.Select(p => new
            {
                name = p.Name, active = p.Id == companion.ActivePersonaId, instructionCharacters = p.Text.Length,
                speechBreaks = Breaks(p.SpokenBreaks)
            }).ToArray() ?? []
        };

        object character;
        var avatar = Path.Combine(directory, "avatar.json");
        if (!File.Exists(avatar)) character = new { state = "none" };
        else
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(avatar, cancellation));
                var root = document.RootElement;
                string? Text(JsonElement parent, string name) =>
                    parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var model = Text(root, "model_path") ?? "";
                const string builtIn = "builtin:";
                character = new
                {
                    state = "loaded",
                    model = model.StartsWith(builtIn, StringComparison.Ordinal) ? "built-in" : "own model",
                    builtInCharacter = model.StartsWith(builtIn, StringComparison.Ordinal) ? model[builtIn.Length..] : null,
                    ownModelType = model.StartsWith(builtIn, StringComparison.Ordinal) ? null
                        : model.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) ? ".model3.json" : Path.GetExtension(model).ToLowerInvariant(),
                    renderer = Text(root, "renderer"),
                    lipSync = Text(root, "lip_sync") ?? "auto",
                    autoShow = root.TryGetProperty("auto_show", out var show) && show.ValueKind == JsonValueKind.True,
                    lipSyncHost = root.TryGetProperty("remote_host", out var host) && host.ValueKind == JsonValueKind.Object ? Text(host, "host_id") : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                character = new { state = "unreadable", problem = error.GetType().Name };
            }
        }

        // Lorebooks live in the folder of the account whose settings the data folder holds (docs/ACCOUNTS.md); a data folder from
        // before accounts keeps them itself.
        var loreFolder = Martlet.Core.Sync.AccountWorkingCopy.Load(directory) is { } signedIn
            ? Martlet.Core.Sync.AccountWorkingCopy.Folder(directory, signedIn.Account) : directory;
        var lore = await new Martlet.Core.Lorebooks.LorebookStore(loreFolder).LoadAsync(cancellation);
        object lorebooks = new
        {
            state = lore.Loaded ? File.Exists(Path.Combine(loreFolder, Martlet.Core.Lorebooks.LorebookStore.FileName)) ? "loaded" : "none" : "unreadable",
            account = loreFolder == directory ? null : Path.GetFileName(loreFolder),
            books = lore.Library.Books.Count,
            on = lore.Library.Books.Count(book => book.Activation != Martlet.Core.Lorebooks.LorebookActivation.Off),
            entries = lore.Library.Books.Sum(book => book.Entries.Count)
        };
        return new { personality, character, placement = CharacterPlacement(directory), clickThrough = CharacterClickThrough(directory),
            voice = CharacterVoice(directory), lorebooks };
    }

    /// <summary>character-click-through.json in a data directory, read through its store (Martlet.Avatar.Hosting's
    /// CharacterClickThroughFile): whether clicks pass through the character on this PC. No file means it catches clicks.</summary>
    private static object CharacterClickThrough(string directory)
    {
        if (!File.Exists(Path.Combine(directory, Martlet.Avatar.Hosting.CharacterClickThroughFile.FileName))) return new { state = "none", on = false };
        try
        {
            return new { state = "loaded", on = Martlet.Avatar.Hosting.CharacterClickThroughFile.Read(directory) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", on = false, problem = error.GetType().Name };
        }
    }

    /// <summary>Whether Martlet's voice is muted and how loud it is, from talk-preferences.json in a data directory as the desktop
    /// reads it (TalkPreferences): SpeakReplies (Companion › Voice's Speak Martlet's replies aloud) is on unless saved
    /// off, and Mute voice / Unmute voice on the character's right-click menu change the same choice; VoiceVolume (Companion ›
    /// Voice's Voice volume, 0 to 1) is full unless saved lower.</summary>
    private static object CharacterVoice(string directory)
    {
        var file = TalkPreferences.Read(directory);
        var (speak, volume) = (file.Preferences.SpeakReplies, file.Preferences.VoiceVolume);
        return file.State switch
        {
            SettingsFileState.Missing => new { state = "none", speakReplies = speak, muted = !speak, volume },
            SettingsFileState.Unreadable => (object)new { state = "unreadable", speakReplies = speak, muted = !speak, volume, problem = file.Problem },
            _ => new { state = "loaded", speakReplies = speak, muted = !speak, volume }
        };
    }

    /// <summary>character-placement.json in a data directory (Martlet.Desktop's CharacterPlacementStore): where the character
    /// was last left on this PC (device-independent pixels), on which monitor and whether it is locked there. No file means it
    /// shows at its default spot.</summary>
    private static object CharacterPlacement(string directory)
    {
        var path = Path.Combine(directory, "character-placement.json");
        if (!File.Exists(path)) return new { state = "none", locked = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new { state = "unreadable", locked = false, problem = "NotAnObject" };
            double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number) ? number : null;
            return new
            {
                state = "loaded", locked = root.TryGetProperty("Locked", out var locked) && locked.ValueKind == JsonValueKind.True,
                left = Number("Left"), top = Number("Top"), width = Number("Width"), height = Number("Height"),
                screen = root.TryGetProperty("Screen", out var screen) && screen.ValueKind == JsonValueKind.String ? screen.GetString() : null,
                screenLeft = Number("ScreenLeft"), screenTop = Number("ScreenTop")
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", locked = false, problem = error.GetType().Name };
        }
    }
}

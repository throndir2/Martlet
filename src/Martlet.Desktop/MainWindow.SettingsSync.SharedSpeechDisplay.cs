using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.SettingsSync.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private sealed record SharedSpeechDisplay(bool SpeechBubbles, bool Subtitles);
    private sealed record SharedConversationHistory(bool Keep, bool Search);
    private sealed record SharedSwitch(bool On);
    private sealed record SharedVoiceprint(float[] Embedding, float Threshold, float Consistency, DateTimeOffset CreatedAt, double SpeechSeconds);
    private sealed record SharedVoiceId(bool On, SharedVoiceprint? Voiceprint);
    private sealed record SharedHomePermissions(bool Control, bool AllowSensitive, bool ModelTools);
    private sealed record SharedUpdates(bool Checks, int IntervalMinutes, bool AutoInstall, bool AutoUpdateHosts);

    /// <summary>The character as it travels: which model (a bundled one; one of your characters by its ID, shown from each
    /// computer's own copy; or a model file at the same place on every computer), its renderer, its Audio2Face mapping and
    /// whether it shows when Martlet starts. Who does lip-sync travels in the who-does-what plan; the overlay's place on the
    /// screen stays with each computer.</summary>
    private sealed record SharedCharacter
    {
        public required AvatarRenderer Renderer { get; init; }
        public required string Model { get; init; }
        public required JsonElement Configuration { get; init; }
        public bool AutoShow { get; init; }
    }

    private const string SharedModelPrefix = "shared:";

    /// <summary>The character an account that never chose one starts with: Martlet's built-in one, not shown at start.</summary>
    private static string DefaultCharacter()
    {
        var builtIn = AvatarProfile.BuiltIn(Guid.NewGuid());
        return JsonSerializer.Serialize(new SharedCharacter
        {
            Renderer = builtIn.Renderer, Model = builtIn.ModelPath, Configuration = builtIn.Configuration, AutoShow = false
        }, SharedJson);
    }

    private async Task<SharedLocal?> ReadCharacterAsync(CancellationToken token)
    {
        var loaded = await setupService!.LoadAsync(token);
        if (loaded.Settings is not { } settings) return null;
        var profiles = new AvatarProfileStore(store!.DataDirectory);
        AvatarProfile? profile;
        try { profile = (await profiles.LoadAsync(settings.Profile.Id, token)).Profile; }
        catch (ContractException error) { throw new ContractException(error.Code, "This PC's character settings can't be read: " + error.Message); }
        if (profile is null) return null;
        // One of your characters is named by its ID: every computer shows its own copy of the same files.
        var model = SharedCharacterModels.ForPath(store.DataDirectory, SharedCharacterModels.View(store.DataDirectory), profile.ModelPath) is { } shared
            ? SharedModelPrefix + shared.Id : profile.ModelPath;
        var value = new SharedCharacter { Renderer = profile.Renderer, Model = model, Configuration = profile.Configuration, AutoShow = profile.AutoShow };
        var isDefault = profile.ModelPath == BundledLive2D.Prefix + BundledLive2D.DefaultCharacter && !profile.AutoShow;
        return new(JsonSerializer.Serialize(value, SharedJson), null, isDefault, FileTime(profiles.FilePath));
    }

    private async Task<SharedApply> ApplyCharacterAsync(SharedSetting setting, CancellationToken token)
    {
        var value = JsonSerializer.Deserialize<SharedCharacter>(setting.Value, SharedJson) ?? throw new JsonException();
        if (avatarWindowOpen) return SharedApply.Waiting("The character settings window is open on this PC.");
        var directory = store!.DataDirectory;
        var path = value.Model;
        if (path.StartsWith(SharedModelPrefix, StringComparison.Ordinal))
        {
            var id = path[SharedModelPrefix.Length..];
            if (SharedCharacterModels.View(directory).Live.FirstOrDefault(m => m.Id == id) is not { } model)
                return SharedApply.Waiting("That character hasn't reached this PC's characters yet.");
            if (!SharedCharacterModels.IsComplete(directory, model))
                return SharedApply.Waiting($"'{model.Name}' is still being copied to this PC.");
            path = SharedCharacterModels.EntryPath(directory, model);
        }
        else if (RemoteCharacterModels.IsRemote(path))
        {
            // A VRoid Hub character is shown from VRoid Hub on each computer with that computer's own link; never copied.
            if (RemoteCharacterModels.VRoidHubId(path) is null) throw new JsonException();
            if (vroidHub?.Tokens() is null)
                return SharedApply.Waiting("That character is from VRoid Hub, so link VRoid Hub on this PC in Account › Linked services.");
        }
        else if (!BundledLive2D.IsBuiltIn(path) && !File.Exists(path))
            return SharedApply.Waiting($"Its model file {Path.GetFileName(path)} isn't on this PC; add it in Companion › Character › Your " +
                "characters on the computer that has it.");
        // A new PC takes the character before anything else is set up on it (its first settings give the profile its ID).
        AvatarProfile profile;
        string? revision;
        try { (profile, revision) = await Pairings().EnsureProfileAsync(token); }
        catch (InvalidOperationException error) { return SharedApply.Waiting(error.Message); }
        var next = profile with
        {
            Renderer = value.Renderer, ModelPath = path, Configuration = value.Configuration.Clone(), AutoShow = value.AutoShow,
            ResourceRevision = null
        };
        next.Validate();
        await new AvatarProfileStore(directory).SaveAsync(next, revision, token);
        homeAvatar = next;
        characterChanged = profile.ModelPath != next.ModelPath || profile.Renderer != next.Renderer;
        return SharedApply.Done;
    }

    private static DateTimeOffset? FileTime(string path) => FileSection.Changed(path);

    /// <summary>Why this PC can't use a shared route yet: Parakeet not downloaded, or Ollama without the model. Null when it
    /// can.</summary>
    private async Task<string?> RouteAvailableAsync(SetupRole role, SharedRoute route, CancellationToken token)
    {
        switch (route.Type)
        {
            case SharedRoute.Parakeet:
                return SharedParakeetWaiting(route.Model, model => parakeet?.Installed(model) == true);
            case SharedRoute.ChatCompletions when route.Origin == LocalOllamaBaseUrl:
                if (LocalOllama.Executable() is null) return "Ollama isn't installed on this PC; set it up in Companion › Thinking.";
                var models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), token);
                return models is not null && !LocalOllama.Serves(models, route.Model)
                    ? $"Ollama on this PC doesn't have {route.Model} yet; download it in Companion › Thinking."
                    : null;
            default:
                return null;
        }
    }

    /// <summary>For who does what: hands a job a host did back to the shared route, when the owner's computers share one.
    /// Shared is false when they share none (the caller then uses this PC's own Setup choice); Problem says why this PC can't
    /// use the shared route yet, and is null once it does.</summary>
    private async Task<(bool Shared, string? Problem)> HandBackToSharedRouteAsync(HostJob job)
    {
        if (settingsNode is null || appSections is null) return (false, null);
        var key = job.Role switch { SetupRole.Llm => AppSettingsSections.Thinking, SetupRole.Stt => AppSettingsSections.Listening, _ => AppSettingsSections.Speaking };
        if (settingsNode.Document.Find(key) is not { } entry) return (false, null);
        var secret = settingsNode.Document.Secret(entry.SecretSha256);
        if (entry.SecretSha256 is not null && secret is null) return (true, "its API key hasn't reached this PC yet.");
        SharedApply result;
        try { result = await appSections.HandBackAsync(job.Role, job.Title, entry, secret, lifetime.Token); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (true, error.Message);
        }
        if (!result.Applied) return (true, result.Note);
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        homeSettings = loaded.Settings ?? homeSettings;
        return (true, null);
    }
}

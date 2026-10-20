using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Character › Your characters: the character models the owner added (Live2D folders and VRM files) and
/// the built-in character. Every model added on one computer is copied to every paired Martlet computer that can be the
/// companion (each Martlet desktop, whether it is a companion or a host PC right now), through the paired hosts, which keep a
/// copy only to pass it on. Which character is shown travels with the shared settings (MainWindow.SettingsSync.cs), by the
/// model's ID, so every computer shows its own copy of the same character.</summary>
public partial class MainWindow
{
    private const string BuiltInCharacterKey = "builtin";
    private readonly DispatcherTimer characterModelTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool characterModelBusy, characterModelQueued, avatarWindowOpen;
    private string characterModelStatus = "No other Martlet computers are paired yet, so your characters stay on this PC.";
    // Model files this run could not add to the shared list (too large, unsupported), so the sync doesn't retry them each time.
    private readonly HashSet<string> unsharedCharacterPaths = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeCharacterModels() => characterModelTimer.Tick += (_, _) => SyncCharacterModelsAsync().Forget();

    private void StartCharacterModels()
    {
        if (store is null || closing) return;
        characterModelTimer.Start();
        SyncCharacterModelsAsync().Forget();
    }

    /// <summary>Shares a change soon (debounced), so the owner's other computers get it quickly.</summary>
    private void QueueCharacterModelSync()
    {
        if (characterModelQueued || closing) return;
        characterModelQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { characterModelQueued = false; }
            await SyncCharacterModelsAsync();
        }
    }

    /// <summary>The name of the character this PC shows: a shared character's name, the built-in character's, or the model
    /// file's.</summary>
    private string CharacterModelName()
    {
        if (homeAvatar is null) return "Built-in character";
        if (BundledLive2D.IsBuiltIn(homeAvatar.ModelPath)) return homeAvatar.ModelPath[BundledLive2D.Prefix.Length..] + " (built-in)";
        if (RemoteCharacterModels.IsRemote(homeAvatar.ModelPath)) return (vroidHub?.Look(homeAvatar.ModelPath)?.Name ?? "A VRoid Hub character") + " (VRoid Hub)";
        if (store is not null && SharedCharacterModels.ForPath(store.DataDirectory, SharedCharacterModels.View(store.DataDirectory), homeAvatar.ModelPath)
            is { } shared)
            return shared.Name!;
        return SharedCharacterModels.NameFor(homeAvatar.ModelPath);
    }

    /// <summary>The key of the shared copy this PC shows (kept even after the character is removed elsewhere, until another is
    /// chosen), or none.</summary>
    private IReadOnlySet<string>? ShownCharacterKeys() =>
        store is not null && SharedCharacterModels.KeyOfPath(store.DataDirectory, homeAvatar?.ModelPath) is { } key
            ? new HashSet<string>([key], StringComparer.OrdinalIgnoreCase) : null;

    private Border CharacterModelsCard()
    {
        var stack = new List<UIElement>
        {
            Heading("Your characters"),
            HelpTip.Explain("Add a Live2D model (its .model3.json) or a VRM model (.vrm). Martlet keeps its own copy and copies it to your " +
                "paired Martlet computers, so any of them can show the same character. The one you use is the one all of them show " +
                "while Martlet is kept the same on all your computers (Devices).",
                new Thickness(0, 0, 0, 10), "Characters", "adding a character")
        };
        var dataDirectory = store?.DataDirectory;
        var library = CharacterModelLibrary.Empty;
        try { if (dataDirectory is not null) library = SharedCharacterModels.View(dataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stack.Add(Note("Martlet couldn't read your characters: " + error.Message, new Thickness(0, 0, 0, 8)));
        }
        var modelPath = homeAvatar?.ModelPath;
        var builtInShown = homeAvatar is null || BundledLive2D.IsBuiltIn(modelPath);
        var shown = dataDirectory is null ? null : SharedCharacterModels.ForPath(dataDirectory, library, modelPath);
        var live = library.Live;
        var status = Note($"{live.Count} character{(live.Count == 1 ? "" : "s")} of your own; " + (builtInShown ? "this PC shows the built-in character."
                : shown is not null ? "this PC shows one of your characters."
                : RemoteCharacterModels.IsRemote(modelPath) ? "this PC shows a VRoid Hub character (below)."
                : SharedCharacterModels.KeyOfPath(dataDirectory ?? "", modelPath) is not null ? "this PC shows a character no longer in your list, so use another one."
                : "this PC shows a model file that isn't in your list yet, and it is added when Martlet next shares your characters."),
            new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "CharacterModelsStatus");
        stack.Add(status);
        var shared = Note(characterModelStatus, new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(shared, "CharacterModelsShared");
        stack.Add(shared);

        stack.Add(CharacterRow(BundledLive2D.DefaultCharacter + " (built-in)", "Live2D \u00b7 part of Martlet on every computer", builtInShown,
            BuiltInCharacterKey, () => UseCharacterAsync(null), null, available: true));
        foreach (var model in live)
        {
            var ready = dataDirectory is not null && SharedCharacterModels.IsComplete(dataDirectory, model);
            var detail = $"{(model.Renderer == CharacterModelLibrary.Vrm ? "VRM" : "Live2D")}, {Size(model.Bytes)} \u00b7 added on {model.AddedBy} " +
                $"{model.AddedAt.ToLocalTime():d}" + (ready ? "" : " \u00b7 copying to this PC...");
            var inUse = shown?.Id == model.Id;
            stack.Add(CharacterRow(model.Name!, detail, inUse, SharedCharacterModels.Key(model.Id), () => UseCharacterAsync(model),
                inUse ? null : () => RemoveCharacterAsync(model), ready));
        }
        stack.Add(Row(PageButton("Add a character...", () => AddCharacterAsync().Forget(), id: "CharacterModelAdd")));
        return Card([.. stack]);

        static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}

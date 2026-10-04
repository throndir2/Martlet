using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;

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
            Note("Add a Live2D model (its .model3.json) or a VRM model (.vrm). Martlet keeps its own copy and copies it to your " +
                "paired Martlet computers, so any of them can show the same character. The one you use is the one all of them show " +
                "while Martlet is kept the same on all your computers (Devices).",
                new Thickness(0, 0, 0, 10))
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
        var status = Note($"{live.Count} character{(live.Count == 1 ? "" : "s")} of your own. " + (builtInShown ? "This PC shows the built-in character."
                : shown is not null ? "This PC shows one of your characters."
                : SharedCharacterModels.KeyOfPath(dataDirectory ?? "", modelPath) is not null ? "This PC shows a character no longer in your list. Use another one."
                : "This PC shows a model file that isn't in your list yet; it is added when Martlet next shares your characters."),
            new Thickness(0, 0, 0, 4));
        AutomationProperties.SetAutomationId(status, "CharacterModelsStatus");
        stack.Add(status);
        var shared = Note(characterModelStatus, new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(shared, "CharacterModelsShared");
        stack.Add(shared);

        stack.Add(CharacterRow(BundledLive2D.DefaultCharacter + " (built-in)", "Live2D. Part of Martlet on every computer.", builtInShown,
            BuiltInCharacterKey, () => UseCharacterAsync(null), null, available: true));
        foreach (var model in live)
        {
            var ready = dataDirectory is not null && SharedCharacterModels.IsComplete(dataDirectory, model);
            var detail = $"{(model.Renderer == CharacterModelLibrary.Vrm ? "VRM" : "Live2D")}, {Size(model.Bytes)}. Added on {model.AddedBy} " +
                $"{model.AddedAt.ToLocalTime():d}." + (ready ? "" : " Copying to this PC...");
            var inUse = shown?.Id == model.Id;
            stack.Add(CharacterRow(model.Name!, detail, inUse, SharedCharacterModels.Key(model.Id), () => UseCharacterAsync(model),
                inUse ? null : () => RemoveCharacterAsync(model), ready));
        }
        stack.Add(Row(PageButton("Add a character...", () => AddCharacterAsync().Forget(), id: "CharacterModelAdd")));
        return Card([.. stack]);

        static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }

    /// <summary>One character: its name (with the in-use mark), a detail line that is readable status
    /// (<c>CharacterModelState-key</c>, never the name), and Use and (when it may go) Remove.</summary>
    private UIElement CharacterRow(string name, string detail, bool inUse, string key, Func<Task> use, Func<Task>? remove, bool available)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name + (inUse ? "  \u00b7  shown on this PC" : ""), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        var state = Note(detail + (inUse ? " Shown on this PC." : ""), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(state, "CharacterModelState-" + key);
        text.Children.Add(state);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Func<Task> run, string id)
        {
            var button = PageButton(label, () => run().Forget(), id: $"CharacterModel{id}-{key}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {name}");
            return button;
        }
        var useButton = Small(inUse ? "In use" : "Use", use, "Use");
        useButton.IsEnabled = !inUse && available;
        buttons.Children.Add(useButton);
        if (remove is not null) buttons.Children.Add(Small("Remove", remove, "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Shows a character on this PC: a shared one (<paramref name="model"/>) or the built-in one (null). Saves it as
    /// this PC's character and switches a character on the desktop over.</summary>
    private async Task UseCharacterAsync(CharacterModel? model)
    {
        if (store is null || setupService is null || closing) return;
        // The character settings window edits this PC's character itself while it is open; saving one here meanwhile would
        // be overwritten by it.
        if (avatarWindowOpen)
        {
            ActionText.Text = "The character settings window is open. Choose the character there, or close it first.";
            return;
        }
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            if (model is not null && !SharedCharacterModels.IsComplete(store.DataDirectory, model))
                throw new InvalidOperationException("That character is still being copied to this PC. Try again in a moment.");
            var pairings = Pairings();
            var (profile, revision) = await pairings.LoadProfileAsync(lifetime.Token);
            var next = profile with
            {
                Renderer = model is null ? AvatarRenderer.Live2D : SharedCharacterModels.Renderer(model),
                ModelPath = model is null ? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter : SharedCharacterModels.EntryPath(store.DataDirectory, model),
                ResourceRevision = null
            };
            if (next != profile) await new AvatarProfileStore(store.DataDirectory).SaveAsync(next, revision, lifetime.Token);
            homeAvatar = next;
            var name = model?.Name ?? BundledLive2D.DefaultCharacter;
            if (avatar.IsShowing && await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
            ActionText.Text = avatar.IsShowing ? $"Martlet now shows '{name}' on this PC." : $"'{name}' is this PC's character. Press Show character to see it.";
            // A character removed elsewhere stays only while this PC shows it.
            QueueCharacterModelSync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            turn?.Dispose();
            if (!closing)
            {
                UpdateCharacterButton();
                RenderHome();
            }
        }
    }

    /// <summary>Adds a character from a model file, then shows it on this PC.</summary>
    private async Task AddCharacterAsync()
    {
        if (store is null || closing) return;
        var added = CharacterModelAddDialog.Add(this, store.DataDirectory, ClusterDevice);
        if (added is null) return;
        QueueCharacterModelSync();
        await UseCharacterAsync(added);
    }

    private async Task RemoveCharacterAsync(CharacterModel model)
    {
        if (store is null || closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Remove '{model.Name}'?\n\nMartlet deletes its copy on this PC and your other Martlet computers. " +
                "A computer that shows it keeps it until another character is chosen there. Your original files aren't touched.", "Remove character"))
            return;
        try
        {
            await SharedCharacterModels.RemoveAsync(store.DataDirectory, model.Id, ClusterDevice, DateTimeOffset.UtcNow, ShownCharacterKeys(), lifetime.Token);
            QueueCharacterModelSync();
            ActionText.Text = $"'{model.Name}' was removed from your characters.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (SharedCharacterModels.IsFailure(error)) { ActionText.Text = error.Message; }
        finally
        {
            if (!closing) RenderHome();
        }
    }

    /// <summary>For the character settings window: a model file chosen there joins the shared list (Martlet's own copy, which
    /// the returned profile shows) so the owner's other computers get it too. The built-in character and shared copies are
    /// returned as they are; a model the list can't take is shown from its file and the note says why.</summary>
    private async Task<(AvatarProfile Profile, string? Note)> ShareCharacterAsync(AvatarProfile profile, CancellationToken token)
    {
        if (store is null || BundledLive2D.IsBuiltIn(profile.ModelPath) || SharedCharacterModels.KeyOfPath(store.DataDirectory, profile.ModelPath) is not null)
            return (profile, null);
        try
        {
            var model = await SharedCharacterModels.ImportAsync(store.DataDirectory, profile.Renderer, profile.ModelPath,
                SharedCharacterModels.NameFor(profile.ModelPath), ClusterDevice, DateTimeOffset.UtcNow, token);
            QueueCharacterModelSync();
            return (profile with { ModelPath = SharedCharacterModels.EntryPath(store.DataDirectory, model) },
                $"'{model.Name}' is now one of your characters, shared with your paired Martlet computers.");
        }
        catch (Exception error) when (SharedCharacterModels.IsFailure(error))
        {
            return (profile, "It isn't shared with your other computers: " + error.Message);
        }
    }

    /// <summary>A model file this PC showed before characters were shared (or chose in the settings window without sharing it)
    /// joins the shared list once, and this PC then shows Martlet's copy (the same files, so nothing visible changes).</summary>
    private async Task ShareShownCharacterAsync(CancellationToken token)
    {
        if (store is null || setupService is null || avatarWindowOpen || changes.Busy) return;
        AvatarProfile profile;
        string? revision;
        try { (profile, revision) = await Pairings().LoadProfileAsync(token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            return;
        }
        if (BundledLive2D.IsBuiltIn(profile.ModelPath) || SharedCharacterModels.KeyOfPath(store.DataDirectory, profile.ModelPath) is not null ||
            unsharedCharacterPaths.Contains(profile.ModelPath) || !File.Exists(profile.ModelPath))
            return;
        var (shared, note) = await ShareCharacterAsync(profile, token);
        if (ReferenceEquals(shared, profile))
        {
            unsharedCharacterPaths.Add(profile.ModelPath);
            if (note is not null) ErrorLog.Info("This PC's character model wasn't added to the shared characters. " + note);
            return;
        }
        if (avatarWindowOpen || changes.TryTake() is not { } turn) return;
        try
        {
            await new AvatarProfileStore(store.DataDirectory).SaveAsync(shared, revision, token);
            homeAvatar = shared;
        }
        catch (ContractException) { }
        finally { turn.Dispose(); }
    }

    /// <summary>Shares the character models through every paired host: merges each host's copy of the list here, copies the
    /// pieces of models this PC lacks from hosts that have them, deletes removed models' copies (not the one this PC shows),
    /// gives each host the merged list and every piece it lacks. Hosts older than shared characters are skipped. Nothing is
    /// written while no host is paired.</summary>
    private async Task SyncCharacterModelsAsync()
    {
        if (characterModelBusy || closing || store is null) return;
        if (!clusterEnabled)
        {
            characterModelStatus = "Keep Martlet the same on all my computers is off, so your characters stay on this PC.";
            return;
        }
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            characterModelStatus = "No other Martlet computers are paired yet, so your characters stay on this PC.";
            return;
        }
        characterModelBusy = true;
        var dataDirectory = store.DataDirectory;
        var token = lifetime.Token;
        var before = characterModelStatus;
        var changed = false;
        var connections = new Dictionary<string, Audio2FaceHostConnection>(StringComparer.Ordinal);
        Audio2FaceHostConnection Connection(PairedHost host) =>
            connections.TryGetValue(host.HostId, out var open) ? open : connections[host.HostId] = ClusterSync.Connect(host.Pairing);
        try
        {
            await ShareShownCharacterAsync(token);
            var copies = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    var copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadCharacterModelsAsync(token));
                    return (Host: host, Copy: (HostCharacterModels?)copy, Old: false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    return (host, null, true);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    return (host, null, false);
                }
            }));
            var reachable = copies.Where(c => c.Copy is not null).ToArray();
            var digest = SharedCharacterModels.View(dataDirectory).Digest();
            SharedCharacterModels.Commit(dataDirectory, reachable.Aggregate(CharacterModelLibrary.Empty,
                (merged, c) => CharacterModelLibrary.Merge(merged, c.Copy!.Library)));

            async Task<byte[]?> FetchAsync(string sha256, CancellationToken fetchToken)
            {
                foreach (var (host, _, _) in reachable.Where(c => c.Copy!.Present.Contains(sha256)))
                {
                    try
                    {
                        if (await Connection(host).ReadCharacterModelChunkAsync(sha256, fetchToken) is { } data) return data;
                    }
                    catch (OperationCanceledException) when (fetchToken.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error)) { }
                }
                return null;
            }
            var result = await SharedCharacterModels.ReconcileAsync(dataDirectory, FetchAsync, ShownCharacterKeys(), token);
            var library = result.Library;
            changed = result.Added > 0 || result.Removed > 0 || library.Digest() != digest;

            var shared = 0;
            foreach (var (host, copy, _) in reachable)
            {
                try
                {
                    var present = copy!.Present;
                    if (copy.Library.Digest() != library.Digest())
                    {
                        var merged = await Connection(host).MergeCharacterModelsAsync(library, token);
                        library = SharedCharacterModels.Commit(dataDirectory, merged.Library);
                        present = merged.Present;
                    }
                    foreach (var model in library.Live.Where(m => result.Local.Contains(m.Id)))
                    foreach (var sha256 in model.Files!.SelectMany(f => f.Chunks).Distinct(StringComparer.Ordinal).Where(s => !present.Contains(s)).ToArray())
                    {
                        if (await SharedCharacterModels.ReadChunkAsync(dataDirectory, model, sha256, token) is not { } data) continue;
                        present = await Connection(host).SendCharacterModelChunkAsync(sha256, data, token);
                    }
                    shared++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error) || SharedCharacterModels.IsFailure(error)) { }
            }
            var old = copies.Where(c => c.Old).Select(c => c.Host.HostId).ToArray();
            characterModelStatus = $"Characters shared with {shared} of {hosts.Count} computer{(hosts.Count == 1 ? "" : "s")} at {DateTime.Now:t}." +
                (result.Waiting > 0 ? $" {result.Waiting} character{(result.Waiting == 1 ? " is" : "s are")} still copying to this PC." : "") +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} to share characters there." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (SharedCharacterModels.IsFailure(error) || ClusterSync.IsHostFailure(error))
        {
            characterModelStatus = "Couldn't share characters just now: " + error.Message;
        }
        finally
        {
            foreach (var connection in connections.Values) connection.Dispose();
            characterModelBusy = false;
            // Re-render when something changed, not merely the time of the last check.
            static string Gist(string text) => System.Text.RegularExpressions.Regex.Replace(text, @" at [^.]+\.", ".");
            if (!closing && openTab == CompanionTab.Character && !CompanionContent.IsKeyboardFocusWithin &&
                (changed || Gist(before) != Gist(characterModelStatus)))
                RenderTab();
        }
    }
}

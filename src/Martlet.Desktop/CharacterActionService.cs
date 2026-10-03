using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The emotes and motions of the character this PC shows (Companion › Character › Emotes and motions): read from
/// the model's files, with the owner's settings for that model, named by the Thinking model once per new model (and again on
/// request), and saved per model in character-actions.json.</summary>
internal sealed class CharacterActionService(string? dataDirectory)
{
    private readonly SemaphoreSlim loading = new(1, 1);
    private readonly HashSet<string> autoNamed = new(StringComparer.Ordinal);
    private CharacterActionCatalog? catalog;
    private string? path;
    private string? problem;
    private string? naming;
    private bool busy;

    /// <summary>Raised (on any thread) when the catalog, its settings or the naming status change.</summary>
    internal event Action? Changed;

    /// <summary>The model path whose emotes and motions are loaded (or couldn't be read), or null.</summary>
    internal string? Path => Volatile.Read(ref path);
    internal CharacterActionCatalog? Current => Volatile.Read(ref catalog);
    /// <summary>Why the model's emotes and motions couldn't be read, or null.</summary>
    internal string? Problem => Volatile.Read(ref problem);
    /// <summary>How naming with the Thinking model went (or is going), or null before it was asked.</summary>
    internal string? Naming => Volatile.Read(ref naming);
    internal bool Busy => Volatile.Read(ref busy);

    /// <summary>The catalog of the model at <paramref name="modelPath"/> when it is the one loaded, otherwise null.</summary>
    internal CharacterActionCatalog? For(string? modelPath) =>
        modelPath is not null && string.Equals(Path, modelPath, StringComparison.OrdinalIgnoreCase) ? Current : null;

    /// <summary>Reads the emotes and motions of <paramref name="profile"/>'s model (the built-in character when null) unless
    /// they are already loaded.</summary>
    internal async Task<CharacterActionCatalog?> LoadAsync(AvatarProfile? profile, bool force, CancellationToken token)
    {
        var modelPath = profile?.ModelPath ?? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter;
        var renderer = profile?.Renderer ?? AvatarRenderer.Live2D;
        await loading.WaitAsync(token);
        try
        {
            if (!force && string.Equals(Path, modelPath, StringComparison.OrdinalIgnoreCase)) return Current;
            CharacterActionCatalog? next = null;
            string? why = null;
            try
            {
                var inventory = await Task.Run(() => CharacterActionInventory.ReadAsync(renderer, modelPath, token), token);
                var saved = dataDirectory is null ? null : CharacterActions.Load(dataDirectory, inventory.ModelId);
                next = new(inventory, CharacterActions.Merge(inventory, saved));
            }
            catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                why = error.Message;
            }
            Volatile.Write(ref catalog, next);
            Volatile.Write(ref problem, why);
            Volatile.Write(ref naming, null);
            Volatile.Write(ref path, modelPath);
        }
        finally { loading.Release(); }
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Saves the settings of one model (normally the loaded one; naming that finishes after another model was chosen
    /// still keeps its result for that model); returns why they couldn't be saved, or null.</summary>
    internal async Task<string?> SaveAsync(CharacterActionSettings settings, CancellationToken token)
    {
        if (dataDirectory is null) return "Martlet's data folder isn't available.";
        if (CharacterActions.Problem(settings) is { } why) return why;
        try
        {
            var saved = await CharacterActions.SaveAsync(dataDirectory, settings, DateTimeOffset.Now, token);
            if (Current is { } current && current.Inventory.ModelId == saved.ModelId) Volatile.Write(ref catalog, current with { Settings = saved });
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>Goes back to the names from the model's own files, forgetting the owner's edits and the Thinking model's names.</summary>
    internal Task<string?> ResetAsync(CancellationToken token) => Current is { } current
        ? SaveAsync(CharacterActions.Merge(current.Inventory, null), token)
        : Task.FromResult<string?>("No character is loaded.");

    /// <summary>Whether the loaded model should be named by the Thinking model on its own: never named by it and not tried
    /// yet since Martlet started. Marks it tried.</summary>
    internal bool ClaimAutomaticNaming()
    {
        lock (autoNamed)
            return Current is { } current && current.Settings.DetectedBy != CharacterActionSettings.ByThinking &&
                CharacterActions.Nameable(current.Inventory).Count > 0 && autoNamed.Add(current.Inventory.ModelId);
    }

    /// <summary>Asks the Thinking model (through <paramref name="ask"/>: purpose, instructions, list) what each of the loaded
    /// model's emotes and motions is, and saves its names, cues and when to use each. Returns what happened, in words.</summary>
    internal async Task<string> NameAsync(Func<string, string, string, CancellationToken, Task<(string? Answer, string? Failure)>> ask,
        PromptSettings? prompts, CancellationToken token)
    {
        if (Current is not { } current) return Report("Show or choose a character first.");
        if (CharacterActions.NamingPrompt(current.Inventory, prompts) is not { } request)
            return Report(CharacterActions.Nameable(current.Inventory).Count == 0
                ? "This model has no emotes or motions of its own to name; Martlet's nod and shake still work."
                : "Naming is off: Companion › Prompts › Naming character emotes is empty.");
        Volatile.Write(ref busy, true);
        Report("Asking the Thinking model what each emote and motion is...");
        try
        {
            var (answer, failure) = await ask("Naming character emotes", request.Instructions, request.List, token);
            var named = CharacterActions.Parse(answer, current.Inventory, current.Settings, DateTimeOffset.Now);
            if (named is null)
                return Report(failure is null
                    ? "The Thinking model's answer couldn't be read, so the names from the model's files stay. Try again or edit them below."
                    : $"Couldn't ask the Thinking model ({failure}), so the names from the model's files stay.");
            if (await SaveAsync(named, token) is { } why) return Report("The Thinking model named them, but they couldn't be saved: " + why);
            var nameable = CharacterActions.Nameable(current.Inventory);
            var off = named.Actions.Count(a => !a.Enabled && nameable.Any(s => s.Id == a.Id));
            ErrorLog.Info($"The Thinking model named {nameable.Count} character emotes and motions ({off} turned off).");
            return Report($"The Thinking model named {nameable.Count} emote{(nameable.Count == 1 ? "" : "s")} and motion" +
                $"{(nameable.Count == 1 ? "" : "s")} at {DateTime.Now:t}" + (off > 0 ? $" and turned off {off} that aren't feelings or gestures." : "."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Report("Naming was stopped."); }
        finally { Volatile.Write(ref busy, false); Changed?.Invoke(); }
    }

    private string Report(string text)
    {
        Volatile.Write(ref naming, text);
        Changed?.Invoke();
        return text;
    }
}

using Martlet.Core.Sync;

namespace Martlet.Avatar.Hosting;

/// <summary>What a computer learned or was told about each character model travels to the owner's other computers as shared
/// settings (docs/CLUSTER.md, "One Martlet on every computer"): its touch zones (<see cref="CharacterTouchZones.SharedKey"/>), its
/// eyes' measurement (<see cref="CharacterEyes.SharedKey"/>), the VRoid Hub credit of a VRoid Hub character
/// (<see cref="VRoidHubLooks.SharedKey"/>), and the character's own changes to how it reacts to touches
/// (<see cref="CharacterReactionChanges.SharedKey"/>, each person's). The pictures beside the zones and measurements stay on the
/// computer that made them. The colors made from a model's textures (character-themes.json) stay on each computer: each one makes
/// them again from the shared model's files, without any model call.</summary>
public static class CharacterSharedSections
{
    /// <summary>What the desktop does around a section: <c>Waiting</c> gives why this computer can't take another computer's value
    /// now (for example while it finds the zones itself), or null; <c>Followed</c> runs after it took one.</summary>
    public sealed record Hooks(Func<string?>? Waiting = null, Action? Followed = null);

    public static ISharedSection TouchZones(string directory, Hooks? hooks = null) => Section(CharacterTouchZones.SharedKey, "Touch zones",
        CharacterTouchZones.Path(directory), () => CharacterTouchZones.Share(directory), () => CharacterTouchZones.LoadAll(directory).Count == 0,
        (value, token) => CharacterTouchZones.ReplaceAllAsync(directory, value, token), hooks, defaultValue: null);

    public static ISharedSection Eyes(string directory, Hooks? hooks = null) => Section(CharacterEyes.SharedKey, "Characters' eyes",
        CharacterEyes.Path(directory), () => CharacterEyes.Share(directory), () => CharacterEyes.LoadAll(directory).Count == 0,
        (value, token) => CharacterEyes.ReplaceAllAsync(directory, value, token), hooks, defaultValue: null);

    public static ISharedSection VRoidHub(string directory, Hooks? hooks = null) => Section(VRoidHubLooks.SharedKey, "VRoid Hub credits",
        Path.Combine(directory, VRoidHubLooks.FileName), () => VRoidHubLooks.Share(directory), () => VRoidHubLooks.Load(directory).Count == 0,
        (value, _) => Task.FromResult(VRoidHubLooks.ReplaceAll(directory, value)), hooks, defaultValue: null);

    /// <summary>Each person's: an account that never had any starts with none.</summary>
    public static ISharedSection ReactionChanges(string directory, Hooks? hooks = null) => Section(CharacterReactionChanges.SharedKey,
        "Changes the character made to its reactions", CharacterReactionChanges.Path(directory), () => CharacterReactionChanges.Share(directory),
        () => CharacterReactionChanges.Load(directory).Count == 0, (value, token) => CharacterReactionChanges.ReplaceAllAsync(directory, value, token),
        hooks, defaultValue: () => CharacterReactionChanges.Share(Path.Combine(directory, AccountWorkingCopy.DirectoryName, "no-reaction-changes")));

    /// <summary>The household's per-model sections, then each person's.</summary>
    public static IReadOnlyList<ISharedSection> All(string directory, Hooks? touchZones = null, Hooks? eyes = null, Hooks? reactionChanges = null) =>
        [TouchZones(directory, touchZones), Eyes(directory, eyes), VRoidHub(directory), ReactionChanges(directory, reactionChanges)];

    private static ISharedSection Section(string key, string title, string path, Func<string> share, Func<bool> empty,
        Func<string, CancellationToken, Task<bool>> replace, Hooks? hooks, Func<string?>? defaultValue) => new DelegateSection(key, title,
        _ =>
        {
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new(share(), null, empty(), changed));
        },
        async (setting, token) =>
        {
            if (hooks?.Waiting?.Invoke() is { } note) return SharedApply.Waiting(note);
            if (string.Equals(setting.Value, share(), StringComparison.Ordinal)) return SharedApply.Done;
            try
            {
                if (!await replace(setting.Value, token).ConfigureAwait(false))
                    return SharedApply.Waiting("It was saved by a newer Martlet. Update this PC to use it.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                return SharedApply.Waiting("It couldn't be saved on this PC: " + error.Message);
            }
            hooks?.Followed?.Invoke();
            return SharedApply.Done;
        }, defaultValue);
}

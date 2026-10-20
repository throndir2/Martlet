using Martlet.Core.Access;
using Martlet.Core.Cluster;
using Martlet.Core.Motion;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Sync;

/// <summary>The household settings backed by one Core store each (#955): the same read, apply and default pattern
/// (<see cref="FileSection.Store"/>) for every one. What the app does after it took a value (the conversation reloads, a page
/// draws again) happens after the sync, by key.</summary>
public static class StoreSections
{
    /// <summary>What Thinking models were found to hear and see: found out once, on whichever computer, for all of them.</summary>
    public static ISharedSection ModelAbilities(string directory) => FileSection.Store(Settings.ModelAbilities.SharedKey,
        "What Thinking models hear and see", [Path.Combine(directory, Settings.ModelAbilities.FileName)],
        () => Settings.ModelAbilities.Load(directory), s => s.Share(), s => s.Models.Count == 0, Settings.ModelAbilities.Parse, s => s.Save(directory));

    /// <summary>Devices › Sharing work: which computers each job may go to and which are kept for one companion PC.</summary>
    public static ISharedSection WorkSharing(string directory) => FileSection.Store(WorkSharingSettings.SharedKey, "Sharing work",
        [Path.Combine(directory, WorkSharingSettings.FileName)], () => WorkSharingSettings.Load(directory), s => s.Share(), s => s.IsDefault,
        WorkSharingSettings.Parse, s => s.Save(directory));

    /// <summary>Where Martlet makes motions (motion.json: automatic, off or one computer's Motion role).</summary>
    public static ISharedSection Motion(string directory) => FileSection.Store(MotionSettings.SharedKey, "Motion",
        [Path.Combine(directory, MotionSettings.FileName)], () => MotionSettings.Load(directory), s => s.Share(), s => s.IsDefault,
        MotionSettings.Parse, s => s.Save(directory));

    /// <summary>Share › Share your language models: the address people use and the host the proxy sends it to.</summary>
    public static ISharedSection OpenAiEndpoint(string directory) => FileSection.Store(OpenAiEndpointSettings.SharedKey, "Language models address",
        [Path.Combine(directory, OpenAiEndpointSettings.FileName)], () => OpenAiEndpointSettings.Load(directory), s => s.Share(), s => s.IsDefault,
        OpenAiEndpointSettings.Parse, s => s.Save(directory));

    /// <summary>The recommendation preferences; each computer's games answer travels in the same document, by its device ID.</summary>
    public static ISharedSection RecommendationPreferences(string directory) => FileSection.Store(Planning.RecommendationPreferences.SharedKey,
        "Recommended setup preferences", [Path.Combine(directory, Planning.RecommendationPreferences.FileName)],
        () => Planning.RecommendationPreferences.Load(directory), s => s.Share(), _ => !Planning.RecommendationPreferences.Saved(directory),
        Planning.RecommendationPreferences.Parse, s => s.Save(directory));

    /// <summary>The pool lists of the areas that are the same on all computers (pools.json).</summary>
    public static ISharedSection Pools(string directory) => FileSection.Store(PoolSettings.SharedKey, "Pools",
        [Path.Combine(directory, PoolSettings.FileName)], () => PoolSettings.Load(directory), s => s.Share(), s => s.IsDefault,
        PoolSettings.Parse, s => s.Save(directory));

    /// <summary>Every section here, in the order the desktop shares them.</summary>
    public static IReadOnlyList<ISharedSection> All(string directory) =>
        [ModelAbilities(directory), WorkSharing(directory), Motion(directory), OpenAiEndpoint(directory), RecommendationPreferences(directory), Pools(directory)];
}

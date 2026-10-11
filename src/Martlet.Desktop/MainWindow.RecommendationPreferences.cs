using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>Settings › Recommended setup preferences: the owner's recommendation preferences (docs/RECOMMENDATION_DESIGN.md) with
/// the same controls as Recommended setup's Your preferences (<see cref="RecommendationPreferencesControls"/>, automation IDs
/// with the Recommendation prefix), so the owner can change them without opening Recommended setup. A change is saved and
/// shared with the other computers; the next Recommended setup plans with it.</summary>
public partial class MainWindow
{
    /// <summary>Draws the card; <paramref name="status"/> is its line under the controls (null: whether they were ever changed).</summary>
    private void RenderRecommendationPreferences(string? status = null)
    {
        var directory = store?.DataDirectory;
        var sources = RecommendedSetupSources();
        RecommendationPreferencesPanel.Children.Clear();
        RecommendationPreferencesPanel.Children.Add(RecommendationPreferencesControls.Build("Recommendation", sources.Preferences,
            RecommendedSetupInputs.GamesComputers(sources), preferences => SaveRecommendationPreferences(preferences, reopen: false)));
        RecommendationPreferencesStatus.Text = status ?? (RecommendationPreferences.Saved(directory) ? ""
            : "You haven't changed these yet, so Martlet uses its defaults.");
    }
}

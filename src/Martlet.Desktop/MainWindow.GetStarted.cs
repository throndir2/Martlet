using System.Windows;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Home's Get started card (<see cref="GetStarted"/>): the main flow in three steps, each ticked from what Martlet already
/// knows (the saved routes, the character profiles and the conversations). It hides once all three are done.</summary>
public partial class MainWindow
{
    private bool talkedHere;

    private void RenderGetStarted(IReadOnlyList<HealthIssue> issues)
    {
        if (GetStartedCard is null) return;
        var state = GetStartedNow(issues);
        if (GetStarted.Done(state))
        {
            GetStartedCard.Visibility = Visibility.Collapsed;
            return;
        }
        GetStartedCard.Visibility = Visibility.Visible;
        GetStartedSummary.Text = GetStarted.Summary(state);
        RenderSteps(GetStartedPanel, GetStarted.Steps(state, new GetStartedActions(
            () => OpenRecommendedSetupAsync().Forget(),
            () => RunNodeAction(NodeAction.AddComputer),
            () => OpenCompanion(CompanionTab.Profiles),
            OpenCharacterFinder,
            () => OpenCompanion(CompanionTab.Character),
            () => OpenCompanion(CompanionTab.Voice),
            () => Conversation_Click(this, new RoutedEventArgs()),
            () => Listen_Click(this, new RoutedEventArgs()))), numbered: true);
    }

    private GetStartedState GetStartedNow(IReadOnlyList<HealthIssue> issues)
    {
        var routes = homeSettings?.Setup?.Routes ?? [];
        var thinking = homeSettings.ThinkingRoute();
        var listening = routes.FirstOrDefault(r => r.Role == SetupRole.Stt);
        var speaking = routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var replies = !issues.Any(i => i.Level == HealthLevel.Problem);
        string Job(string name, SetupRoute? route) => $"{name}: {(route is null ? "not set up" : PlaceName(route))}";
        var servers = string.Join(" · ", Job("Thinking", thinking), Job("Listening", listening), Job("Voice", speaking)) + ".";

        var companion = homeSettings?.Companion;
        var persona = companion?.ActivePersona.Name ?? "Martlet";
        var profile = companion?.ActiveCharacterId is { } id ? companion.CharacterList.FirstOrDefault(c => c.Id == id) : null;
        var character = profile is not null ? $"Martlet is {profile.Name} now." : persona == "Martlet" ? "" : $"It is {persona} now.";

        if (openConversation is not null) talkedHere = true;
        var talked = talkedHere;
        if (!talked)
        {
            try { talked = conversationHistory?.Store.Conversations(1).Count > 0; }
            catch (InvalidOperationException) { talked = false; }
        }
        return new GetStartedState(thinking is not null && listening is not null && speaking is not null && replies, servers,
            companion?.CharacterList.Count > 0, character, talked, thinking is not null && replies);
    }
}

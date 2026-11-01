using System.Windows.Threading;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Hints and secrets (docs/APP_GUIDES.md, "Hints and secrets"): <see cref="GameHints"/> for the game in front, with the
/// place and goal from the game state, the user's preferences, the App guides and their research, Thinking pool Research jobs,
/// and the talk window, which brings each hint up at a quiet moment like a check-in. Owns only the hint keeper's lifetime; the
/// talk window decides the moment and the reply says the hint in character.</summary>
public partial class MainWindow
{
    private GameHints? gameHints;

    /// <summary>Starts the hints when the conversation, the App guides and their inputs are there (once).</summary>
    private void StartGameHints(IGamePlaceSource places, IHintPreferences preferences)
    {
        if (conversation is not { } talk || appGuides is not { } guides || gameHints is not null) return;
        var hints = new GameHints(places, preferences, new AppGuideHintGuide(guides, talk.GuideResearch),
            new PoolGuideThinker(() => talk.ThinkingPool), new TalkWindowHintSpeaker(this)) { Log = ErrorLog.Info };
        hints.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (!closing && openTab == CompanionTab.AppGuides) appGuidesPage?.RenderStatus();
        });
        gameHints = hints;
    }

    private void StopGameHints() => Interlocked.Exchange(ref gameHints, null)?.Dispose();

    /// <summary>The talk window that runs: a quiet moment there (<see cref="LiveConversationWindow.HintMoment"/>) and the hint
    /// brought up as a check-in's notice. No talk window, no hint.</summary>
    private sealed class TalkWindowHintSpeaker(MainWindow window) : IHintSpeaker
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        public bool Quiet => window.Dispatcher.Invoke(() => !window.closing && window.openConversation is { HintMoment: true },
            DispatcherPriority.Background, CancellationToken.None, Wait);

        public Task<bool> SayAsync(GameHint hint, CancellationToken cancellationToken) =>
            window.Dispatcher.InvokeAsync(() => !window.closing && window.openConversation is { HintMoment: true } talk &&
                talk.BringUp(GameHints.Label, GameHints.Words(hint)) is not null, DispatcherPriority.Normal, cancellationToken).Task;
    }
}

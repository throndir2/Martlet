using System.Windows;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Personality (<see cref="PersonalityPage"/> in Pages\Personality) and
/// Companion › Memory (<see cref="MemoryPage"/> in Pages\Memory).</summary>
public partial class MainWindow : IPersonalityPageServices, IMemoryPageServices
{
    private PersonalityPage? personalityPage;
    private MemoryPage? memoryPage;

    private PersonalityPage PersonalityPage => personalityPage ??= new(this);
    private MemoryPage MemoryPage => memoryPage ??= new(this);

    AppSettings? IPersonalityPageServices.Settings => homeSettings;
    UIElement IPersonalityPageServices.ProfilesLinkCard() => ProfilesLinkCard();
    void IPersonalityPageServices.OpenPersonality(bool importCard)
    {
        if (importCard) OpenCompanionWindowAsync(importCard: true).Forget();
        else Companion_Click(this, new RoutedEventArgs());
    }
    void IPersonalityPageServices.OpenPrompts() => OpenCompanion(CompanionTab.Prompts);

    AppSettings? IMemoryPageServices.Settings => homeSettings;
    DesktopConversationHistory? IMemoryPageServices.History => conversationHistory;
    UIElement? IMemoryPageServices.MemoryBriefCard(bool memoryOn) =>
        conversation?.MemoryBrief is { } brief ? MemoryBriefCard(brief, memoryOn) : null;
    UIElement? IMemoryPageServices.MeaningSearchCard() => conversation?.Meaning is { } meaning ? MeaningSearchCard(meaning) : null;
    void IMemoryPageServices.OpenMemory() => Memory_Click(this, new RoutedEventArgs());
    void IMemoryPageServices.OpenHistory() => History_Click();
    void IMemoryPageServices.QueueSettingsSync() => QueueSettingsSync();
}

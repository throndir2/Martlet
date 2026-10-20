using System.Windows;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Memory (<see cref="MemoryPage"/>) reads and starts in the app. MainWindow gives it to the page
/// (MainWindow.MemoryPage.cs). The Memory brief and Finding by meaning cards stay in MainWindow (MainWindow.MemoryBrief.cs,
/// MainWindow.MeaningSearch.cs), which makes them for the page.</summary>
internal interface IMemoryPageServices
{
    /// <summary>The saved settings (memory on or off), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>The record of conversations, or null when this PC has none.</summary>
    DesktopConversationHistory? History { get; }

    /// <summary>The Memory brief card, or null while the conversation has no memory brief.</summary>
    UIElement? MemoryBriefCard(bool memoryOn);

    /// <summary>The Finding by meaning card, or null while the conversation has no meaning search.</summary>
    UIElement? MeaningSearchCard();

    /// <summary>Opens the Memory window (Manage memory).</summary>
    void OpenMemory();

    /// <summary>Opens the Conversations page.</summary>
    void OpenHistory();

    /// <summary>Lets your other computers take a changed shared setting on their next check.</summary>
    void QueueSettingsSync();
}

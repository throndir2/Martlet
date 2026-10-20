using System.Windows;
using System.Windows.Automation;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Martlet.Core.Settings;
using Martlet.Core.Lorebooks;
using Martlet.Core.Installation;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Credentials.Windows;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.xaml.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartAmbientMotion();
        StartSimulatedTask();
        StartSimulatedRequests();
        await StartRunningAsync();
    }

    private bool started;

    /// <summary>Everything Martlet does once it runs: status, the saved character, sync, the node agent, logs and updates. It
    /// runs when the window first shows, or straight away when Martlet starts in the notification area.</summary>
    private async Task StartRunningAsync()
    {
        if (started) return;
        started = true;
        // A host PC stays awake from the start, before its first network sync or host check (after a Wake-on-LAN wake, Windows
        // sleeps again within minutes otherwise).
        UpdateStayAwake();
        // A switch between accounts cut short at the last start is finished before the character and the conversation load.
        await FinishAccountSettingsAsync();
        ReadMachineAsync().Forget();
        await RefreshAsync();
        if (!closing) ContinueSetupAsync().Forget();
        // A host PC starts Docker Desktop and its host roles by itself, warm for the first request.
        if (!closing && Role == DeviceRole.Host && deviceRole is not null) StartHostRolesByItself();
        // A host lends its power to your companion PC: the character, listening and Parakeet stay off here, while their
        // saved choices are kept for when this PC is your companion PC again.
        if (Role == DeviceRole.Companion)
        {
            await ShowSavedCharacterAsync(onlyIfAutoShow: true);
            if (background.StartCompanion && !closing) await StartCompanionAsync();
            // Messaging apps (Companion › Messaging): the paired chats reach Martlet whenever it runs on this companion PC.
            if (!closing) messaging.Start();
        }
        else ErrorLog.Info("Martlet started as a Martlet host: the character and listening stay off on this PC" +
            (background.StartCompanion ? " (When Martlet starts, show the character and start listening is kept for when it's your companion PC)." : "."));
        // An update Martlet just restarted into brings back the character, listening and watching that were on when it closed for it.
        if (!closing) await ResumeAfterUpdateAsync();
        StartCluster();
        StartNodePresence();
        StartSituations();
        StartSettingsSync();
        StartSettingsWatch();
        StartHouseholdSharing();
        StartReminders();
        StartCheckIns();
        StartMemorySync();
        StartConversationSync();
        StartNetwork();
        StartAccounts();
        StartApiKeys();
        StartFriends();
        StartRouterPort();
        StartVoiceSync();
        StartSpeakingVoices();
        StartCharacterModels();
        StartCreations();
        StartModelCatalog();
        StartHomeShare();
        InitializeDiscordCompanion();
        discord.StartIfEnabledAsync(lifetime.Token).Forget();
        StartNodeAgent();
        StartLogSharing();
        // Parakeet takes a few seconds to load; do it now rather than on the first thing said.
        WarmListening();
        // Martlet is usable now; this PC's own host service follows its version in the background (after an update, right away).
        FollowOwnHostAsync().Forget();
        if (!closing) await StartUpdatesAsync();
    }
    private async Task RefreshAsync()
    {
        if (model is null)
        {
            ActionText.Text = startupError ?? "";
            return;
        }
        if (!closing)
        {
            await model.RefreshAsync();
            support.ObserveReport(model.Report, record: true);
        }
        if (!closing) await RefreshHomeAsync(SettingsChangeSource.File);
    }

    private void Render()
    {
        if (closing || model is null)
            return;
        support.ObserveReport(model.Report);
        ConversationButton.IsEnabled = PrimaryStageButton.IsEnabled = !model.IsRunning;
    }

    private async void AudioSetup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || model?.IsRunning == true) return;
        new AudioSetupWindow(setupService!, setupOperations, audioSetup, sessionEvents: audioSessionEvents)
            { Owner = this, Troubleshooting = OpenTroubleshooting }.ShowDialog();
        // Closing can leave its last save or device listing finishing; a refresh now would be skipped and show stale status.
        for (var i = 0; i < 50 && setupOperations.IsRunning && !closing; i++) await Task.Delay(100);
        await RefreshAsync();
    }

    private async void Companion_Click(object sender, RoutedEventArgs e) => await OpenCompanionWindowAsync(importCard: false);

    private async Task OpenCompanionWindowAsync(bool importCard)
    {
        if (companionService is null || closing || model?.IsRunning == true) return;
        var personalities = homeSettings?.Companion?.Personas.ToDictionary(p => p.Id, p => p.Text);
        new CompanionWindow(companionService, setupOperations, importCardOnOpen: importCard, lorebooks: lorebooks) { Owner = this }.ShowDialog();
        homeLore = null;
        // The window's last save can still be finishing; the refresh would be skipped and miss a changed personality.
        for (var i = 0; i < 50 && setupOperations.IsRunning && !closing; i++) await Task.Delay(100);
        await RefreshAsync();
        PersonalitiesSaved(personalities);
    }

    private async Task OpenLorebooksAsync(bool import = false)
    {
        if (lorebooks is null || closing || model?.IsRunning == true) return;
        new LorebookWindow(lorebooks, homeSettings?.Companion, importOnOpen: import) { Owner = this }.ShowDialog();
        homeLore = null;
        await RefreshAsync();
        if (!closing && openTab == CompanionTab.Lorebook) RenderTab();
    }

    private async void Memory_Click(object sender, RoutedEventArgs e) => await OpenMemoryAsync();

    /// <summary>Opens Memory, showing the facts of <paramref name="person"/> (a voice ID, from People) when given.</summary>
    private async Task OpenMemoryAsync(string? person = null)
    {
        if (memory is null || closing || model?.IsRunning == true) return;
        memoryWindowOpen = true;
        try { new MemoryWindow(memory, setupOperations, voices: () => localVoices.Roster, person: person, yours: localVoices.IsYours) { Owner = this, Sharing = MemorySharingOptionsNow() }.ShowDialog(); }
        finally { memoryWindowOpen = false; }
        QueueMemorySync();
        await RefreshAsync();
    }

    /// <summary>Companion › Memory › Open conversations: goes to the Conversations page, to read, search, edit and delete the record.</summary>
    private void History_Click()
    {
        if (conversationHistory is null || closing) return;
        Navigate(NavConversations);
    }

    /// <summary>Opens the talk window beside Martlet (modeless, so Home, Companion and the rest stay usable while you talk), or
    /// brings it back to the front when it is already open or runs hidden while Martlet listens.</summary>
    private void Conversation_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationSession() is not { } window) return;
        window.ShowInFront();
        UpdateTray();
    }

    /// <summary>Home's Start talking reads Show conversation while a conversation runs (shown or hidden).</summary>
    private void RenderConversationButton()
    {
        var open = openConversation is not null;
        ConversationButton.Content = open ? "Show conversation" : "Start talking";
        AutomationProperties.SetName(ConversationButton, open ? "Show conversation" : "Start talking");
    }

    private void VoiceLibrary_Click(object sender, RoutedEventArgs e) => OpenCompanion(CompanionTab.Voice);

    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => OpenTroubleshooting(this);
    private AdvisorAnswers? advisorAnswers;
    private void Advisor_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        var advisor = new SetupAdvisorWindow(advisorAnswers, DetectThisPcGpu(), PairedHostsForAdvisor()) { Owner = this };
        advisor.ShowDialog();
        advisorAnswers = advisor.Answers;
        switch (advisor.RequestedStep)
        {
            case AdvisorNextStep.Prerequisites:
                var missing = SetupAdvisor.Recommend(advisor.Answers).ThisPcInstalls.Select(Prerequisites.For).Where(Prerequisites.IsMissing).ToArray();
                if (missing.Length == 0) ActionText.Text = "This PC already has everything needed for that plan.";
                else InstallPrerequisitesAsync(missing).Forget();
                break;
            case AdvisorNextStep.Setup: OpenCompanion(CompanionTab.Thinking); break;
            case AdvisorNextStep.AudioSetup: AudioSetup_Click(sender, e); break;
            case AdvisorNextStep.Hosts: Hosts_Click(sender, e); break;
            case AdvisorNextStep.VoiceLibrary: VoiceLibrary_Click(sender, e); break;
            case AdvisorNextStep.Character: Avatar_Click(sender, e); break;
        }
    }
    private void Avatar_Click(object sender, RoutedEventArgs e) => OpenAvatar(this);

    /// <summary>This PC's graphics card, read locally from Windows (no probing), as an advisor answer.</summary>
    private (AdvisorGpu Gpu, string Text)? DetectThisPcGpu()
    {
        var info = machine;
        if (ReferenceEquals(info, MachineInfo.Unknown))
        {
            try { info = MachineInfo.Read(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception) { return null; }
        }
        if (info.BestGpu is not { } gpu) return (AdvisorGpu.None, "No dedicated graphics card found");
        var answer = SetupAdvisor.Classify(gpu.Name, gpu.MemoryGb);
        return (answer, answer == AdvisorGpu.None ? $"{gpu.Name} (not dedicated)" : gpu.Describe());
    }

    /// <summary>Paired hosts (other than this PC) with the hardware they last reported.</summary>
    private IReadOnlyList<AdvisorComputer> PairedHostsForAdvisor()
    {
        if (store is null) return [];
        return new HostHardwareStore(store.DataDirectory).Load()
            .Where(host => !Uri.TryCreate(host.Origin, UriKind.Absolute, out var origin) || origin.Host != machine.LanAddress)
            .Select(host => new AdvisorComputer(host.AdvisorGpu, host.HostId, HostsWindow.DescribeHardware(host)))
            .ToArray();
    }
    private void Hosts_Click(object sender, RoutedEventArgs e) => OpenHosts(0);
    /// <summary>Shows the prerequisites checklist in Martlet; it changes nothing until the user ticks and installs items.</summary>
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => ChoosePrerequisitesAsync().Forget();

    private async Task ChoosePrerequisitesAsync()
    {
        if (closing) return;
        if (await Prerequisites.ChooseAndInstallAsync(this) is { } status && !closing) ActionText.Text = status;
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>Installs prerequisites in a run window (no console) and reports the outcome on the home screen.</summary>
    private async Task InstallPrerequisitesAsync(IReadOnlyCollection<Prerequisite> items, string? ollamaModel = null)
    {
        if (closing) return;
        ActionText.Text = $"Installing {string.Join(", ", items.Select(i => i.Title))}. The progress window shows details.";
        var status = await Prerequisites.InstallAsync(this, items, ollamaModel);
        if (closing) return;
        ActionText.Text = status;
        await ReadMachineAsync();
    }
    private bool togglingCharacter;
    private async void Character_Click(object sender, RoutedEventArgs e)
    {
        if (togglingCharacter) return;
        togglingCharacter = true;
        try
        {
            if (avatar.IsShowing)
            {
                if (await StopAvatarSafelyAsync())
                {
                    ActionText.Text = "Character hidden.";
                    NoticeCharacterChange(global::Martlet.Conversation.PhysicalKind.Hidden);
                }
            }
            else
            {
                await ShowSavedCharacterAsync(onlyIfAutoShow: false);
                if (avatar.IsShowing) NoticeCharacterChange(global::Martlet.Conversation.PhysicalKind.Shown);
            }
        }
        finally
        {
            togglingCharacter = false;
            UpdateCharacterButton();
            if (!closing)
            {
                RenderHome();
                RenderDevicePages();
            }
        }
    }
    private void UpdateCharacterButton()
    {
        CharacterButton.Content = avatar.IsShowing ? "Hide character" : "Show character";
        // Reset stays reachable while the character is hidden (or lost off screen) once it has a saved place.
        ResetCharacterButton.Visibility = avatar.IsShowing || avatar.Placement is not null ? Visibility.Visible : Visibility.Collapsed;
        ResetCharacterZoomButton.Visibility = avatar.IsShowing ? Visibility.Visible : Visibility.Collapsed;
        var locked = avatar.PlacementLocked;
        ResetCharacterButton.IsEnabled = true;
        ResetCharacterButton.ToolTip = avatar.IsShowing
            ? "Move the character back to the lower-right of your main screen" + (locked ? " (it stays locked there)" : "")
            : "Forget where the character was, so it shows at the lower-right of your main screen";
    }
}

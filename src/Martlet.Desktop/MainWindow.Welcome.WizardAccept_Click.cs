using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Martlet.Avatar.Hosting;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Welcome.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private void WizardAccept_Click(object sender, RoutedEventArgs e)
    {
        if (welcomePlan is not { } plan) return;
        var thinking = homeSettings.ThinkingRoute();
        if (plan.ThinkingHosted?.Option.ProviderId == "nvidia-build" && thinking is null)
        {
            welcomeNeedsKey = true;
            ShowWelcomeKey();
            return;
        }
        ShowWelcomeCharacter();
    }

    // ---------- step 5: a free NVIDIA Build key ----------

    private void ShowWelcomeKey()
    {
        var model = PlanningCatalog.SuggestedModel(ChatCompletionsEndpointCatalog.NvidiaBuildId) ?? ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId;
        var intro = $"Martlet thinks with {model} on NVIDIA Build, free with a free NVIDIA account, but NVIDIA logs what is sent, so " +
            "don't share personal data or voices with it. It takes about two minutes and needs no payment card. Listening stays on " +
            "this PC (Parakeet), so Thinking gets the words you said, not your voice. Windows Credential Manager keeps the key, and " +
            "the same key also works for Picture making.";
        var (summary, _) = HelpTip.Split(intro);
        WizardKeyIntro.Inlines.Clear();
        WizardKeyIntro.Inlines.Add(new System.Windows.Documents.Run(summary));
        var tip = HelpTip.For("the free NVIDIA key", intro, "WizardKeyIntro", wiki: "First-Steps#4-use-the-suggested-setup");
        tip.Margin = new Thickness(6, 0, 0, 0);
        WizardKeyIntro.Inlines.Add(new System.Windows.Documents.InlineUIContainer(tip) { BaselineAlignment = BaselineAlignment.Center });
        AutomationProperties.SetName(WizardKeyIntro, summary);
        AutomationProperties.SetHelpText(WizardKeyIntro, intro);
        WizardKeySteps.Text =
            "1. Press Open build.nvidia.com below and sign in, or create a free NVIDIA account and confirm the email NVIDIA sends.\n" +
            "2. On the API Keys page, choose Generate API Key (accept NVIDIA's trial terms or verify a phone number if it asks).\n" +
            "3. Copy the key now: it starts with nvapi- and isn't shown again.\n" +
            "4. Paste it below, tick the box and press Save key.";
        WizardKeyStatus.Text = "";
        ShowTour(TourKey);
    }

    private void WizardKeyOpen_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(NvidiaKeyPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            WizardKeyStatus.Text = $"Couldn't open the browser ({error.Message}): go to {NvidiaKeyPage} yourself.";
        }
    }

    private async void WizardKeySave_Click(object sender, RoutedEventArgs e)
    {
        if (WizardKeyConsent.IsChecked != true)
        {
            WizardKeyStatus.Text = "Tick the box to send your conversation to NVIDIA Build, then press Save key.";
            return;
        }
        if (WizardKeyBox.SecurePassword.Length == 0)
        {
            WizardKeyStatus.Text = "Paste your NVIDIA API key first (it starts with nvapi-).";
            return;
        }
        WizardKeySaveButton.IsEnabled = false;
        try
        {
            var provider = ThinkingProviders.First(p => p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl);
            await SaveCloudAsync(HostJob.Thinking, provider, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl,
                PlanningCatalog.SuggestedModel(ChatCompletionsEndpointCatalog.NvidiaBuildId) ?? ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
                null, WizardKeyBox, consent: true);
            await RefreshHomeAsync();
            if (closing) return;
            var saved = homeSettings.ThinkingRoute();
            if (saved?.Origin != ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl || saved.CredentialId is null)
            {
                WizardKeyStatus.Text = string.IsNullOrWhiteSpace(ActionText.Text) ? "The key wasn't saved; try again." : ActionText.Text;
                return;
            }
            ErrorLog.Info("Welcome: Thinking uses NVIDIA Build with the key saved in Windows Credential Manager.");
            welcomeNeedsKey = false;
            ShowWelcomeCharacter();
        }
        finally { if (!closing) WizardKeySaveButton.IsEnabled = true; }
    }

    private void WizardKeySkip_Click(object sender, RoutedEventArgs e)
    {
        welcomeNeedsKey = false;
        ShowWelcomeCharacter();
    }

    // ---------- step 6: apply ----------

    /// <summary>Sets up what the accepted suggestion puts on this PC after one confirmation (<see cref="SetUpDefaultsAsync"/>:
    /// jobs already set up, or done by another computer in the network or online, are left alone), then lip-sync (Audio2Face here
    /// or on the host the plan chose, otherwise the voice's loudness), and leaves the owner on Home.</summary>
    private async Task ApplyWelcomeAsync()
    {
        if (welcomePlan is not { } plan) return;
        HideTour();
        Navigate(NavHome);
        bool Here(PlanComponent component) => plan.Placement.Primary(component)?.MachineId == DefaultSetup.ThisPc;
        var lipSync = plan.Placement.Primary(PlanComponent.LipSync);
        var lipSyncHost = lipSync is { MachineId: { } id } && id != DefaultSetup.ThisPc && FindHost(id) is not null ? id : null;
        var lipSyncLine = plan.Setup.LipSyncOnGpu
            ? "Lip-sync: Audio2Face on the graphics card, in a host service Martlet sets up in Docker on this PC (until then, the mouth follows the voice's loudness)."
            : lipSyncHost is not null ? $"Lip-sync: Audio2Face on {lipSyncHost}."
            : "Lip-sync: the mouth opens and closes with the voice's loudness, with nothing to install.";
        ErrorLog.Info($"Welcome: applying the suggestion ({welcomePreference}).");
        if (!await SetUpDefaultsAsync(Here(PlanComponent.Thinking), Here(PlanComponent.Listening), Here(PlanComponent.Voice),
                extra: lipSyncLine, planned: plan.Setup))
            return;
        if (closing) return;
        if (plan.Setup.LipSyncOnGpu) await SetUpThisPcHostAsync(AssignLipSyncAsync);
        else await AssignLipSyncAsync(lipSyncHost is null ? "off" : "host:" + lipSyncHost);
        if (!closing) ActionText.Text = DefaultSetupOutcome();
    }
}

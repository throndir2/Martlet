using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>Companion › Listening › Martlet in your Discord calls (companion mode on the owner's own Discord account; the bot
/// lives on Companion › Discord). Every control has an automation ID; the status lines are MCP SafeValues.</summary>
public partial class MainWindow
{
    private readonly DiscordCallService discordCalls;
    private bool callOutputsListed;

    private static readonly string[] CallCaptureChoices = ["The Discord app only (recommended)", "Everything this PC plays except Martlet"];
    private static readonly string[] CallCameraChoices = ["Green", "Blue", "Magenta", "Black"];

    private Border DiscordCallsCard()
    {
        var saved = discordCalls.Preferences;
        if (!callOutputsListed)
        {
            callOutputsListed = true;
            RefreshCallOutputsAsync().Forget();
        }
        var on = new CheckBox { Content = "Martlet joins my Discord calls", IsChecked = saved.On, Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = saved.On || conversation?.CanHearPc != false };
        AutomationProperties.SetAutomationId(on, "DiscordCallOn");
        on.Checked += (_, _) => SaveCall(prefs => prefs with { On = true });
        on.Unchecked += (_, _) => SaveCall(prefs => prefs with { On = false });

        var status = Note(discordCalls.Status(openConversation is { ListeningStarted: true }), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(status, "DiscordCallStatus");

        var capture = new ComboBox { Width = 320, ItemsSource = CallCaptureChoices, SelectedIndex = (int)saved.Capture,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        AutomationProperties.SetName(capture, "What Martlet hears of the call");
        AutomationProperties.SetAutomationId(capture, "DiscordCallCapture");
        capture.SelectionChanged += (_, _) =>
        {
            if (capture.SelectedIndex >= 0 && capture.SelectedIndex != (int)discordCalls.Preferences.Capture)
                SaveCall(prefs => prefs with { Capture = (DiscordCallCapture)capture.SelectedIndex });
        };

        var see = new CheckBox { Content = "See who is talking in the Discord window (on this PC only)", IsChecked = saved.SeeSpeakers,
            Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(see, "DiscordCallSeeSpeakers");
        see.Checked += (_, _) => SaveCall(prefs => prefs with { SeeSpeakers = true });
        see.Unchecked += (_, _) => SaveCall(prefs => prefs with { SeeSpeakers = false });
        var owner = new TextBox { Text = saved.OwnerName ?? "", Width = 240, HorizontalAlignment = HorizontalAlignment.Left,
            MaxLength = DiscordCallPreferences.MaximumNameLength, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(owner, "Your Discord display name");
        AutomationProperties.SetAutomationId(owner, "DiscordCallOwnerName");
        owner.LostFocus += (_, _) =>
        {
            var name = owner.Text.Trim();
            if (name != (discordCalls.Preferences.OwnerName ?? "")) SaveCall(prefs => prefs with { OwnerName = name });
        };
        var attribution = Note($"Who is talking: {discordCalls.Attribution.Source}; {discordCalls.Attribution.Attributed} " +
            $"{(discordCalls.Attribution.Attributed == 1 ? "person" : "people")} named so far.", new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(attribution, "DiscordCallAttribution");

        var outputs = discordCalls.Outputs ?? [];
        var items = new List<string> { "Martlet's usual output" };
        items.AddRange(outputs.Select(output => output.Name + (DiscordCallOutputs.LooksVirtual(output.Name) ? " (virtual cable)" : "")));
        var plan = discordCalls.Plan;
        var chosen = saved.OutputId is null ? 0 : outputs.ToList().FindIndex(output => output.Id == plan.OutputId) + 1;
        if (saved.OutputId is not null && chosen <= 0)
        {
            items.Add((saved.OutputName ?? "The chosen output") + " (not connected)");
            chosen = items.Count - 1;
        }
        var output = new ComboBox { Width = 320, ItemsSource = items, SelectedIndex = chosen, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(output, "Where Martlet's voice goes in the call");
        AutomationProperties.SetAutomationId(output, "DiscordCallOutput");
        output.SelectionChanged += (_, _) =>
        {
            var index = output.SelectedIndex;
            if (index < 0 || index == chosen || index > outputs.Count) return;
            SaveCall(prefs => index == 0 ? prefs with { OutputId = null, OutputName = null }
                : prefs with { OutputId = outputs[index - 1].Id, OutputName = outputs[index - 1].Name });
        };
        var outputStatus = plan.Present ? Note("Martlet's voice goes to " + plan.Summary, new Thickness(0, 0, 0, 4)) : Warning(plan.Summary);
        AutomationProperties.SetAutomationId(outputStatus, "DiscordCallOutputStatus");
        var also = new CheckBox { Content = "Also play Martlet's voice on my usual output", IsChecked = saved.AlsoSpeakers,
            Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetAutomationId(also, "DiscordCallAlsoSpeakers");
        also.Checked += (_, _) => SaveCall(prefs => prefs with { AlsoSpeakers = true });
        also.Unchecked += (_, _) => SaveCall(prefs => prefs with { AlsoSpeakers = false });
        var bargeIn = new CheckBox { Content = "Stop talking when someone in the call talks over Martlet", IsChecked = saved.BargeIn,
            Margin = new Thickness(0, 2, 0, 8) };
        AutomationProperties.SetAutomationId(bargeIn, "DiscordCallBargeIn");
        bargeIn.Checked += (_, _) => SaveCall(prefs => prefs with { BargeIn = true });
        bargeIn.Unchecked += (_, _) => SaveCall(prefs => prefs with { BargeIn = false });

        var background = new ComboBox { Width = 160, ItemsSource = CallCameraChoices, SelectedIndex = (int)saved.CameraBackground,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(background, "Camera view background");
        AutomationProperties.SetAutomationId(background, "DiscordCallCameraBackground");
        background.SelectionChanged += (_, _) =>
        {
            if (background.SelectedIndex >= 0 && background.SelectedIndex != (int)discordCalls.Preferences.CameraBackground)
            {
                SaveCall(prefs => prefs with { CameraBackground = (DiscordCameraBackground)background.SelectedIndex });
                if (discordCalls.CameraOpen) avatar.SetCameraAsync(discordCalls.Preferences.CameraColor, CancellationToken.None).Forget();
            }
        };
        var camera = PageButton(discordCalls.CameraOpen ? "Close camera view" : "Open camera view", () => ToggleCallCameraAsync().Forget(),
            id: "DiscordCallCamera");
        var cameraStatus = Note(!discordCalls.CameraOpen ? "The camera view is closed."
            : avatar.IsShowing
                ? $"The camera view is open: the character in its own 16:9 window titled \"Martlet camera\" on {saved.CameraBackground.ToString().ToLowerInvariant()}."
                : "The camera view is on, but the character isn't showing yet; it opens there as soon as the character shows.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(cameraStatus, "DiscordCallCameraStatus");

        var check = PageButton("Check this PC", () => CheckCallAsync().Forget(), link: true, id: "DiscordCallCheck");
        var doctor = Note(callDoctor ?? "Check this PC to see whether Windows can hear the Discord app alone and your output is connected.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(doctor, "DiscordCallDoctor");

        return Card([Heading("Martlet in your Discord calls"), on, status,
            Note("Join a DM, group DM or server call on your own Discord account as usual; Martlet takes part through this PC. " +
                "Martlet never controls Discord (no clicks, typing or account access) and works while always listening runs. " +
                "What it hears of the call is transcribed like Hear what this PC plays and goes to Thinking marked as the call's, " +
                "never as you, never into memory or voice recognition. It answers when someone says its name, and otherwise " +
                "only now and then.", new Thickness(0, 0, 0, 8)),
            Note("Hear:", new Thickness(0, 0, 0, 0)), capture,
            see,
            Note("Your Discord display name (so your own tile lighting up is never taken for someone else):", new Thickness(0, 2, 0, 0)),
            owner, attribution,
            Note("Martlet's voice in the call:", new Thickness(0, 0, 0, 0)), output, outputStatus, also, bargeIn,
            Note("To speak into the call, install a virtual audio cable yourself (for example VB-Audio Virtual Cable), choose its " +
                "\"CABLE Input\" here and choose \"CABLE Output\" as your Input Device in Discord › User Settings › Voice & Video. " +
                "Your own voice then needs to reach the cable too: speak through Martlet's microphone listening, or mix your " +
                "microphone into the cable with Voicemeeter or Windows' Listen to this device.", new Thickness(0, 0, 0, 8)),
            Heading("Webcam: the character"),
            Note("Open the camera view, add it to OBS as a Window Capture of \"Martlet camera\", key out the background with a " +
                "Chroma Key filter, then Start Virtual Camera in OBS and pick \"OBS Virtual Camera\" as your camera in Discord. " +
                "Martlet installs no camera driver.", new Thickness(0, 0, 0, 4)),
            Note("Background:", new Thickness(0, 0, 0, 0)), background, Row(camera), cameraStatus,
            Row(check), doctor]);
    }

    private string? callDoctor;

    private void SaveCall(Func<DiscordCallPreferences, DiscordCallPreferences> update)
    {
        if (!discordCalls.Save(update)) ActionText.Text = "Couldn't save this choice.";
        RenderTab();
    }

    private async Task RefreshCallOutputsAsync()
    {
        await discordCalls.RefreshOutputsAsync(CancellationToken.None);
        if (!closing && openTab == CompanionTab.Listening && !tabEdited) RenderTab();
    }

    private async Task CheckCallAsync()
    {
        await discordCalls.RefreshOutputsAsync(CancellationToken.None);
        var saved = discordCalls.Preferences;
        var outputs = discordCalls.Outputs;
        var result = await Task.Run(() => DiscordCallDoctor.Check(saved, outputs));
        callDoctor = result.Describe();
        ErrorLog.Info("Discord call check: " + callDoctor);
        if (!closing && openTab == CompanionTab.Listening) RenderTab();
    }

    private async Task ToggleCallCameraAsync()
    {
        var open = !discordCalls.CameraOpen;
        try
        {
            if (open && !avatar.IsShowing) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
            var showing = await avatar.SetCameraAsync(open ? discordCalls.Preferences.CameraColor : null, CancellationToken.None);
            discordCalls.CameraOpen = open;
            ActionText.Text = !open ? "The camera view is closed."
                : showing ? "The camera view is open. Capture the \"Martlet camera\" window in OBS."
                : "The camera view opens as soon as the character shows (Companion › Character).";
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or TimeoutException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            ActionText.Text = "Couldn't change the camera view: " + error.Message;
        }
        if (!closing && openTab == CompanionTab.Listening) RenderTab();
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Discord;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The Companion page's Discord tab: the guided setup of Martlet's own Discord application and bot token, the
/// connection (on/off and live status), the invite links, the chat modes with per-channel rules, and the owner's account and
/// home server. The token goes straight from the password box into Windows Credential Manager (<see cref="DiscordService"/>);
/// it is never shown, logged or readable through MCP.</summary>
public partial class MainWindow
{
    private string? discordTokenStatus;
    private string? discordOwnerDraft;
    private bool discordWatched;
    private bool discordBusy;

    private async Task SaveDiscordTokenAsync(PasswordBox box, Button button)
    {
        if (closing || discordBusy) return;
        using (var entered = box.SecurePassword)
            if (entered.Length == 0)
            {
                discordTokenStatus = "Paste the bot token from the Developer Portal first.";
                RenderTab();
                return;
            }
        discordBusy = true;
        button.IsEnabled = false;
        try
        {
            CredentialError error;
            using (var token = TakeKey(box)) error = discord.SaveToken(token);
            if (error != CredentialError.None)
            {
                discordTokenStatus = error == CredentialError.InvalidInput
                    ? "That isn't a Discord bot token: on the Bot page in the Developer Portal, choose Reset Token and copy the whole token."
                    : $"Couldn't save the token: {CredentialMessages.Describe(error)}";
                ActionText.Text = discordTokenStatus;
                return;
            }
            discordTokenStatus = "Bot token saved in Windows Credential Manager; connecting...";
            if (!discord.Save(current => current with { Enabled = true }))
            {
                discordTokenStatus = "The token is saved, but Martlet couldn't save the Discord setup: check access to your data directory.";
                return;
            }
            var problem = await discord.StartAsync(lifetime.Token);
            discordTokenStatus = problem is null ? "Bot token saved in Windows Credential Manager." : $"Bot token saved, but {problem}";
            ActionText.Text = problem ?? "Martlet's Discord bot is connecting.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            discordBusy = false;
            if (!closing) RenderTab();
        }
    }

    private async Task ForgetDiscordAsync()
    {
        if (!ConfirmationDialog.Confirm(this, "Forget Martlet's Discord bot, disconnect it and remove its token from Windows Credential Manager " +
                "(your chat modes and people stay)?", "Martlet - Discord"))
            return;
        await discord.ForgetAsync();
        discordTokenStatus = "The bot token is forgotten.";
        ActionText.Text = "Martlet's Discord bot is disconnected and its token removed.";
        if (!closing) RenderTab();
    }

    private async Task SetDiscordEnabledAsync(bool on)
    {
        if (closing || discordBusy) return;
        if (discord.Preferences.Enabled != on && !discord.Save(current => current with { Enabled = on }))
        {
            ActionText.Text = "Couldn't save the Discord setting: check access to your data directory.";
            RenderTab();
            return;
        }
        discordBusy = true;
        try
        {
            if (on)
            {
                ActionText.Text = "Connecting to Discord...";
                var problem = await discord.StartAsync(lifetime.Token);
                ActionText.Text = problem ?? "Martlet's Discord bot is connecting.";
            }
            else
            {
                await discord.StopAsync();
                ActionText.Text = "Martlet's Discord bot is off.";
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            discordBusy = false;
            if (!closing) RenderTab();
        }
    }

    private void SaveDiscordOwner(string text)
    {
        if (text.Trim().Length == 0)
        {
            discordOwnerDraft = null;
            SaveDiscord(p => p with { OwnerUserId = 0 }, "Your Discord account is cleared.");
            return;
        }
        if (!DiscordSetup.TryParseId(text, out var id))
        {
            ActionText.Text = "That isn't a Discord user ID: it's a long number that Copy User ID gives you.";
            return;
        }
        discordOwnerDraft = null;
        SaveDiscord(p => p with { OwnerUserId = id }, "Martlet knows your Discord account.");
    }

    private void SaveDiscord(Func<DiscordPreferences, DiscordPreferences> update, string done)
    {
        ActionText.Text = discord.Save(update) ? done : "Couldn't save the Discord setup: check access to your data directory.";
        Dispatcher.BeginInvoke(() =>
        {
            if (!closing && openTab == CompanionTab.Discord) RenderTab();
        });
    }

    /// <summary>The Discord page may be rebuilt for a status change: it is open and nobody is typing or choosing in it.</summary>
    private bool DiscordCanRender() => !closing && openTab == CompanionTab.Discord &&
        System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox or ComboBoxItem);
}

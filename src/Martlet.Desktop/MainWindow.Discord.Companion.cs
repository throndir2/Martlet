using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatars;
using Martlet.Discord;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Discord › Friends and calls: the people Martlet knows on Discord as its friends (requests from
/// <c>/friend ask</c> to approve, adding someone by ID, who takes calls), calling them through the home server, and the bot's
/// status and picture following Martlet and its character.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer discordPresenceTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private (string Source, byte[]? Png)? discordFallbackPicture;
    private bool discordPresenceBusy;
    private string discordFriendsResult = "";

    /// <summary>Wires the companion features to the character, the conversation and the presence timer (once, at start).</summary>
    private void InitializeDiscordCompanion()
    {
        discord.AttachCompanion(
            (portrait, token) => Dispatcher.InvokeAsync(() => avatar.SnapshotAsync(portrait, token)).Task.Unwrap(),
            () => Dispatcher.Invoke(DiscordAvatarSource),
            DiscordAvatarPictureAsync,
            () => Dispatcher.Invoke(() => homeSettings?.Companion?.ActivePersona?.Name));
        if (conversation is not null)
        {
            conversation.DiscordCaller = discord;
            conversation.CallCamera = new CallCameraBridge(this);
        }
        discord.Companion.Requested += request => Dispatcher.BeginInvoke(() =>
            ErrorLog.Info("Discord: someone asked to be Martlet's friend; approve or decline in Companion › Discord."));
        discordPresenceTimer.Tick += (_, _) => UpdateDiscordPresence();
        discordPresenceTimer.Start();
    }

    /// <summary>What the bot's picture is made from: the chosen character model (hashed, never its path), or null for none.</summary>
    private string? DiscordAvatarSource() => homeAvatar is { } profile
        ? "model:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Renderer + "|" + profile.ModelPath)))[..16]
        : null;

    /// <summary>A head-and-shoulders picture of the showing character, else a VRM model's own thumbnail.</summary>
    private async Task<byte[]?> DiscordAvatarPictureAsync(CancellationToken token)
    {
        var shown = await Dispatcher.InvokeAsync(() => avatar.SnapshotAsync(portrait: true, token)).Task.Unwrap().ConfigureAwait(false);
        if (shown is not null) return shown;
        var (profile, source) = await Dispatcher.InvokeAsync(() => (homeAvatar, DiscordAvatarSource()));
        if (profile is null || source is null || profile.Renderer != AvatarRenderer.Vrm) return null;
        if (discordFallbackPicture is { } cached && cached.Source == source) return cached.Png;
        byte[]? thumbnail = null;
        try
        {
            var (_, images) = await CharacterThemeImages.ReadModelAsync(profile.Renderer, profile.ModelPath, token).ConfigureAwait(false);
            thumbnail = images.FirstOrDefault(image => image.Thumbnail)?.Bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            Martlet.Core.Contracts.ContractException)
        {
            ErrorLog.Warn($"Couldn't read the character's thumbnail for Discord: {error.Message}");
        }
        discordFallbackPicture = (source, thumbnail);
        return thumbnail;
    }

    /// <summary>What Martlet is doing, for the bot's status.</summary>
    private DiscordPresenceInputs DiscordPresenceNow()
    {
        var talk = openConversation;
        var call = discord.Companion.ActiveCall();
        return new(
            Paused: talk?.Paused == true,
            Away: UserIdle() > TimeSpan.FromMinutes(10),
            Talking: conversation?.Replying == true || talk?.HearingYou == true,
            Listening: talk?.ListeningStarted == true,
            Watching: talk?.WatchingStarted == true,
            CallingWith: call is null ? null : discord.Preferences.People.FirstOrDefault(p => p.UserId == call.UserId)?.Name);
    }

    private void UpdateDiscordPresence()
    {
        if (closing || discordPresenceBusy || discord.Status.State != DiscordBotState.Online) return;
        discordPresenceBusy = true;
        var inputs = DiscordPresenceNow();
        _ = Task.Run(async () =>
        {
            try
            {
                await discord.UpdatePresenceAsync(inputs, lifetime.Token).ConfigureAwait(false);
                await discord.UpdateAvatarAsync(manual: false, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { await Dispatcher.InvokeAsync(() => discordPresenceBusy = false); }
        });
    }

    private void CallDiscordFriend(DiscordPerson person, TextBlock result)
    {
        result.Text = $"Calling {person.Name}…";
        discord.CallAsync(person, lifetime.Token).ContinueWith(task => Dispatcher.BeginInvoke(() =>
            result.Text = discordFriendsResult = task.IsCompletedSuccessfully ? task.Result.Message : $"Couldn't call {person.Name}."), TaskScheduler.Default);
    }

    private static TimeSpan UserIdle() => DiscordIdle.UserIdle();
}

/// <summary>How long since the last keyboard or mouse input on this PC (for the Discord status's Away).</summary>
internal static class DiscordIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput info);

    internal static TimeSpan UserIdle()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }
}

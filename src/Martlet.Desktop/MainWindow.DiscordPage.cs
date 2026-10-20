using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Creations;
using Martlet.Discord;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Discord (<see cref="DiscordPage"/> in Pages\Discord). The work the page shows
/// stays in the window (MainWindow.Discord.cs, MainWindow.Discord.Companion.cs and MainWindow.DiscordCalls.cs).</summary>
public partial class MainWindow : IDiscordPageServices
{
    private DiscordPage? discordPage;

    /// <summary>The Discord page, made once: it keeps the camera picture's preview. The first time, the window starts drawing the
    /// page again when the bot's state changes.</summary>
    private DiscordPage DiscordPage
    {
        get
        {
            if (!discordWatched)
            {
                discordWatched = true;
                discord.Changed += () => Dispatcher.BeginInvoke(() =>
                {
                    if (DiscordCanRender()) RenderTab();
                });
            }
            return discordPage ??= new(this);
        }
    }

    DiscordService IDiscordPageServices.Discord => discord;
    DiscordCallService IDiscordPageServices.Calls => discordCalls;
    string? IDiscordPageServices.DataDirectory => store?.DataDirectory;
    string? IDiscordPageServices.CreationsFolder => CreationsFolder;
    string? IDiscordPageServices.TokenStatus => discordTokenStatus;
    bool IDiscordPageServices.Busy => discordBusy;
    string? IDiscordPageServices.OwnerDraft
    {
        get => discordOwnerDraft;
        set => discordOwnerDraft = value;
    }
    string IDiscordPageServices.FriendsResult => discordFriendsResult;
    IReadOnlyList<string> IDiscordPageServices.VoiceNames() =>
        [.. localVoices.Roster.Voices.Where(v => !v.Removed && v.Named).Select(MemoryPeople.Label)];
    bool IDiscordPageServices.CanHearPc => conversation?.CanHearPc != false;
    bool IDiscordPageServices.Listening => openConversation is { ListeningStarted: true };
    bool IDiscordPageServices.CharacterShowing => avatar.IsShowing;
    string? IDiscordPageServices.CallDoctor => callDoctor;
    string? IDiscordPageServices.CallPictureState
    {
        get => callPictureState;
        set => callPictureState = value;
    }
    string IDiscordPageServices.CallPicturePrompt
    {
        get => callPicturePrompt;
        set => callPicturePrompt = value;
    }
    bool IDiscordPageServices.CallPictureDrawing => callPictureDrawing;

    void IDiscordPageServices.SaveToken(PasswordBox token, Button save) => SaveDiscordTokenAsync(token, save).Forget();
    void IDiscordPageServices.Forget() => ForgetDiscordAsync().Forget();
    void IDiscordPageServices.SetEnabled(bool on) => SetDiscordEnabledAsync(on).Forget();
    void IDiscordPageServices.Save(Func<DiscordPreferences, DiscordPreferences> update, string done) => SaveDiscord(update, done);
    void IDiscordPageServices.SaveOwner(string text) => SaveDiscordOwner(text);
    void IDiscordPageServices.FriendsDone(string message)
    {
        discordFriendsResult = message;
        if (openTab is not null) Dispatcher.BeginInvoke(RenderTab, DispatcherPriority.Background);
    }
    void IDiscordPageServices.CallFriend(DiscordPerson person, TextBlock result) => CallDiscordFriend(person, result);
    void IDiscordPageServices.UpdatePictureNow(Action<string> done) =>
        discord.UpdateAvatarAsync(manual: true, lifetime.Token).ContinueWith(task =>
            Dispatcher.BeginInvoke(() => done(task.IsCompletedSuccessfully ? task.Result : "Couldn't update the picture.")), TaskScheduler.Default);
    void IDiscordPageServices.ListCallOutputsOnce()
    {
        if (callOutputsListed) return;
        callOutputsListed = true;
        RefreshCallOutputsAsync().Forget();
    }
    void IDiscordPageServices.SaveCall(Func<DiscordCallPreferences, DiscordCallPreferences> update) => SaveCall(update);
    void IDiscordPageServices.RefreshCallCamera() => RefreshCallCamera();
    void IDiscordPageServices.ToggleCallCamera() => ToggleCallCameraAsync().Forget();
    void IDiscordPageServices.FrameCallCamera(string action) => FrameCallCameraAsync(action).Forget();
    void IDiscordPageServices.CheckCall() => CheckCallAsync().Forget();
    void IDiscordPageServices.ChooseCallPicture() => ChooseCallPicture();
    void IDiscordPageServices.UseCreationPicture(Creation creation) => UseCreationPictureAsync(creation).Forget();
    void IDiscordPageServices.DrawCallPicture(string instruction) => DrawCallPictureAsync(instruction).Forget();
    void IDiscordPageServices.OpenInBrowser(Uri address) => OpenInBrowser(address);
}

using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Creations;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Motions in conversation (docs/AVATARS.md#motions-in-conversation): while motions are set up and the chosen character
/// is a VRM, replies get body_motion, Martlet makes new body motions on the motion computer, keeps them (shared creations) and
/// plays them on the character; every VRM character's Emotes and motions lists the kept motions, so reply tags do them again at
/// once. Companion › Emotes and motions has a Make a motion row for the owner, and Martlet makes its starter motions once.</summary>
public partial class MainWindow
{
    private CharacterMotions? characterMotions;
    private string? characterMotionStatus;
    private DateTimeOffset startersTried = DateTimeOffset.MinValue;
    private static readonly TimeSpan StartersEvery = TimeSpan.FromMinutes(10);
    private DateTimeOffset preloadTried = DateTimeOffset.MinValue;
    private bool preloading;
    private static readonly TimeSpan PreloadEvery = TimeSpan.FromSeconds(30);

    private void InitializeCharacterMotions()
    {
        if (store is null) return;
        var motions = new CharacterMotions(store.DataDirectory, () => CreationsFolder, MotionClient.IsSetUp, MotionStudio.For, new MotionPlayer(this));
        characterMotions = motions;
        characterActions.KeptMotions = motions.Kept;
        avatar.KeptMotionClip = motions.ClipAsync;
        // Another model loaded or the character hid: the kept motions given ahead go again once a VRM shows.
        avatar.AnimationsForgotten += () => Dispatcher.BeginInvoke(() => preloadTried = DateTimeOffset.MinValue);
        if (conversation is not null) conversation.Motions = motions;
        // A motion made here or on another computer, or deleted, joins (or leaves) the VRM character's emotes and motions.
        CreationStore.Changed += directory =>
        {
            if (closing || CreationsFolder is not { } folder || !string.Equals(directory, folder, StringComparison.OrdinalIgnoreCase)) return;
            characterActions.RefreshKeptMotionsAsync(lifetime.Token).Forget();
        };
        FollowCharacterMotions();
    }

    /// <summary>Keeps the motions on the saved choices (the chosen character is a VRM; the active persona), gives a showing VRM
    /// its first kept motions ahead of use, and makes the starter motions once when they are due and the character shows (at
    /// most every ten minutes). Runs from the character timer.</summary>
    private void FollowCharacterMotions()
    {
        if (characterMotions is not { } motions || closing) return;
        // The character shown (else the saved choice), as Emotes and motions follows it: both are the saved choice once saved.
        var vrm = (avatar.IsShowing ? avatar.InspectedProfile : homeAvatar)?.Renderer == AvatarRenderer.Vrm;
        if (vrm != motions.ChosenVrm)
            ErrorLog.Info(vrm ? "Motions: the chosen character is a VRM; replies get body_motion while motions are set up."
                : "Motions: the chosen character isn't a VRM; replies don't get body_motion.");
        motions.ChosenVrm = vrm;
        motions.CharacterName = homeSettings?.Companion?.ActivePersona?.Name;
        if (avatar.IsShowing && avatar.InspectedProfile?.Renderer == AvatarRenderer.Vrm && !preloading &&
            DateTimeOffset.UtcNow - preloadTried > PreloadEvery)
        {
            preloadTried = DateTimeOffset.UtcNow;
            PreloadAsync().Forget();
        }
        if (!avatar.IsShowing || DateTimeOffset.UtcNow - startersTried < StartersEvery || !motions.Offered) return;
        startersTried = DateTimeOffset.UtcNow;
        StartersAsync().Forget();

        async Task PreloadAsync()
        {
            preloading = true;
            try
            {
                var keys = await Task.Run(motions.PreloadKeys, lifetime.Token);
                if (keys.Count == 0 || keys.Any(key => avatar.HasAnimation(AvatarController.ClipName(key)))) return;
                var given = await avatar.PreloadKeptMotionsAsync(keys, lifetime.Token);
                if (given > 0) ErrorLog.Info($"Motions: gave the character {given} kept motion{(given == 1 ? "" : "s")} ahead of use.");
            }
            catch (Exception error) when (error is OperationCanceledException || RendererFailures.Is(error, lifetime.Token)) { }
            finally { preloading = false; }
        }

        async Task StartersAsync()
        {
            try
            {
                if (!await Task.Run(motions.StartersDue, lifetime.Token)) return;
                ErrorLog.Info("Motions: making Martlet's starter motions.");
                var said = await Task.Run(() => motions.MakeStartersAsync(lifetime.Token), lifetime.Token);
                ErrorLog.Info("Motions: " + said);
                await characterActions.RefreshKeptMotionsAsync(lifetime.Token);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>The kept motions offered as reply tags in this conversation: those kept when it began (its first request). A
    /// motion kept later (made now, a starter, or synced from another computer) joins at the next conversation, so the
    /// reply instructions, and with them the start of every request, stay the same while the conversation goes on;
    /// body_motion with its name does it meanwhile.</summary>
    private CharacterActionCatalog OfferedMotions(CharacterActionCatalog catalog) =>
        characterMotions?.OfferedIn(conversation?.ConversationId ?? Guid.Empty, catalog) ?? catalog;

    /// <summary>Make a motion (Companion › Emotes and motions, a VRM while motions are set up): what the body does, a name, Make,
    /// and how it went; or, while motions aren't set up, a line that says what they need.</summary>
    private void AddMakeMotion(List<UIElement> stack, CharacterActionCatalog catalog)
    {
        if (catalog.Inventory.Renderer != AvatarRenderer.Vrm || characterMotions is not { } motions) return;
        var status = Note(characterMotionStatus ?? MotionsStatus(motions), new Thickness(0, 8, 0, 4));
        AutomationProperties.SetAutomationId(status, "CharacterMotionStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        stack.Add(status);
        if (!motions.IsSetUp) return;
        var describe = Compact(new TextBox { MinWidth = 260, MaxLength = MotionTools.MaximumMotionCharacters });
        AutomationProperties.SetName(describe, "What the body does");
        AutomationProperties.SetAutomationId(describe, "CharacterMotionDescription");
        AutomationProperties.SetHelpText(describe, "Plain English body movement only, such as \"A person raises both fists above the head and jumps once.\"");
        var name = Compact(new TextBox { Width = 120, MaxLength = MotionTools.MaximumNameCharacters });
        AutomationProperties.SetName(name, "Motion name");
        AutomationProperties.SetAutomationId(name, "CharacterMotionName");
        InputHint.SetText(name, "cheer");
        var make = Compact(PageButton(motions.Busy ? "Making..." : "Make", () => MakeMotionFromPageAsync(describe.Text, name.Text, status).Forget(),
            id: "CharacterMotionMake"));
        make.IsEnabled = !motions.Busy;
        var fields = new FillWrapPanel { Margin = new Thickness(0, 4, 0, 0), FillMinimum = 300 };
        var what = new DockPanel { Margin = new Thickness(0, 0, 18, 6) };
        what.Children.Add(RowLabel("Make a motion", describe));
        what.Children.Add(WithHint(describe, "A person raises both fists above the head and jumps once."));
        fields.Children.Add(what);
        fields.Children.Add(RowGroup(RowLabel("Name", name), name, make));
        stack.Add(fields);
    }

    private static string MotionsStatus(CharacterMotions motions)
    {
        if (!motions.IsSetUp)
            return "Body motions: a computer with the motion role (an NVIDIA GPU with about 24 GB) in the Motion list (Companion › " +
                "Motion) lets Martlet make new body motions for VRM characters and keep them here.";
        var kept = motions.Kept().Count;
        return (motions.ChosenVrm ? "Body motions: Martlet makes new ones in conversation when asked and keeps them; "
                : "Body motions: replies make new ones while the chosen character is a VRM (Companion › Character); ") +
            $"{kept} kept motion{(kept == 1 ? " is" : "s are")} in the list below." + (motions.Starters is { } starters ? " " + starters : "");
    }

    /// <summary>Make: makes the described motion on the motion computer and keeps it, the same way body_motion does; it joins the
    /// list, where Try plays it.</summary>
    private async Task MakeMotionFromPageAsync(string description, string name, TextBlock status)
    {
        if (characterMotions is not { } motions) return;
        var (arguments, problem) = MotionTools.Parse(JsonSerializer.Serialize(new { name, motion = description }));
        void Say(string text)
        {
            characterMotionStatus = text;
            status.Text = text;
        }
        if (arguments is null || arguments.Motion is null)
        {
            Say(problem is null ? "Write what the body does, then a name." : "Give the motion a name in English letters, and write what the body does.");
            return;
        }
        using var studio = motions.Studio();
        if (studio is null)
        {
            Say("Motions aren't set up.");
            return;
        }
        Say($"Making {arguments.Name}...");
        try
        {
            var (creation, made) = await Task.Run(() => motions.MakeAndKeepAsync(arguments, studio,
                step => Dispatcher.InvokeAsync(() => status.Text = step + "..."), lifetime.Token), lifetime.Token);
            await characterActions.RefreshKeptMotionsAsync(lifetime.Token);
            Say($"Kept {arguments.Name} ({creation.Key}){(made.Fixture ? ", a FIXTURE test motion (NOT AI)" : "")}, made on {made.Where} in " +
                $"{made.Took.TotalSeconds:0.#} s; it's in the list below, and Try plays it.");
            ErrorLog.Info($"Motions: the owner made {arguments.Name} ({creation.Key}) on {made.Where}{(made.Fixture ? ", FIXTURE - NOT AI" : "")}.");
            if (openTab == CompanionTab.Emotes && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
        }
        catch (MotionStudioException error) { Say($"{arguments.Name} wasn't made: {error.Message}."); }
        catch (OperationCanceledException) { }
        finally { studio.FreeLater(); }
    }

    /// <summary>Plays motions on the character this PC shows, for body_motion (on any thread).</summary>
    private sealed class MotionPlayer(MainWindow window) : ICharacterMotionPlayer
    {
        public Task<string?> PlayTagAsync(string tag, string reason, CancellationToken token) =>
            window.Dispatcher.InvokeAsync(async () =>
            {
                // The loaded character's (the one shown, else the chosen one): a tag it has is never made again.
                if (window.characterActions.Current is not { } catalog) return null;
                var sources = catalog.For("{" + tag + "}");
                if (sources.Count == 0) return null;
                if (!window.avatar.IsShowing || window.characterActions.For(window.avatar.InspectedProfile?.ModelPath) is null)
                    return $"The character is hidden now, so it can't do {tag}; carry on without it.";
                var started = false;
                foreach (var source in sources) started |= await window.avatar.PlayActionAsync(source, reason, null, token);
                return started ? MotionTools.Played(tag) : $"The character couldn't do {tag} now; carry on without it.";
            }).Task.Unwrap();

        public async Task<string?> PlayKeptAsync(string key, string reason, CancellationToken token)
        {
            await window.characterActions.RefreshKeptMotionsAsync(token).ConfigureAwait(false);
            return await window.Dispatcher.InvokeAsync(async () =>
            {
                if (!window.avatar.IsShowing) return "the character is hidden";
                if (window.characterActions.For(window.avatar.InspectedProfile?.ModelPath) is not { } catalog) return "the character's motions aren't loaded";
                if (catalog.Inventory.Renderer != AvatarRenderer.Vrm) return "the character shown isn't a VRM";
                if (catalog.Inventory.Find(CharacterActionInventory.KeptMotionPrefix + key) is not { } source)
                    return "the character's motions don't list it yet";
                return await window.avatar.PlayActionAsync(source, reason, null, token) ? null : "the character couldn't play it";
            }).Task.Unwrap().ConfigureAwait(false);
        }
    }
}

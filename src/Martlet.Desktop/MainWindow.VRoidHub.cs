using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Find a character and VRoid Hub (docs/AVATARS.md#find-a-character-online): the owner's linked VRoid Hub account,
/// VRoid Hub looks shown from memory (<c>vroidhub:&lt;ID&gt;</c>, never saved or shared as files), and the Find a character
/// window that writes a personality and finds a model for a character the owner names.</summary>
public partial class MainWindow : ICharacterFinderHost
{
    private VRoidHubService? vroidHub;
    private CharacterFinderWindow? characterFinder;

    VRoidHubService ICharacterFinderHost.VRoidHub => vroidHub!;

    private void InitializeVRoidHub()
    {
        vroidHub = new VRoidHubService(store?.DataDirectory, () => VRoidHubService.AccountKey(accounts?.AccountId));
        RemoteCharacterModels.Loader = vroidHub.LoadModelAsync;
        // Another person's account doesn't use this one's VRoid Hub models.
        if (accounts is not null) accounts.AccountChanged += () => RemoteCharacterModels.Clear();
        vroidHub.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (!closing && openTab is CompanionTab.Character or CompanionTab.Profiles) RenderTab();
        });
    }

    /// <summary>Opens Find a character (one window at a time).</summary>
    private void OpenCharacterFinder()
    {
        if (vroidHub is null || closing) return;
        if (characterFinder is { IsLoaded: true } open)
        {
            open.Activate();
            return;
        }
        characterFinder = new CharacterFinderWindow(this) { Owner = this };
        characterFinder.Closed += (_, _) => characterFinder = null;
        characterFinder.Show();
    }

    async Task<(string? Text, string? Failure)> ICharacterFinderHost.WritePersonalityAsync(string name, string? notes, CancellationToken token)
    {
        if (conversation is not { } talk) return (null, "Thinking isn't set up yet");
        if (PromptSettings.Fill(homeSettings?.Prompts, PromptCatalog.CharacterWriting) is not { Length: > 0 } instructions)
            return (null, "Companion › Prompts › Writing a character's personality is empty");
        var text = "Character: " + name + (notes is null ? "" : "\nNotes from the owner: " + notes);
        var (answer, failure) = await talk.AskHelperAsync(HelperJobKind.CharacterWriting, "Writing a character's personality", instructions, text, null, token);
        return (CleanPersonality(answer), failure);
    }

    /// <summary>The personality from the Thinking model's answer: without code fences, quotes around it or thinking tags, and no
    /// longer than a personality may be.</summary>
    internal static string? CleanPersonality(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = System.Text.RegularExpressions.Regex.Replace(answer, @"<think>[\s\S]*?</think>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
            text = string.Join('\n', text.Split('\n').Skip(1).TakeWhile(line => !line.StartsWith("```", StringComparison.Ordinal))).Trim();
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        if (text.Length > PersonaProfile.MaximumTextCharacters) text = text[..PersonaProfile.MaximumTextCharacters].TrimEnd();
        return text.Length == 0 ? null : text;
    }

    async Task<Martlet.Conversation.CharacterWebFinds> ICharacterFinderHost.FindOnWebAsync(string name, bool models, bool voices, CancellationToken token)
    {
        using var web = new Martlet.Conversation.WebAccess();
        var found = await Martlet.Conversation.CharacterWebFinder.FindAsync(web, name, models, voices, token);
        ErrorLog.Info($"Find a character: the web search found {found.Models.Count} model page(s) and {found.Voices.Count} voice page(s)" +
            (found.Notes.Count > 0 ? $"; {found.Notes.Count} search(es) didn't work." : "."));
        return found;
    }

    void ICharacterFinderHost.AddModelFile() => AddCharacterAsync().Forget();

    void ICharacterFinderHost.OpenVoices()
    {
        OpenCompanion(CompanionTab.Voice);
        Activate();
        ActionText.Text = "Press Add a voice... and choose the clip you downloaded. Voices need a voice engine that copies voices.";
    }

    async Task<string> ICharacterFinderHost.CreateCharacterAsync(string name, string personality, CharacterModelCandidate? look, Action<string> progress,
        CancellationToken token)
    {
        if (store is null || vroidHub is null || closing) throw new InvalidOperationException("Martlet isn't ready yet.");
        if (personality.Length > 0) PersonaProfile.ValidateText(personality);
        string? lookId = null;
        if (look is { Source: CharacterModelCandidate.OpenSourceAvatarsSource })
        {
            progress($"Downloading '{look.Name}' from Open Source Avatars...");
            var bytes = await vroidHub.Search.OpenSourceAvatars.DownloadAsync(look, token);
            var modelName = look.CreditRequired ? $"{look.Name} (by {look.Author}, {look.License})" : look.Name;
            if (modelName.Length > CharacterModelLibrary.MaximumNameLength) modelName = modelName[..CharacterModelLibrary.MaximumNameLength].TrimEnd();
            var model = await SharedCharacterModels.ImportVrmAsync(store.DataDirectory, ModelFileName(look.Name), bytes, modelName, ClusterDevice,
                DateTimeOffset.UtcNow, token);
            QueueCharacterModelSync();
            lookId = model.Id;
        }
        else if (look is { Source: CharacterModelCandidate.VRoidHubSource })
        {
            ContractRules.Require(look.Usable, "Its creator doesn't let others use it as a character.");
            progress($"Downloading '{look.Name}' from VRoid Hub into memory (it isn't saved)...");
            await RemoteCharacterModels.ReadAsync(look.Look, token);
            vroidHub.Remember(VRoidHubLook.From(look, DateTimeOffset.UtcNow));
            lookId = look.Look;
        }
        progress("Making the character...");
        Guid profileId = default;
        var saved = await ChangeCompanionAsync(companion =>
        {
            ContractRules.Require(companion.Personas.Count < CompanionSettings.MaximumPersonas,
                $"Martlet keeps at most {CompanionSettings.MaximumPersonas} personalities. Remove one in Companion › Personality first.");
            var personaName = UniqueName(companion.Personas.Select(p => p.Name), name, PersonaProfile.MaximumNameCharacters);
            var next = companion.Add(personaName);
            next = next.Update(next.ActivePersonaId, personaName, personality);
            var profileName = UniqueName(next.CharacterList.Select(c => c.Name), personaName, CharacterProfile.MaximumNameCharacters);
            next = next.AddCharacter(profileName, next.ActivePersonaId, lookId, null, out var added);
            profileId = added.Id;
            return next;
        }, "A character was made in Find a character.");
        if (saved is null) throw new InvalidOperationException(ActionText.Text);
        await UseCharacterProfileAsync(profileId);
        ErrorLog.Info($"Find a character: a character was made with {(look is null ? "no new look" : look.Source + " look")}.");
        var credit = look is null ? "" : look.CreditRequired || look.Source == CharacterModelCandidate.VRoidHubSource ? $" Look: {look.Credit}." : "";
        return $"{ActionText.Text}{credit}";
    }

    private static string UniqueName(IEnumerable<string> taken, string name, int maximum)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        name = name.Trim();
        if (name.Length > maximum) name = name[..maximum].TrimEnd();
        if (!used.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var suffix = $" {i}";
            var candidate = (name.Length + suffix.Length > maximum ? name[..(maximum - suffix.Length)].TrimEnd() : name) + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    /// <summary>A safe file name for a downloaded VRM: letters, digits, spaces and a few marks, then .vrm.</summary>
    internal static string ModelFileName(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ').ToArray()).Trim();
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\s+", " ");
        if (safe.Length > 60) safe = safe[..60].TrimEnd();
        if (safe.Length == 0 || !LocalAvatarFiles.IsSafeModelName(safe + ".vrm")) safe = "Character";
        return safe + ".vrm";
    }

    /// <summary>Shows a VRoid Hub look (<c>vroidhub:&lt;ID&gt;</c>) on this PC: downloads it into memory first (so a look that
    /// can't be shown is never saved), then saves it as this PC's character and switches a showing character over. The caller
    /// holds the change turn. Returns the look's name.</summary>
    private async Task<string> SwitchRemoteLookAsync(string path, CancellationToken token)
    {
        RemoteCharacterModels.Validate(path);
        await RemoteCharacterModels.ReadAsync(path, token);
        var described = vroidHub is null ? null : await vroidHub.DescribeAsync(path, token);
        var (profile, revision) = await Pairings().EnsureProfileAsync(token);
        var next = profile with { Renderer = AvatarRenderer.Vrm, ModelPath = path, ResourceRevision = null };
        if (next != profile) await new AvatarProfileStore(store!.DataDirectory).SaveAsync(next, revision, token);
        homeAvatar = next;
        if (avatar.IsShowing && await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        return described?.Name ?? "VRoid Hub character";
    }

    private async Task UseRemoteLookAsync(VRoidHubLook look)
    {
        if (store is null || closing) return;
        if (avatarWindowOpen)
        {
            ActionText.Text = "The character settings window is open. Close it first.";
            return;
        }
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            vroidHub?.Remember(look with { UsedAt = DateTimeOffset.UtcNow });
            var name = await SwitchRemoteLookAsync(look.Path, lifetime.Token);
            ActionText.Text = (avatar.IsShowing ? $"Martlet now shows '{name}' on this PC." : $"'{name}' is this PC's character. Press Show character to see it.") +
                $" Look: {look.Credit}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            turn?.Dispose();
            if (!closing)
            {
                UpdateCharacterButton();
                RenderHome();
            }
        }
    }

    /// <summary>Companion › Character › VRoid Hub: the link, and the VRoid Hub looks this PC used with their credit and
    /// conditions of use, each with Use and Forget.</summary>
    private Border VRoidHubCard()
    {
        var stack = new List<UIElement>
        {
            Heading("Find a character online"),
            HelpTip.Explain("Name a character and Martlet writes its personality and finds a 3D model of it on VRoid Hub or Open Source Avatars. " +
                "VRoid Hub models are shown from VRoid Hub with your linked account and are never saved; free Open Source Avatars models join your characters.",
                new Thickness(0, 0, 0, 8), "FindCharacter", "finding a character online")
        };
        var state = Note("VRoid Hub: " + (vroidHub?.Status() ?? "not ready."), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(state, "VRoidHubState");
        stack.Add(state);
        var shownPath = homeAvatar?.ModelPath;
        foreach (var look in vroidHub?.Looks() ?? [])
        {
            var key = "vroidhub-" + look.Id;
            var inUse = shownPath == look.Path;
            var detail = $"VRoid Hub, by {look.Author}. Conditions of use: {string.Join(" · ", look.Conditions)}";
            stack.Add(CharacterRow(look.Name + " (VRoid Hub)", detail, inUse, key, () => UseRemoteLookAsync(look),
                inUse ? null : () => { vroidHub?.Forget(look.Id); RenderTab(); return Task.CompletedTask; }, available: vroidHub?.Tokens() is not null));
        }
        stack.Add(Row(PageButton("Find a character...", OpenCharacterFinder, primary: true, id: "CharacterFinderOpen")));
        return Card([.. stack]);
    }
}

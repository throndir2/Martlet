using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion's job pages (Thinking, Listening and Voice): the page is <see cref="JobPage"/> in Pages\Jobs; the window keeps
/// saving a job's route and a cloud provider here, and gives the page its services in MainWindow.JobPage.cs.</summary>
public partial class MainWindow
{
    // ---------- job pages ----------

    /// <summary>Routes that run on this PC without Martlet's host service: Ollama or another model app on this PC for thinking,
    /// installed Windows speech recognition, Parakeet and native whisper.cpp.</summary>
    internal static bool RunsHereWithoutHost(SetupRoute route) =>
        IsLocalOllama(route) || IsLocalServer(route) ||
        route.RouteType is SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWhisper or SetupRouteType.LocalParakeet;

    /// <summary>Draws Companion › Thinking, Listening or Voice (<see cref="JobPage"/> in Pages\Jobs).</summary>
    private void RenderJobTab(Panel page, CompanionTab section) => JobPage.Render(page, section);

    /// <summary>The voice a route speaks with, in words: ", voice alloy", ", voice Mia (cloned on ElevenLabs)", or nothing.</summary>
    internal static string VoiceSuffix(SetupRoute route) =>
        route.Reference is { } reference ? $", voice {reference.PresetName}"
        : route.ClonedVoice is { } cloned ? $", voice {cloned.Name} (cloned on ElevenLabs)"
        : route.VoiceId is { } voice ? $", voice {voice}"
        : "";

    internal static string Lowered(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private async Task SaveCloudAsync(HostJob job, CloudProvider provider, string baseUrl, string model, string? voice, PasswordBox keyBox, bool consent)
    {
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} for {job.Job}, then press Use.";
            return;
        }
        var role = job.Role;
        // A voice engine Speaking leaves on a host stops there once the cloud voice is saved (asked first, before the key is taken).
        var leaving = role == SetupRole.Tts ? LeavingEngine(null, null) : null;
        if (leaving is not null && !ConfirmationDialog.Confirm(this, $"Use {provider.Name} for {job.Job}{StopsClause([], leaving)}?",
                $"Use {provider.Name}"))
            return;
        var url = provider.Chat ? provider.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl : null;
        SecretLease? key = null;
        try
        {
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            if (!provider.Chat)
            {
                var supported = role switch
                {
                    SetupRole.Llm => OpenAiTextGenerationCatalog.SupportedModelIds,
                    SetupRole.Stt => OpenAiTranscriptionCatalog.SupportedModelIds,
                    _ => OpenAiSpeechSynthesisCatalog.SupportedModelIds
                };
                if (!supported.Contains(model, StringComparer.Ordinal))
                    throw new ContractException(ErrorCode.ProviderCapability, $"Choose one of the listed OpenAI models: {string.Join(", ", supported)}.");
                if (role == SetupRole.Tts && !OpenAiSpeechSynthesisCatalog.SupportsVoice(voice))
                    throw new ContractException(ErrorCode.ProviderCapability, "Choose one of the listed voices.");
            }
            var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
            var keySaved = route?.CredentialId is not null && (provider.Chat
                ? route.RouteType == SetupRouteType.ChatCompletions && route.Origin == url
                : route.RouteType is null or SetupRouteType.OpenAi) || SetupSettings.SetAsideCredentials(homeSettings, role, url).Count > 0;
            var missingKey = $"Paste your {provider.Name} API key first.";
            // No key pasted and none for this job: a key for the same provider saved anywhere else in Martlet is copied here.
            string? borrowedFrom = null;
            if (key is null && !keySaved && SavedKeyWhere(url ?? OpenAiSetup.Origin) is { } from &&
                await ReadSavedKeyAsync(url ?? OpenAiSetup.Origin) is { } copy)
            {
                key = copy;
                borrowedFrom = from;
            }
            if (key is null && provider.NeedsKey && !keySaved)
                throw new ContractException(ErrorCode.InvalidContract, missingKey);
            if (!await SaveSectionRouteAsync(job, settings => provider.Chat
                    ? ChatCompletionsSetup.SelectRoute(settings, url!, model)
                    : SetupSettings.SelectRoute(settings, role, model, role == SetupRole.Tts ? voice : null),
                key, $"{job.Title} now uses {provider.Name} ({model}{(voice is null ? "" : ", voice " + voice)})" +
                    (borrowedFrom is not null ? $", with your {provider.Name} key from {borrowedFrom}"
                        : key is null ? "" : ", with your API key saved in Windows Credential Manager") +
                    (FreeKeyPrompt.IsFree(url) ? "." : ", where requests may cost money."),
                provider.NeedsKey ? missingKey : null))
                return;
            // A new Thinking model: ask its server how much context it takes, so replies stay within it. Choosing a model Martlet
            // found retired tries it again; another HTTP 410 marks it retired again.
            if (!closing && role == SetupRole.Llm && provider.Chat &&
                homeSettings.ThinkingRoute() is { } chosen && chosen.Origin == url && chosen.ModelId == model)
            {
                ForgetRetired(chosen.Origin, model, "you chose it again");
                CheckNewModelContextAsync().Forget();
            }
            if (!closing && leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        finally { key?.Dispose(); }
    }

    /// <summary>Saves a job's route chosen on its Companion tab: the route, then its key (which resets consent), then the user's
    /// confirmed choice. A key the route no longer uses is set aside (listed under Keys from before), and a key set aside
    /// earlier for the chosen destination is used again, so switching providers never waits on old keys. With
    /// <paramref name="missingKey"/>, a route that ends without a key is refused with that message. Returns whether it saved.
    /// A change still saving goes first (<see cref="ChangeTurns"/>); installs, runs and replies in progress don't hold it up.</summary>
    private async Task<bool> SaveSectionRouteAsync(HostJob job, Func<AppSettings, AppSettings> select, SecretLease? key, string done,
        string? missingKey = null)
    {
        if (store is null || setupService is null || closing) return false;
        ChangeTurns.Turn? turn = null;
        var role = job.Role;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var settings = UseModels(SetupSettings.Begin(loaded.Settings));
            var revision = loaded.Revision;
            var old = settings.Setup!.Routes.SingleOrDefault(r => r.Role == role);
            var updated = select(settings);
            var chosen = updated.Setup!.Routes.Single(r => r.Role == role);
            var reused = false;
            if (key is null && chosen is { CredentialId: null, RouteType: SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs })
            {
                var service = setupService;
                foreach (var setAside in chosen.RouteType == SetupRouteType.ElevenLabs
                             ? SetupSettings.SetAsideElevenLabsCredentials(updated)
                             : SetupSettings.SetAsideCredentials(updated, role, chosen.RouteType == SetupRouteType.ChatCompletions ? chosen.Origin : null))
                {
                    // Only a key still in Windows Credential Manager is used again; it is read on a worker and never shown.
                    var candidate = SetupSettings.ReattachSetAsideCredential(updated, setAside);
                    if (await Task.Run(() => service.CheckCredential(candidate, role), token) != CredentialError.None) continue;
                    updated = candidate;
                    reused = true;
                    break;
                }
            }
            if (key is null && missingKey is not null && updated.Setup!.Routes.Single(r => r.Role == role).CredentialId is null)
                throw new InvalidOperationException(missingKey);
            updated = SetupSettings.QueueReplacedCredential(updated, old);
            if (key is not null)
            {
                var staged = await setupService.SaveAsync(updated, revision, token);
                if (!staged.Save.Saved) throw new InvalidOperationException(staged.Summary);
                var service = setupService;
                var stored = await Task.Run(() => service.ReplaceCredentialAsync(staged.Settings, staged.Save.Revision, role, key, token), token);
                if (!stored.Save.Saved) throw new InvalidOperationException(stored.Summary);
                updated = stored.Settings;
                revision = stored.Save.Revision;
            }
            var route = updated.Setup!.Routes.Single(r => r.Role == role);
            var confirmed = SetupSettings.ReplaceRoute(updated, route with { Consent = route.Selection() });
            var saved = await setupService.SaveAsync(confirmed, revision, token);
            if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
            homeSettings = confirmed;
            FollowSavedSetup(saved.Save.Revision);
            pendingJobHosts.Remove(role);
            pendingJobVoices.Remove(role);
            RecordClusterJob(job.Job, new(null, false));
            ActionText.Text = (reused ? done.TrimEnd('.') + " (it uses the key you saved for it before)." : done) + OpenConversationFollows;
            tabPlace.Remove(openTab ?? CompanionTab.Thinking);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
            return false;
        }
        finally
        {
            turn?.Dispose();
            if (!closing)
            {
                tabEdited = false;
                RenderHome();
            }
        }
    }
}

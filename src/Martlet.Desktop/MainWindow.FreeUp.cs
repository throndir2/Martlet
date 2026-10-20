using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Home's Martlet on this PC card and its Warming up indicator. A companion PC often runs games and other heavy
/// programs, so you don't always want Martlet to hold its memory. The card says what Martlet holds on this PC now: the models
/// Ollama on this PC has loaded (with the memory Ollama says they take), Parakeet in memory for listening, the voice-recognition
/// engine, the character and the conversation. Free up this PC ends the conversation (listening and watching stop with it),
/// hides the character and takes the models out of memory: every model Ollama on this PC has loaded (keep_alive 0), Parakeet
/// and the voice-recognition engine. Settings stay as they are. Martlet then rests: While gaming stops keeping the model
/// loaded, and nothing loads again until you start talking or listening, show the character or a reply needs it (Discord,
/// messaging). Starting again warms Martlet up: Home's Warming up indicator and the talk window's banner say what still loads,
/// for how long, and when it is done. Loopback only: Ollama's /api/ps every 5 seconds while Home shows, never while Martlet
/// replies or hears you, so a reply never waits for it.</summary>
public partial class MainWindow
{
    private static readonly HttpClient PcClient = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly TimeSpan PcModelsEvery = TimeSpan.FromSeconds(5);
    private IReadOnlyList<OllamaLoadedModel> pcModels = [];
    private long pcModelsAt;
    private bool pcModelsBusy, freeingUp, pcHostService;
    /// <summary>What the last Free up this PC did, while Martlet rests after it; null otherwise.</summary>
    private FreeUpResult? resting;
    /// <summary>The models Ollama still had loaded after Free up this PC (it refused to unload them): they don't end the rest.</summary>
    private HashSet<string> restingLeft = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When the character started to show (0 while it isn't starting).</summary>
    private long characterStartingAt;
    private long warmupStartedAt, warmupDoneAt;
    private bool warmupShown, warmupSpinning;
    private TimeSpan warmupTook;
    private string? pcCardSignature, warmupSignature;

    private bool HomeShowing => !closing && IsVisible && WindowState != WindowState.Minimized && HomePage.IsVisible &&
        Role == DeviceRole.Companion;

    /// <summary>Reads what Ollama on this PC has loaded (<c>/api/ps</c>) every 5 seconds while Home shows on a companion PC, or at
    /// once with <paramref name="now"/>. It waits while Martlet replies or hears you.</summary>
    private void PollPcModels(bool now = false)
    {
        if (pcModelsBusy || closing || Role != DeviceRole.Companion) return;
        if (!now && (!HomeShowing || Talking || pcModelsAt != 0 && Stopwatch.GetElapsedTime(pcModelsAt) < PcModelsEvery)) return;
        pcModelsBusy = true;
        var token = lifetime.Token;
        Task.Run(async () =>
        {
            var loaded = await ReadPcModelsAsync(token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                pcModelsBusy = false;
                pcModelsAt = Stopwatch.GetTimestamp();
                if (closing) return;
                pcModels = loaded;
                RenderPcCard(refreshHost: true);
            });
        }).Forget();
    }

    /// <summary>What Ollama on this PC has loaded; empty when it doesn't answer (or while the Ollama FIXTURE stands in for it).</summary>
    private static async Task<IReadOnlyList<OllamaLoadedModel>> ReadPcModelsAsync(CancellationToken token)
    {
        if (SimulatedOllamaCrashLoop.Active) return [];
        try { return await OllamaSideBySide.LoadedAsync(PcClient, LocalOllama.OriginUri, token).ConfigureAwait(false) ?? []; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException) { return []; }
    }

    /// <summary>What Martlet holds on this PC now; nothing on a host PC.</summary>
    private PcHoldings PcHoldingsNow() => Role != DeviceRole.Companion ? PcHoldings.Nothing
        : new(pcModels, parakeet?.InMemory, localVoices.Loaded, avatar.IsShowing, openConversation is not null,
            openConversation is { IsListening: true }, openConversation is { IsWatching: true });

    /// <summary>Home's Martlet on this PC card: what Martlet holds here, or that it rests after Free up this PC, and the button.
    /// <paramref name="refreshHost"/> reads again whether this PC runs a host service (it builds the network map, so not on
    /// every tick).</summary>
    private void RenderPcCard(bool refreshHost = false)
    {
        if (closing || PcCard is null) return;
        var companion = Role == DeviceRole.Companion;
        PcCard.Visibility = companion ? Visibility.Visible : Visibility.Collapsed;
        if (!companion) return;
        if (refreshHost) pcHostService = ThisPcHost() is not null;
        var held = PcHoldingsNow();
        // The rest ends once Martlet holds something again: you talk, listen or show the character, or a reply loads a model.
        if (resting is not null && !freeingUp && (held.Conversation || held.Character || held.Listening is not null ||
            held.Models.Any(m => !restingLeft.Contains(m.Name)) || WarmupItemsNow().Count > 0))
        {
            resting = null;
            restingLeft.Clear();
        }
        var (title, text, icon) = freeingUp
            ? ("Freeing up this PC…", "Martlet is ending the conversation, hiding the character and unloading its models.", "\uE895")
            : resting is { } rest ? ("Resting", CompanionWarmup.RestingLine(rest), "\uE708")
            : ("Martlet on this PC", CompanionWarmup.HoldingsLine(held), "\uE7F4");
        if (pcHostService) text += " This PC's host service keeps running for your other computers.";
        var canFree = !freeingUp && held.Any;
        var signature = $"{title}|{text}|{icon}|{canFree}|{freeingUp}";
        if (signature == pcCardSignature) return;
        pcCardSignature = signature;
        PcCardTitle.Text = title;
        PcCardText.Text = text;
        PcCardIcon.Text = icon;
        FreeUpButton.IsEnabled = canFree;
        FreeUpText.Text = freeingUp ? "Freeing up…" : "Free up this PC";
        AutomationProperties.SetName(FreeUpButton, FreeUpText.Text);
        var tip = freeingUp ? "Martlet is freeing up this PC."
            : canFree ? "End the conversation, hide the character and unload Martlet's models from this PC's memory and graphics card " +
                "(your settings stay, and Start talking or Start listening loads them again)."
            : "Martlet holds nothing to free on this PC now.";
        FreeUpButton.ToolTip = tip;
        AutomationProperties.SetHelpText(FreeUpButton, tip);
    }

    private async void FreeUp_Click(object sender, RoutedEventArgs e) => await FreeUpAsync();

    /// <summary>Free up this PC: ends the conversation, hides the character, unloads every model Ollama on this PC has loaded,
    /// Parakeet and the voice-recognition engine, then Martlet rests. Settings and the conversation record stay.</summary>
    private async Task FreeUpAsync()
    {
        if (closing || freeingUp || Role != DeviceRole.Companion) return;
        freeingUp = true;
        RenderPcCard();
        var problems = new List<string>();
        try
        {
            var ended = false;
            if (openConversation is { } talk)
            {
                // Ending it first means nothing asks Ollama to load the model again while it unloads.
                talk.End();
                ended = true;
            }
            var hid = false;
            if (avatar.IsShowing)
            {
                hid = await StopAvatarSafelyAsync();
                if (!hid) problems.Add("The character didn't close cleanly.");
                UpdateCharacterButton();
            }
            var listening = parakeet;
            var voices = localVoices;
            var token = lifetime.Token;
            var (unloaded, graphics, memory, left, unloadProblems) = await Task.Run(async () =>
            {
                var freed = await UnloadPcModelsAsync(token).ConfigureAwait(false);
                if (listening?.Unload() is { } name) freed.Unloaded.Add(name);
                if (voices.Unload()) freed.Unloaded.Add("voice recognition");
                // Hand the freed memory back to Windows now rather than at the next collection.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                return freed;
            });
            problems.AddRange(unloadProblems);
            if (closing) return;
            var result = new FreeUpResult(ended, hid, unloaded, graphics, memory, problems);
            resting = result;
            restingLeft = new(left.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            pcModels = left;
            pcModelsAt = Stopwatch.GetTimestamp();
            var line = CompanionWarmup.FreedLine(result);
            ErrorLog.Info("Free up this PC: " + line);
            ActionText.Text = line + " Start talking or Start listening warms it up again.";
        }
        catch (OperationCanceledException) when (closing) { }
        finally
        {
            freeingUp = false;
            if (!closing)
            {
                RenderPcCard(refreshHost: true);
                RenderListening();
                UpdateTray();
            }
        }
    }

    /// <summary>Has Ollama on this PC unload every model it has loaded (keep_alive 0), then waits up to 10 seconds until it no
    /// longer lists them (it answers before they have left memory). Loopback only.</summary>
    private static async Task<(List<string> Unloaded, long Graphics, long Memory, IReadOnlyList<OllamaLoadedModel> Left, List<string> Problems)>
        UnloadPcModelsAsync(CancellationToken token)
    {
        var unloaded = new List<string>();
        var problems = new List<string>();
        long graphics = 0, memory = 0;
        var loaded = await ReadPcModelsAsync(token).ConfigureAwait(false);
        foreach (var model in loaded)
        {
            if (await OllamaSideBySide.LoadAsync(PcClient, LocalOllama.OriginUri, model.Name, unload: true, TimeSpan.FromSeconds(15), token)
                .ConfigureAwait(false))
            {
                unloaded.Add(model.Name);
                var (onCard, inMemory) = CompanionWarmup.Split(model);
                graphics += onCard;
                memory += inMemory;
            }
            else problems.Add($"Ollama on this PC didn't unload {model.Name}.");
        }
        var left = loaded;
        for (var i = 0; i < 20 && unloaded.Count > 0; i++)
        {
            left = await ReadPcModelsAsync(token).ConfigureAwait(false);
            if (!left.Any(m => unloaded.Contains(m.Name, StringComparer.OrdinalIgnoreCase))) break;
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        foreach (var still in left.Where(m => unloaded.Contains(m.Name, StringComparer.OrdinalIgnoreCase)))
            problems.Add($"Ollama on this PC still lists {still.Name} as loaded.");
        return (unloaded, graphics, memory, left, problems);
    }

    // ---------- warming up ----------

    /// <summary>What still loads for talking on this PC: the conversation's parts (its settings, Thinking's model in Ollama on
    /// this PC), Parakeet for listening and the character.</summary>
    private IReadOnlyList<WarmupItem> WarmupItemsNow()
    {
        if (closing || Role != DeviceRole.Companion) return [];
        var items = new List<WarmupItem>();
        if (openConversation is { } talk) items.AddRange(talk.Warmup);
        items.AddRange(LocalWarmup());
        return items;
    }

    /// <summary>What loads on this PC outside the conversation: Parakeet for listening and the character. The talk window's
    /// banner shows them too (<see cref="LiveConversationWindow.MoreWarmup"/>).</summary>
    private IReadOnlyList<WarmupItem> LocalWarmup()
    {
        var items = new List<WarmupItem>();
        if (parakeet?.Warming is { } name) items.Add(new(WarmupPart.Listening, name, Stopwatch.GetElapsedTime(parakeet.WarmingSince)));
        if (characterStartingAt != 0) items.Add(new(WarmupPart.Character, null, Stopwatch.GetElapsedTime(characterStartingAt)));
        return items;
    }

    /// <summary>Has Parakeet load when Listening uses it on this PC, so the first thing said isn't slower: as Martlet starts, and
    /// when a conversation starts again (after Free up this PC unloaded it).</summary>
    private void WarmListening()
    {
        if (Role == DeviceRole.Companion &&
            homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Stt) is { RouteType: SetupRouteType.LocalParakeet } listening)
            parakeet?.WarmAsync(listening.ModelId).Forget();
    }

    /// <summary>Home's Warming up indicator: what still loads and for how long, then "Warmed up in 18 s." for a few seconds.</summary>
    private void RenderWarmup()
    {
        if (closing || WarmupIndicator is null) return;
        var raw = WarmupItemsNow();
        var items = CompanionWarmup.Showing(raw);
        var now = Stopwatch.GetTimestamp();
        if (raw.Count > 0)
        {
            if (warmupStartedAt == 0) warmupStartedAt = now - (long)(raw.Max(i => i.Elapsed).TotalSeconds * Stopwatch.Frequency);
            if (items.Count > 0 && !warmupShown)
            {
                warmupShown = true;
                ErrorLog.Info("Martlet is warming up: " + string.Join(", ", items.Select(i => i.Part + (i.Name is { } name ? " " + name : ""))) + ".");
            }
            warmupDoneAt = 0;
        }
        else if (warmupStartedAt != 0)
        {
            if (warmupShown)
            {
                warmupTook = Stopwatch.GetElapsedTime(warmupStartedAt);
                warmupDoneAt = now;
                ErrorLog.Info($"Martlet warmed up in {warmupTook.TotalSeconds:0.0} s.");
            }
            warmupStartedAt = 0;
            warmupShown = false;
        }
        var done = warmupDoneAt != 0 && Stopwatch.GetElapsedTime(warmupDoneAt) < CompanionWarmup.DoneFor;
        var text = items.Count > 0 ? CompanionWarmup.Line(items) + " " + CompanionWarmup.Advice(items, talkWindow: false)
            : done ? CompanionWarmup.Done(warmupTook) : null;
        if ((text ?? "") == warmupSignature) return;
        warmupSignature = text ?? "";
        WarmupIndicator.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        WarmupStatusText.Text = text ?? "";
        var spinning = items.Count > 0;
        WarmupSpinner.Visibility = WarmupTrack.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        WarmupDot.Visibility = spinning ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(WarmupIndicator, spinning ? "Warming up" : "Warmed up");
        if (spinning == warmupSpinning) return;
        warmupSpinning = spinning;
        Motion.Spin(WarmupSpinner, spinning);
        if (!spinning && text is not null) Motion.Blink(WarmupDot);
    }
}

using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Martlet.Companion.Platform;
using Martlet.Core.Cluster;
using Martlet.Providers;

namespace Martlet.Companion;

public sealed partial class MainWindow : Window
{
    private const string SilentLabel = "Silent (text only)";
    private readonly CompanionPlatform platform;
    private readonly CompanionGuardrails guardrails;
    private readonly CompanionSettingsStore store;
    private readonly CompanionConversation conversation;
    private readonly IVoiceAudio audio;
    private CompanionSettings settings;
    private CharacterServer? server;
    private CharacterWindow? character;
    private IVoiceRecording? recording;
    private IVoicePlayback? playback;
    private CancellationTokenSource? turn;
    private HotkeyGesture? gesture;

    /// <summary>The tray/menu bar state, for the app's tray.</summary>
    public event Action<TrayState, string>? StateChanged;

    public MainWindow() : this(CompanionPlatform.Defaults(), CompanionPlatform.Defaults().Probe.Probe(), new SoundFlowAudio(),
        new CompanionSettingsStore(Path.Combine(Path.GetTempPath(), "martlet-companion-designer"))) { }

    public MainWindow(CompanionPlatform platform, PlatformInfo info, IVoiceAudio audio, CompanionSettingsStore store)
    {
        this.platform = platform;
        this.audio = audio;
        this.store = store;
        guardrails = new CompanionGuardrails(info);
        conversation = new CompanionConversation(platform.Credentials);
        InitializeComponent();
        (settings, var refusals) = store.Load(guardrails);
        FillChoices();
        ShowSettings(settings);
        ShowRefusals(refusals);
        StatusText.Text = CompanionStatus.Json(platform, info);
        KeyNote.Text = platform.Credentials.Status.Reason;

        SendButton.Click += async (_, _) => await SendTypedAsync();
        Message.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendTypedAsync(); } };
        TalkButton.AddHandler(PointerPressedEvent, (_, _) => StartTalking(), RoutingStrategies.Tunnel);
        TalkButton.AddHandler(PointerReleasedEvent, async (_, _) => await StopTalkingAsync(), RoutingStrategies.Tunnel);
        StopButton.Click += (_, _) => StopTurn();
        ClearButton.Click += (_, _) => { conversation.Clear(); Transcript.Text = ""; };
        CharacterToggle.IsCheckedChanged += (_, _) => ToggleCharacter(CharacterToggle.IsChecked == true);
        SaveButton.Click += async (_, _) => await SaveAsync();
        ImportButton.Click += async (_, _) => await ImportAsync();
        BrowseCharacter.Click += async (_, _) => await BrowseCharacterAsync();
        Thinking.SelectionChanged += (_, _) => ShowThinkingWarnings();
        ChatBaseUrl.TextChanged += (_, _) => ShowThinkingWarnings();
        PresetOllama.Click += (_, _) => Preset("http://127.0.0.1:11434/v1");
        PresetLmStudio.Click += (_, _) => Preset("http://127.0.0.1:1234/v1");
        PresetDockerModelRunner.Click += (_, _) => Preset("http://127.0.0.1:12434/engines/v1");
        FindLocalModels.Click += async (_, _) => await FindLocalModelsAsync();
        KeyDown += (_, e) => { if (Matches(e)) { e.Handled = true; StartTalking(); } };
        KeyUp += async (_, e) => { if (Matches(e)) { e.Handled = true; await StopTalkingAsync(); } };
        platform.Hotkey.Pressed += (_, _) => Dispatcher.UIThread.Post(StartTalking);
        platform.Hotkey.Released += (_, _) => Dispatcher.UIThread.Post(() => _ = StopTalkingAsync());
        Opened += async (_, _) =>
        {
            await RegisterHotkeyAsync();
            if (settings.ShowCharacter) CharacterToggle.IsChecked = true;
            SetState(TrayState.Idle, "Ready");
        };
        Closed += (_, _) =>
        {
            turn?.Cancel();
            character?.Close();
            server?.Dispose();
            playback?.Dispose();
            recording?.Dispose();
            audio.Dispose();
            conversation.Dispose();
        };
    }

    // ---- settings ----

    private void FillChoices()
    {
        Fill(Thinking, guardrails.Offered(ClusterJobs.Thinking));
        Fill(Listening, guardrails.Offered(ClusterJobs.Listening));
        Fill(Speaking, guardrails.Offered(ClusterJobs.Speaking), SilentLabel);
        OpenAiModel.ItemsSource = OpenAiTextGenerationCatalog.SupportedModelIds;
        Voice.ItemsSource = OpenAiSpeechSynthesisCatalog.SupportedVoices;
        var hidden = CompanionGuardrails.Jobs.SelectMany(guardrails.Options).Where(o => !o.Offered).ToList();
        NotOffered.Text = hidden.Count == 0 ? "" : "Not offered on this computer:\n" +
            string.Join("\n", hidden.Select(o => $"• {o.Name}: {o.Reason}"));
    }

    private static void Fill(ComboBox box, IEnumerable<EngineOption> options, string? silent = null)
    {
        var items = options.Select(o => new ComboBoxItem { Content = o.Name, Tag = o.Id }).ToList();
        if (silent is not null) items.Add(new ComboBoxItem { Content = silent, Tag = CompanionSettings.Silent });
        box.ItemsSource = items;
    }

    private static void Select(ComboBox box, string id)
    {
        var items = (box.ItemsSource as IEnumerable<ComboBoxItem>)?.ToList() ?? [];
        box.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == id) ?? items.FirstOrDefault();
    }

    private static string Selected(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void ShowSettings(CompanionSettings value)
    {
        CharacterName.Text = value.CharacterName;
        Persona.Text = value.Persona;
        CharacterModelPath.Text = value.CharacterModel ?? "";
        Select(Thinking, value.Thinking);
        OpenAiModel.SelectedItem = OpenAiTextGenerationCatalog.SupportsModel(value.OpenAiModel) ? value.OpenAiModel : OpenAiTextGenerationCatalog.DefaultModelId;
        ChatBaseUrl.Text = value.ChatBaseUrl;
        ChatModel.Text = value.ChatModel;
        Select(Listening, value.Listening);
        Select(Speaking, value.Speaking);
        Voice.SelectedItem = OpenAiSpeechSynthesisCatalog.SupportsVoice(value.Voice) ? value.Voice : OpenAiSpeechSynthesisCatalog.DefaultVoice;
        PushToTalkKey.Text = value.PushToTalkKey;
        CloudConsent.IsChecked = value.CloudConsent;
        TalkButton.Content = $"Hold to talk ({value.PushToTalkKey})";
        ShowThinkingWarnings();
    }

    private CompanionSettings ReadSettings() => settings with
    {
        CharacterName = string.IsNullOrWhiteSpace(CharacterName.Text) ? "Martlet" : CharacterName.Text.Trim(),
        Persona = string.IsNullOrWhiteSpace(Persona.Text) ? new CompanionSettings().Persona : Persona.Text.Trim(),
        CharacterModel = string.IsNullOrWhiteSpace(CharacterModelPath.Text) ? null : CharacterModelPath.Text.Trim(),
        Thinking = Selected(Thinking, settings.Thinking),
        OpenAiModel = OpenAiModel.SelectedItem as string ?? settings.OpenAiModel,
        ChatBaseUrl = ChatBaseUrl.Text?.Trim() ?? "",
        ChatModel = ChatModel.Text?.Trim() ?? "",
        Listening = Selected(Listening, settings.Listening),
        Speaking = Selected(Speaking, settings.Speaking),
        Voice = Voice.SelectedItem as string ?? settings.Voice,
        PushToTalkKey = string.IsNullOrWhiteSpace(PushToTalkKey.Text) ? settings.PushToTalkKey : PushToTalkKey.Text.Trim(),
        CloudConsent = CloudConsent.IsChecked == true
    };

    private async Task SaveAsync()
    {
        var (admitted, refusals) = guardrails.Admit(ReadSettings(), settings);
        settings = admitted;
        store.Save(settings);
        if (!string.IsNullOrWhiteSpace(OpenAiKey.Text))
            await platform.Credentials.SetAsync(CompanionSettings.OpenAiKey, OpenAiKey.Text.Trim(), CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(ChatKey.Text))
            await platform.Credentials.SetAsync(CompanionSettings.ChatCompletionsKey, ChatKey.Text.Trim(), CancellationToken.None);
        OpenAiKey.Text = ChatKey.Text = "";
        ShowSettings(settings);
        ShowRefusals(refusals);
        await RegisterHotkeyAsync();
        if (character is not null) ShowCharacterModel();
        Status("Settings saved" + (platform.Credentials.IsPersistent ? "." : "; keys are kept until Martlet quits."));
    }

    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Settings from another device", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Martlet settings") { Patterns = ["*.json"] }]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var (imported, refusals) = CompanionSettingsStore.Import(await File.ReadAllTextAsync(path), guardrails, settings);
            settings = imported;
            store.Save(settings);
            ShowSettings(settings);
            ShowRefusals(refusals);
            Status(refusals.Count == 0 ? "Settings imported." : $"Settings imported; {refusals.Count} choice(s) this computer can't run were refused.");
        }
        catch (System.Text.Json.JsonException error) { Status("That file isn't Martlet settings: " + error.Message); }
    }

    private void ShowRefusals(IReadOnlyList<string> refusals) =>
        Refusals.Text = refusals.Count == 0 ? "" : "Refused (this computer can't run them):\n" + string.Join("\n", refusals.Select(r => "• " + r));

    private void ShowThinkingWarnings()
    {
        var engine = Selected(Thinking, settings.Thinking);
        var chat = engine == "chat-completions";
        ChatBaseUrl.IsVisible = ChatModel.IsVisible = ChatKey.IsVisible = PresetOllama.IsVisible =
            PresetLmStudio.IsVisible = PresetDockerModelRunner.IsVisible = FindLocalModels.IsVisible = LocalModelsFound.IsVisible = chat;
        OpenAiModel.IsVisible = engine == "openai-llm";
        ThinkingWarnings.Text = string.Join("\n", guardrails.ThinkingWarnings(engine, ChatBaseUrl.Text));
    }

    private void Preset(string url)
    {
        ChatBaseUrl.Text = url;
        Select(Thinking, "chat-completions");
    }

    /// <summary>Find model apps: asks this computer's loopback ports which model apps answer (Ollama, LM Studio, llama.cpp,
    /// Docker Model Runner and others) and fills in the first with a model. Nothing leaves this computer; nothing is saved.</summary>
    private async Task FindLocalModelsAsync()
    {
        FindLocalModels.IsEnabled = false;
        LocalModelsFound.Text = "Looking for model apps on this computer...";
        try
        {
            var found = await LocalModelServers.DetectAsync();
            LocalModelsFound.Text = LocalModelChoice.Describe(found);
            if (LocalModelChoice.Pick(found, ChatModel.Text) is { } pick)
            {
                Preset(pick.BaseUrl);
                if (pick.Model is not null) ChatModel.Text = pick.Model;
            }
        }
        finally { FindLocalModels.IsEnabled = true; }
    }

    private async Task BrowseCharacterAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a character", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("VRM or Live2D") { Patterns = ["*.vrm", "*.glb", "*.model3.json"] }]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) CharacterModelPath.Text = path;
    }

    private async Task RegisterHotkeyAsync()
    {
        gesture = CompanionSettings.ParseGesture(settings.PushToTalkKey);
        if (gesture is null) return;
        var status = await platform.Hotkey.RegisterAsync(gesture, CancellationToken.None);
        if (!status.Available) Status($"{gesture} works while Martlet's window is in front. " + status.Reason);
    }

    private bool Matches(KeyEventArgs e)
    {
        if (gesture is null || e.Source is TextBox || !string.Equals(e.Key.ToString(), gesture.Key, StringComparison.OrdinalIgnoreCase)) return false;
        var modifiers = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyModifiers.Meta;
        return modifiers == gesture.Modifiers;
    }

    // ---- conversation ----

    private async Task SendTypedAsync()
    {
        var text = Message.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        Message.Text = "";
        await TurnAsync(text);
    }

    private void StartTalking()
    {
        if (recording is not null) return;
        StopTurn();
        try
        {
            recording = audio.StartRecording();
            SetState(TrayState.Listening, "Listening… release to send");
        }
        catch (Exception error) when (error is CompanionException or InvalidOperationException or DllNotFoundException)
        {
            recording = null;
            SetState(TrayState.Problem, "The microphone couldn't start: " + error.Message);
        }
    }

    private async Task StopTalkingAsync()
    {
        var current = recording;
        if (current is null) return;
        recording = null;
        var pcm = current.Stop();
        var rate = current.SampleRate;
        current.Dispose();
        if (pcm.Length < rate * 2 * 3 / 10)
        {
            SetState(TrayState.Idle, "Too short; hold the key while you talk.");
            return;
        }
        SetState(TrayState.Thinking, "Listening to what you said…");
        try
        {
            var heard = await conversation.TranscribeAsync(ReadSettings(), pcm, rate, CancellationToken.None);
            if (heard is null) { SetState(TrayState.Idle, "Nothing was heard."); return; }
            await TurnAsync(heard);
        }
        catch (Exception error) when (error is CompanionException or HttpRequestException or Core.Contracts.ContractException)
        {
            SetState(TrayState.Problem, error.Message);
        }
    }

    private async Task TurnAsync(string text)
    {
        StopTurn();
        var cancel = turn = new CancellationTokenSource();
        var current = ReadSettings();
        Append($"You: {text}\n{current.CharacterName}: ");
        SetState(TrayState.Thinking, "Thinking…");
        var reply = new StringBuilder();
        try
        {
            await foreach (var delta in conversation.ReplyAsync(current, text, cancel.Token))
            {
                reply.Append(delta);
                Append(delta);
            }
            Append("\n\n");
            if (current.Speaking != CompanionSettings.Silent && reply.Length > 0)
            {
                SetState(TrayState.Speaking, "Speaking…");
                playback ??= audio.StartPlayback();
                await foreach (var pcm in conversation.SpeakAsync(current, reply.ToString(), cancel.Token))
                    playback.Write(pcm.Span);
                await playback.DrainAsync(cancel.Token);
            }
            SetState(TrayState.Idle, "Ready");
        }
        catch (OperationCanceledException) { Append("\n\n"); SetState(TrayState.Idle, "Stopped."); }
        catch (Exception error) when (error is CompanionException or HttpRequestException or Core.Contracts.ContractException or InvalidOperationException)
        {
            Append("\n\n");
            SetState(TrayState.Problem, error.Message);
        }
    }

    private void StopTurn()
    {
        turn?.Cancel();
        playback?.Stop();
    }

    private void Append(string text)
    {
        Transcript.Text += text;
        Transcript.CaretIndex = Transcript.Text?.Length ?? 0;
    }

    private void Status(string text) => StatusLine.Text = text;

    private void SetState(TrayState state, string text)
    {
        Status(text);
        StateChanged?.Invoke(state, text);
    }

    // ---- character ----

    /// <summary>The tray's "show or hide the character".</summary>
    public void ToggleCharacterFromTray() => CharacterToggle.IsChecked = CharacterToggle.IsChecked != true;

    private void ToggleCharacter(bool show)
    {
        if (!show)
        {
            character?.Hide();
            return;
        }
        if (!CharacterModel.BundleBuilt)
        {
            Status("The character renderer isn't in this build (npm ci in src/Martlet.Avatar.Vrm, then build Martlet.Companion).");
            CharacterToggle.IsChecked = false;
            return;
        }
        server ??= new CharacterServer(CharacterModel.BundleFolder);
        if (character is null)
        {
            character = new CharacterWindow(server, platform.Overlay, () => playback?.Level ?? 0);
            character.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => CharacterState.Text = "Character: " + state);
            character.Closed += (_, _) => { character = null; CharacterToggle.IsChecked = false; };
        }
        character.Show();
        ShowCharacterModel();
    }

    private void ShowCharacterModel()
    {
        try { character?.ShowModel(CharacterModel.Resolve(settings.CharacterModel)); }
        catch (CompanionException error) { Status(error.Message); }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Logging;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What models hear and see (model-abilities.json, shared as <c>model-abilities</c>): kept when a model's server metadata
/// says so (choosing, testing or checking a model; MainWindow.ContextCheck.cs), when Test hearing asks a model to say a spoken
/// test word or Test vision asks it to read a written one, and when a model refuses a recording or a picture in use. Test hearing
/// and Test vision ask the Thinking model (Companion › Listening and Vision) or an image or audio model of its own
/// (MainWindow.SenseModels.cs).</summary>
public partial class MainWindow
{
    /// <summary>A model that Test hearing or Test vision asks directly on its OpenAI-compatible server: the base URL, the model,
    /// how its key is read (on a worker thread; null: none), the server's name, where the request goes in words, and whether it
    /// stays on this PC.</summary>
    private sealed record ModelProbe(string Origin, string ModelId, Func<string?> Key, string Server, string Destination, bool Local)
    {
        /// <summary>The test of <paramref name="kind"/> on this model: its result shows wherever the same model is tested.</summary>
        internal string Test(SenseKind kind) => TestOf(kind, Origin, ModelId);
    }

    private static string TestOf(SenseKind kind, string origin, string model) => $"{kind}|{origin}|{model}";

    private readonly HashSet<string> modelTestsRunning = new(StringComparer.Ordinal);
    /// <summary>What the last Test hearing or Test vision of each model found (never anything you said or showed).</summary>
    private readonly Dictionary<string, string> modelTestResults = new(StringComparer.Ordinal);

    private ModelAbilities SavedModelAbilities() => ModelAbilities.Load(store?.DataDirectory);

    /// <summary>Keeps what Martlet found out about a model, lets a running conversation use it at once and shows it on an open
    /// Listening or Vision page. Another computer gets it on the next settings sync.</summary>
    private void RecordModelAbility(ModelAbility ability)
    {
        var directory = store?.DataDirectory;
        if (directory is null) return;
        var before = ModelAbilities.Load(directory);
        var known = before.Find(ability.Origin, ability.ModelId);
        var after = before.With(ability);
        if (!after.Save(directory))
        {
            ErrorLog.Warn("Martlet couldn't save what it found out about a model (model-abilities.json).");
            return;
        }
        var now = after.Find(ability.Origin, ability.ModelId)!;
        if (known?.Hears != now.Hears || known?.Sees != now.Sees)
            ErrorLog.Info($"{ability.ModelId}: {Senses(now)} ({ability.Source}).");
        conversation?.ReloadAbilities();
        // Where pictures and recordings go follows at once (the desktop's status file and log say it again).
        conversation?.ReloadSenseModels();
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision or CompanionTab.Hearing && !tabEdited) RenderTab();
        QueueSettingsSync();
    }

    private static string Senses(ModelAbility ability) =>
        $"{(ability.Hears switch { true => "hears recordings", false => "doesn't hear recordings", _ => "hearing not known" })}, " +
        $"{(ability.Sees switch { true => "sees pictures", false => "doesn't see pictures", _ => "vision not known" })}";

    /// <summary>The Thinking route as a test target. Only an OpenAI-compatible endpoint (Ollama on this PC included) is asked
    /// directly.</summary>
    private ModelProbe? ThinkingProbe(SetupRoute? thinking) =>
        thinking?.RouteType == SetupRouteType.ChatCompletions
            ? new(thinking.Origin, thinking.ModelId, () => RouteKey(thinking), ContextServerName(thinking),
                LiveConversationConfiguration.LlmDestinationName(thinking), OnThisPc(thinking.Origin))
            : null;

    /// <summary>An image or audio model of its own on an endpoint as a test target, with the key it uses.</summary>
    private ModelProbe? OwnProbe(DeepThinkingSettings? own, SetupRoute? thinking) =>
        own is { Place: DeepThinkingPlace.Endpoint, Origin: { } origin, ModelId: { } model }
            ? new(origin, model, () => OwnKey(own, thinking), ServerName(origin), own.Describe(), OnThisPc(origin))
            : null;

    private static bool OnThisPc(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && ModelContextProbe.IsLoopback(uri);

    /// <summary>The key of an image or audio model of its own: its own (Windows Credential Manager), else Thinking's for the same
    /// base URL, else none; null when it can't be read.</summary>
    private string? OwnKey(DeepThinkingSettings own, SetupRoute? thinking)
    {
        if (own.CredentialId is not { } id) return own.UsesThinkingKey(thinking) ? RouteKey(thinking!) : null;
        if (homeSettings is null) return null;
        using var read = new WindowsCredentialStore().Read(own.Binding(homeSettings.Profile.Id, id));
        if (read.Error != CredentialError.None || read.Secret is null) return null;
        string? key = null;
        read.Secret.Use(secret => key = new string(secret));
        return key;
    }

    /// <summary>Companion › Listening's Test hearing for the Thinking model: the button, what it does and what the last test found.
    /// Thinking on a paired computer is asked through its gateway.</summary>
    private UIElement[] HearingTestControls(SetupRoute? thinking)
    {
        if (thinking is not null && LiveConversationConfiguration.Target(thinking, SetupRouteType.GatewayOllama) is { } host)
        {
            var test = TestOf(SenseKind.Audio, thinking.Origin, thinking.ModelId);
            var running = modelTestsRunning.Contains(test);
            var button = new Button
            {
                Content = running ? "Testing hearing..." : "Test hearing", HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6), IsEnabled = !running
            };
            AutomationProperties.SetAutomationId(button, "TalkHearVoiceTest");
            button.Click += (_, _) => TestHostHearingAsync(thinking, host).Forget();
            var line = Note(modelTestResults.GetValueOrDefault(test) ??
                $"Sends {thinking.ModelId} one short recording of a single word, made by Windows speech (never your voice), through " +
                $"{host.HostId}'s paired, pinned connection, and asks which word it heard.", new Thickness(0, 0, 0, 6));
            AutomationProperties.SetAutomationId(line, "TalkHearVoiceTestStatus");
            return [button, line];
        }
        return ModelTestControls(SenseKind.Audio, ThinkingProbe(thinking), "TalkHearVoiceTest", "TalkHearVoiceTestStatus",
            "Only an OpenAI-compatible endpoint (Ollama on this PC included) or Ollama on a paired computer can take a recording, so " +
            "there is nothing to test.");
    }

    /// <summary>Test hearing for Thinking on a paired computer (<see cref="HostHearingTest"/>). What it finds is kept by the
    /// computer's gateway origin, as a refused recording is.</summary>
    private async Task TestHostHearingAsync(SetupRoute thinking, HostTextTarget host)
    {
        var test = TestOf(SenseKind.Audio, thinking.Origin, thinking.ModelId);
        if (closing || !modelTestsRunning.Add(test)) return;
        modelTestResults[test] = $"Asking {thinking.ModelId} on {host.HostId} which word it hears...";
        ShowModelTest();
        try
        {
            var word = ModelHearingTest.Words[Random.Shared.Next(ModelHearingTest.Words.Count)];
            var pcm = await WindowsTestSpeech.SayAsync(ModelHearingTest.Spoken(word), lifetime.Token);
            var clip = BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = 24_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm);
            var report = await Task.Run(() => HostHearingTest.RunAsync(host, thinking.ModelId, clip, word, lifetime.Token), lifetime.Token);
            modelTestResults[test] = report.Summary + (report.Milliseconds is { } ms ? $" It answered in {ms:N0} ms." : "");
            ErrorLog.Info($"Test hearing: {report.Summary}");
            if (report.Hears is { } hears)
                RecordModelAbility(new() { Origin = thinking.Origin, ModelId = thinking.ModelId, Hears = hears, Source = "a test request",
                    CheckedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            modelTestResults[test] = "Couldn't test hearing: " + error.Message;
        }
        finally { EndModelTest(test); }
    }

    /// <summary>Test hearing (<paramref name="kind"/> Audio) or Test vision (Image) for <paramref name="probe"/>: the button, what
    /// it does and what the last test of that model found; <paramref name="unavailable"/> says why there is nothing to test.</summary>
    private UIElement[] ModelTestControls(SenseKind kind, ModelProbe? probe, string buttonId, string statusId, string unavailable)
    {
        var image = kind == SenseKind.Image;
        var running = probe is not null && modelTestsRunning.Contains(probe.Test(kind));
        var test = new Button
        {
            Content = running ? image ? "Testing vision..." : "Testing hearing..." : image ? "Test vision" : "Test hearing",
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), IsEnabled = probe is not null && !running
        };
        AutomationProperties.SetAutomationId(test, buttonId);
        if (probe is not null) test.Click += (_, _) => (image ? TestVisionAsync(probe) : TestHearingAsync(probe)).Forget();
        var found = probe is not null ? modelTestResults.GetValueOrDefault(probe.Test(kind)) : null;
        var status = Note(found ?? (probe is null ? unavailable : TestAbout(kind, probe.ModelId, probe.Local, probe.Destination)),
            new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, statusId);
        return [test, status];
    }

    /// <summary>What Test hearing or Test vision sends, and where.</summary>
    internal static string TestAbout(SenseKind kind, string model, bool local, string destination) =>
        (kind == SenseKind.Image
            ? $"Sends {model} one picture of a single word, drawn on this PC (never your screen), and asks which word it shows"
            : $"Sends {model} one short recording of a single word, made by Windows speech (never your voice), and asks which word it heard") +
        (local ? "; it stays on this PC." : $", to {destination}. It's one small request that may cost a little.") +
        " Choosing a model already asks its server what it takes, when the server says.";

    // A test started or ended: the open Listening or Vision page shows it.
    private void ShowModelTest()
    {
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision or CompanionTab.Hearing && !tabEdited) RenderTab();
    }

    private async Task TestHearingAsync(ModelProbe probe)
    {
        var test = probe.Test(SenseKind.Audio);
        if (closing || modelTestsRunning.Contains(test)) return;
        if (!probe.Local && !ConfirmationDialog.Confirm(this, $"Send {probe.ModelId} one short recording of a test word (made by Windows " +
                $"speech, not your voice) at {probe.Destination} to see whether it hears? It's one small request with your API key and may cost a little.",
                "Test hearing"))
            return;
        modelTestsRunning.Add(test);
        modelTestResults[test] = $"Asking {probe.ModelId} which word it hears...";
        ShowModelTest();
        try
        {
            var word = ModelHearingTest.Words[Random.Shared.Next(ModelHearingTest.Words.Count)];
            var pcm = await WindowsTestSpeech.SayAsync(ModelHearingTest.Spoken(word), lifetime.Token);
            var clip = BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = 24_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm);
            var key = await Task.Run(probe.Key, lifetime.Token);
            using var client = ModelContextProbe.CreateClient(probe.Local);
            var report = await ModelHearingTest.RunAsync(client, probe.Origin, probe.ModelId, key, clip, word, probe.Server, lifetime.Token);
            modelTestResults[test] = report.Summary + (report.Milliseconds is { } ms ? $" It answered in {ms:N0} ms." : "");
            ErrorLog.Info($"Test hearing: {report.Summary}");
            if (report.Hears is { } hears)
                RecordModelAbility(new() { Origin = probe.Origin, ModelId = probe.ModelId, Hears = hears, Source = "a test request",
                    CheckedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            modelTestResults[test] = "Couldn't test hearing: " + error.Message;
        }
        finally { EndModelTest(test); }
    }

    private async Task TestVisionAsync(ModelProbe probe)
    {
        var test = probe.Test(SenseKind.Image);
        if (closing || modelTestsRunning.Contains(test)) return;
        if (!probe.Local && !ConfirmationDialog.Confirm(this, $"Send {probe.ModelId} one picture of a test word (drawn on this PC, not your " +
                $"screen) at {probe.Destination} to see whether it sees? It's one small request with your API key and may cost a little.",
                "Test vision"))
            return;
        modelTestsRunning.Add(test);
        modelTestResults[test] = $"Asking {probe.ModelId} which word it sees...";
        ShowModelTest();
        try
        {
            var word = ModelVisionTest.Words[Random.Shared.Next(ModelVisionTest.Words.Count)];
            var picture = VisionTestPicture.Render(word);
            var key = await Task.Run(probe.Key, lifetime.Token);
            using var client = ModelContextProbe.CreateClient(probe.Local);
            var report = await ModelVisionTest.RunAsync(client, probe.Origin, probe.ModelId, key, picture, word, probe.Server, lifetime.Token);
            modelTestResults[test] = report.Summary + (report.Milliseconds is { } ms ? $" It answered in {ms:N0} ms." : "");
            ErrorLog.Info($"Test vision: {report.Summary}");
            if (report.Sees is { } sees)
                RecordModelAbility(new() { Origin = probe.Origin, ModelId = probe.ModelId, Sees = sees, Source = "a test request",
                    CheckedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            modelTestResults[test] = "Couldn't test vision: " + error.Message;
        }
        finally { EndModelTest(test); }
    }

    /// <summary>Test vision for an image model on a paired computer: the same question goes through its gateway with the image
    /// model's runner. While the route sends pictures to that model, the test waits in its lane like the model's own jobs
    /// (<see cref="LiveConversationController.RunSenseAsync"/>); otherwise (Martlet found it doesn't see) it goes straight to it
    /// (<see cref="LiveConversationController.TestSenseAsync"/>), so a new test can change that. Either way it waits while a reply
    /// needs that computer. What it finds is kept by the computer's gateway origin, as a refused picture is.</summary>
    private async Task TestHostVisionAsync(DeepThinkingSettings own)
    {
        if (own is not { Place: DeepThinkingPlace.Host, HostOrigin: { } origin, ModelId: { } model, HostId: { } host } || conversation is null)
            return;
        var test = TestOf(SenseKind.Image, origin, model);
        if (closing || !modelTestsRunning.Add(test)) return;
        var name = own.Describe();
        modelTestResults[test] = $"Asking {name} which word it sees...";
        ShowModelTest();
        try
        {
            var word = ModelVisionTest.Words[Random.Shared.Next(ModelVisionTest.Words.Count)];
            var job = new SenseJob
            {
                Purpose = "Test vision", Instructions = "This is a test of whether you can read a picture. Answer with only the word you read.",
                Text = ModelVisionTest.Question, Image = VisionTestPicture.Render(word), MaxOutputTokens = 32,
                Timeout = TimeSpan.FromSeconds(90), DropWhenStale = false
            };
            SenseAnswer answer;
            long milliseconds;
            if (conversation.SenseRoute(SenseKind.Image) is { Described: true, Model: { } routed } && routed.Key == own.Key)
            {
                var result = await conversation.RunSenseAsync(SenseKind.Image, job, lifetime.Token);
                answer = result.Outcome switch
                {
                    SenseJobOutcome.Succeeded => SenseAnswer.Done(result.Text!),
                    SenseJobOutcome.Refused => SenseAnswer.Rejected(result.Problem ?? $"{name} refused the picture"),
                    _ => SenseAnswer.Failed(result.Problem ?? "it didn't answer")
                };
                milliseconds = (long)result.Took.TotalMilliseconds;
            }
            else
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                answer = await conversation.TestSenseAsync(SenseKind.Image, own, job, lifetime.Token);
                milliseconds = watch.ElapsedMilliseconds;
            }
            var report = answer switch
            {
                { Text: { } text } => ModelVisionTest.Read(text, word, model, host, milliseconds),
                { Refused: true } => new VisionTestReport(false, $"{name} refused the picture, so it can't see.", true),
                _ => new VisionTestReport(null, $"Couldn't test {name}: {answer.Problem ?? "it didn't answer"}.", false)
            };
            modelTestResults[test] = report.Summary + (report.Milliseconds is { } ms ? $" It answered in {ms:N0} ms." : "");
            ErrorLog.Info($"Test vision: {report.Summary}");
            // The runner keeps a refused picture itself.
            if (answer.Text is not null && report.Sees is { } sees)
                RecordModelAbility(new() { Origin = origin, ModelId = model, Sees = sees, Source = "a test request", CheckedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            modelTestResults[test] = "Couldn't test vision: " + error.Message;
        }
        finally { EndModelTest(test); }
    }

    private void EndModelTest(string test)
    {
        modelTestsRunning.Remove(test);
        if (closing) return;
        ActionText.Text = modelTestResults.GetValueOrDefault(test) ?? "";
        ShowModelTest();
    }
}

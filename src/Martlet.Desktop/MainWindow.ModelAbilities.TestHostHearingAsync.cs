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

/// <summary>More of MainWindow.ModelAbilities.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
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
            modelTestResults[test] = WithTime(report.Summary, report.Milliseconds);
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
        (local ? "; it stays on this PC." : $", to {destination} (one small request that may cost a little).");

    // A test started or ended: the open Listening, Vision or Thinking page shows it.
    private void ShowModelTest()
    {
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision or CompanionTab.Hearing or CompanionTab.Thinking && !tabEdited)
            RenderTab();
    }

    private static string ToolsTest(ModelProbe probe) => $"Tools|{probe.Origin}|{probe.ModelId}";

    /// <summary>Companion › Thinking's Test tools for the Thinking model on an OpenAI-compatible endpoint (Ollama on this PC
    /// included): the button, what it sends and what the last test found.</summary>
    private UIElement[] ToolsTestControls(SetupRoute? thinking)
    {
        var probe = ThinkingProbe(thinking);
        if (probe is null) return [];
        var test = ToolsTest(probe);
        var running = modelTestsRunning.Contains(test);
        var button = new Button
        {
            Content = running ? "Testing tools..." : "Test tools", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 6), IsEnabled = !running
        };
        AutomationProperties.SetAutomationId(button, "ThinkingToolsTest");
        button.Click += (_, _) => TestToolsAsync(probe).Forget();
        var status = Note(modelTestResults.GetValueOrDefault(test) ?? ToolsTestAbout(probe.ModelId, probe.Local, probe.Destination),
            new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(status, "ThinkingToolsTestStatus");
        return [button, status];
    }

    /// <summary>What Test tools sends, and where.</summary>
    internal static string ToolsTestAbout(string model, bool local, string destination) =>
        $"Sends {model} one made-up tool and asks it to call the tool with a test word (never anything you said or typed)" +
        (local ? "; it stays on this PC." : $", to {destination} (one small request that may cost a little).");

    private async Task TestToolsAsync(ModelProbe probe)
    {
        var test = ToolsTest(probe);
        if (closing || modelTestsRunning.Contains(test)) return;
        if (!probe.Local && !ConfirmationDialog.Confirm(this, $"Send {probe.ModelId} one request with a made-up tool at {probe.Destination} " +
                "to see whether it calls tools (nothing you said or typed; it uses your API key and may cost a little)?",
                "Test tools"))
            return;
        modelTestsRunning.Add(test);
        modelTestResults[test] = $"Asking {probe.ModelId} to call a test tool...";
        ShowModelTest();
        try
        {
            var word = ModelVisionTest.Words[Random.Shared.Next(ModelVisionTest.Words.Count)];
            var key = await Task.Run(probe.Key, lifetime.Token);
            using var client = ModelContextProbe.CreateClient(probe.Local);
            var report = await ModelToolTest.RunAsync(client, probe.Origin, probe.ModelId, key, word, probe.Server, lifetime.Token);
            modelTestResults[test] = WithTime(report.Summary, report.Milliseconds);
            ErrorLog.Info($"Test tools: {report.Summary}");
            RecordAnswer(probe, report.Status);
            if (report.Tools is { } tools)
                RecordModelAbility(new() { Origin = probe.Origin, ModelId = probe.ModelId, Tools = tools, Source = ModelAbility.TestRequest,
                    CheckedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            modelTestResults[test] = "Couldn't test tools: " + error.Message;
        }
        finally { EndModelTest(test); }
    }

    /// <summary>Companion › Thinking's line on what Martlet knows the Thinking model takes on its route, each with where it came
    /// from: hearing and seeing (found out, else by its name), video, tool calls and whether its server retired it; and which
    /// test finds out what isn't known.</summary>
    internal static string ThinkingAbilitiesText(SetupRoute? thinking, ModelAbilities abilities)
    {
        if (thinking is null) return "";
        var found = abilities.Find(thinking.Origin, thinking.ModelId);
        string Said(ModelFact fact) => found?.SourceOf(fact) is { } said
            ? $" ({(ModelAbility.IsTest(said.Source) ? "found by " + said.Source : said.Source)}, {said.At.LocalDateTime:d MMM})" : "";
        string Answer(bool? value, ModelFact fact, bool? byName = null) => value switch
        {
            true => "yes" + Said(fact),
            false => "no" + Said(fact),
            _ => byName switch { true => "yes, by its name", false => "no, by its name", _ => "not known" }
        };
        if (ModelSenses.Retired(thinking, abilities) is { } retired)
            return $"{retired.Server} retired {thinking.ModelId}" +
                (retired.Since is { } since ? $" ({retired.Source}, {since.LocalDateTime:d MMM})" : "") + $": {retired.Remedy}";
        var hearing = HearingModelCatalog.CarriesAudio(thinking.RouteType)
            ? Answer(found?.Hears, ModelFact.Hears, HearingModelCatalog.Classify(thinking.ModelId) switch
                { HearingSupport.Supported => true, HearingSupport.Unsupported => false, _ => null })
            : "no (this route carries no recordings)";
        var seeing = Answer(found?.Sees, ModelFact.Sees, VisionModelCatalog.Classify(thinking.ModelId) switch
            { VisionSupport.Supported => true, VisionSupport.Unsupported => false, _ => null });
        var tools = !RouteAbilities.CarriesTools(thinking.RouteType) ? "no (this route carries no tools)"
            : thinking.RouteType == SetupRouteType.OpenAi ? "yes" : Answer(found?.Tools, ModelFact.Tools);
        var unknown = new List<string>();
        if (found?.Hears is null && HearingModelCatalog.CarriesAudio(thinking.RouteType)) unknown.Add("Test hearing (Companion › Listening)");
        if (found?.Sees is null) unknown.Add("Test vision (Companion › Vision)");
        if (found?.Tools is null && thinking.RouteType == SetupRouteType.ChatCompletions) unknown.Add("Test tools");
        return $"What Martlet knows about {thinking.ModelId}: hears recordings: {hearing}; sees pictures: {seeing}; takes video: " +
            $"{Answer(found?.Video, ModelFact.Video)}; calls tools: {tools}" +
            (unknown.Count > 0 && thinking.RouteType == SetupRouteType.ChatCompletions ? $" (to find out for sure: {string.Join(", ", unknown)})." : ".");
    }

    private async Task TestHearingAsync(ModelProbe probe)
    {
        var test = probe.Test(SenseKind.Audio);
        if (closing || modelTestsRunning.Contains(test)) return;
        if (!probe.Local && !ConfirmationDialog.Confirm(this, $"Send {probe.ModelId} one short recording of a test word (made by Windows " +
                $"speech, not your voice) at {probe.Destination} to see whether it hears (it uses your API key and may cost a little)?",
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
            modelTestResults[test] = WithTime(report.Summary, report.Milliseconds);
            ErrorLog.Info($"Test hearing: {report.Summary}");
            RecordAnswer(probe, report.Status);
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
                $"screen) at {probe.Destination} to see whether it sees (it uses your API key and may cost a little)?",
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
            modelTestResults[test] = WithTime(report.Summary, report.Milliseconds);
            ErrorLog.Info($"Test vision: {report.Summary}");
            RecordAnswer(probe, report.Status);
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
                Timeout = TimeSpan.FromSeconds(90), DropWhenStale = false, OnlyChosen = true
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
            modelTestResults[test] = WithTime(report.Summary, report.Milliseconds);
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

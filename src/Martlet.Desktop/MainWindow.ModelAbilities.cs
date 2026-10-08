using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Audio;
using Martlet.Core.Settings;
using Martlet.Logging;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What Thinking models hear and see (model-abilities.json, shared as <c>model-abilities</c>): kept when a model's server
/// metadata says so (choosing, testing or checking a model; MainWindow.ContextCheck.cs), when Companion › Listening › Test hearing
/// asks the model to say a spoken test word, and when a model refuses a recording in a conversation.</summary>
public partial class MainWindow
{
    private bool testingHearing;
    /// <summary>What the last Test hearing found, for Companion › Listening (never anything you said).</summary>
    private string? hearingTestResult;

    private ModelAbilities SavedModelAbilities() => ModelAbilities.Load(store?.DataDirectory);

    /// <summary>Keeps what Martlet found out about a Thinking model, lets a running conversation use it at once and shows it on an
    /// open Listening or Vision page. Another computer gets it on the next settings sync.</summary>
    private void RecordModelAbility(ModelAbility ability)
    {
        var directory = store?.DataDirectory;
        if (directory is null) return;
        var before = ModelAbilities.Load(directory);
        var known = before.Find(ability.Origin, ability.ModelId);
        var after = before.With(ability);
        if (!after.Save(directory))
        {
            ErrorLog.Warn("Martlet couldn't save what it found out about the Thinking model (model-abilities.json).");
            return;
        }
        var now = after.Find(ability.Origin, ability.ModelId)!;
        if (known?.Hears != now.Hears || known?.Sees != now.Sees)
            ErrorLog.Info($"{ability.ModelId}: {Senses(now)} ({ability.Source}).");
        conversation?.ReloadAbilities();
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision && !tabEdited) RenderTab();
        QueueSettingsSync();
    }

    private static string Senses(ModelAbility ability) =>
        $"{(ability.Hears switch { true => "hears recordings", false => "doesn't hear recordings", _ => "hearing not known" })}, " +
        $"{(ability.Sees switch { true => "sees pictures", false => "doesn't see pictures", _ => "vision not known" })}";

    /// <summary>Companion › Listening's Test hearing: the button, what it does and what the last test found.</summary>
    private UIElement[] HearingTestControls(SetupRoute? thinking)
    {
        var chat = thinking?.RouteType == SetupRouteType.ChatCompletions;
        var local = chat && ModelContextProbe.IsLoopback(ChatCompletionsSetup.BaseUri(thinking!.Origin));
        var test = new Button
        {
            Content = testingHearing ? "Testing hearing..." : "Test hearing", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6), IsEnabled = chat && !testingHearing
        };
        AutomationProperties.SetAutomationId(test, "TalkHearVoiceTest");
        test.Click += (_, _) => TestHearingAsync().Forget();
        var about = !chat
            ? "Only an OpenAI-compatible endpoint (Ollama on this PC included) can take a recording, so there is nothing to test."
            : $"Sends {thinking!.ModelId} one short recording of a single word, made by Windows speech (never your voice), and asks which " +
              $"word it heard{(local ? "; it stays on this PC" : $", to {LiveConversationConfiguration.LlmDestinationName(thinking)}. It's one small request that may cost a little")}." +
              " Choosing a model already asks its server what it takes, when the server says.";
        var status = Note(hearingTestResult ?? about, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetAutomationId(status, "TalkHearVoiceTestStatus");
        return [test, status];
    }

    private async Task TestHearingAsync()
    {
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (thinking?.RouteType != SetupRouteType.ChatCompletions || testingHearing || closing) return;
        var baseUri = ChatCompletionsSetup.BaseUri(thinking.Origin);
        var local = ModelContextProbe.IsLoopback(baseUri);
        var destination = LiveConversationConfiguration.LlmDestinationName(thinking);
        if (!local && !ConfirmationDialog.Confirm(this, $"Send {thinking.ModelId} one short recording of a test word (made by Windows " +
                $"speech, not your voice) at {destination} to see whether it hears? It's one small request with your API key and may cost a little.",
                "Test hearing"))
            return;
        testingHearing = true;
        hearingTestResult = $"Asking {thinking.ModelId} which word it hears...";
        if (openTab == CompanionTab.Listening && !tabEdited) RenderTab();
        try
        {
            var word = ModelHearingTest.Words[Random.Shared.Next(ModelHearingTest.Words.Count)];
            var pcm = await WindowsTestSpeech.SayAsync(ModelHearingTest.Spoken(word), lifetime.Token);
            var clip = BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = 24_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm);
            var key = local ? null : await Task.Run(() => RouteKey(thinking), lifetime.Token);
            using var client = ModelContextProbe.CreateClient(local);
            var report = await ModelHearingTest.RunAsync(client, thinking.Origin, thinking.ModelId, key, clip, word, ContextServerName(thinking),
                lifetime.Token);
            hearingTestResult = report.Summary + (report.Milliseconds is { } ms ? $" It answered in {ms:N0} ms." : "");
            ErrorLog.Info($"Test hearing: {report.Summary}");
            if (report.Hears is { } hears)
                RecordModelAbility(new() { Origin = thinking.Origin, ModelId = thinking.ModelId, Hears = hears, Source = "a test request",
                    CheckedAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            hearingTestResult = "Couldn't test hearing: " + error.Message;
        }
        finally
        {
            testingHearing = false;
            if (!closing)
            {
                ActionText.Text = hearingTestResult ?? "";
                if (openTab == CompanionTab.Listening && !tabEdited) RenderTab();
            }
        }
    }
}

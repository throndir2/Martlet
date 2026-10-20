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

    /// <summary>A test's summary with how long the model took to answer, in one sentence.</summary>
    private static string WithTime(string summary, long? milliseconds) =>
        milliseconds is { } ms ? $"{summary.TrimEnd('.')} (it answered in {ms:N0} ms)." : summary;

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
        if (known is null || Senses(known) != Senses(now))
            ErrorLog.Info($"{ability.ModelId}: {Senses(now)} ({ability.Source}).");
        AbilitiesChanged();
    }

    /// <summary>Martlet no longer counts the model retired on that route: it answered again (a test), or the owner chose it again
    /// (<paramref name="why"/> says which, for the log).</summary>
    private void ForgetRetired(string origin, string modelId, string why)
    {
        var directory = store?.DataDirectory;
        if (directory is null) return;
        var before = ModelAbilities.Load(directory);
        var after = before.Answered(origin, modelId);
        if (ReferenceEquals(after, before)) return;
        if (!after.Save(directory))
        {
            ErrorLog.Warn("Martlet couldn't save what it found out about a model (model-abilities.json).");
            return;
        }
        ErrorLog.Info($"Martlet no longer counts {modelId} retired on {origin}: {why}.");
        AbilitiesChanged();
    }

    // A test's HTTP answer: 410 Gone marks the model retired on that route, any answer clears it.
    private void RecordAnswer(ModelProbe probe, int? status)
    {
        if (status == 410)
            RecordModelAbility(new() { Origin = probe.Origin, ModelId = probe.ModelId, Retired = DateTimeOffset.UtcNow,
                Source = ModelAbility.GoneAnswer, CheckedAt = DateTimeOffset.UtcNow });
        else if (status is >= 200 and <= 299) ForgetRetired(probe.Origin, probe.ModelId, "it answered a test");
    }

    private void AbilitiesChanged()
    {
        conversation?.ReloadAbilities();
        // Where pictures and recordings go follows at once (the desktop's status file and log say it again).
        conversation?.ReloadSenseModels();
        if (!closing && openTab is CompanionTab.Listening or CompanionTab.Vision or CompanionTab.Hearing or CompanionTab.Thinking && !tabEdited)
            RenderTab();
        // Home says when the Thinking model was found retired, or no longer is, with its replacement.
        QueueHealth();
        FollowRetiredAsync().Forget();
        QueueSettingsSync();
    }

    private static string Senses(ModelAbility ability) =>
        $"{(ability.Hears switch { true => "hears recordings", false => "doesn't hear recordings", _ => "hearing not known" })}, " +
        $"{(ability.Sees switch { true => "sees pictures", false => "doesn't see pictures", _ => "vision not known" })}, " +
        $"{(ability.Video switch { true => "takes video", false => "doesn't take video", _ => "video not known" })}, " +
        $"{(ability.Tools switch { true => "calls tools", false => "doesn't call tools", _ => "tools not known" })}" +
        (ability.Retired is { } since ? $", retired since {since:yyyy-MM-dd}" : "");

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
}

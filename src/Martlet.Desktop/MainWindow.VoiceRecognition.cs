using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Speakers;

namespace Martlet.Desktop;

/// <summary>
/// Companion › People › Where it runs (docs/VOICES.md#where-voice-recognition-runs): the Voice recognition list
/// (<see cref="PoolAreas.VoiceRecognition"/>, shared by all your computers) with the shared list control, and how recognition is
/// doing now. It lives on People, beside Recognize voices in conversations and the voices themselves, because it is the same
/// feature: one page turns it on, says where it runs and shows whom it knows. This PC recognizes on its processor; a computer
/// runs the Voice recognition role (on its NVIDIA graphics card when it has one). When no computer answers in time, this PC's
/// processor recognizes. Automation IDs: the list's <c>Pool-voice-recognition-...</c>, <c>PeopleRecognitionWhere</c> (where
/// the last voice was recognized and how long it took, or what the list means), <c>PeopleRecognitionCheck</c> (Check it now: a
/// test tone through the list) and <c>PeopleRecognitionCheckResult</c> (what it found).
/// </summary>
public partial class MainWindow
{
    private void InitializeVoiceRecognition()
    {
        localVoices.WritesState = store is not null;
        localVoices.RecognitionChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            QueueHealth();
            if (openTab != CompanionTab.People) return;
            if (CompanionContent.IsKeyboardFocusWithin) peopleStale = true;
            else RenderTab();
        });
    }

    private Border VoiceRecognitionPoolCard() => PoolListCard(new PoolListOptions
    {
        Area = PoolAreas.VoiceRecognition,
        Heading = "Where it runs",
        Intro = "The places that recognize who is talking, in order. This PC recognizes on its processor. Your other computers run " +
            "Martlet's Voice recognition role, on a graphics card when they have an NVIDIA one. A computer that answers before this " +
            "PC would have finished recognizes; when none does, this PC's processor recognizes, so recognition keeps working. " +
            "This list is the same on all your computers.",
        Status = VoiceRecognitionMemberStatus
    });

    /// <summary>A member's state line: this PC's engine (ready and its speed, failed and when it tries again, or not loaded yet),
    /// or whether a computer runs the role and how it answered lately.</summary>
    private string? VoiceRecognitionMemberStatus(PoolMember member)
    {
        var parts = new List<string>();
        if (member.Kind == PoolMemberKind.ThisPc)
        {
            if (member.Off) parts.Add("used only when no computer above answers in time");
            var engine = localVoices.Engine;
            parts.Add(!localVoices.Included ? "its voice recognition files are missing: reinstall Martlet"
                : engine.State switch
                {
                    VoiceEngineState.Failed => $"stopped working: {engine.Problem}" +
                        (engine.RetryAt is { } at ? $" Martlet tries again at {at.ToLocalTime():t}" : ""),
                    VoiceEngineState.Ready => engine.MillisecondsPerSecond is { } rate
                        ? $"ready; about {rate:0} ms for each second of speech" : "ready",
                    _ => "loads when it first hears a voice"
                });
            return string.Join("; ", parts);
        }
        var hostId = member.HostId!;
        if (FindHost(hostId) is null && FindSharedHost(hostId) is null) parts.Add($"{hostId} isn't paired with this PC");
        else
        {
            var check = hostChecks.GetValueOrDefault(hostId);
            parts.Add(check?.Offers?.ContainsKey(HostRoles.VoiceRecognition) == true ? "runs Voice recognition"
                : check?.Reachable == false ? "not reachable right now"
                : check?.Reachable == true ? "doesn't run Voice recognition yet: add the role on Devices" : "not checked yet");
        }
        if (localVoices.RecognitionHosts.FirstOrDefault(h => h.HostId == hostId) is { } known)
        {
            if (known.RestUntil is { } until && until > DateTimeOffset.UtcNow)
                parts.Add($"left out until {until.ToLocalTime():t} ({known.Problem}); this PC recognizes instead");
            else if (known.Milliseconds is { } ms) parts.Add($"last answered in {ms:0} ms");
        }
        if (member.Kind == PoolMemberKind.Gpu) parts.Add("its computer picks the card");
        return string.Join("; ", parts);
    }

    /// <summary>Where the last voice was recognized and how long it took, or what the list means before the first.</summary>
    internal static string VoiceRecognitionNow(bool active, VoiceRecognitionLast? last, IReadOnlyList<VoiceRecognitionTarget> tries)
    {
        if (!active) return "Voice recognition is off, so it runs nowhere.";
        if (last is null) return VoiceRecognitionPool.Summary(tries);
        return $"Last voice recognized by {last.Where} in {last.Milliseconds:0} ms ({last.Seconds:0.#} s of speech, at {last.At.ToLocalTime():t})" +
            (last.Raced ? ", after a computer didn't answer in time" : "") +
            (last.Passed.Count > 0 && !last.Raced ? ", after passing over " + string.Join("; ", last.Passed) : "") + ".";
    }

    private bool recognitionChecking;
    private string? recognitionCheckResult;

    private UIElement VoiceRecognitionWhere()
    {
        var stack = new StackPanel();
        var line = Note(VoiceRecognitionNow(localVoices.Active, localVoices.LastRecognition, localVoices.RecognitionTargets), new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(line, "PeopleRecognitionWhere");
        stack.Children.Add(line);
        if (!localVoices.Active) return stack;
        var check = PageButton(recognitionChecking ? "Checking…" : "Check it now", () => CheckVoiceRecognitionAsync().Forget(), link: true,
            id: "PeopleRecognitionCheck");
        check.IsEnabled = !recognitionChecking;
        check.ToolTip = "Recognizes three seconds of a test tone the way your voice goes: through the list below, in order. Nothing is learned or kept.";
        stack.Children.Add(Row(check));
        if (recognitionCheckResult is { } result)
        {
            var said = Note(result, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(said, "PeopleRecognitionCheckResult");
            stack.Children.Add(said);
        }
        return stack;
    }

    /// <summary>People's Check it now: a test tone through the list, off the dispatcher.</summary>
    private async Task CheckVoiceRecognitionAsync()
    {
        recognitionChecking = true;
        RenderTab();
        try
        {
            var last = await Task.Run(localVoices.Check);
            recognitionCheckResult = $"Checked at {last.At.ToLocalTime():t}: {last.Where} recognized the test tone in {last.Milliseconds:0} ms" +
                (last.Passed.Count > 0 ? $" ({string.Join("; ", last.Passed)})" : "") + ".";
        }
        catch (Exception error) when (error is VoiceEngineException or InvalidOperationException)
        {
            recognitionCheckResult = $"Checked at {DateTime.Now:t}: nothing could recognize the test tone. {error.Message}";
        }
        finally { recognitionChecking = false; }
        if (closing) return;
        ActionText.Text = recognitionCheckResult;
        RenderTab();
    }

    /// <summary>Home's Try again now: loads this PC's engine again, off the dispatcher.</summary>
    private async Task RetryVoiceRecognitionAsync()
    {
        ActionText.Text = "Loading voice recognition on this PC again…";
        var works = await Task.Run(localVoices.CheckEngine);
        if (closing) return;
        ActionText.Text = works ? "Voice recognition works again on this PC."
            : $"Voice recognition still doesn't work on this PC: {localVoices.Engine.Problem} Martlet keeps trying.";
        RenderHealth(force: true);
    }
}

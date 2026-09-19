using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Desktop;
using Martlet.Participation;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

public sealed class LiveConversationTests
{
    [Fact]
    public Task OpeningConfiguredWindowHasNoDefaultEffectsAndConsentIsNotRestored() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Assert.False(Control<Button>(window, "SendButton").IsEnabled);
            Assert.False(Control<Button>(window, "PttButton").IsEnabled);
            Assert.False(Control<CheckBox>(window, "VoiceChoice").IsChecked);
            Assert.Contains("800,044", Text(window, "EnvelopeText"));
            Assert.Contains("UNKNOWN", Text(window, "EnvelopeText"));
            Assert.Contains("150 s", Text(window, "EnvelopeText"));
            Assert.Contains("configured, NOT live verified", Text(window, "ConfigurationText"));
            fixture.NoEffects();
            Click(window, "SendButton"); // Even a programmatic routed click cannot bypass missing permission.
            fixture.NoEffects();
            Assert.Contains("Permission missing", Text(window, "ResultText"));
        }
        finally { window.Close(); }
        var reopened = fixture.Open();
        try { await Loaded(reopened); Assert.False(Control<CheckBox>(reopened, "AcceptAction").IsChecked); fixture.NoEffects(); }
        finally { reopened.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TypedWindowRunsActualBridgePolicyParsersRuntimeAndSelectedSink(bool voice) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Hello there. ", "Second sentence.");
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Control<CheckBox>(window, "VoiceChoice").IsChecked = voice;
            Control<TextBox>(window, "InputText").Text = "typed-content-canary";
            Control<CheckBox>(window, "AcceptAction").IsChecked = true;
            Assert.True(Control<Button>(window, "SendButton").IsEnabled);
            Click(window, "SendButton");
            await fixture.Finish();
            await Until(() => Text(window, "StatusText").Contains("app worker released: True", StringComparison.Ordinal));
            Assert.Contains("Hello there. Second sentence.", Text(window, "AnswerText"));
            Assert.Contains("FIXTURE HTTP evidence", Text(window, "AnswerText"));
            Assert.Equal("", Text(window, "RefusalText"));
            Assert.False(Control<CheckBox>(window, "AcceptAction").IsChecked);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(voice ? 2 : 0, fixture.Tts.Calls);
            Assert.Equal(voice ? 2 : 0, fixture.Output.Opens);
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Stt.Calls);
            using var body = JsonDocument.Parse(fixture.Llm.Body);
            Assert.Equal(TextFixtures.Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(256, body.RootElement.GetProperty("max_output_tokens").GetInt32());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Contains("typed-content-canary", Encoding.UTF8.GetString(fixture.Llm.Body));
            Assert.Equal(voice ? 3 : 1, fixture.Native.Targets.Count);
            Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
            Assert.All(fixture.Native.Threads, thread => Assert.NotEqual(Environment.CurrentManagedThreadId, thread));
            Assert.Contains("/openai-llm/", fixture.Native.Targets.First());
            if (voice)
            {
                using var speech = JsonDocument.Parse(fixture.Tts.Body);
                Assert.Equal("coral", speech.RootElement.GetProperty("voice").GetString());
                Assert.Equal("gpt-4o-mini-tts-2025-12-15", speech.RootElement.GetProperty("model").GetString());
                Assert.Equal(OutputPolicy.FixedEndpoint, fixture.Output.Selection!.Policy);
                Assert.Equal("private-output-id", fixture.Output.Selection.EndpointId);
                Assert.All(fixture.Native.Targets.Skip(1), target => Assert.Contains("/openai-tts/", target));
                Assert.NotEmpty(fixture.Output.Bytes);
            }
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
            Assert.DoesNotContain("typed-content-canary", Text(window, "StatusText"));
            Assert.DoesNotContain(LiveFixture.Secret, Text(window, "StatusText") + Text(window, "EnvelopeText") + Text(window, "ResultText"));
            Assert.DoesNotContain(LiveFixture.Secret, await File.ReadAllTextAsync(fixture.Store.FilePath));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task KeyboardPttStreamsCanonicalWaveThroughSttPolicyAndVoice() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        byte[] pcm = ProviderFixtures.Wave(1600)[44..];
        fixture.Capture.Packets.Enqueue(pcm);
        fixture.Answer("Spoken answer.");
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Permit(window, voice: true, microphone: true);
            SendKey(window, Key.Space, down: true);
            await Until(() => fixture.Capture.Reads > 0);
            Assert.True(Control<Button>(window, "PttButton").IsEnabled);
            SendKey(window, Key.Space, down: false);
            await fixture.Finish();
            await Until(() => Text(window, "AnswerText").Contains("Spoken answer.", StringComparison.Ordinal));
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(1, fixture.Stt.Calls);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(1, fixture.Tts.Calls);
            Assert.Equal(1, fixture.Output.Opens);
            Assert.Equal(InputPolicy.FixedEndpoint, fixture.Capture.Selection!.Policy);
            Assert.Equal("private-input-id", fixture.Capture.Selection.EndpointId);
            Assert.Equal("Bearer " + LiveFixture.Secret, fixture.Stt.Authorization);
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", fixture.Stt.Uri!.AbsoluteUri);
            var multipart = Encoding.UTF8.GetString(fixture.Stt.Body);
            Assert.Contains("gpt-transcribe", multipart);
            Assert.Contains("utterance.wav", multipart);
            int riff = fixture.Stt.Body.AsSpan().IndexOf("RIFF"u8);
            Assert.True(riff >= 0);
            Assert.Equal(ProviderFixtures.Wave(1600), fixture.Stt.Body[riff..(riff + pcm.Length + 44)]);
            Assert.Equal(3, fixture.Native.Targets.Count);
            Assert.Contains("/openai-stt/", fixture.Native.Targets.ElementAt(0));
            Assert.Contains("/openai-llm/", fixture.Native.Targets.ElementAt(1));
            Assert.Contains("/openai-tts/", fixture.Native.Targets.ElementAt(2));
            Assert.Contains("Synthetic fixture transcript.", Encoding.UTF8.GetString(fixture.Llm.Body));
            Assert.Contains("confidence UNKNOWN", Text(window, "TranscriptText"));
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("no-speech")]
    [InlineData("stt-failed")]
    [InlineData("refused")]
    [InlineData("markup")]
    [InlineData("tts-failed")]
    public async Task SuppressionRefusalAndAudioFailuresDoNotCauseHiddenRequests(string scenario)
    {
        await using var fixture = await LiveFixture.Create();
        bool microphone = scenario.StartsWith("stt", StringComparison.Ordinal) || scenario == "no-speech";
        if (scenario == "no-speech") fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("""{"text":""}"""));
        if (scenario == "stt-failed") fixture.Stt.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 401));
        if (scenario == "refused") fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(string.Concat(TextFixtures.Trace("Not available.", refusal: true))));
        if (scenario == "markup") fixture.Answer("`code` https://example.invalid\n");
        if (scenario == "tts-failed") fixture.Tts.Respond = (_, _) => Task.FromResult(ProviderFixtures.Json("{}", 429));
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(voice: true, microphone: microphone);
        if (microphone) { await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0); operation.ReleasePress(); }
        await fixture.Finish(operation);
        Assert.Equal(0, fixture.Output.Opens);
        Assert.Equal(microphone ? 0 : 1, fixture.Llm.Calls);
        Assert.Equal(scenario == "tts-failed" ? 1 : 0, fixture.Tts.Calls);
        Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        if (scenario == "refused")
        {
            Assert.Equal(ConversationState.Refused, operation.Turn!.Snapshot.State);
            Assert.Equal("Not available.", operation.Turn.Content.Refusal);
            Assert.Equal("", operation.Turn.Content.Text);
        }
        if (scenario == "tts-failed") Assert.Equal("Hello fixture.", operation.Turn!.Content.Text);
        if (microphone) Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
    }

    [Fact]
    public async Task PolicyRejectsExactPunctuationInputBeforeAnyCredentialOrProvider()
    {
        await using var fixture = await LiveFixture.Create();
        var operation = fixture.Start(text: "...");
        await fixture.Finish(operation);
        Assert.Equal(PolicyReason.NoSpeech, operation.Status.Policy);
        Assert.Null(operation.Turn);
        fixture.NoEffects();
    }

    [Fact]
    public async Task UnsupportedRoleAndMissingConsentOrOutputRejectBeforeEffectsWhileTextOnlyStillWorks()
    {
        await using var fixture = await LiveFixture.Create();
        var settings = (await fixture.Store.LoadAsync()).Settings!;
        var changed = SetupSettings.SelectRoute(settings, SetupRole.Tts, "unsupported-canary", "unsupported-voice");
        var route = changed.Setup!.Routes.Single(r => r.Role == SetupRole.Tts);
        changed = SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() });
        await fixture.Save(changed);
        Assert.Throws<LiveActionException>(() => fixture.Start(voice: true));
        Assert.DoesNotContain("unsupported-canary", fixture.Controller.Configuration!.Disclosure(true));
        Assert.Throws<LiveActionException>(() => fixture.Controller.Start("hi", false, true, true, true, false));
        fixture.NoEffects();
        await fixture.Finish(fixture.Start());
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(0, fixture.Tts.Calls);
        Assert.Equal(0, fixture.Output.Opens);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("escape")]
    [InlineData("pause")]
    [InlineData("mute")]
    [InlineData("lock")]
    [InlineData("deactivate")]
    [InlineData("close")]
    [InlineData("voice-change")]
    public Task SlowVaultNeverBlocksDispatcherOrReleasesAppOwnershipEarly(string transition) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            if (transition == "deactivate")
                SendMessage(new WindowInteropHelper(window).Handle, 0x0006, new IntPtr(1), IntPtr.Zero);
            Control<TextBox>(window, "InputText").Text = "test";
            Permit(window);
            Click(window, "SendButton");
            await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Heartbeat();
            switch (transition)
            {
                case "stop": Click(window, "StopButton"); break;
                case "escape": Escape(window, "InputText"); break;
                case "pause": Control<CheckBox>(window, "PauseChoice").IsChecked = true; break;
                case "mute": Control<CheckBox>(window, "MuteChoice").IsChecked = true; break;
                case "lock": fixture.Events.Signal(true); break;
                case "deactivate": SendMessage(new WindowInteropHelper(window).Handle, 0x0006, IntPtr.Zero, IntPtr.Zero); break;
                case "close": window.Close(); break;
                case "voice-change": Control<CheckBox>(window, "VoiceChoice").IsChecked = true; break;
            }
            await Heartbeat();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await Heartbeat();
            Assert.True(fixture.Runner.IsRunning);
            Assert.NotNull(fixture.Controller.PolicySnapshot.ActiveIntentId);
            Assert.Null(fixture.Runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            Assert.Equal(0, fixture.Llm.Calls);
            fixture.Native.Release.Set();
            await fixture.Finish();
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        }
        finally { fixture.Native.Release.Set(); window.Close(); }
    });

    [Fact]
    public async Task CredentialRevisionIsCheckedAgainAfterBlockedNativeReturn()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        var operation = fixture.Start();
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loaded = await fixture.Store.LoadAsync();
        var changed = SetupSettings.SelectRoute(loaded.Settings!, SetupRole.Llm, "unsupported", null);
        Assert.True((await fixture.Store.SaveAsync(changed, loaded.Revision)).Saved);
        fixture.Native.Release.Set();
        await fixture.Finish(operation);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Single(fixture.Native.Targets);
        Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
    }

    [Fact]
    public async Task OriginalCallerFlagBeatsBlockedNewerCancellationCallbackAndLateVaultReturn()
    {
        await using var fixture = await LiveFixture.Create();
        using var caller = new CancellationTokenSource();
        using var unblock = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.Block = true;
        var operation = fixture.Start(caller: caller.Token);
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var newest = caller.Token.Register(() => { callbackEntered.SetResult(); unblock.Wait(); });
        var cancellation = caller.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Native.Release.Set();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(25));
            await fixture.Finish(operation);
            Assert.False(cancellation.IsCompleted);
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
        }
        finally { unblock.Set(); await cancellation; }
    }

    [Fact]
    public async Task OriginalActionClockCannotBeRenewedBySlowLoadAndUtcRollback()
    {
        await using var fixture = await LiveFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.BeforeLoad = async _ => { entered.TrySetResult(); await release.Task; };
        var operation = fixture.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.DeferCallbacks = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(151));
        fixture.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        release.SetResult();
        await fixture.Finish(operation, advance: false);
        Assert.Equal("conversation.expired", operation.Status.Code);
        fixture.NoEffects();
    }

    [Fact]
    public async Task StaleStopAndReleaseCannotCancelTheNextAction()
    {
        await using var fixture = await LiveFixture.Create();
        var old = fixture.Start();
        await fixture.Finish(old);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Llm.Respond = async (_, _) => { entered.TrySetResult(); await release.Task; return TextRecordingHandler.Sse(Harness.Trace("New response.")); };
        var next = fixture.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Controller.Stop(old);
        old.ReleasePress();
        Assert.False(next.Authorization.IsCanceled);
        release.SetResult();
        await fixture.Finish(next);
        Assert.NotEqual(old.Id, next.Id);
        Assert.NotEqual(old.Turn!.TurnId, next.Turn!.TurnId);
        Assert.True(next.Turn.Epoch > old.Turn.Epoch);
        Assert.Equal(ConversationState.Completed, next.Turn.Snapshot.State);
    }

    [Theory]
    [InlineData(ErrorCode.AudioAccessDenied)]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioDeviceChanged)]
    public async Task CaptureFailuresNeverUploadAndTypedFallbackRequiresNewAction(ErrorCode code)
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.ReadFailure = new CaptureDeviceException(code);
        var operation = fixture.Start(microphone: true);
        await fixture.Finish(operation);
        Assert.Equal(code, operation.Status.AudioFailure);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
        await fixture.Finish(fixture.Start());
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(1, fixture.Capture.Opens);
    }

    [Fact]
    public async Task TranscriptIntentStartsAtActualReceiptWithoutRenewingAnOldPermit()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        fixture.Stt.Respond = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(8));
            return Task.FromResult(ProviderFixtures.Json());
        };
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await fixture.Finish(operation);
        Assert.Equal(ConversationState.Completed, operation.Turn!.Snapshot.State);
        Assert.Equal(1, fixture.Controller.PolicySnapshot.AcceptedThroughIntentId);
        Assert.Equal(0, fixture.Controller.PolicySnapshot.RecentUnsolicitedDispatches);
    }

    [Theory]
    [InlineData(1168, CredentialError.Missing)]
    [InlineData(5, CredentialError.AccessDenied)]
    [InlineData(87, CredentialError.InvalidInput)]
    public async Task NativeCredentialFailureHasTypedRemedyAndNeverSends(int nativeError, CredentialError expected)
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Error = nativeError;
        var operation = fixture.Start();
        await fixture.Finish(operation);
        Assert.Equal(expected, operation.Authorization.CredentialFailure);
        Assert.Equal(ProviderFailureCode.CredentialUnavailable, operation.Turn!.Snapshot.ProviderFailure);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.All(fixture.Native.Leases, lease => Assert.Throws<ObjectDisposedException>(() => lease.Use(_ => { })));
        Assert.DoesNotContain(LiveFixture.Secret, JsonSerializer.Serialize(operation.Status));
        Assert.DoesNotContain(LiveFixture.Secret, operation.Authorization.ToString());
    }

    [Theory]
    [InlineData(ProviderRole.Llm, "https://api.openai.com")]
    [InlineData(ProviderRole.Stt, "https://api.openai.com")]
    [InlineData(ProviderRole.Llm, "https://wrong.invalid")]
    public async Task CredentialBridgeRequiresMatchingActiveAuthorizedRoleBeforeNativeAccess(ProviderRole role, string origin)
    {
        await using var fixture = await LiveFixture.Create();
        var authorization = new ConversationAuthorization(fixture.Controller.Configuration!, false, false, fixture.Clock,
            () => true, fixture.Settings.LoadAsync, new WindowsCredentialStore(fixture.Native), CancellationToken.None);
        var source = new ConversationCredentialSource(() => authorization);
        await Assert.ThrowsAsync<CredentialUnavailableException>(async () =>
            await source.ResolveAsync(new(new Uri(origin), role, TextFixtures.Model), CancellationToken.None));
        fixture.NoEffects();
    }

    [Fact]
    public async Task DelayedPressKeepsOriginalCaptureExpiryAcrossUtcRollback()
    {
        await using var fixture = await LiveFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.BeforeLoad = async _ => { entered.TrySetResult(); await release.Task; };
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.DeferCallbacks = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(28));
        fixture.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        release.SetResult();
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        operation.ReleasePress();
        await fixture.Finish(operation, advance: false);
        Assert.Equal(CaptureEndReason.AuthorizationExpired, operation.Capture!.Snapshot.EndReason);
        Assert.Equal(0, operation.Capture.Snapshot.RetainedPcmBytes);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
    }

    [Fact]
    public async Task CaptureCleanupAfterExpiryNeverRescuesOldPcmForUpload()
    {
        await using var fixture = await LiveFixture.Create();
        using var release = new ManualResetEventSlim();
        fixture.Capture.DisposeBlock = release;
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await Until(() => fixture.Capture.DisposeEntered.IsSet);
        fixture.Clock.DeferCallbacks = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        fixture.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        Assert.True(fixture.Runner.IsRunning);
        release.Set();
        await fixture.Finish(operation, advance: false);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
    }

    [Fact]
    public async Task PartialTtsPreservesTextAndPlayedAccountingWithoutRetry()
    {
        await using var fixture = await LiveFixture.Create();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new FragmentedTextBody(SpeechFixtures.Audio(4800).Concat(new byte[] { 1 }).ToArray(), 960);
        body.BeforeRead = async _ =>
        {
            if (body.BytesRead >= 9600) { waiting.TrySetResult(); await release.Task; }
        };
        fixture.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(body));
        var operation = fixture.Start(voice: true);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => fixture.Output.Starts > 0 && fixture.Output.Samples > 0);
        release.SetResult();
        await fixture.Finish(operation);
        Assert.Equal(ConversationState.Partial, operation.Turn!.Snapshot.State);
        Assert.Equal(ProviderFailureCode.ResponseTruncated, operation.Turn.Snapshot.ProviderFailure);
        Assert.True(operation.Turn.Snapshot.MayHavePlayed);
        Assert.Equal("Hello fixture.", operation.Turn.Content.Text);
        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(1, fixture.Tts.Calls);
        Assert.Equal(1, fixture.Output.Opens);
        Assert.True(operation.OwnershipReleased);
    }

    [Fact]
    public async Task OutputLossKeepsTextAndNeverSelectsAnotherDevice()
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { PaddingError = ErrorCode.AudioDeviceLost });
        var operation = fixture.Start(voice: true);
        await fixture.Finish(operation);
        Assert.Equal(ConversationState.Partial, operation.Turn!.Snapshot.State);
        Assert.Equal("Hello fixture.", operation.Turn.Content.Text);
        Assert.Equal(1, fixture.Output.Opens);
        Assert.Equal("private-output-id", fixture.Output.Selection!.EndpointId);
        Assert.Equal(ErrorCode.AudioDeviceLost, operation.Turn.Snapshot.Playback!.Error!.Code);
        await fixture.Finish(fixture.Start());
        Assert.Equal(1, fixture.Output.Opens);
    }

    [Fact]
    public async Task FailedNativeCleanupQuarantinesAppWideOwnerAfterVisibleTerminal()
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { FailDispose = true });
        var operation = fixture.Start(voice: true);
        await Until(() => operation.Turn?.Completion.IsCompleted == true);
        await Until(() => operation.Status.Quarantined);
        Assert.Equal("Hello fixture.", operation.Turn!.Content.Text);
        Assert.True(fixture.Runner.IsRunning);
        Assert.False(operation.OwnershipReleased);
        Assert.NotNull(fixture.Controller.PolicySnapshot.ActiveIntentId);
        Assert.Throws<LiveActionException>(() => fixture.Start());
        Assert.Null(fixture.Runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
        Assert.Equal(1, fixture.Output.Opens);
    }

    [Fact]
    public async Task SttDeadlineReportsWhileNoncooperativeVaultStillOwnsSlot()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Native.Block = true;
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var operation = fixture.Start(microphone: true);
        await Until(() => operation.Capture?.Snapshot.CanonicalSamples > 0);
        operation.ReleasePress();
        await fixture.Native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await Until(() => operation.Status.Code == "stt.deadline_exceeded");
        Assert.True(fixture.Runner.IsRunning);
        Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
        Assert.Equal(0, fixture.Stt.Calls);
        fixture.Native.Release.Set();
        await fixture.Finish(operation);
        Assert.Equal("stt.deadline_exceeded", operation.Status.Code);
        Assert.Equal(0, fixture.Stt.Calls);
        Assert.Equal(0, fixture.Llm.Calls);
        Assert.Equal(0, operation.Capture!.Snapshot.RetainedPcmBytes);
    }

    [Fact]
    public Task LostHeldKeyboardFocusCancelsCaptureInsteadOfSending() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Permit(window, microphone: true);
            SendKey(window, Key.Space, down: true);
            await Until(() => fixture.Capture.Reads > 0);
            var ptt = Control<Button>(window, "PttButton");
            ptt.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, ptt, Control<TextBox>(window, "InputText"))
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            SendKey(window, Key.Space, down: false);
            await fixture.Finish();
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.False(Control<CheckBox>(window, "AcceptCapture").IsChecked);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(550, 450)]
    [InlineData(920, 850)]
    public Task StopRemainsVisibleAndClickableAtEveryScrollPosition(double width, double height) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            window.Width = width;
            window.Height = height;
            Permit(window);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var scroll = Assert.IsType<ScrollViewer>(
                Assert.IsType<StackPanel>(Control<TextBox>(window, "InputText").Parent).Parent);
            var stop = Control<Button>(window, "StopButton");
            window.UpdateLayout();
            Assert.True(scroll.ScrollableHeight > 0);
            Point? fixedPosition = null;
            foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
                window.UpdateLayout();
                var bounds = stop.TransformToAncestor(content).TransformBounds(new Rect(stop.RenderSize));
                Assert.True(new Rect(content.RenderSize).Contains(bounds), $"Stop outside window content: {bounds}");
                var hit = Assert.IsAssignableFrom<DependencyObject>(content.InputHitTest(
                    new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)));
                while (hit is FrameworkContentElement element)
                    hit = Assert.IsAssignableFrom<DependencyObject>(element.Parent);
                Assert.True(ReferenceEquals(hit, stop) || stop.IsAncestorOf(hit), "Stop is clipped or covered.");
                if (fixedPosition is { } position) Assert.Equal(position, bounds.TopLeft);
                fixedPosition = bounds.TopLeft;
            }
            Assert.Equal("Esc", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(stop));
            fixture.NoEffects();
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StopOrEscapeRevokesUnusedPermissionsWithoutStartingWork(bool escape) => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Permit(window, voice: true, microphone: true);
            Assert.True(Control<Button>(window, "StopButton").IsEnabled);
            if (escape) Escape(window, "InputText");
            else Click(window, "StopButton");
            AssertPermissionsCleared(window);
            Assert.False(Control<Button>(window, "StopButton").IsEnabled);
            fixture.NoEffects();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EscapeDiscardsHeldPttAndLateSpaceReleaseCannotUploadOrRearm() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Capture.Packets.Enqueue(new byte[3200]);
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Permit(window, voice: true, microphone: true);
            SendKey(window, Key.Space, down: true);
            await Until(() => fixture.Capture.Reads > 0);
            Escape(window, "PttButton");
            SendKey(window, Key.Space, down: false);
            await fixture.Finish();
            await Until(() => Text(window, "StatusText").Contains("app worker released: True", StringComparison.Ordinal));
            Assert.Contains("conversation.canceled", Text(window, "StatusText"));
            Assert.Contains("retained PCM: 0", Text(window, "StatusText"));
            AssertPermissionsCleared(window);
            SendKey(window, Key.Space, down: true);
            SendKey(window, Key.Space, down: false);
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(1, fixture.Capture.Stops);
            Assert.Equal(1, fixture.Capture.Disposals);
            Assert.Empty(fixture.Native.Targets);
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Tts.Calls);
            Assert.Equal(0, fixture.Output.Opens);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EscapeFromResponseStopsPlaybackAndPreservesTextWithoutReplay() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create(new ControlledDevice { AutoConsume = false });
        fixture.Answer("Retained response.");
        var window = fixture.Open();
        try
        {
            await Loaded(window);
            Control<TextBox>(window, "InputText").Text = "test";
            Permit(window, voice: true);
            Click(window, "SendButton");
            await Until(() => fixture.Output.Starts > 0);
            Escape(window, "AnswerText");
            await fixture.Finish();
            await Until(() => Text(window, "StatusText").Contains("app worker released: True", StringComparison.Ordinal));
            Assert.Contains("Retained response.", Text(window, "AnswerText"));
            Assert.Contains("conversation.canceled", Text(window, "StatusText"));
            Assert.True(fixture.Output.Samples > 0);
            Assert.Equal(1, fixture.Output.Stops);
            Assert.Equal(1, fixture.Output.Disposals);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(1, fixture.Tts.Calls);
            AssertPermissionsCleared(window);
            Escape(window, "AnswerText");
            Assert.Equal(1, fixture.Output.Opens);
            Assert.Equal(1, fixture.Tts.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EscapeDuringSettingsLoadRetainsOwnershipUntilWorkerReturns() => DispatcherTest(async () =>
    {
        await using var fixture = await LiveFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.BeforeLoad = async _ => { entered.TrySetResult(); await release.Task; };
        var window = fixture.Open();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Control<Button>(window, "StopButton").IsEnabled);
            Escape(window, "ConfigurationText");
            await Heartbeat();
            Assert.True(fixture.Runner.IsRunning);
            Assert.Null(fixture.Runner.TryStart(_ => Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed))));
            AssertPermissionsCleared(window);
            fixture.NoEffects();
            release.TrySetResult();
            await fixture.Finish();
            Assert.False(Control<Button>(window, "SendButton").IsEnabled);
            fixture.NoEffects();
        }
        finally { release.TrySetResult(); window.Close(); }
    });

    private static void AssertPermissionsCleared(Window window)
    {
        Assert.False(Control<CheckBox>(window, "AcceptAction").IsChecked);
        Assert.False(Control<CheckBox>(window, "AcceptCapture").IsChecked);
        Assert.False(Control<CheckBox>(window, "AcceptUpload").IsChecked);
        Assert.False(Control<Button>(window, "SendButton").IsEnabled);
        Assert.False(Control<Button>(window, "PttButton").IsEnabled);
    }
    private static void Escape(Window window, string target)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Assert.IsAssignableFrom<UIElement>(window.FindName(target)).RaiseEvent(key);
        Assert.True(key.Handled);
    }

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    private static string Text(Window window, string name) => name == "ResultText"
        ? Control<TextBlock>(window, name).Text : Control<TextBox>(window, name).Text;
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Loaded(Window window) => Until(() => Text(window, "ResultText").StartsWith("Choices loaded", StringComparison.Ordinal));
    private static void Permit(Window window, bool voice = false, bool microphone = false)
    {
        Control<CheckBox>(window, "VoiceChoice").IsChecked = voice;
        Control<CheckBox>(window, "AcceptCapture").IsChecked = microphone;
        Control<CheckBox>(window, "AcceptUpload").IsChecked = microphone;
        Control<CheckBox>(window, "AcceptAction").IsChecked = true;
    }
    private static void SendKey(Window window, Key key, bool down) =>
        Control<Button>(window, "PttButton").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
        { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static Task Heartbeat() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(2));
    private static async Task DispatcherTest(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) => { args.Handled = true; finished.TrySetException(args.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}

internal sealed class LiveFixture : IAsyncDisposable
{
    internal const string Secret = "synthetic-vault-canary-NOT-A-KEY";
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Live.Fixture." + Guid.NewGuid().ToString("N"));
    internal SettingsStore Store { get; }
    internal DelayedSettings Settings { get; }
    internal RuntimeClock Clock { get; } = new();
    internal SetupOperationRunner Runner { get; } = new();
    internal NativeFixture Native { get; } = new();
    internal SessionFixture Events { get; } = new();
    internal RecordingHandler Stt { get; } = new();
    internal TextRecordingHandler Llm { get; } = new();
    internal TextRecordingHandler Tts { get; } = SpeechFixtures.Handler();
    internal ControlledCapture Capture { get; } = new();
    internal ControlledDevice Output { get; }
    internal LiveConversationController Controller { get; }
    internal LiveFixture(ControlledDevice? output = null)
    {
        Store = new(DirectoryPath);
        Output = output ?? new();
        var vault = new WindowsCredentialStore(Native);
        Settings = new(new SetupService(Store, vault));
        Controller = new(Runner, Settings, vault, Capture, Output, Clock,
            (credentials, clock) => ConversationRuntime.ForFixture(
                OpenAiTextGenerationAdapter.CreateForFixture(Llm, credentials, clock),
                OpenAiSpeechSynthesisAdapter.CreateForFixture(Tts, credentials, clock), Output, new(), clock),
            (credentials, clock) => OpenAiTranscriptionAdapter.CreateForFixture(Stt, credentials, clock));
        Events.LockedChanged += Controller.SetSessionLocked;
        Llm.Inspect = Tts.Inspect = request =>
        {
            Assert.Equal("Bearer " + Secret, request.Headers.Authorization!.ToString());
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
        };
    }
    internal static async Task<LiveFixture> Create(ControlledDevice? output = null)
    {
        var fixture = new LiveFixture(output);
        var settings = SetupSettings.Begin(null);
        settings = settings with { Profile = settings.Profile with { Kind = ProfileKind.Api },
            Audio = AudioSettings.Create() };
        settings = settings with { Audio = settings.Audio with
        {
            Input = settings.Audio.Input.Select("private-input-id", "Selected test microphone"),
            Output = settings.Audio.Output.Select("private-output-id", "Selected test output")
        } };
        foreach (var role in Enum.GetValues<SetupRole>())
        {
            settings = SetupSettings.SelectRoute(settings, role, role switch
            {
                SetupRole.Stt => "gpt-transcribe", SetupRole.Llm => TextFixtures.Model, _ => "gpt-4o-mini-tts-2025-12-15"
            }, role == SetupRole.Tts ? "coral" : null);
            var route = settings.Setup!.Routes.Single(r => r.Role == role).WithCredential(Guid.NewGuid());
            settings = SetupSettings.ReplaceRoute(settings, route with { Consent = route.Selection() });
        }
        await fixture.Save(settings);
        return fixture;
    }
    internal async Task Save(AppSettings settings)
    {
        var prior = await Store.LoadAsync();
        Assert.True((await Store.SaveAsync(settings, prior.Revision)).Saved);
        Controller.Configure(await Store.LoadAsync());
    }
    internal LiveConversationOperation Start(string text = "Explicit test input.", bool voice = false, bool microphone = false, CancellationToken caller = default) =>
        Controller.Start(microphone ? null : text, voice, microphone, true, microphone, microphone, caller);
    internal LiveConversationWindow Open()
    {
        var window = new LiveConversationWindow(Settings, Runner, Controller, Events, clock: Clock)
        { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        return window;
    }
    internal void Answer(params string[] text) => Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Harness.Trace(text)));
    internal async Task Finish(LiveConversationOperation? operation = null, bool advance = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (operation is null ? Runner.IsRunning : !operation.Worker.Completion.IsCompleted)
        {
            if (advance) Clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1, timeout.Token);
        }
        if (operation is not null) await operation.Worker.Completion;
    }
    internal void NoEffects()
    {
        Assert.Empty(Native.Targets);
        Assert.Equal(0, Stt.Calls);
        Assert.Equal(0, Llm.Calls);
        Assert.Equal(0, Tts.Calls);
        Assert.Equal(0, Capture.Opens);
        Assert.Equal(0, Output.Opens);
    }
    public async ValueTask DisposeAsync()
    {
        Native.Release.Set();
        Output.Release.Set();
        Capture.OpenBlock?.Set();
        Capture.ReadBlock?.Set();
        Capture.DisposeBlock?.Set();
        await Controller.DisposeAsync();
        if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, true);
    }
    internal sealed class NativeFixture : ICredentialNative
    {
        public bool IsSupported => true;
        internal bool Block;
        internal int Error;
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Targets { get; } = new();
        internal ConcurrentQueue<int> Threads { get; } = new();
        internal ConcurrentQueue<SecretLease> Leases { get; } = new();
        public int Read(string target, out SecretLease? secret)
        {
            Targets.Enqueue(target);
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            secret = new SecretLease(Secret);
            Leases.Enqueue(secret);
            Entered.TrySetResult();
            if (Block) Release.Wait();
            return Error;
        }
        public int Write(string target, ReadOnlySpan<char> secret) => throw new InvalidOperationException("Unexpected native write.");
        public int Delete(string target) => throw new InvalidOperationException("Unexpected native delete.");
    }
    internal sealed class SessionFixture : IAudioSessionEvents
    {
        public event Action<bool>? LockedChanged;
        internal void Signal(bool value) => LockedChanged?.Invoke(value);
        public void Dispose() { }
    }
    internal sealed class DelayedSettings(ISetupService inner) : ISetupService
    {
        internal Func<CancellationToken, Task>? BeforeLoad;
        public async Task<SettingsLoadResult> LoadAsync(CancellationToken token = default)
        {
            if (BeforeLoad is { } before) await before(token);
            return await inner.LoadAsync(token);
        }
        public Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default) => inner.SaveAsync(settings, revision, token);
        public Task<SetupSaveResult> ReplaceCredentialAsync(AppSettings settings, string? revision, SetupRole role, SecretLease secret, CancellationToken token = default) => inner.ReplaceCredentialAsync(settings, revision, role, secret, token);
        public Task<SetupSaveResult> DetachCredentialAsync(AppSettings settings, string? revision, SetupRole role, CancellationToken token = default) => inner.DetachCredentialAsync(settings, revision, role, token);
        public Task<SetupSaveResult> RemoveDetachedAsync(AppSettings settings, string? revision, PendingCredentialRemoval removal, CancellationToken token = default) => inner.RemoveDetachedAsync(settings, revision, removal, token);
        public CredentialError CheckCredential(AppSettings settings, SetupRole role) => inner.CheckCredential(settings, role);
    }
}

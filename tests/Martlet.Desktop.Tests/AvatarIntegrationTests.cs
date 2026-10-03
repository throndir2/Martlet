using System.Collections.Concurrent;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Avatar.Audio2Face.Tests;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Contracts;
using Martlet.Desktop;
using Martlet.Providers.Tests;
using NvidiaAce.AnimationData.V1;
using NvidiaAce.Status.V1;
using Output = NvidiaAce.Controller.V1.AnimationDataStream;

namespace Martlet.Desktop.Tests;

public sealed class AvatarIntegrationTests
{
    private sealed class Renderer : IAvatarRenderer
    {
        internal ConcurrentQueue<RendererMessage> Messages { get; } = new();
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        internal bool FailApply { get; set; }
        internal TaskCompletionSource? ConfigureRelease { get; set; }
        internal TaskCompletionSource EnteredConfigure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? StartRelease { get; set; }
        internal TaskCompletionSource EnteredStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid activation = Guid.NewGuid();
        internal RendererParameter[] Parameters { get; init; } = [new("Jaw", -10, 10, 0, ["Mouth"])];
        public async Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, CancellationToken token)
        {
            EnteredStart.TrySetResult();
            if (StartRelease is { } held) await held.Task;
            Capabilities = new(revision.ToLowerInvariant(), Parameters);
        }
        public async Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested();
            if (HasExited || FailApply && kind == "apply") throw new IOException("controlled renderer failure");
            Messages.Enqueue(RendererProtocol.Message(kind, activation, data));
            if (kind == "configure")
            {
                EnteredConfigure.TrySetResult();
                if (ConfigureRelease is { } held) await held.Task;
            }
            return RendererProtocol.Message("ok", activation, new { });
        }
        public ValueTask DisposeAsync() { HasExited = true; exited.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private static AvatarConfiguration Mapping(string modelId) => new()
    {
        Version = ContractVersion.Current, Enabled = false, PreferredBackend = AvatarBackend.Audio2Face,
        RequestedAspects = [AvatarAspect.Mouth], OmittedAspects = [],
        Assignments = [new() { Aspect = AvatarAspect.Mouth, SourceId = AvatarController.SourceId,
            MappingId = "explicit", AcceptReduced = true }],
        MappingProfiles = [new() { Id = "explicit", SourceId = AvatarController.SourceId, ModelId = modelId,
            Mappings = [new() { Source = new() { Blendshape = "jawOpen" }, TargetParameterId = "Jaw",
                OutputMinimum = -10, OutputMaximum = 10 }] }]
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_conversation_pcm_stream_protocol_composer_and_renderer_boundary_preserve_voice(bool rendererFails)
    {
        await using var backend = await ProtocolFixture.StartAsync(async (writer, context) =>
        {
            await writer.WriteAsync(new Output { AnimationDataStreamHeader = new()
            {
                SkelAnimationHeader = new() { BlendShapes = { "JawOpen" } }
            } });
            await writer.WriteAsync(new Output { AnimationData = new()
            {
                SkelAnimation = new() { BlendShapeWeights = { new FloatArrayWithTimeCode
                    { TimeCode = 0, Values = { 0.75f } } } }
            } });
            await writer.WriteAsync(new Output { Status = new() { Code = Status.Types.Code.Success } });
        });
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { FailApply = rendererFails };
        await using var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        await controller.InspectAsync(scope.Profile(backend.Endpoint.AbsoluteUri), default);
        var profile = controller.InspectedProfile! with
        {
            Configuration = AvatarProfile.ConfigurationElement(Mapping(controller.Capabilities!.ModelId))
        };
        await controller.ActivateAsync(profile, true, default);
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: controller.Observer);
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(() => controller.Status.Contains("unavailable", StringComparison.Ordinal) ||
            renderer.Messages.Any(m => m.Kind == "apply"));
        Assert.True(rendererFails || renderer.Messages.Any(m => m.Kind == "apply"), controller.Status);
        Assert.False(turn.Completion.IsCompleted);
        Assert.NotEmpty(backend.Requests);
        if (!rendererFails)
        {
            var applied = RendererProtocol.Data<RendererParameters>(renderer.Messages.First(m => m.Kind == "apply"));
            Assert.Equal(5, applied.Parameters["Jaw"]);
            Assert.Equal(turn.SessionId, applied.Identity.SessionId);
            Assert.Equal(turn.TurnId, applied.Identity.TurnId);
            Assert.Equal(AvatarController.SourceId, applied.Identity.SourceId);
            Assert.Equal(0, applied.ActualPlaybackSampleOffset);
        }
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        Assert.Equal(SpeechFixtures.Audio(), device.Bytes);
        await controller.StopAsync();
        Assert.False(controller.Observer.IsEnabled);
    }

    private sealed record MouthLevel(double Level);

    [Fact]
    public async Task Shown_character_moves_its_mouth_with_generated_speech_loudness_without_audio2face()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        await controller.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        Assert.True(controller.IsShowing);
        Assert.True(controller.Observer.IsEnabled);
        controller.Revoke();
        Assert.True(controller.Observer.IsEnabled);
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: controller.Observer);
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(() => renderer.Messages.Any(m => m.Kind == "mouth" && RendererProtocol.Data<MouthLevel>(m).Level > 0));
        Assert.DoesNotContain(renderer.Messages, m => m.Kind is "configure" or "apply");
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        Assert.Equal(SpeechFixtures.Audio(), device.Bytes);
        await controller.StopAsync();
        Assert.False(controller.Observer.IsEnabled);
        Assert.False(controller.IsShowing);
    }

    [Fact]
    public async Task Automatic_lip_sync_uses_a_detected_audio2face_service_with_the_built_in_mouth_mapping()
    {
        await using var backend = await ProtocolFixture.StartAsync(async (writer, context) =>
        {
            await writer.WriteAsync(new Output { AnimationDataStreamHeader = new()
            {
                SkelAnimationHeader = new() { BlendShapes = { "JawOpen" } }
            } });
            await writer.WriteAsync(new Output { AnimationData = new()
            {
                SkelAnimation = new() { BlendShapeWeights = { new FloatArrayWithTimeCode
                    { TimeCode = 0, Values = { 0.75f } } } }
            } });
            await writer.WriteAsync(new Output { Status = new() { Code = Status.Types.Code.Success } });
        });
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Parameters = [new("aa", 0, 1, 0, ["Mouth"]), new("blink", 0, 1, 0, ["Expression"])] };
        await using var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        await controller.ShowAsync(scope.Profile(backend.Endpoint.AbsoluteUri), default);
        Assert.Contains("detected", controller.Status, StringComparison.Ordinal);
        var configured = RendererProtocol.Data<RendererConfiguration>(renderer.Messages.Single(m => m.Kind == "configure"));
        Assert.Equal("aa", Assert.Single(configured.Targets).Target);
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: controller.Observer);
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(() => renderer.Messages.Any(m => m.Kind == "apply"));
        var applied = RendererProtocol.Data<RendererParameters>(renderer.Messages.First(m => m.Kind == "apply"));
        Assert.Equal(0.75, applied.Parameters["aa"], 3);
        Assert.Equal(turn.TurnId, applied.Identity.TurnId);
        Assert.NotEmpty(backend.Requests);
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        Assert.Equal(SpeechFixtures.Audio(), device.Bytes);
        await controller.StopAsync();
        Assert.False(controller.IsShowing);
    }

    private sealed class FakeHostLink : IAvatarHostLink
    {
        internal ConcurrentQueue<(long Samples, CorrelationIds Ids)> Requests { get; } = new();
        internal string Name { get; init; } = "192.168.1.20:9443";
        internal bool Disposed { get; private set; }
        public string Authority => Name;
        public Task<bool> ReadyAsync(CancellationToken token) => Task.FromResult(true);
        public async IAsyncEnumerable<Martlet.Avatar.Audio2Face.Remote.RemoteFaceFrame> AnimateAsync(CorrelationIds ids, long epoch,
            int sampleRate, ReadOnlyMemory<byte> pcm, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            Requests.Enqueue((pcm.Length / 2, ids));
            await Task.Yield();
            yield return new(0, new Dictionary<string, double> { ["jawOpen"] = 0.6, ["unknownShape"] = 0.9 });
            yield return new(480, new Dictionary<string, double> { ["jawOpen"] = 0.3 });
        }
        public void Invalidate() { }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Lip_sync_moves_to_another_host_while_the_character_keeps_showing()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Parameters = [new("aa", 0, 1, 0, ["Mouth"])] };
        var first = new FakeHostLink();
        var second = new FakeHostLink { Name = "192.168.1.30:9443" };
        AvatarRemoteHost Remote(string id, string ip) => new()
        {
            Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        };
        await using var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true,
            openHost: host => host.HostId == "gpu-a" ? first : second);
        await controller.ShowAsync(scope.Profile($"http://127.0.0.1:{port}/") with { RemoteHost = Remote("gpu-a", "192.168.1.20") }, default);
        await controller.UseHostAsync(Remote("gpu-b", "192.168.1.30"), default);
        Assert.True(first.Disposed);
        Assert.Contains("192.168.1.30:9443", controller.Status, StringComparison.Ordinal);
        Assert.Equal("gpu-b", controller.InspectedProfile!.RemoteHost!.HostId);
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: controller.Observer);
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(() => renderer.Messages.Any(m => m.Kind == "apply"));
        Assert.Empty(first.Requests);
        Assert.NotEmpty(second.Requests);
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        await controller.UseHostAsync(null, default);
        Assert.True(second.Disposed);
        Assert.Contains("this PC", controller.Status, StringComparison.Ordinal);
        await controller.StopAsync();
    }

    [Fact]
    public async Task Automatic_lip_sync_uses_the_paired_martlet_host_when_this_pc_has_no_audio2face()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Parameters = [new("aa", 0, 1, 0, ["Mouth"])] };
        var link = new FakeHostLink();
        AvatarRemoteHost? opened = null;
        await using var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true,
            openHost: host => { opened = host; return link; });
        var remote = new AvatarRemoteHost
        {
            Origin = "https://192.168.1.20:9443", HostId = "gpu-host", SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        };
        await controller.ShowAsync(scope.Profile($"http://127.0.0.1:{port}/") with { RemoteHost = remote }, default);
        Assert.Same(remote, opened);
        Assert.Contains("host 192.168.1.20:9443", controller.Status, StringComparison.Ordinal);
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: controller.Observer);
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(() => renderer.Messages.Any(m => m.Kind == "apply"));
        var applied = RendererProtocol.Data<RendererParameters>(renderer.Messages.First(m => m.Kind == "apply"));
        Assert.Equal(0.6, applied.Parameters["aa"], 3);
        Assert.Equal(turn.TurnId, applied.Identity.TurnId);
        var request = Assert.Single(link.Requests);
        Assert.Equal(SpeechFixtures.Audio().Length / 2, request.Samples);
        Assert.Equal(turn.TurnId, request.Ids.TurnId);
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        Assert.Equal(SpeechFixtures.Audio(), device.Bytes);
        await controller.StopAsync();
    }

    [Fact]
    public async Task Automatic_lip_sync_falls_back_to_loudness_when_no_audio2face_service_is_listening()
    {
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { Parameters = [new("aa", 0, 1, 0, ["Mouth"])] };
        await using var controller = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        await controller.ShowAsync(scope.Profile($"http://127.0.0.1:{port}/"), default);
        Assert.Contains("Audio2Face isn't running", controller.Status, StringComparison.Ordinal);
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: controller.Observer);
        harness.Answer("Actual generated PCM test.");
        var turn = harness.Start();
        await Harness.Until(() => renderer.Messages.Any(m => m.Kind == "mouth" && RendererProtocol.Data<MouthLevel>(m).Level > 0));
        Assert.DoesNotContain(renderer.Messages, m => m.Kind is "reset" or "apply");
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        await controller.StopAsync();
    }

    [Fact]
    public async Task Hundred_activation_stop_schedules_never_reuse_old_renderer_identity_or_authorization()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderers = new List<Renderer>();
        await using var controller = new AvatarController(createRenderer: () =>
        {
            var next = new Renderer(); renderers.Add(next); return next;
        });
        for (var schedule = 0; schedule < 100; schedule++)
        {
            await controller.InspectAsync(scope.Profile(), default);
            var profile = controller.InspectedProfile! with
            {
                Configuration = AvatarProfile.ConfigurationElement(Mapping(controller.Capabilities!.ModelId))
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActivateAsync(profile, false, default));
            await controller.ActivateAsync(profile, true, default);
            if (schedule % 2 == 0) controller.Revoke();
            await controller.StopAsync();
            Assert.False(controller.Observer.IsEnabled);
            Assert.False(controller.IsActive);
            Assert.All(renderers, r => Assert.True(r.HasExited));
            Assert.DoesNotContain(renderers.SelectMany(r => r.Messages), m => m.Kind == "apply");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_or_profile_drift_requires_fresh_inspection(bool profileChanged)
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var controller = new AvatarController(createRenderer: () => renderer);
        await controller.InspectAsync(scope.Profile(), default);
        var selected = controller.InspectedProfile! with
        {
            Configuration = AvatarProfile.ConfigurationElement(Mapping(controller.Capabilities!.ModelId))
        };
        if (profileChanged) selected = selected with { ProfileId = Guid.NewGuid() };
        else await File.WriteAllBytesAsync(scope.Model, [9, 9, 9]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActivateAsync(selected, true, default));
        Assert.False(controller.Observer.IsEnabled);
        Assert.DoesNotContain(renderer.Messages, m => m.Kind == "configure");
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("revoke")]
    [InlineData("caller")]
    public async Task Revoking_held_configuration_cannot_commit_late_activation_or_feed_pcm(string cause)
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { ConfigureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var controller = new AvatarController(createRenderer: () => renderer);
        await controller.InspectAsync(scope.Profile(), default);
        var profile = controller.InspectedProfile! with
        { Configuration = AvatarProfile.ConfigurationElement(Mapping(controller.Capabilities!.ModelId)) };
        using var caller = new CancellationTokenSource();
        var activating = controller.ActivateAsync(profile, true, caller.Token);
        await renderer.EnteredConfigure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? stopping = null;
        if (cause == "stop") stopping = controller.StopAsync();
        else if (cause == "caller") caller.Cancel();
        else controller.Revoke();
        renderer.ConfigureRelease.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activating);
        if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controller.Observer.IsEnabled);
        Assert.False(controller.IsActive);
        await using var voice = new Harness(generatedSpeech: controller.Observer);
        voice.Answer("Voice unaffected after canceled activation.");
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(voice.Start())).State);
        Assert.False(controller.Observer.Segments.TryRead(out _));
        Assert.DoesNotContain(renderer.Messages, m => m.Kind is "reset" or "apply");
    }

    [Fact]
    public async Task Stop_during_inspection_cannot_publish_stale_capabilities()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { StartRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var controller = new AvatarController(createRenderer: () => renderer);
        var inspecting = controller.InspectAsync(scope.Profile(), default);
        await renderer.EnteredStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = controller.StopAsync();
        renderer.StartRelease.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspecting);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(controller.Capabilities);
        Assert.False(controller.Observer.IsEnabled);
        Assert.True(renderer.HasExited);
    }

    [Fact]
    public async Task Idle_renderer_failure_is_reported_without_waiting_for_next_voice_segment()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var controller = new AvatarController(createRenderer: () => renderer);
        await controller.InspectAsync(scope.Profile(), default);
        var profile = controller.InspectedProfile! with
        { Configuration = AvatarProfile.ConfigurationElement(Mapping(controller.Capabilities!.ModelId)) };
        await controller.ActivateAsync(profile, true, default);
        await renderer.DisposeAsync();
        await Harness.Until(() => controller.Status.Contains("unavailable", StringComparison.Ordinal));
        Assert.False(controller.Observer.IsEnabled);
        Assert.False(controller.IsActive);
    }
}

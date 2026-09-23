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
        internal bool FailApply { get; set; }
        private readonly Guid activation = Guid.NewGuid();
        public Task StartAsync(AvatarProfile profile, string revision, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested();
            if (HasExited || FailApply && kind == "apply") throw new IOException("controlled renderer failure");
            Messages.Enqueue(RendererProtocol.Message(kind, activation, data));
            return Task.FromResult(RendererProtocol.Message("ok", activation, new { }));
        }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
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
}

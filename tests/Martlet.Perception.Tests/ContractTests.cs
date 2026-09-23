using Martlet.Perception;
using System.Runtime.InteropServices;

namespace Martlet.Perception.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Protocol_and_roles_are_exact_and_detector_is_absent()
    {
        Assert.Equal("martlet.perception.worker", PerceptionProtocol.ContractId);
        Assert.Equal(new PerceptionProtocolVersion(1, 0),
            PerceptionProtocolVersion.Current);
        Assert.Equal(
            [PerceptionRole.Ocr, PerceptionRole.VisualQuestionAnswering],
            Enum.GetValues<PerceptionRole>());
        Assert.Equal(
            [PerceptionCaptureScope.SelectedWindow],
            Enum.GetValues<PerceptionCaptureScope>());
    }

    [Theory]
    [InlineData(PerceptionRole.Ocr, 3)]
    [InlineData(PerceptionRole.VisualQuestionAnswering, 5)]
    public void Fixture_identity_pins_role_runtime_model_artifacts_and_resources(
        PerceptionRole role,
        int expectedArtifacts)
    {
        var identity = DeterministicPerceptionFixtureIdentity.Create(
            role, cpuUnits: 2, gpuMemoryMiB: 1024);

        Assert.Equal(role, identity.Role);
        Assert.Equal(PerceptionEvidenceKind.SyntheticFixture, identity.Evidence);
        Assert.Equal(expectedArtifacts, identity.Artifacts.Count);
        Assert.Equal(2, identity.Resources.CpuUnits);
        Assert.Equal(1024, identity.Resources.GpuMemoryMiB);
        Assert.All(identity.Artifacts, artifact =>
        {
            Assert.Equal(40, artifact.Revision.Length);
            Assert.Equal(64, artifact.Sha256.Length);
            Assert.NotEqual("unknown", artifact.ArtifactId);
        });
        Assert.Contains("fixture", identity.Runtime.RuntimeVersion);
        Assert.Contains("fixture", identity.Model.AdapterVersion);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("main")]
    [InlineData("unknown")]
    [InlineData("unpinned")]
    [InlineData("floating")]
    public void Floating_runtime_versions_are_rejected(string version)
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var runtime = worker.Runtime with { RuntimeVersion = version };

        PerceptionTestData.Failure(PerceptionWorkerFailure.InvalidData, () => new PerceptionWorkerIdentity(
            worker.WorkerId,
            worker.Evidence,
            worker.Role,
            runtime,
            worker.Model,
            worker.Artifacts,
            worker.Limits,
            worker.Resources,
            worker.Cancellation));
    }

    [Fact]
    public void Inline_png_is_validated_copied_and_digest_bound()
    {
        var bytes = PerceptionTestData.Png(width: 3, height: 2, colorType: 2);
        var content = SelectedWindowFrameContent.FromInlinePng(bytes);
        var digest = content.Sha256;
        bytes[0] = 0;

        Assert.Equal(PerceptionFrameContentKind.InlinePng, content.Kind);
        Assert.Equal(3, content.Width);
        Assert.Equal(2, content.Height);
        Assert.Equal(137, content.InlineBytes.Span[0]);
        Assert.Equal(64, digest.Length);
        Assert.Null(content.ReferenceId);
        Assert.DoesNotContain("137", content.ToString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> MalformedPngCases()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[64]];

        var badSignature = PerceptionTestData.Png();
        badSignature[0] = 0;
        yield return [badSignature];

        var badCrc = PerceptionTestData.Png();
        badCrc[^1] ^= 0xff;
        yield return [badCrc];

        yield return [PerceptionTestData.Png(colorType: 0)];
        yield return [PerceptionTestData.Png(interlace: 1)];
        yield return [PerceptionTestData.Png(filter: 9)];

        var trailing = PerceptionTestData.Png().Concat(new byte[] { 0 }).ToArray();
        yield return [trailing];

        var truncated = PerceptionTestData.Png()[..^4];
        yield return [truncated];

        yield return
        [
            PerceptionTestData.Png(compressedSuffix: [0xaa, 0xbb])
        ];
    }

    [Theory]
    [MemberData(nameof(MalformedPngCases))]
    public void Malformed_or_unsupported_png_is_rejected(byte[] bytes)
    {
        PerceptionTestData.Failure(
            PerceptionWorkerFailure.InvalidImage,
            () => SelectedWindowFrameContent.FromInlinePng(bytes));
    }

    [Fact]
    public void Oversized_inline_image_is_rejected_before_parsing()
    {
        var bytes = new byte[PerceptionProtocol.MaximumImageBytes + 1];

        PerceptionTestData.Failure(
            PerceptionWorkerFailure.LimitExceeded,
            () => SelectedWindowFrameContent.FromInlinePng(bytes));
    }

    [Fact]
    public async Task Public_inline_memory_cannot_mutate_authorized_transport_bytes()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: worker);
        var exposed = request.Frame.Content.InlineBytes;
        Assert.True(MemoryMarshal.TryGetArray(exposed, out var segment));
        segment.Array![segment.Offset] = 0;

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionJobOutcome.Completed, result.Outcome);
        Assert.Equal(137,
            Assert.Single(transport.Requests).Frame.Content.InlineBytes.Span[0]);
        Assert.Equal(request.Frame.Content.Sha256,
            transport.Requests[0].Frame.Content.Sha256);
    }

    [Theory]
    [InlineData("https://host-2/frame")]
    [InlineData("C:\\frames\\one.png")]
    [InlineData("../frame")]
    [InlineData("/tmp/frame")]
    public void Ephemeral_reference_rejects_endpoint_and_path_shapes(string reference)
    {
        PerceptionTestData.Failure(
            PerceptionWorkerFailure.InvalidFrameReference,
            () => SelectedWindowFrameContent.FromEphemeralGatewayReference(
                reference,
                new string('a', 64),
                100,
                10,
                10,
                PerceptionTestData.Now.AddSeconds(5)));
    }

    [Fact]
    public void Ephemeral_reference_carries_only_bounded_metadata()
    {
        var content = SelectedWindowFrameContent.FromEphemeralGatewayReference(
            "frame-ref-001",
            new string('a', 64),
            100,
            10,
            10,
            PerceptionTestData.Now.AddSeconds(5));

        Assert.Equal(PerceptionFrameContentKind.EphemeralGatewayReference, content.Kind);
        Assert.Equal("frame-ref-001", content.ReferenceId);
        Assert.True(content.InlineBytes.IsEmpty);
        Assert.Equal(PerceptionTestData.Now.AddSeconds(5),
            content.ReferenceExpiresAtUtc);
    }

    [Fact]
    public void Ocr_has_no_prompt_and_vlm_question_is_bounded()
    {
        Assert.Null(PerceptionTaskDefinition.ReadVisibleText().Question);
        Assert.Equal("What is visible?",
            PerceptionTaskDefinition.AskAboutSelectedWindow("What is visible?").Question);
        var oversized = new string('x', PerceptionProtocol.MaximumQuestionCharacters + 1);
        PerceptionTestData.Failure(
            PerceptionWorkerFailure.InvalidData,
            () => PerceptionTaskDefinition.AskAboutSelectedWindow(oversized));
    }

    [Fact]
    public void Job_requires_exact_role_and_selected_worker_limits()
    {
        var ocr = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var frame = PerceptionTestData.Frame(1);

        PerceptionTestData.Failure(
            PerceptionWorkerFailure.RoleMismatch,
            () => new PerceptionJobIntent(
                PerceptionTestData.Ids(),
                PerceptionTestData.Destination,
                ocr,
                frame,
                PerceptionTaskDefinition.AskAboutSelectedWindow("What?"),
                PerceptionTestData.Now,
                PerceptionTestData.Now.AddSeconds(5),
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Authorization_is_default_no_exact_and_content_redacted()
    {
        var request = PerceptionTestData.Intent(
            PerceptionRole.VisualQuestionAnswering,
            question: "Private fixture question");

        PerceptionTestData.Failure(
            PerceptionWorkerFailure.PermissionRequired,
            () => request.Authorize(
                PerceptionVisionAuthorizationDecision.No,
                Guid.NewGuid(),
                request.DeadlineUtc));
        var authorization = PerceptionTestData.Authorize(request);

        Assert.Equal(request.Frame.FrameId, authorization.FrameId);
        Assert.Equal(request.Frame.Content.Sha256, authorization.FrameSha256);
        Assert.Equal(request.ExpectedWorker.Model.ModelRevision,
            authorization.ModelRevision);
        Assert.DoesNotContain("Private fixture question", request.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Private fixture question", authorization.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://host-2/perception")]
    [InlineData("/martlet/v1/perception")]
    [InlineData("C:\\worker")]
    public void Job_destination_rejects_arbitrary_endpoint_or_path(string destination)
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);

        PerceptionTestData.Failure(
            PerceptionWorkerFailure.InvalidData,
            () => new PerceptionJobIntent(
                PerceptionTestData.Ids(),
                destination,
                worker,
                PerceptionTestData.Frame(1),
                PerceptionTaskDefinition.ReadVisibleText(),
                PerceptionTestData.Now,
                PerceptionTestData.Now.AddSeconds(5),
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Adapter_rejects_arbitrary_model_change_before_transport()
    {
        var selected = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var changed = new PerceptionWorkerIdentity(
            selected.WorkerId,
            selected.Evidence,
            selected.Role,
            selected.Runtime,
            selected.Model with { ModelId = "another-model" },
            selected.Artifacts,
            selected.Limits,
            selected.Resources,
            selected.Cancellation);
        var (adapter, transport) = PerceptionTestData.Adapter(selected);
        var request = PerceptionTestData.Intent(
            PerceptionRole.Ocr, worker: changed);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionWorkerFailure.IdentityMismatch, result.Failure);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void Gateway_binding_rejects_automatic_redirects()
    {
        var binding = PerceptionTestData.Binding() with
        {
            AutomaticRedirectsAllowed = true
        };
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);

        PerceptionTestData.Failure(
            PerceptionWorkerFailure.RedirectRejected,
            () => new DeterministicPerceptionWorkerTransport(binding, worker));
    }

    [Fact]
    public async Task Worker_request_policy_forbids_every_hidden_effect()
    {
        var worker = DeterministicPerceptionFixtureIdentity.Create(PerceptionRole.Ocr);
        var (adapter, transport) = PerceptionTestData.Adapter(worker);
        var request = PerceptionTestData.Intent(PerceptionRole.Ocr, worker: worker);

        var result = await adapter.ExecuteAsync(
            request, PerceptionTestData.Authorize(request));

        Assert.Equal(PerceptionJobOutcome.Completed, result.Outcome);
        var policy = Assert.Single(transport.Requests).Policy;
        Assert.True(policy.SelectedWindowOnly);
        Assert.False(policy.ScreenWideFallbackAllowed);
        Assert.False(policy.ArtifactDownloadAllowed);
        Assert.False(policy.ModelFallbackAllowed);
        Assert.False(policy.ProviderFallbackAllowed);
        Assert.False(policy.RedirectAllowed);
        Assert.False(policy.PersistentImageAllowed);
        Assert.False(policy.ArbitraryEndpointAllowed);
        Assert.False(policy.ArbitraryPathAllowed);
    }

    [Fact]
    public void Every_failure_has_a_unique_code_and_actionable_remedy()
    {
        var details = Enum.GetValues<PerceptionWorkerFailure>()
            .Select(PerceptionWorkerFailureCatalog.Get)
            .ToArray();

        Assert.Equal(details.Length,
            details.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(details, item =>
        {
            Assert.StartsWith("perception.", item.Code, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(item.Summary));
            Assert.False(string.IsNullOrWhiteSpace(item.Remedy));
        });
    }
}

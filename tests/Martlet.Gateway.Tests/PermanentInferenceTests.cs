using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Gateway;
using Martlet.Perception;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway.Tests;

public sealed class PermanentInferenceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Wrong_gateway_protocol_never_reaches_worker(int major)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        var body = JsonNode.Parse(GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()))!;
        body["protocol_version"]!["major"] = major;
        using var request = host.SignedPost(worker.Route.Path, GatewayRole.Voice, signer, Encode(body));
        using var response = await host.Client.SendAsync(request);
        Assert.Equal("protocol.unsupported", await GatewayTestHost.FailureCode(response));
        Assert.False(worker.Preparing.Task.IsCompleted);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData(true, "action.denied")]
    [InlineData(false, "worker.unavailable")]
    public async Task Paired_device_is_not_action_permission_or_worker_readiness(bool denied, string code)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            Denied = denied, Ready = denied
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(code, await GatewayTestHost.FailureCode(response));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("rotate")]
    [InlineData("device")]
    public async Task Authority_removed_during_permission_preparation_prevents_dispatch(string operation)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            PreparationGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = Post(host, worker, signer);
        var responseTask = host.Client.SendAsync(request).AsTask();
        try
        {
            await worker.Preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RemoveAuthority(host, credential, operation);
        }
        finally { worker.PreparationGate.TrySetResult(); }
        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(await GatewayTestHost.FailureCode(response),
            new[] { "auth.revoked", "auth.expired", "auth.invalid" });
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("rotate")]
    [InlineData("device")]
    public async Task Midstream_authority_loss_publishes_no_late_data(string operation)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            DataGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Contains("\"type\":\"started\"", await reader.ReadLineAsync());
        try
        {
            await worker.DataPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RemoveAuthority(host, credential, operation);
        }
        finally { worker.DataGate.TrySetResult(); }
        var remainder = await ReadAbortedAsync(reader);
        Assert.DoesNotContain("text_delta", remainder, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"completed\"", remainder, StringComparison.Ordinal);
        Assert.Contains(host.Audit.Events, item => item.Code.StartsWith("auth.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rotation_overlap_preserves_current_authority_until_actual_retirement()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            DataGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        await worker.DataPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Server.Credentials.Rotate(credential.CredentialId, TimeSpan.FromMinutes(1));
        worker.DataGate.TrySetResult();
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);
        Assert.Equal("completed", events[^1].GetProperty("type").GetString());
        Assert.All(events, item => Assert.Equal(2, item.GetProperty("protocol_version").GetProperty("major").GetInt32()));
    }

    [Fact]
    public async Task Action_permission_revocation_discards_pending_data_without_unpairing()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            DataGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        await worker.DataPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.PermissionRevocation.Cancel();
        worker.DataGate.TrySetResult();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var output = await ReadAbortedAsync(reader);
        Assert.DoesNotContain("text_delta", output, StringComparison.Ordinal);
        Assert.IsType<PairedDeviceLifetime>(Assert.Single(host.Server.Credentials.ListRegistrations()).Lifetime);
    }

    [Fact]
    public async Task New_nonce_does_not_duplicate_a_completed_batch_or_reuse_its_permission()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        var bytes = GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow());
        using (var request = host.SignedPost(worker.Route.Path, GatewayRole.Voice, signer, bytes))
        using (var response = await host.Client.SendAsync(request))
            Assert.Equal("completed", (await GatewayInferenceTestData.ReadEventsAsync(response))[^1].GetProperty("type").GetString());
        using (var duplicate = host.SignedPost(worker.Route.Path, GatewayRole.Voice, signer, bytes))
        using (var response = await host.Client.SendAsync(duplicate))
            Assert.Equal("job.replay", await GatewayTestHost.FailureCode(response));
        worker.ReusedPermission = worker.LastPermission;
        using var next = Post(host, worker, signer);
        using var denied = await host.Client.SendAsync(next);
        Assert.Equal("action.denied", await GatewayTestHost.FailureCode(denied));
        Assert.Equal(1, worker.Calls);
    }

    [Fact]
    public async Task Cancel_ack_preserves_payload_and_quarantines_until_delayed_move_retires()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.F5Route())
        {
            DataGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        try
        {
            await worker.DataPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = host.SignedPost("/martlet/v1/inference/cancel", GatewayRole.Voice, signer,
                GatewayInferenceTestData.Cancellation(worker.Route, worker.LastRequest!.RequestId));
            using var canceled = await host.Client.SendAsync(cancel);
            Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
            Assert.Contains(((GatewayF5SynthesisPayload)worker.LastRequest.Payload).ReferenceAudio.ToArray(), value => value != 0);
            using var replacement = Post(host, worker, signer);
            using var rejected = await host.Client.SendAsync(replacement);
            Assert.Equal("job.busy", await GatewayTestHost.FailureCode(rejected));
            var events = await GatewayInferenceTestData.ReadEventsAsync(response).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("stream.cleanup", events[^1].GetProperty("code").GetString());
            Assert.Contains(((GatewayF5SynthesisPayload)worker.LastRequest.Payload).ReferenceAudio.ToArray(), value => value != 0);
            using var retry = Post(host, worker, signer);
            using var quarantined = await host.Client.SendAsync(retry);
            Assert.Equal("worker.quarantined", await GatewayTestHost.FailureCode(quarantined));
            Assert.Equal(1, worker.Calls);
        }
        finally { worker.DataGate.TrySetResult(); }
    }

    [Fact]
    public async Task Delayed_stream_disposal_retains_admission_even_after_terminal_received()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            DisposalGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        try
        {
            await worker.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var replacement = Post(host, worker, signer);
            using var busy = await host.Client.SendAsync(replacement);
            Assert.Equal("job.busy", await GatewayTestHost.FailureCode(busy));
        }
        finally { worker.DisposalGate.TrySetResult(); }
        Assert.Equal("completed", (await GatewayInferenceTestData.ReadEventsAsync(response))[^1].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2147483647L)]
    [InlineData(9223372036854775807L)]
    public async Task Worker_epoch_bounds_are_rejected_before_dispatch(long epoch)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr));
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(GatewayRole.Perception), host.Clock);
        var body = JsonNode.Parse(GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()))!;
        body["epoch"] = epoch;
        using var request = host.SignedPost(worker.Route.Path, GatewayRole.Perception, signer, Encode(body));
        using var response = await host.Client.SendAsync(request);
        Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task F5_reference_revision_tampering_is_rejected_before_permission()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.F5Route());
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        var body = JsonNode.Parse(GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()))!;
        body["payload"]!["transcript"] = "Changed reference transcript.";
        using var request = host.SignedPost(worker.Route.Path, GatewayRole.Voice, signer, Encode(body));
        using var response = await host.Client.SendAsync(request);
        Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        Assert.False(worker.Preparing.Task.IsCompleted);
    }

    [Fact]
    public async Task Mutable_registered_route_cannot_replace_selected_model()
    {
        var original = GatewayInferenceTestData.OllamaRoute();
        var worker = new ControlledInferenceWorker(original);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        worker.Route = GatewayInferenceTestData.OllamaRoute();
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal("worker.identity", await GatewayTestHost.FailureCode(response));
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task Empty_composition_reports_no_inference_routes()
    {
        await using var host = await GatewayTestHost.StartAsync(workers: []);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = host.SignedGet("/martlet/v1/capabilities", GatewayRole.Voice, signer);
        using var response = await host.Client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("routes").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("workers").EnumerateArray());
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2147483646L)]
    public async Task Perception_epoch_accepted_extremes_preserve_exact_value(long epoch)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr));
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(GatewayRole.Perception), host.Clock);
        var body = JsonNode.Parse(GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()))!;
        body["epoch"] = epoch;
        using var request = host.SignedPost(worker.Route.Path, GatewayRole.Perception, signer, Encode(body));
        using var response = await host.Client.SendAsync(request);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);
        Assert.Equal("completed", events[^1].GetProperty("type").GetString());
        Assert.All(events, item => Assert.Equal(epoch, item.GetProperty("epoch").GetInt64()));
    }

    [Fact]
    public async Task Perception_evidence_cannot_be_promoted_from_fixture_to_live()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr))
        {
            Transform = item =>
            {
                if (item.Kind != GatewayInferenceEventKind.Observation) return item;
                var output = JsonNode.Parse(item.Payload.Span)!;
                output["provenance"]!["evidence"] = "live_worker";
                return new(item.Kind, item.Sequence, item.RouteId, item.DestinationId, item.WorkerId,
                    item.ModelId, item.ModelRevision, item.ModelSha256, item.ArtifactIdentitySha256,
                    item.SessionId, item.TurnId, item.RequestId, item.Epoch, Encode(output));
            }
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(GatewayRole.Perception), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);
        Assert.Equal("stream.invalid", events[^1].GetProperty("code").GetString());
        Assert.DoesNotContain(events, item => item.GetProperty("type").GetString() == "observation");
    }

    [Fact]
    public async Task Perception_freshness_is_rechecked_after_permission_preparation()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr))
        {
            PreparationGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(GatewayRole.Perception), host.Clock);
        var body = JsonNode.Parse(GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()))!;
        body["payload"]!["maximum_frame_age_milliseconds"] = 500;
        using var request = host.SignedPost(worker.Route.Path, GatewayRole.Perception, signer, Encode(body));
        var sending = host.Client.SendAsync(request).AsTask();
        try
        {
            await worker.Preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.Clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        finally { worker.PreparationGate.TrySetResult(); }
        using var response = await sending;
        Assert.Equal("request.invalid", await GatewayTestHost.FailureCode(response));
        Assert.Equal(0, worker.Calls);
    }

    private static byte[] Encode(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Acknowledged_job_or_effective_token_cancellation_blocks_worker_initiation(bool explicitCancel)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(workers: []);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var signed = host.SignedGet("/martlet/v1/version", GatewayRole.Voice, signer);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = "GET", RawTarget = "/martlet/v1/version"
        });
        foreach (var header in signed.Headers)
            context.Request.Headers[header.Key] = header.Value.ToArray();
        var principal = new GatewayRequestAuthenticator(host.Identity, host.Server.Credentials).Authenticate(context.Request);
        var registry = new GatewayInferenceRouteRegistry([worker], host.Clock);
        var request = GatewayInferenceJson.ParseRequest(
            GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()), worker.Route, host.Clock.GetUtcNow());
        var job = registry.Begin(principal, request);
        using var effective = new CancellationTokenSource();
        try
        {
            await job.PrepareAsync(effective.Token);
            effective.Token.ThrowIfCancellationRequested();
            if (explicitCancel)
                Assert.True((await registry.CancelAsync(principal, new()
                {
                    RouteId = worker.Route.RouteId, RequestId = request.RequestId
                })).LocalDiscardAcknowledged);
            else
                effective.Cancel();
            var effects = 0;
            Assert.ThrowsAny<OperationCanceledException>(() =>
                job.WithPermission(() => ++effects, effective.Token));
            Assert.Equal(0, effects);
            Assert.Equal(0, worker.Calls);
        }
        finally
        {
            await job.CompleteAsync(null, cancelWorker: false);
            await job.ReleaseAsync();
        }
    }

    [Fact]
    public async Task Revocation_during_normal_iterator_cleanup_still_disposes_permission()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.F5Route())
        {
            RetirementGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var credential = await host.PairAsync();
        var signer = new GatewayRequestSigner(host.Identity, credential, host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        try
        {
            await worker.RetirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.Server.Credentials.RevokeCredential(credential.CredentialId);
            await Task.Delay(300);
            Assert.False(worker.PermissionDisposed.Task.IsCompleted);
            Assert.Contains(((GatewayF5SynthesisPayload)worker.LastRequest!.Payload).ReferenceAudio.ToArray(), value => value != 0);
        }
        finally { worker.RetirementGate.TrySetResult(); }
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var output = await ReadAbortedAsync(reader);
        Assert.DoesNotContain("\"type\":\"completed\"", output, StringComparison.Ordinal);
        await worker.PermissionDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(((GatewayF5SynthesisPayload)worker.LastRequest!.Payload).ReferenceAudio.ToArray(), value => Assert.Equal(0, value));
    }

    private static HttpRequestMessage Post(GatewayTestHost host, ControlledInferenceWorker worker,
        GatewayRequestSigner signer) => host.SignedPost(worker.Route.Path, worker.Route.RequiredRole, signer,
            GatewayInferenceTestData.Request(worker.Route, host.Clock.GetUtcNow()));

    private static void RemoveAuthority(GatewayTestHost host, IssuedDeviceCredential credential, string operation)
    {
        if (operation == "rotate") host.Server.Credentials.Rotate(credential.CredentialId, TimeSpan.Zero);
        else if (operation == "device") host.Server.Credentials.RevokeDevice(credential.DeviceId);
        else host.Server.Credentials.RevokeCredential(credential.CredentialId);
    }

    private static async Task<string> ReadAbortedAsync(StreamReader reader)
    {
        var output = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)) is { } line)
                output.AppendLine(line);
        }
        catch (HttpIOException) { }
        catch (IOException) { }
        return output.ToString();
    }

    [Fact]
    public async Task Permission_disposal_failure_is_audited_and_cannot_leave_successful_eof()
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            FailPermissionDisposal = true,
            PermissionDisposalGate = ControlledInferenceWorker.NewGate()
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        worker.PermissionDisposalGate.TrySetResult();
        await Assert.ThrowsAnyAsync<IOException>(() => GatewayInferenceTestData.ReadEventsAsync(response));
        Assert.Contains(host.Audit.Events, item => item.Code == "stream.cleanup" && item.TraceId != Guid.Empty);
        Assert.DoesNotContain("SECRET_FROM_BAD_ADAPTER", JsonSerializer.Serialize(host.Audit.Events), StringComparison.Ordinal);
        using var replacement = Post(host, worker, signer);
        using var rejected = await host.Client.SendAsync(replacement);
        Assert.Equal("worker.quarantined", await GatewayTestHost.FailureCode(rejected));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Identity_failure_at_enumerator_or_move_boundary_keeps_exact_code_and_quarantine(int validation)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            ValidationFailureAt = validation
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var events = await GatewayInferenceTestData.ReadEventsAsync(response);
            Assert.Equal("worker.identity", events[^1].GetProperty("code").GetString());
        }
        else
            Assert.Equal("worker.identity", await GatewayTestHost.FailureCode(response));
        using var replacement = Post(host, worker, signer);
        using var rejected = await host.Client.SendAsync(replacement);
        Assert.Equal("worker.quarantined", await GatewayTestHost.FailureCode(rejected));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Permission_cancellation_during_enumerator_or_move_validation_is_not_worker_failure(int validation)
    {
        var worker = new ControlledInferenceWorker(GatewayInferenceTestData.OllamaRoute())
        {
            RevokeAtValidation = validation
        };
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var signer = new GatewayRequestSigner(host.Identity, await host.PairAsync(), host.Clock);
        using var request = Post(host, worker, signer);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal("action.denied", await GatewayTestHost.FailureCode(response));
        Assert.Equal(0, worker.Calls);
    }
}

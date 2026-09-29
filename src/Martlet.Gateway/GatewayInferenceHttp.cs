using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway;

internal sealed partial class GatewayHttpApplication
{
    private async ValueTask InvokeInferenceAsync(
        HttpContext context,
        Guid traceId,
        GatewayInferenceRoute route)
    {
        var bytes = await ReadInferenceBodyAsync(
            context.Request,
            route.MaximumRequestBytes,
            context.RequestAborted).ConfigureAwait(false);
        GatewayInferenceRequest request;
        GatewayPrincipal principal;
        try
        {
            var bodyHash = crypto.Sha256(bytes);
            principal = authenticator.Authenticate(context.Request, bodyHash);
            GatewayRules.Require(principal.Role == route.RequiredRole, "auth.role");
            request = GatewayInferenceJson.ParseRequest(
                bytes,
                route,
                clock.GetUtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        GatewayInferenceRouteRegistry.GatewayInferenceJob job;
        try
        {
            job = inference.Begin(principal, request);
        }
        catch
        {
            request.Payload.Clear();
            throw;
        }
        await StreamInferenceAsync(context, traceId, job).ConfigureAwait(false);
    }

    private async ValueTask InvokeInferenceCancellationAsync(
        HttpContext context,
        Guid traceId)
    {
        var bytes = await ReadInferenceBodyAsync(
            context.Request,
            GatewayInferenceProtocol.MaximumCancelRequestBytes,
            context.RequestAborted).ConfigureAwait(false);
        GatewayPrincipal principal;
        GatewayInferenceCancellationRequest request;
        try
        {
            var bodyHash = crypto.Sha256(bytes);
            principal = authenticator.Authenticate(context.Request, bodyHash);
            request = GatewayInferenceJson.ParseCancellation(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        inference.ValidateRouteRole(request.RouteId, principal.Role);
        var receipt = await inference.CancelAsync(principal, request)
            .ConfigureAwait(false);
        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            protocol_version = GatewayProtocolVersion.Current,
            route_id = receipt.RouteId,
            request_id = receipt.RequestId,
            local_discard_acknowledged = receipt.LocalDiscardAcknowledged,
            compute_cancellation = receipt.ComputeCancellation,
            worker_may_continue = receipt.WorkerMayContinue,
            trace_id = traceId
        }).ConfigureAwait(false);
    }

    private async ValueTask StreamInferenceAsync(
        HttpContext context,
        Guid traceId,
        GatewayInferenceRouteRegistry.GatewayInferenceJob job)
    {
        var request = job.Request;
        var remaining = request.DeadlineUtc - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _ = await job.CompleteAsync(
                enumerator: null,
                cancelWorker: false).ConfigureAwait(false);
            _ = await job.ReleaseAsync().ConfigureAwait(false);
            throw new GatewayProtocolException("job.deadline");
        }
        CancellationTokenSource deadline;
        try
        {
            deadline = new(remaining, clock);
        }
        catch
        {
            _ = await job.CompleteAsync(
                enumerator: null,
                cancelWorker: false).ConfigureAwait(false);
            _ = await job.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
        using var deadlineOwner = deadline;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            deadline.Token,
            job.CancellationToken);

        IAsyncEnumerator<GatewayInferenceEvent>? enumerator = null;
        GatewayInferenceEvent? terminal = null;
        string? failureCode = null;
        var expectedSequence = 0L;
        var publishedSequence = 0L;
        var eventCount = 0;
        var responseBytes = 0;
        var responseStarted = false;
        var completed = false;
        var cancelWorker = false;
        Task<bool>? pendingMove = null;
        var pendingMoveHealthy = true;
        var quarantineWorker = false;
        CancellationTokenRegistration permissionRevocation = default;
        var streamState =
            new GatewayInferenceEventValidator.GatewayInferenceStreamState(
                request,
                identity.HostId,
                clock);

        try
        {
            try
            {
                await job.PrepareAsync(lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
                permissionRevocation = job.PermissionRevoked.Register(() => lifetime.Cancel());
                lifetime.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                throw new GatewayProtocolException(deadline.IsCancellationRequested
                    ? "job.deadline" : job.PermissionRevoked.IsCancellationRequested
                        ? "action.denied" : "job.canceled");
            }
            catch (GatewayInferenceWorkerException error)
            {
                throw new GatewayProtocolException(error.Code);
            }
            IAsyncEnumerable<GatewayInferenceEvent>? stream;
            try
            {
                stream = job.WithPermission(() => job.Registration.Worker.ExecuteAsync(
                    request,
                    lifetime.Token), lifetime.Token);
            }
            catch (GatewayInferenceWorkerException error)
            {
                failureCode = error.Code;
                stream = null;
            }
            if (failureCode is null)
            {
                if (stream is null)
                {
                    failureCode = "stream.invalid";
                }
                else
                {
                    try
                    {
                        enumerator = job.WithPermission(() => stream.GetAsyncEnumerator(lifetime.Token), lifetime.Token);
                    }
                    catch (GatewayInferenceWorkerException error)
                    {
                        failureCode = error.Code;
                    }
                    catch (GatewayProtocolException error)
                    {
                        failureCode = error.Failure.Code;
                        cancelWorker = true;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        failureCode = "worker.failed";
                    }
                }
                if (enumerator is not null)
                {
                    while (failureCode is null)
                    {
                        bool moved;
                        Task<bool>? move = null;
                        try
                        {
                            move = job.WithPermission(() => enumerator.MoveNextAsync().AsTask(), lifetime.Token);
                            job.PendingOperation = move;
                            moved = await move
                                .WaitAsync(lifetime.Token).ConfigureAwait(false);
                        }
                        catch (GatewayInferenceWorkerException error)
                        {
                            failureCode = error.Code;
                            cancelWorker = true;
                            break;
                        }
                        catch (GatewayProtocolException error)
                        {
                            failureCode = error.Failure.Code;
                            cancelWorker = true;
                            break;
                        }
                        catch (OperationCanceledException)
                            when (lifetime.IsCancellationRequested)
                        {
                            cancelWorker = true;
                            pendingMove = move;
                            break;
                        }
                        catch (Exception)
                        {
                            failureCode = "worker.failed";
                            cancelWorker = true;
                            break;
                        }

                        if (!moved)
                        {
                            if (terminal is null)
                                failureCode = "stream.truncated";
                            break;
                        }
                        if (terminal is not null)
                        {
                            failureCode = "stream.late";
                            cancelWorker = true;
                            break;
                        }

                        var item = enumerator.Current;
                        job.WithPermission(() => true);
                        try
                        {
                            GatewayInferenceEventValidator.Validate(
                                item,
                                request,
                                expectedSequence);
                            streamState.Accept(item);
                        }
                        catch (GatewayProtocolException error)
                        {
                            failureCode = error.Failure.Code;
                            cancelWorker = true;
                            break;
                        }
                        eventCount = checked(eventCount + 1);
                        if (eventCount > request.Route.MaximumEvents)
                        {
                            failureCode = "stream.limit";
                            cancelWorker = true;
                            break;
                        }
                        expectedSequence++;
                        if (item.IsTerminal)
                        {
                            terminal = item;
                            continue;
                        }

                        var serialized = SerializeInferenceEvent(
                            item,
                            traceId);
                        if (serialized.Length > request.Route.MaximumEventBytes ||
                            responseBytes + serialized.Length + 1 + 2_048 >
                                request.Route.MaximumStreamBytes)
                        {
                            failureCode = "stream.limit";
                            cancelWorker = true;
                            break;
                        }
                        try
                        {
                            await WriteInferenceBytesAsync(
                                context,
                                serialized,
                                lifetime.Token,
                                startResponse: !responseStarted,
                                job: job,
                                validatePublication: streamState.ValidatePublication).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                            when (lifetime.IsCancellationRequested)
                        {
                            cancelWorker = true;
                            break;
                        }
                        responseStarted = true;
                        publishedSequence++;
                        responseBytes = checked(
                            responseBytes + serialized.Length + 1);
                    }
                }
            }

            if (pendingMove is not null)
            {
                _ = await job.CancelAsync().ConfigureAwait(false);
                try
                {
                    var movedAfterCancellation = await pendingMove.WaitAsync(
                        GatewayInferenceProtocol.MaximumRetirementDuration)
                        .ConfigureAwait(false);
                    if (movedAfterCancellation)
                        failureCode ??= "stream.late";
                }
                catch (OperationCanceledException)
                    when (pendingMove.IsCompleted)
                {
                }
                catch (TimeoutException)
                {
                    pendingMoveHealthy = false;
                }
                catch (Exception) when (pendingMove.IsCompleted)
                {
                    failureCode ??= "worker.failed";
                }
            }

            if (failureCode is null && deadline.IsCancellationRequested)
            {
                failureCode = "job.deadline";
                cancelWorker = true;
            }
            else if (failureCode is null && job.IsCancellationRequested)
            {
                failureCode = "job.canceled";
                cancelWorker = true;
            }
            else if (failureCode is null && job.PermissionRevoked.IsCancellationRequested)
            {
                failureCode = "action.denied";
                cancelWorker = true;
            }
            quarantineWorker = failureCode is "worker.identity" or
                "stream.invalid" or "stream.limit" or
                "stream.truncated" or "stream.late";

            if (context.RequestAborted.IsCancellationRequested)
            {
                cancelWorker = true;
                _ = await job.CompleteAsync(
                    enumerator,
                    cancelWorker,
                    pendingMoveHealthy,
                    quarantineWorker)
                    .ConfigureAwait(false);
                completed = true;
                audit.Record(new()
                {
                    TraceId = traceId,
                    Code = failureCode ?? "job.canceled",
                    HttpStatus = failureCode is null
                        ? 499
                        : GatewayFailures.Get(failureCode).HttpStatus
                });
                context.Abort();
                return;
            }

            var healthy = await job.CompleteAsync(
                enumerator,
                cancelWorker,
                pendingMoveHealthy,
                quarantineWorker)
                .ConfigureAwait(false);
            completed = true;
            if (!healthy)
                failureCode = "stream.cleanup";

            if (failureCode is null && terminal is not null)
            {
                if (terminal.Kind == GatewayInferenceEventKind.Failed)
                    failureCode = terminal.ErrorCode;
                else if (terminal.Kind == GatewayInferenceEventKind.Canceled)
                    failureCode = "job.canceled";
            }

            if (failureCode is not null)
            {
                if (!responseStarted)
                    throw new GatewayProtocolException(failureCode);
                var failureEvent = failureCode == "job.canceled"
                    ? GatewayInferenceEvent.Canceled(
                        request,
                        publishedSequence,
                        request.Route.Kind == GatewayInferenceKind.F5Synthesis
                            ? streamState.F5FinalSampleCount
                            : null)
                    : GatewayInferenceEvent.Failure(
                        request,
                        publishedSequence,
                        failureCode);
                RecordStreamFailure(traceId, failureCode);
                var serialized = SerializeInferenceEvent(
                    failureEvent,
                    traceId);
                await WriteInferenceBytesAsync(
                    context,
                    serialized,
                    context.RequestAborted,
                    startResponse: false,
                    job: job,
                    failure: true).ConfigureAwait(false);
                return;
            }

            GatewayRules.Require(terminal is not null, "stream.truncated");
            GatewayRules.Require(terminal!.Sequence == publishedSequence,
                "stream.invalid");
            var terminalBytes = SerializeInferenceEvent(terminal!, traceId);
            GatewayRules.Require(
                terminalBytes.Length <= request.Route.MaximumEventBytes &&
                responseBytes + terminalBytes.Length + 1 <=
                    request.Route.MaximumStreamBytes,
                "stream.limit");
            await WriteInferenceBytesAsync(
                context,
                terminalBytes,
                lifetime.Token,
                startResponse: !responseStarted,
                job: job,
                validatePublication: streamState.ValidatePublication).ConfigureAwait(false);
        }
        catch (GatewayProtocolException error)
        {
            failureCode = error.Failure.Code;
            throw;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || job.IsCancellationRequested)
        {
            failureCode = deadline.IsCancellationRequested ? "job.deadline" :
                job.PermissionRevoked.IsCancellationRequested ? "action.denied" : "job.canceled";
            throw new GatewayProtocolException(failureCode);
        }
        finally
        {
            permissionRevocation.Dispose();
            if (!completed)
                _ = await job.CompleteAsync(
                    enumerator,
                    cancelWorker: true,
                    cleanupHealthy: pendingMoveHealthy,
                    quarantineWorker: quarantineWorker ||
                        IsIntegrityFailure(failureCode))
                    .ConfigureAwait(false);
            if (!await job.ReleaseAsync().ConfigureAwait(false))
            {
                if (failureCode != "stream.cleanup")
                    RecordStreamFailure(traceId, "stream.cleanup");
                if (failureCode is null)
                    context.Abort();
            }
        }
    }

    private static async ValueTask<byte[]> ReadInferenceBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        GatewayRules.Require(
            (request.ContentType is "application/json" or
                "application/json; charset=utf-8") &&
            !request.Headers.ContainsKey("Content-Encoding") &&
            (request.ContentLength is null or >= 1) &&
            (request.ContentLength is null ||
                request.ContentLength <= maximumBytes),
            request.ContentLength > maximumBytes
                ? "request.too_large"
                : "request.invalid");
        var feature = request.HttpContext.Features
            .Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maximumBytes;

        using var output = request.ContentLength is > 0
            ? new MemoryStream(checked((int)request.ContentLength.Value))
            : new MemoryStream();
        var buffer = new byte[8_192];
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(
                    buffer,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                    throw new GatewayProtocolException("request.too_large");
                output.Write(buffer, 0, read);
            }
            GatewayRules.Require(output.Length > 0 &&
                (request.ContentLength is null ||
                    output.Length == request.ContentLength),
                "request.invalid");
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
                CryptographicOperations.ZeroMemory(
                    segment.AsSpan(0, checked((int)output.Length)));
        }
    }

    private byte[] SerializeInferenceEvent(
        GatewayInferenceEvent value,
        Guid traceId)
    {
        GatewayFailure? failure = null;
        if (value.Kind == GatewayInferenceEventKind.Failed)
            failure = GatewayFailures.Get(value.ErrorCode!);
        string? text = null;
        string? data = null;
        string? mediaType = null;
        if (value.Kind == GatewayInferenceEventKind.TextDelta)
            text = new UTF8Encoding(false, true).GetString(value.Payload.Span);
        else if (value.Kind == GatewayInferenceEventKind.AudioFrame)
        {
            data = Convert.ToBase64String(value.Payload.Span);
            mediaType = "audio/L16;rate=24000;channels=1";
        }
        else if (value.Kind is GatewayInferenceEventKind.Observation or GatewayInferenceEventKind.FaceFrame)
        {
            data = Convert.ToBase64String(value.Payload.Span);
            mediaType = "application/json";
        }

        return JsonSerializer.SerializeToUtf8Bytes(new InferenceEventDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            RegistryVersion = GatewayInferenceProtocol.RegistryVersion,
            Type = value.Kind,
            Sequence = value.Sequence,
            RouteId = value.RouteId,
            DestinationId = value.DestinationId,
            WorkerId = value.WorkerId,
            ModelId = value.ModelId,
            ModelRevision = value.ModelRevision,
            ModelSha256 = value.ModelSha256,
            ArtifactIdentitySha256 = value.ArtifactIdentitySha256,
            SessionId = value.SessionId,
            TurnId = value.TurnId,
            RequestId = value.RequestId,
            Epoch = value.Epoch,
            Text = text,
            DataBase64 = data,
            DataMediaType = mediaType,
            FrameSequence = value.FrameSequence,
            ChunkIndex = value.ChunkIndex,
            SampleOffset = value.SampleOffset,
            SampleCount = value.SampleCount,
            FinalSampleCount = value.FinalSampleCount,
            Code = failure?.Code,
            Summary = failure?.Summary,
            Remedy = failure?.Remedy,
            TraceId = traceId
        }, Json);
    }

    private static async ValueTask WriteInferenceBytesAsync(
        HttpContext context,
        byte[] bytes,
        CancellationToken cancellationToken,
        bool startResponse,
        GatewayInferenceRouteRegistry.GatewayInferenceJob job,
        bool failure = false,
        Action? validatePublication = null)
    {
        if (startResponse)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType =
                "application/x-ndjson; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
        }
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        ValueTask Write()
        {
            cancellationToken.ThrowIfCancellationRequested();
            validatePublication?.Invoke();
            return context.Response.Body.WriteAsync(line, cancellationToken);
        }
        await (failure ? job.Principal.WithAuthority(Write) : job.WithPermission(Write, cancellationToken))
            .ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private void RecordStreamFailure(Guid traceId, string code)
    {
        var failure = GatewayFailures.Get(code);
        audit.Record(new()
        {
            TraceId = traceId,
            Code = code,
            HttpStatus = failure.HttpStatus
        });
    }

    private static bool IsIntegrityFailure(string? code) =>
        code is "worker.identity" or "stream.invalid" or "stream.limit" or
            "stream.truncated" or "stream.late";

    private sealed record InferenceEventDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string RegistryVersion { get; init; }
        public required GatewayInferenceEventKind Type { get; init; }
        public required long Sequence { get; init; }
        public required string RouteId { get; init; }
        public required string DestinationId { get; init; }
        public required string WorkerId { get; init; }
        public required string ModelId { get; init; }
        public required string ModelRevision { get; init; }
        public required string ModelSha256 { get; init; }
        public required string ArtifactIdentitySha256 { get; init; }
        public required Guid SessionId { get; init; }
        public required Guid TurnId { get; init; }
        public required Guid RequestId { get; init; }
        public required long Epoch { get; init; }
        public string? Text { get; init; }
        public string? DataBase64 { get; init; }
        public string? DataMediaType { get; init; }
        public long? FrameSequence { get; init; }
        public int? ChunkIndex { get; init; }
        public long? SampleOffset { get; init; }
        public int? SampleCount { get; init; }
        public long? FinalSampleCount { get; init; }
        public string? Code { get; init; }
        public string? Summary { get; init; }
        public string? Remedy { get; init; }
        public required Guid TraceId { get; init; }
    }
}

using System.Runtime.CompilerServices;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Martlet.Avatars;
using NvidiaAce.A2F.V1;
using NvidiaAce.Audio.V1;
using NvidiaAce.Services.A2FController.V1;
using Input = NvidiaAce.Controller.V1.AudioStream;
using InputHeader = NvidiaAce.Controller.V1.AudioStreamHeader;
using Output = NvidiaAce.Controller.V1.AnimationDataStream;

namespace Martlet.Avatar.Audio2Face;

public sealed class Audio2FaceAdapter
{
    public const string SourceId = "nvidia-audio2face";
    private readonly Audio2FaceOptions options;

    public Audio2FaceAdapter(Audio2FaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    public Audio2FacePrerequisiteReport InspectPrerequisites() => new(true, false,
    [
        "No connection or inference was attempted. Runtime health and output quality are unverified.",
        "An independently provisioned Audio2Face-3D NIM v2 service must expose the v1.2 bidirectional controller protocol on the configured loopback port.",
        "The service owner must separately verify NVIDIA GPU, driver, container/runtime and model requirements for the installed NIM release.",
        "NIM product and model license terms require separate user review; the native SDK's MIT license does not grant those rights.",
        "Only synthesized mono signed-16 little-endian PCM at 16000, 24000, 44100 or 48000 Hz is supported.",
        "This adapter generates face coefficients only; it does not generate body gestures, play audio, or select a renderer."
    ]);

    public async IAsyncEnumerable<AvatarFrame> AnimateAsync(GeneratedSpeechClip clip,
        Audio2FaceAuthorization authorization, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();
        authorization.Consume(clip, options);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, authorization.Revocation);
        var remaining = authorization.Remaining;
        if (remaining <= TimeSpan.Zero) throw new Audio2FaceException(Audio2FaceFailure.AuthorizationExpired);
        var timeout = remaining < options.RequestTimeout ? remaining : options.RequestTimeout;
        lifetime.CancelAfter(timeout);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = options.IdleTimeout
        };
        using var channel = GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 1024 * 1024,
            MaxSendMessageSize = 32 * 1024,
            ThrowOperationCanceledOnCancellation = true
        });
        var client = new A2FControllerService.A2FControllerServiceClient(channel);
        using var call = client.ProcessAudioStream(deadline: DateTime.UtcNow + timeout, cancellationToken: lifetime.Token);
        var writer = SendAsync(call.RequestStream, clip, lifetime);
        var decoder = new AnimationDecoder(clip, options);
        try
        {
            while (await ReadAsync(call.ResponseStream, writer, lifetime.Token,
                cancellationToken, authorization.Revocation).ConfigureAwait(false))
            {
                foreach (var frame in decoder.Decode(call.ResponseStream.Current))
                {
                    CheckCancellation(lifetime.Token, cancellationToken, authorization.Revocation);
                    yield return frame;
                }
            }
            var sendFailure = await writer.ConfigureAwait(false);
            CheckCancellation(lifetime.Token, cancellationToken, authorization.Revocation);
            if (sendFailure is { } failure) throw new Audio2FaceException(failure);
            decoder.Complete();
        }
        finally
        {
            lifetime.Cancel();
            call.Dispose();
            // Disposal cancels pending HTTP/2 writes; do not leave an unobserved producer behind.
            await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private async Task<Audio2FaceFailure?> SendAsync(IClientStreamWriter<Input> stream,
        GeneratedSpeechClip clip, CancellationTokenSource lifetime)
    {
        try
        {
            await WriteAsync(new Input
            {
                AudioStreamHeader = new InputHeader
                {
                    AudioHeader = new AudioHeader
                    {
                        AudioFormat = AudioHeader.Types.AudioFormat.Pcm,
                        ChannelCount = 1, SamplesPerSecond = (uint)clip.SampleRate, BitsPerSample = 16
                    },
                    BlendshapeParams = new BlendShapeParameters { EnableClampingBsWeight = true }
                }
            }).ConfigureAwait(false);
            foreach (var frame in clip.Frames)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                await WriteAsync(new Input
                {
                    AudioWithEmotion = new AudioWithEmotion { AudioBuffer = ByteString.CopyFrom(frame.Data.Span) }
                }).ConfigureAwait(false);
            }
            await WriteAsync(new Input { EndOfAudio = new Input.Types.EndOfAudio() }).ConfigureAwait(false);
            await stream.CompleteAsync().WaitAsync(options.IdleTimeout, lifetime.Token).ConfigureAwait(false);
            return null;
        }
        catch (RpcException exception)
        {
            lifetime.Cancel();
            return exception.StatusCode == StatusCode.DeadlineExceeded
                ? Audio2FaceFailure.DeadlineExceeded : Audio2FaceFailure.TransportFailure;
        }
        catch (OperationCanceledException)
        {
            lifetime.Cancel();
            return Audio2FaceFailure.DeadlineExceeded;
        }
        catch (TimeoutException)
        {
            lifetime.Cancel();
            return Audio2FaceFailure.DeadlineExceeded;
        }

        Task WriteAsync(Input message) => stream.WriteAsync(message, lifetime.Token)
            .WaitAsync(options.IdleTimeout, lifetime.Token);
    }

    private async Task<bool> ReadAsync(IAsyncStreamReader<Output> stream, Task<Audio2FaceFailure?> writer,
        CancellationToken lifetime, CancellationToken caller, CancellationToken revoked)
    {
        try
        {
            return await stream.MoveNext(lifetime).WaitAsync(options.IdleTimeout, lifetime).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            caller.ThrowIfCancellationRequested();
            revoked.ThrowIfCancellationRequested();
            throw new Audio2FaceException(exception.StatusCode == StatusCode.DeadlineExceeded
                ? Audio2FaceFailure.DeadlineExceeded : Audio2FaceFailure.TransportFailure);
        }
        catch (OperationCanceledException)
        {
            caller.ThrowIfCancellationRequested();
            revoked.ThrowIfCancellationRequested();
            var failure = writer.IsCompletedSuccessfully ? writer.Result : null;
            throw new Audio2FaceException(failure ?? Audio2FaceFailure.DeadlineExceeded);
        }
        catch (TimeoutException)
        {
            throw new Audio2FaceException(Audio2FaceFailure.DeadlineExceeded);
        }
    }

    private static void CheckCancellation(CancellationToken lifetime, CancellationToken caller, CancellationToken revoked)
    {
        caller.ThrowIfCancellationRequested();
        revoked.ThrowIfCancellationRequested();
        if (lifetime.IsCancellationRequested)
            throw new Audio2FaceException(Audio2FaceFailure.DeadlineExceeded);
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;
using Martlet.Gateway;
using Martlet.Gateway.F5;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// voice_engine_check: speaks one sentence with a real self-hosted voice engine (a host role's loopback service, for example
/// the chatterbox role's martlet_chatterbox_host.py on 127.0.0.1:50083) through the production path: the engine's own relay
/// (the one the Linux host's HostApplication.RoleWorker creates) inside a real gateway on 127.0.0.1 (Kestrel, pinned TLS,
/// pairing) and the desktop's paired client, with a starter voice as the reference. Nothing is played or recorded. Reports the
/// service's own /status (state, error, runtime versions) before and after, and what came back.
/// </summary>
internal static class VoiceEngineCheck
{
    private const int SampleRate = 24_000;

    internal static async Task<(bool Ok, object Report)> RunAsync(string engineKey, string endpointText, string? text,
        string? styleDirectory, CancellationToken token)
    {
        var engine = SpeechEngines.ForKey(engineKey)
            ?? throw new ArgumentException($"engine must be one of {string.Join(", ", SpeechEngines.All.Select(e => e.Key))}.");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50083/.");
        var sentence = string.IsNullOrWhiteSpace(text) ? DefaultText(engine) : text.Trim();
        var voice = F5BundledVoices.All.FirstOrDefault(v => SpeechEngines.ReferenceProblem(engine, v.Check().DurationMilliseconds) is null)
            ?? F5BundledVoices.Default;
        // What the desktop sends Chatterbox Original: the style saved in its data directory, else Resemble's suggestions.
        var style = engine == SpeechEngines.ChatterboxOriginal
            ? styleDirectory is { Length: > 0 } ? ChatterboxStyle.Load(styleDirectory) : ChatterboxStyle.Default : null;

        var before = await StatusAsync(endpoint, token);
        var watch = Stopwatch.StartNew();
        double? firstAudioMs = null;
        long samples = 0, sumSquares = 0;
        var peak = 0;
        var pcm = new List<short>();
        string? failure = null, problem = null;
        try
        {
            await using var host = await LiveHost.StartAsync(Relay(engine, endpoint));
            using var connection = await host.PairAsync("voice-check-desktop", token);
            var route = (await connection.ReadRoutesAsync(token)).Single(r => r.RouteId == engine.RouteId);
            var audio = voice.ReadAudio();
            var reference = new HostSpeechReference(Guid.NewGuid(), SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript),
                voice.AudioSha256, voice.Transcript,
                Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(voice.Transcript))), audio);
            watch.Restart();
            await foreach (var frame in connection.StreamSpeechAsync(route,
                new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 1,
                DateTimeOffset.UtcNow.AddMinutes(4), reference, sentence, token, style))
            {
                firstAudioMs ??= watch.Elapsed.TotalMilliseconds;
                for (var i = 0; i + 1 < frame.Length; i += 2)
                {
                    int sample = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i));
                    peak = Math.Max(peak, Math.Abs(sample));
                    sumSquares += (long)sample * sample;
                    samples++;
                    pcm.Add((short)sample);
                }
            }
        }
        catch (Audio2FaceHostException error)
        {
            failure = error.Code;
            problem = error.Message;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or ContractException)
        {
            failure = error.GetType().Name;
            problem = error.Message;
        }
        var elapsedMs = watch.Elapsed.TotalMilliseconds;
        var after = await StatusAsync(endpoint, token);

        var seconds = (double)samples / SampleRate;
        double? Level(double value) => samples == 0 ? null : value <= 0 ? -120 : Math.Round(20 * Math.Log10(value / short.MaxValue), 1);
        var rms = samples == 0 ? 0 : Math.Sqrt((double)sumSquares / samples);
        var audible = samples > 0 && rms / short.MaxValue > 0.001;
        var ok = failure is null && seconds >= 0.5 && audible;
        return (ok, new
        {
            ok,
            engine = engine.Key,
            endpoint = endpoint.ToString(),
            route = engine.RouteId,
            voice = voice.Key,
            text = sentence,
            // Chatterbox Original's General and Expressive exaggeration and CFG weight sent with the sentence.
            style = style?.Describe(),
            statusBefore = before,
            seconds = Math.Round(seconds, 2),
            sampleRate = SampleRate,
            firstAudioMs = firstAudioMs is { } first ? Math.Round(first) : (double?)null,
            elapsedMs = Math.Round(elapsedMs),
            realTimeFactor = seconds > 0 ? Math.Round(elapsedMs / 1000 / seconds, 2) : (double?)null,
            peakDbfs = Level(peak),
            rmsDbfs = Level(rms),
            audible,
            // Near 0 for a whisper (Chatterbox's [whispering] sentences), about 0.6-0.9 for ordinary speech.
            voicedShare = Voicing.VoicedShare(CollectionsMarshal.AsSpan(pcm), SampleRate),
            failure,
            problem,
            statusAfter = after
        });
    }

    private static string DefaultText(SpeechEngine engine) =>
        engine.Tags.FirstOrDefault(tag => tag.Kind == VoiceTagKind.Sound) is { } sound
            ? $"That's hilarious {sound.Text} okay, so where were we?"
            : engine.Tags.FirstOrDefault(tag => tag.Cue == "expressive") is { } expressive
            ? $"Hello! This is a quick check of my voice. {expressive.Text} And this sentence is said with feeling!"
            : "Hello! This is a quick check of my voice.";

    // The same relay per role kind as Martlet.Gateway.Host.Linux's HostApplication.RoleWorker, with each engine's default model.
    private static F5RelayWorker Relay(SpeechEngine engine, Uri endpoint) => engine.HostRoleKind switch
    {
        "f5" => new F5RelayWorker(endpoint, F5RelayWorker.DefaultModel),
        "xtts" => Martlet.Gateway.Xtts.XttsRelay.Create(endpoint, Martlet.Gateway.Xtts.XttsRelay.DefaultModel),
        "chatterbox" or "chatterbox-original" or "chatterbox-nano" => ChatterboxRelay.Create(endpoint, engine.DefaultModel),
        "gpt-sovits" => Martlet.Gateway.GptSovits.GptSovitsRelay.Create(endpoint, Martlet.Gateway.GptSovits.GptSovitsRelay.DefaultModel),
        "dia" => Martlet.Gateway.Dia.DiaRelay.Create(endpoint, Martlet.Gateway.Dia.DiaRelay.DefaultModel),
        _ => throw new ArgumentException($"No relay for the {engine.HostRoleKind} role.")
    };

    /// <summary>The service's own GET /status: its state, error and runtime (package and CUDA versions; nothing secret), or
    /// why it could not be read.</summary>
    private static async Task<object> StatusAsync(Uri endpoint, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri(endpoint, "status"), token);
            var body = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() is { Length: > 600 } long_ ? long_[..600] + "..." : value.GetString()
                : null;
            object? runtime = root.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.Object &&
                worker.TryGetProperty("runtime", out var r) && r.ValueKind == JsonValueKind.Object ? r.Clone() : null;
            // Chatterbox's idle check (checks, every_seconds, fastest_ms, last_ms): how long running the model briefly took
            // while nobody spoke; a slow one means the card was busy or Windows had moved the model out of graphics memory.
            object? idleCheck = root.TryGetProperty("idle_check", out var idle) && idle.ValueKind == JsonValueKind.Object ? idle.Clone() : null;
            // Chatterbox's whisper (level_db, parts): how many sentences it has whispered since it started.
            object? whisper = root.TryGetProperty("whisper", out var whispered) && whispered.ValueKind == JsonValueKind.Object
                ? whispered.Clone() : null;
            // Chatterbox Original's default style and how many sentences it has said expressively.
            object? style = root.TryGetProperty("style", out var styled) && styled.ValueKind == JsonValueKind.Object ? styled.Clone() : null;
            // Chatterbox on the CPU (threads, pinned_cpus): PyTorch's threads and the performance cores' CPUs it is pinned to.
            object? cpu = root.TryGetProperty("cpu", out var cpus) && cpus.ValueKind == JsonValueKind.Object ? cpus.Clone() : null;
            int? decoderSteps = root.TryGetProperty("decoder_steps", out var steps) && steps.ValueKind == JsonValueKind.Number
                ? steps.GetInt32() : null;
            return new
            {
                answered = true,
                httpStatus = (int)response.StatusCode,
                state = Text("state"),
                ready = root.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True,
                error = Text("error"),
                // The service's model and the device it runs on (cuda:0 or cpu).
                model = Text("model"),
                device = Text("device"),
                // Turbo's and Nano's decoder steps a whole piece takes (1 on the CPU, 2 on a GPU).
                decoderSteps,
                cpu,
                runtime,
                idleCheck,
                whisper,
                style
            };
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new { answered = false, problem = $"{error.GetType().Name}: {error.Message}" };
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with one role's relay route over its live loopback service.</summary>
    internal sealed class LiveHost : IAsyncDisposable, IGatewayAuditSink
    {
        private const string HostId = "voice-check-host";
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private IGatewayInferenceWorker worker = null!;
        private GatewayServer server = null!;
        private GatewayHostIdentity identity = null!;
        private string origin = "";

        internal static async Task<LiveHost> StartAsync(IGatewayInferenceWorker worker)
        {
            var host = new LiveHost { worker = worker, certificate = Certificate() };
            try
            {
                host.identity = GatewayHostIdentity.FromCertificate(HostId, host.certificate);
                host.origin = $"https://127.0.0.1:{FreePort()}";
                var gatewayOrigin = new GatewayOrigin(host.origin);
                host.server = new GatewayServer(host.identity, gatewayOrigin, [], host, inferenceWorkers: [worker]);
                host.listener = await host.server.StartAsync(new GatewayTlsBinding(gatewayOrigin, host.identity, host.certificate),
                    new KestrelGatewayListenerFactory());
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        internal async Task<Audio2FaceHostConnection> PairAsync(string deviceId, CancellationToken token)
        {
            var card = server.Pairing.OpenWindow(new() { DeviceId = deviceId, DisplayName = deviceId, Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairAsync(origin, HostId, identity.SpkiFingerprint, deviceId,
                card.PairingId, card.Token.Reveal(), token);
            return new Audio2FaceHostConnection(pairing, secret);
        }

        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            if (worker is IAsyncDisposable disposable) await disposable.DisposeAsync();
            certificate?.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet voice engine check (loopback)", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;
using Martlet.F5;
using Martlet.Gateway;
using Martlet.Gateway.F5;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses the shared speaking voices end to end on this PC with the production code: two real gateways (Kestrel, pinned
/// TLS) on 127.0.0.1, each with the real reference-voice relay route in front of a fixture voice service (canned PCM, NOT AI)
/// and an in-memory copy of speaking-voices.json and the recordings, and two simulated desktops that keep a real F5 voice
/// store in a temporary folder and use the desktop's paired client and Martlet.F5's reconcile engine. Nothing leaves
/// loopback; the temporary folder is deleted afterwards and nothing touches the credential vault.
/// </summary>
internal static class VoiceRehearsal
{
    private const string FixtureModel = "fixture-model-weights";
    private static readonly string FixtureRevision = new('2', 40);
    private static readonly string FixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("model_weights-fixture-2")));

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "martlet-voice-rehearsal-" + Guid.NewGuid().ToString("N"));
        await using var h1 = await VoiceHost.StartAsync("lab-voice-1");
        await using var h2 = await VoiceHost.StartAsync("lab-voice-2");
        var a = new LabDesktop("lab-desktop-a", Path.Combine(root, "a"));
        var b = new LabDesktop("lab-desktop-b", Path.Combine(root, "b"));
        var own = Wave(2.0, 0.031);
        var ownSha = Convert.ToHexStringLower(SHA256.HashData(own));
        const string ownWords = "This is my own voice, recorded for Martlet.";
        var ownId = SpeakingVoiceLibrary.ReferenceId(ownSha, ownWords);
        var starter = F5BundledVoices.Default;
        var starterId = SpeakingVoiceLibrary.ReferenceId(starter.AudioSha256, starter.Transcript);
        SpeakingVoiceLibrary? beforeRemoval = null;

        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add((name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add((name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        try
        {
            await Run("Desktops A and B pair with lab-voice-1 and lab-voice-2 (signed, pinned connections)", async () =>
            {
                foreach (var desktop in new[] { a, b })
                foreach (var host in new[] { h1, h2 })
                    await desktop.PairAsync(host);
                return (true, "each desktop paired with both hosts");
            });
            await Run($"A new voice list starts with the {F5BundledVoices.All.Count} starter voices; A adds its own recording and its F5 store holds every recording", async () =>
            {
                var path = Path.Combine(root, "own.wav");
                Directory.CreateDirectory(root);
                await File.WriteAllBytesAsync(path, own, token);
                using (var store = F5ReferencePresetStore.Open(a.StoreDirectory))
                    await store.SnapshotAsync(new()
                    {
                        PresetName = "My voice", AbsoluteSourcePath = path, Transcript = ownWords,
                        Rights = new()
                        {
                            AcknowledgementId = Guid.NewGuid(), Basis = F5VoiceRightsBasis.OwnVoice, StatementVersion = F5ReferenceLimits.RightsStatementVersion,
                            ProcessingDestinationId = F5RelayWorker.DefaultDestinationId, AcknowledgedAtUtc = DateTimeOffset.UtcNow, Confirmed = true
                        }
                    }, token);
                a.Library = SpeakingVoiceLibrary.Empty.Seed(F5SharedVoices.Starters);
                var result = await a.ReconcileAsync(null, token);
                var live = a.Library.Live;
                return (live.Count == F5BundledVoices.All.Count + 1 && result.Local.Count == live.Count && result.Added == F5BundledVoices.All.Count &&
                        live.Single(v => v.Id == ownId).Rights == SpeakingVoiceRights.OwnVoice &&
                        live.Where(v => v.Id != ownId).All(v => v.Revision == SpeakingVoiceLibrary.StarterRevision),
                    $"list: {live.Count} voices ({F5BundledVoices.All.Count} starters at revision 1, the own recording joined it); store: {result.Local.Count} recordings, {result.Added} copied in");
            });
            await Run("A shares the list and every recording with lab-voice-1", async () =>
            {
                var present = await a.ShareAsync(h1, token);
                return (present.Count == a.Library.Live.Count && present.Contains(ownSha) && h1.SavedAudio.Count == present.Count && h1.SavedLibrary is not null,
                    $"lab-voice-1 holds {present.Count} recordings; saved: speaking-voices.json and {h1.SavedAudio.Count} recordings");
            });
            await Run("Speaking on lab-voice-1 names the recording by its SHA-256 only, and the host's engine gets the exact recording", async () =>
            {
                var (ok, sent, frames) = await a.SpeakAsync(h1, ownId, token);
                var received = h1.Voice.Received.LastOrDefault();
                return (ok && !sent && received == ownSha && frames > 0,
                    $"reply streamed: {ok} ({frames} samples); recording sent with the request: {sent} ({own.Length:N0} bytes not sent); engine got {Short(received)}");
            });
            await Run("lab-voice-2 has the list but not the recording: the first reply sends it once (reference.missing), the host keeps it, the next names it", async () =>
            {
                await a.MergeAsync(h2, token);
                var first = await a.SpeakAsync(h2, ownId, token);
                var kept = h2.Server is not null && (await a.ReadAsync(h2, token)).Present.Contains(ownSha);
                var second = await a.SpeakAsync(h2, ownId, token);
                return (first.Ok && first.Sent && kept && second.Ok && !second.Sent && h2.Voice.Received.Count(r => r == ownSha) == 2,
                    $"first reply sent the recording: {first.Sent}; host kept it: {kept}; second reply sent it: {second.Sent}");
            });
            await Run("Desktop B, new and empty, takes the list from lab-voice-1: starter recordings come from Martlet, the own one from the host", async () =>
            {
                b.Library = SpeakingVoiceLibrary.Empty.Seed(F5SharedVoices.Starters);
                var copy = await b.ReadAsync(h1, token);
                b.Library = SpeakingVoiceLibrary.Merge(b.Library, copy.Library);
                var fetched = 0;
                var result = await b.ReconcileAsync(async (sha256, t) =>
                {
                    fetched++;
                    return await b.FetchAsync(h1, sha256, t);
                }, token);
                return (result.Local.Count == a.Library.Live.Count && result.Local.ContainsKey(ownId) && fetched == 1 && result.Waiting == 0,
                    $"B now holds {result.Local.Count} recordings; downloaded from lab-voice-1: {fetched} (the own recording); waiting: {result.Waiting}");
            });
            await Run("B chooses the own voice; A follows the choice through the host", async () =>
            {
                b.Library = b.Library.Choose(ownId, b.DeviceId, DateTimeOffset.UtcNow);
                await b.MergeAsync(h1, token);
                a.Library = SpeakingVoiceLibrary.Merge(a.Library, (await a.ReadAsync(h1, token)).Library);
                return (a.Library.ChosenVoice?.Id == ownId && a.Library.Chosen!.UpdatedBy == b.DeviceId,
                    $"A's chosen voice: {(a.Library.ChosenVoice?.Id == ownId ? "the own voice" : "another")}, chosen on {a.Library.Chosen?.UpdatedBy}");
            });
            await Run("B removes a starter voice: both hosts delete its recording and A deletes its copy", async () =>
            {
                beforeRemoval = a.Library;
                b.Library = b.Library.Remove(starterId, b.DeviceId, DateTimeOffset.UtcNow);
                var on1 = await b.MergeAsync(h1, token);
                var on2 = await b.MergeAsync(h2, token);
                a.Library = SpeakingVoiceLibrary.Merge(a.Library, (await a.ReadAsync(h1, token)).Library);
                var result = await a.ReconcileAsync(null, token);
                return (!on1.Present.Contains(starter.AudioSha256) && !h1.SavedAudio.ContainsKey(starter.AudioSha256) && !on2.Present.Contains(starter.AudioSha256) &&
                        result.Removed == 1 && !result.Local.ContainsKey(starterId),
                    $"lab-voice-1 holds {on1.Present.Count} recordings, lab-voice-2 {on2.Present.Count}; A deleted {result.Removed} copy");
            });
            await Run("An older copy (from before the removal) can't bring the voice back", async () =>
            {
                var merged = await a.MergeAsync(h1, token, beforeRemoval!);
                return (merged.Library.Find(starterId)?.Removed == true && !merged.Present.Contains(starter.AudioSha256),
                    $"after merging the stale copy the voice is {(merged.Library.Find(starterId)?.Removed == true ? "still removed" : "back")}");
            });
            await Run("Speaking with a removed voice falls back to sending the recording, which the host does not keep", async () =>
            {
                var reply = await a.SpeakAsync(h1, starterId, token, starter.ReadAudio(), starter.Transcript);
                var present = (await a.ReadAsync(h1, token)).Present;
                return (reply.Ok && reply.Sent && !present.Contains(starter.AudioSha256),
                    $"reply streamed: {reply.Ok}; recording sent: {reply.Sent}; host kept it: {present.Contains(starter.AudioSha256)}");
            });
            await Run("lab-voice-1 restarts from its saved copy and still speaks the own voice by SHA-256 alone", async () =>
            {
                var before = (await a.ReadAsync(h1, token)).Present.Count;
                await h1.RestartAsync();
                var after = await a.ReadAsync(h1, token);
                var reply = await a.SpeakAsync(h1, ownId, token);
                return (after.Present.Count == before && after.Library.ChosenVoice?.Id == ownId && reply.Ok && !reply.Sent,
                    $"recordings before {before}, after restart {after.Present.Count}; reply sent the recording: {reply.Sent}");
            });
            await Run("Recordings are checked: a wrong SHA-256, a recording no voice has and a voice whose recording isn't a WAV are refused", async () =>
            {
                var other = Wave(1.5, 0.07);
                var otherSha = Convert.ToHexStringLower(SHA256.HashData(other));
                var wrong = await Code(() => a.SendAsync(h1, ownSha, other, token));
                var unknown = await Code(() => a.SendAsync(h1, otherSha, other, token));
                var text = Encoding.ASCII.GetBytes("not a recording");
                var textSha = Convert.ToHexStringLower(SHA256.HashData(text));
                const string words = "Words that were never recorded.";
                var bogus = a.Library.Add("Not a recording", words, textSha, 2_000, SpeakingVoiceRights.OwnVoice, a.DeviceId, DateTimeOffset.UtcNow);
                await a.MergeAsync(h1, token, bogus);
                var notWave = await Code(() => a.SendAsync(h1, textSha, text, token));
                await a.MergeAsync(h1, token, bogus.Remove(SpeakingVoiceLibrary.ReferenceId(textSha, words), a.DeviceId, DateTimeOffset.UtcNow));
                return (wrong == "request.invalid" && unknown == "request.invalid" && notWave == "request.invalid",
                    $"wrong SHA-256: {wrong}; not in the list: {unknown}; listed but not a WAV: {notWave}");
            });
            await Run("Reading a recording the host doesn't hold answers reference.missing (none)", async () =>
            {
                var missing = await a.FetchAsync(h1, new string('0', 64), token);
                return (missing is null, missing is null ? "no recording" : "unexpected bytes");
            });
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, the real reference-voice relay route over a fixture voice " +
                "service, NOT AI) with in-memory speaking-voices.json and recordings, and two simulated desktops using the desktop's " +
                "paired client and Martlet.F5's reconcile engine over real F5 voice stores in a temporary folder. Not covered: the " +
                "desktop window and its 30-second sync, the Linux host's files, a real voice engine, an older host and a real LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private static string Short(string? sha256) => sha256 is null ? "nothing" : sha256[..12] + "...";

    private static async Task<string?> Code(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Audio2FaceHostException error) { return error.Code; }
    }

    /// <summary>A quiet 24 kHz mono PCM16 tone of <paramref name="seconds"/>: a recording every voice engine accepts.</summary>
    internal static byte[] Wave(double seconds, double step)
    {
        const int rate = 24_000;
        var samples = (int)(rate * seconds);
        var wav = new byte[44 + samples * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)(samples * 2));
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), (short)(Math.Sin(i * step) * 3000));
        return wav;
    }

    /// <summary>A simulated desktop: its pairings (secrets in memory), its shared list and its F5 voice store, with the calls
    /// the real desktop makes.</summary>
    private sealed class LabDesktop(string deviceId, string directory)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        internal string DeviceId => deviceId;
        internal string StoreDirectory => Path.Combine(directory, "f5-voices");
        internal SpeakingVoiceLibrary Library { get; set; } = SpeakingVoiceLibrary.Empty;
        private IReadOnlyDictionary<string, F5ReferenceSnapshot> local = new Dictionary<string, F5ReferenceSnapshot>();

        internal async Task PairAsync(VoiceHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = deviceId, DisplayName = deviceId.ToUpperInvariant(), Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, deviceId,
                card.PairingId, card.Token.Reveal());
        }

        internal async Task<F5SharedVoicesResult> ReconcileAsync(Func<string, CancellationToken, Task<byte[]?>>? fetch, CancellationToken token)
        {
            var result = await F5SharedVoices.ReconcileAsync(StoreDirectory, Path.Combine(directory, "incoming"), F5RelayWorker.DefaultDestinationId,
                Library, fetch ?? ((_, _) => Task.FromResult<byte[]?>(null)), deviceId, DateTimeOffset.UtcNow, null, token);
            Library = result.Library;
            local = result.Local;
            return result;
        }

        internal async Task<HostSpeakingVoices> ReadAsync(VoiceHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadSpeakingVoicesAsync(token);
        }

        internal async Task<HostSpeakingVoices> MergeAsync(VoiceHost host, CancellationToken token, SpeakingVoiceLibrary? library = null)
        {
            using var connection = Connect(host);
            var merged = await connection.MergeSpeakingVoicesAsync(library ?? Library, token);
            if (library is null) Library = SpeakingVoiceLibrary.Merge(Library, merged.Library);
            return merged;
        }

        internal async Task<byte[]?> FetchAsync(VoiceHost host, string sha256, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadSpeakingVoiceAudioAsync(sha256, token);
        }

        internal async Task SendAsync(VoiceHost host, string sha256, byte[] audio, CancellationToken token)
        {
            using var connection = Connect(host);
            await connection.SendSpeakingVoiceAudioAsync(sha256, audio, token);
        }

        /// <summary>What the real sync does for one host: merge the list, then send every recording it lacks.</summary>
        internal async Task<IReadOnlySet<string>> ShareAsync(VoiceHost host, CancellationToken token)
        {
            var merged = await MergeAsync(host, token);
            var present = new HashSet<string>(merged.Present, StringComparer.Ordinal);
            foreach (var voice in Library.Live.Where(v => !present.Contains(v.AudioSha256!) && local.ContainsKey(v.Id)))
            {
                var audio = await F5SharedVoices.ReadAsync(StoreDirectory, local[voice.Id], token);
                using var connection = Connect(host);
                present = [.. await connection.SendSpeakingVoiceAudioAsync(voice.AudioSha256!, audio!, token)];
            }
            return present;
        }

        /// <summary>Speaks one sentence with a voice through the host's voice route, as HostSpeechClient does.</summary>
        internal async Task<(bool Ok, bool Sent, int Samples)> SpeakAsync(VoiceHost host, string voiceId, CancellationToken token,
            byte[]? audio = null, string? transcript = null)
        {
            HostSpeechReference reference;
            if (audio is not null)
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(audio));
                var transcriptRevision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transcript!)));
                reference = new(Guid.NewGuid(), voiceId, sha, transcript!, transcriptRevision, audio);
            }
            else
            {
                var snapshot = local[voiceId];
                using var store = F5ReferencePresetStore.Open(StoreDirectory);
                using var lease = await store.AcquireForPreviewAsync(snapshot.PresetId, snapshot.ReferenceRevision, token);
                var r = lease.Reference;
                reference = new(r.PresetId, r.ReferenceRevision, r.AudioSha256, r.Transcript, r.TranscriptRevision, r.Audio.ToArray());
            }
            using var connection = Connect(host);
            var route = (await connection.ReadRoutesAsync(token)).Single(r => r.RouteId == HostRoute.F5RouteId);
            var samples = 0;
            await foreach (var frame in connection.StreamSpeechAsync(route,
                new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 1,
                DateTimeOffset.UtcNow.AddSeconds(30), reference, "Hello from the rehearsal.", token))
                samples += frame.Length / 2;
            return (samples > 0, connection.LastSpeechSentRecording, samples);
        }

        // A restarted lab host has a new port and forgets volatile pairings; the desktop pairs again, as it would after a reset.
        private Audio2FaceHostConnection Connect(VoiceHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            if (pairing.Origin != host.Origin)
            {
                PairAsync(host).GetAwaiter().GetResult();
                (pairing, secret) = pairings[host.HostId];
            }
            return new Audio2FaceHostConnection(pairing, secret);
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with the reference-voice relay route over a fixture voice service and an
    /// in-memory copy of speaking-voices.json and the recordings that survives a restart.</summary>
    private sealed class VoiceHost : IAsyncDisposable, IGatewaySpeakingVoiceStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private F5RelayWorker? worker;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal FixtureVoice Voice { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal byte[]? SavedLibrary { get; private set; }
        internal Dictionary<string, byte[]> SavedAudio { get; } = new(StringComparer.Ordinal);

        internal static async Task<VoiceHost> StartAsync(string hostId)
        {
            var host = new VoiceHost { HostId = hostId, certificate = Certificate() };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                await host.ListenAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        private async Task ListenAsync()
        {
            Origin = $"https://127.0.0.1:{FreePort()}";
            var origin = new GatewayOrigin(Origin);
            // The relay owns (and disposes) its handler; the requests the fixture saw outlive a restart.
            Voice = new FixtureVoice(Voice?.Received ?? []);
            worker = new F5RelayWorker(new Uri("http://127.0.0.1:50080/"), FixtureModel, FixtureRevision, FixtureSha256, handler: Voice);
            Server = new GatewayServer(Identity, origin, [], this, inferenceWorkers: [worker]);
            Server.AttachSpeakingVoiceStorage(this);
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        internal async Task RestartAsync()
        {
            await StopAsync();
            await ListenAsync();
        }

        public byte[]? LoadLibrary() => SavedLibrary;
        public void SaveLibrary(byte[] bytes) => SavedLibrary = bytes;
        public byte[]? LoadAudio(string sha256) => SavedAudio.GetValueOrDefault(sha256);
        public void SaveAudio(string sha256, byte[] bytes) => SavedAudio[sha256] = (byte[])bytes.Clone();
        public void RemoveAudio(string sha256) => SavedAudio.Remove(sha256);
        public void Record(GatewayAuditEvent gatewayEvent) { }

        private async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            if (worker is not null) await worker.DisposeAsync();
            listener = null;
            worker = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
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
            var request = new CertificateRequest("CN=Martlet voice rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

    /// <summary>Stands in for a voice role's loopback service (FIXTURE, NOT AI): checks the recording it is handed against its
    /// SHA-256, notes it, and streams 0.1 s of canned PCM in the worker's event shape.</summary>
    internal sealed class FixtureVoice(List<string> received) : HttpMessageHandler
    {
        internal List<string> Received => received;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/cancel") return new HttpResponseMessage(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var reference = root.GetProperty("reference");
            var audio = Convert.FromBase64String(reference.GetProperty("audio_base64").GetString()!);
            var sha256 = reference.GetProperty("audio_sha256").GetString();
            if (Convert.ToHexStringLower(SHA256.HashData(audio)) != sha256)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            lock (Received) Received.Add(sha256!);
            var pcm = new byte[2_400 * 2];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i * 7);
            string Event(string kind, long sequence, object? frame = null, int? chunk = null, long? final = null) =>
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["cancellation"] = null, ["chunk_index"] = chunk, ["contract_id"] = "martlet.f5.worker", ["error"] = null,
                    ["final_sample_count"] = final, ["frame"] = frame,
                    ["ids"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(root.GetProperty("ids").GetRawText()),
                    ["kind"] = kind, ["protocol_version"] = new { major = 1, minor = 0 },
                    ["reference_revision"] = reference.GetProperty("reference_revision").GetString(),
                    ["sequence"] = sequence, ["type"] = "event",
                    ["worker"] = new
                    {
                        artifacts = new[] { new { artifact_id = FixtureModel, revision = FixtureRevision, role = "model_weights", sha256 = FixtureSha256 } }
                    }
                });
            var lines = string.Join("\n",
                Event("started", 0),
                Event("audio_frame", 1, new { chunk_index = 0, data_base64 = Convert.ToBase64String(pcm), sample_count = pcm.Length / 2, sample_offset = 0, sequence = 0 }),
                Event("chunk_completed", 2, chunk: 0, final: pcm.Length / 2),
                Event("completed", 3, final: pcm.Length / 2)) + "\n";
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(lines));
            content.Headers.ContentType = new("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}

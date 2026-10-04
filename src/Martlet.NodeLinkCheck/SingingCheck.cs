using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Singing;
using Martlet.Core.Voices;
using Martlet.F5;
using Martlet.Gateway.Singing;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// singing_check: makes one song through the production path: the singing role's relay
/// (<see cref="SongRelayWorker"/>, the one the Linux host creates for the role) inside a real gateway on 127.0.0.1 (Kestrel,
/// pinned TLS, pairing), the gateway's shared speaking-voice list holding a starter voice, and the desktop's paired client
/// (<see cref="Audio2FaceHostConnection.MakeSongAsync"/>, what <c>SongClient</c> runs). The singing service is a live one on a
/// numeric loopback endpoint, or (with "fixture") this checkout's workers/singing service started here with the FIXTURE -
/// NOT AI engine. Nothing is played or recorded. Reports the service's own status before and after, every stage seen with
/// when, the host's stage timings, and the three tracks.
/// </summary>
internal static class SingingCheck
{
    internal static async Task<(bool Ok, object Report)> RunAsync(string endpointText, int seconds, string quality, string voiceMatch,
        string? saveDirectory, string? voiceRecording, string? voiceTranscript, int? bpm, string? key, CancellationToken token)
    {
        if (seconds is < SongRequest.MinimumDurationSeconds or > SongRequest.MaximumDurationSeconds)
            throw new ArgumentException($"seconds must be {SongRequest.MinimumDurationSeconds} to {SongRequest.MaximumDurationSeconds}.");
        FixtureService? fixture = null;
        Uri endpoint;
        if (endpointText == "fixture")
        {
            fixture = await FixtureService.StartAsync(token);
            endpoint = fixture.Endpoint;
        }
        else if (!Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint!) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("endpoint must be \"fixture\" or a numeric loopback address such as http://127.0.0.1:50085/.");

        var voice = F5BundledVoices.Default;
        var voiceId = SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript);
        var voices = SpeakingVoiceLibrary.Empty.Seed(F5SharedVoices.Starters);
        var voiceName = voice.Key;
        byte[]? recording = null;
        if (voiceRecording is not null)
        {
            // A copy of one of the owner's recordings (mono 16-bit PCM WAV), added to the gateway's list as the desktop adds a
            // voice the owner recorded.
            recording = await File.ReadAllBytesAsync(voiceRecording, token);
            var (rate, frames) = WaveShape(recording);
            var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(recording));
            var transcript = string.IsNullOrWhiteSpace(voiceTranscript) ? "A recording of my voice." : voiceTranscript.Trim();
            voices = voices.Add("Singing check voice", transcript, sha256, (int)(frames * 1000L / rate), SpeakingVoiceRights.OwnVoice,
                "singing-check", DateTimeOffset.UtcNow);
            voiceId = SpeakingVoiceLibrary.ReferenceId(sha256, transcript);
            voiceName = Path.GetFileName(voiceRecording);
        }
        var request = new SongRequest
        {
            Lyrics = "[verse]\nMorning light is on the window\nCoffee steaming by the door\nEvery little thing feels easy\n" +
                "When you're laughing like before\n\n[chorus]\nSing it with me, sing it slowly\nLet the quiet carry on\n" +
                "Hold the moment, hold it softly\nWe'll be dancing till the dawn",
            Style = "gentle acoustic pop ballad, warm female lead vocal, acoustic guitar, soft piano",
            VoiceId = voiceId,
            DurationSeconds = seconds,
            Seed = 42,
            // As Martlet's own model writes them; without a tempo or key the host's music planner runs first.
            Bpm = bpm,
            Key = key,
            Quality = quality == "high_quality" ? SongQuality.HighQuality : SongQuality.Fast,
            VoiceMatch = voiceMatch == "vevosing" ? SongVoiceMatch.VevoSing : SongVoiceMatch.SoulX
        };
        var stages = new List<object>();
        var watch = Stopwatch.StartNew();
        object? before = null, after = null, host_ = null;
        SongResult? song = null;
        string? failure = null, problem = null;
        try
        {
            await using var host = await VoiceEngineCheck.LiveHost.StartAsync(new SongRelayWorker(endpoint));
            using var connection = await host.PairAsync("singing-check-desktop", token);
            var route = (await connection.ReadRoutesAsync(token)).Single(r => r.RouteId == Audio2FaceHostConnection.SongRouteId);
            // The gateway resolves a song's voice from its shared speaking-voice list, as a paired desktop keeps it.
            await connection.MergeSpeakingVoicesAsync(voices, token);
            before = await StatusAsync(connection, route, token);
            var progress = new SynchronousProgress(p => stages.Add(new
            {
                stage = p.Stage.ToString(), fraction = Math.Round(p.Fraction, 3), queuePosition = p.QueuePosition,
                atMs = Math.Round(watch.Elapsed.TotalMilliseconds)
            }));
            watch.Restart();
            song = await connection.MakeSongAsync(route, request, _ => Task.FromResult<byte[]?>(recording ?? voice.ReadAudio()), progress, token,
                TimeSpan.FromMilliseconds(500));
            host_ = await JobReportAsync(connection, route, song.JobId, token);
            after = await StatusAsync(connection, route, token);
            if (saveDirectory is not null)
            {
                Directory.CreateDirectory(saveDirectory);
                await File.WriteAllBytesAsync(Path.Combine(saveDirectory, "mix.wav"), song.Mix.ToWave(), token);
                await File.WriteAllBytesAsync(Path.Combine(saveDirectory, "vocals.wav"), song.Vocals.ToWave(), token);
                await File.WriteAllBytesAsync(Path.Combine(saveDirectory, "backing.wav"), song.Backing.ToWave(), token);
            }
        }
        catch (SongException error)
        {
            failure = error.Code;
            problem = error.Message;
        }
        catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException or InvalidOperationException
            or ContractException)
        {
            failure = error is Audio2FaceHostException host ? host.Code : error.GetType().Name;
            problem = error.Message;
        }
        finally
        {
            if (fixture is not null) await fixture.DisposeAsync();
        }
        var elapsed = watch.Elapsed;
        object? Track(SongTrack? track)
        {
            if (track is null) return null;
            var span = track.Pcm16.Span;
            long sum = 0;
            var peak = 0;
            for (var i = 0; i + 1 < span.Length; i += 2)
            {
                int sample = BinaryPrimitives.ReadInt16LittleEndian(span[i..]);
                peak = Math.Max(peak, Math.Abs(sample));
                sum += (long)sample * sample;
            }
            var count = span.Length / 2;
            var rms = count == 0 ? 0 : Math.Sqrt((double)sum / count);
            return new
            {
                sampleRate = track.SampleRate, channels = track.Channels, seconds = Math.Round(track.Duration.TotalSeconds, 2),
                peakDbfs = peak == 0 ? -120 : Math.Round(20 * Math.Log10((double)peak / short.MaxValue), 1),
                rmsDbfs = rms <= 0 ? -120 : Math.Round(20 * Math.Log10(rms / short.MaxValue), 1)
            };
        }
        var ok = failure is null && song is not null && song.Mix.Frames == song.Vocals.Frames && song.Mix.Frames == song.Backing.Frames &&
            Math.Abs(song.Duration.TotalSeconds - seconds) < 2 && song.Beats.Count > 0;
        return (ok, new
        {
            ok,
            endpoint = fixture is null ? endpoint.ToString() : "fixture (workers/singing, FIXTURE - NOT AI)",
            route = Audio2FaceHostConnection.SongRouteId,
            voice = voiceName,
            request = new { seconds, quality, voiceMatch, bpm, key },
            statusBefore = before,
            stages,
            elapsedMs = Math.Round(elapsed.TotalMilliseconds),
            realTimeFactor = song is null ? (double?)null : Math.Round(elapsed.TotalSeconds / song.Duration.TotalSeconds, 2),
            song = song is null ? null : new
            {
                jobId = song.JobId,
                engine = song.Engine,
                seconds = Math.Round(song.Duration.TotalSeconds, 2),
                bpm = song.Bpm is { } measured ? Math.Round(measured, 1) : (double?)null,
                key = song.Key,
                beatsPerBar = song.BeatsPerBar,
                beats = song.Beats.Count,
                downbeats = song.Downbeats.Count,
                firstDownbeats = song.Downbeats.Take(4).Select(d => Math.Round(d.TotalSeconds, 2)),
                words = song.Words.Count,
                wordTimingSource = song.WordTimingSource,
                firstWords = song.Words.Take(8).Select(w => new
                {
                    start = Math.Round(w.Start.TotalSeconds, 2), end = Math.Round(w.End.TotalSeconds, 2), w.Text, line = w.LineIndex
                }),
                lines = song.LyricTimestamps.Count,
                firstLines = song.LyricTimestamps.Take(4).Select(l => new
                {
                    start = Math.Round(l.Start.TotalSeconds, 2), end = l.End is { } end ? Math.Round(end.TotalSeconds, 2) : (double?)null,
                    l.Section, l.Text
                }),
                stageTimings = song.StageTimings.Select(t => new { stage = t.Stage.ToString(), seconds = Math.Round(t.Duration.TotalSeconds, 2) }),
                mix = Track(song.Mix),
                vocals = Track(song.Vocals),
                backing = Track(song.Backing)
            },
            host = host_,
            saved = saveDirectory,
            failure,
            problem,
            statusAfter = after
        });
    }

    /// <summary>The singing service's own status through the gateway (state, engine, voice matches, queue, models, GPU memory),
    /// or why it could not be read.</summary>
    private static async Task<object> StatusAsync(Audio2FaceHostConnection connection, HostRoute route, CancellationToken token)
    {
        try
        {
            var answer = await connection.SongOperationAsync(route, new Dictionary<string, object> { ["operation"] = "status" }, token);
            if (answer.Count != 1) return new { answered = false, problem = "no status" };
            var status = answer[0];
            string? Text(string name) => status.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var models = status.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.Object &&
                worker.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array
                ? artifacts.EnumerateArray().Select(a => new
                {
                    id = a.GetProperty("artifact_id").GetString(), license = a.GetProperty("license_id").GetString(),
                    bytes = a.GetProperty("bytes").GetInt64()
                }).ToArray()
                : null;
            return new
            {
                answered = true, state = Text("state"), engine = Text("engine"), error = Text("error"),
                voiceMatches = status.TryGetProperty("voice_matches", out var matches) ? matches.Clone() : default(JsonElement?),
                queue = status.TryGetProperty("queue", out var queue) ? queue.GetInt32() : (int?)null,
                workerRunning = status.TryGetProperty("worker_running", out var running) && running.GetBoolean(),
                gpu = status.TryGetProperty("gpu", out var gpu) ? gpu.Clone() : default(JsonElement?),
                models = models?.Length, modelBytes = models?.Sum(m => m.bytes),
                licenses = models?.Select(m => m.license).Distinct()
            };
        }
        catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException or JsonException or
            InvalidOperationException or KeyNotFoundException)
        {
            return new { answered = false, problem = error.Message };
        }
    }

    /// <summary>What the host measured for the finished song beyond the contract: the word-timing source and its sanity
    /// metric (median word start to vocal onset), the lyric-timing source, the backing bleed removed from the vocals, the
    /// planner, quantization and the worker's graphics-memory peak.</summary>
    private static async Task<object?> JobReportAsync(Audio2FaceHostConnection connection, HostRoute route, string jobId,
        CancellationToken token)
    {
        try
        {
            var answer = await connection.SongOperationAsync(route,
                new Dictionary<string, object> { ["operation"] = "status", ["job_id"] = jobId }, token);
            if (answer.Count != 1 || !answer[0].TryGetProperty("result", out var result)) return null;
            JsonElement? Field(string name) => result.TryGetProperty(name, out var value) ? value.Clone() : null;
            return new
            {
                wordTiming = Field("word_timing"), lyricTimingSource = Field("lyric_timing_source"),
                vocalBleedDb = Field("vocal_bleed_db"), peakVramMib = Field("peak_vram_mib"), plannedBpm = Field("planned_bpm"),
                engine = Field("engine"), timings = Field("timings")
            };
        }
        catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException)
        {
            return new { problem = error.Message };
        }
    }

    private sealed class SynchronousProgress(Action<SongProgress> report) : IProgress<SongProgress>
    {
        public void Report(SongProgress value) => report(value);
    }

    /// <summary>This checkout's workers/singing service with the FIXTURE - NOT AI engine, on a free loopback port, with its
    /// own temporary root; stopped and deleted afterwards.</summary>
    private sealed class FixtureService : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string root;

        private FixtureService(Process process, string root, Uri endpoint)
        {
            this.process = process;
            this.root = root;
            Endpoint = endpoint;
        }

        internal Uri Endpoint { get; }

        internal static async Task<FixtureService> StartAsync(CancellationToken token)
        {
            var workers = Path.Combine(SourceRoot(), "workers", "singing");
            if (!Directory.Exists(Path.Combine(workers, "martlet_singing")))
                throw new InvalidOperationException("workers/singing is not in this checkout.");
            var root = Path.Combine(Path.GetTempPath(), "martlet-singing-check-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var port = FreePort();
            ProcessStartInfo Python(params string[] arguments)
            {
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MARTLET_PYTHON") ?? "python")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add("martlet_singing.host");
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["PYTHONPATH"] = workers;
                start.Environment["MARTLET_SINGING_ROOT"] = root;
                start.Environment["MARTLET_SINGING_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                start.Environment["MARTLET_SINGING_FIXTURE_STAGE_SECONDS"] = "0.4";
                return start;
            }
            using (var provision = Process.Start(Python("provision", "--fixture")) ?? throw new InvalidOperationException("Python did not start."))
            {
                await provision.WaitForExitAsync(token);
                if (provision.ExitCode != 0)
                    throw new InvalidOperationException($"Provisioning the fixture failed: {await provision.StandardError.ReadToEndAsync(token)}");
            }
            var serve = Process.Start(Python("serve")) ?? throw new InvalidOperationException("Python did not start.");
            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    using var response = await http.GetAsync(new Uri(endpoint, "status"), token);
                    if (response.IsSuccessStatusCode) return new FixtureService(serve, root, endpoint);
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(100, token);
            }
            serve.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The fixture singing service did not start.");
        }

        public async ValueTask DisposeAsync()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            process.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string SourceRoot()
        {
            // bin/<configuration>/net10.0 under src/Martlet.NodeLinkCheck in a source checkout.
            var directory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            for (var current = directory; current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "Martlet.slnx"))) return current.FullName;
            throw new InvalidOperationException("Run singing_check from a Martlet source checkout's build.");
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }
    }

    /// <summary>The sample rate and frame count of a mono 16-bit PCM WAV.</summary>
    private static (int Rate, long Frames) WaveShape(byte[] wave)
    {
        if (wave.Length < 44 || wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) is false || wave.AsSpan(8, 4).SequenceEqual("WAVE"u8) is false)
            throw new ArgumentException("voiceRecording must be a mono 16-bit PCM WAV file.");
        int rate = 0, channels = 0, bits = 0;
        for (var at = 12; at + 8 <= wave.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(at + 4, 4));
            if (wave.AsSpan(at, 4).SequenceEqual("fmt "u8) && size >= 16)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(at + 10, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(at + 12, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(at + 22, 2));
            }
            else if (wave.AsSpan(at, 4).SequenceEqual("data"u8))
            {
                if (channels != 1 || bits != 16 || rate <= 0) break;
                return (rate, Math.Min(size, wave.Length - at - 8) / 2);
            }
            if (size < 0) break;
            at += 8 + size + (size & 1);
        }
        throw new ArgumentException("voiceRecording must be a mono 16-bit PCM WAV file.");
    }
}

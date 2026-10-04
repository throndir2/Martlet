using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Core.Settings;
using Martlet.Core.Singing;

namespace Martlet.Mcp;

/// <summary>songs_status and song_playback_check: Martlet singing in conversation. The status reads the song library and the
/// desktop's songs-status.json (states, times, line numbers and sections; never titles or words). The check runs the
/// production playback code (<see cref="SongTransport"/>, <see cref="SongMixer"/> and <see cref="SongPlayer"/> pumping a fixture
/// output that plays ten times faster than real time and keeps what it was given) on the FIXTURE - NOT AI song or a stored one,
/// and measures the transitions in the audio it produced: the lead-in on a downbeat with its equal-power fade-in, the vocals
/// muted until the line, the band vamping while Martlet talks, ducking, the musical and quick stops and the stop record.
/// Nothing is played aloud and nothing leaves this PC.</summary>
internal static class SongsCheck
{
    private const int Rate = SongTrack.SampleRateHz;
    private const string FixtureLyrics = "[verse]\nMorning light is on the window\nCoffee steaming by the door\n" +
        "Every little thing feels easy\nWhen you're laughing like before\n[chorus]\nSing it with me, sing it slowly\n" +
        "Let the quiet carry on\n[verse]\nAll the clocks forgot to hurry\nAll the noise has come and gone\n[chorus]\n" +
        "Sing it with me, sing it slowly\nLet the quiet carry on";

    // ---------- songs_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinkLonger = ThinkLongerSettings.Of(loaded.Settings?.Generation);
        var songs = SongLibrary.In(dataDirectory).List();
        return new
        {
            backgroundWork = thinkLonger.On,
            library = new
            {
                count = songs.Count, kept = SongLibrary.Kept,
                songs = songs.Select(song => new
                {
                    id = song.Id, durationSeconds = song.DurationSeconds, lines = song.Lines.Count,
                    sections = song.Lines.Select(line => line.Section).Where(section => section.Length > 0).Distinct().ToArray(),
                    bpm = song.Bpm, beatsPerBar = song.BeatsPerBar, beats = song.Beats.Count, downbeats = song.Downbeats.Count,
                    titleCharacters = song.Title.Length, lyricsCharacters = song.Lyrics.Length, generator = song.Generator,
                    converter = song.Converter, quality = song.Quality, voiceMatch = song.VoiceMatch, fixture = song.Fixture,
                    createdAt = song.CreatedAt
                }).ToArray()
            },
            desktop = Status(dataDirectory),
            kind = new
            {
                name = SongTools.Kind.Name, maxActive = SongTools.Kind.MaxActive, perHour = SongTools.Kind.MaxPerHour,
                timeLimitMinutes = SongTools.Kind.TimeLimit.TotalMinutes, offer = SongTools.Kind.Offer, doing = SongTools.Kind.Doing
            },
            tools = SongTools.Definitions.Select(tool => new
            {
                name = tool.Name, description = tool.Description, parameters = JsonNode.Parse(tool.ParametersJson)
            }).ToArray(),
            prompt = SongTools.Instructions(loaded.Settings?.Prompts)
        };
    }

    // What the desktop wrote last (the song playing and the last stop; never titles or words), or why there is nothing.
    private static object Status(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "songs-status.json");
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't sung with this data directory." };
            if (new FileInfo(path).Length > 65_536) return new { state = "unreadable", why = "songs-status.json is too large." };
            return new { state = "loaded", file = JsonNode.Parse(File.ReadAllText(path)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
    }

    // ---------- song_playback_check ----------

    internal static async Task<object> RunAsync(string? dataDirectory, string? songId, CancellationToken cancellation)
    {
        StoredSong song;
        SongAudio audio;
        if (songId is not null)
        {
            if (dataDirectory is null) throw new ArgumentException("songId needs the dataDirectory it is stored in.");
            var library = SongLibrary.In(dataDirectory);
            song = library.Find(songId) ?? throw new ArgumentException($"There's no song '{songId}' in that data directory.");
            audio = library.LoadAudio(song) ?? throw new ArgumentException($"The song '{songId}' couldn't be read.");
        }
        else (song, audio) = Fixture();
        var map = song.Map();
        if (map.Lines.Count < 4) throw new ArgumentException("The song needs at least four sung lines for the check.");
        var envelope = new VocalEnvelope(audio.Vocals, audio.SampleRate);
        var resolve = Resolve(map);
        var leadIn = LeadIn(map, audio, envelope);
        var vamp = Vamp(map, audio, envelope);
        var duck = Duck(map, audio, envelope);
        var played = await PlayedAsync(song, map, audio, cancellation);
        return new
        {
            ok = resolve.Ok && leadIn.Ok && vamp.Ok && duck.Ok && played.Ok,
            song = new
            {
                id = song.Id, fixture = song.Fixture, durationSeconds = Math.Round(map.Duration.TotalSeconds, 2), bpm = map.Bpm,
                beatSeconds = Math.Round(map.Beat.TotalSeconds, 3), barSeconds = Math.Round(map.Bar.TotalSeconds, 3), lines = map.Lines.Count,
                sections = map.Sections
            },
            note = song.Fixture
                ? "FIXTURE - NOT AI tone song; the transport, mixer, player and stop records are Martlet's own. Nothing is played aloud."
                : "A stored song; the transport, mixer, player and stop records are Martlet's own. Nothing is played aloud.",
            resolve = resolve.Report, leadIn = leadIn.Report, vamp = vamp.Report, duck = duck.Report, played = played.Report
        };
    }

    private static (StoredSong, SongAudio) Fixture()
    {
        var request = new SongRequest
        {
            Lyrics = FixtureLyrics, Style = "gentle acoustic pop", VoiceId = "fixture-voice", DurationSeconds = 40, Bpm = 96, Seed = 42
        };
        var result = FixtureSongMaker.Compose(request);
        var song = new StoredSong
        {
            Id = "song-f17e00", Title = "Morning Light (fixture)", Lyrics = FixtureLyrics, Style = request.Style, VoiceId = request.VoiceId,
            DurationSeconds = result.Duration.TotalSeconds, Bpm = result.Bpm, BeatsPerBar = result.BeatsPerBar,
            Beats = [.. result.Beats.Select(b => b.TotalSeconds)], Downbeats = [.. result.Downbeats.Select(b => b.TotalSeconds)],
            Lines = [.. result.LyricTimestamps.Select(l => new StoredSongLine(l.Start.TotalSeconds, l.End?.TotalSeconds, l.Text, l.Section))],
            Generator = result.Engine.Generator, Converter = result.Engine.Converter, Fixture = true, CreatedAt = DateTimeOffset.UtcNow
        };
        return (song, SongAudio.From(result));
    }

    // Where play_song's "from" points.
    private static (bool Ok, object Report) Resolve(SongMap map)
    {
        var cases = new[] { "start", "chorus", "verse 2", "second verse", "line:3", "0:12", "bridge", "line:99" }.Select(from =>
        {
            var (target, problem) = SongTransport.Resolve(map, from, null);
            return new { from, line = target?.Line + 1, top = target?.Top, describe = target?.Describe, problem };
        }).ToArray();
        var chorus = map.Lines.ToList().FindIndex(line => line.Section == "chorus");
        var verse2 = map.Lines.ToList().FindIndex(line => line.Section == "verse 2");
        var ok = cases[0].top == true && (chorus < 0 || cases[1].line == chorus + 1) && (verse2 < 0 || cases[2].line == verse2 + 1 && cases[3].line == verse2 + 1) &&
            cases[4].line == 3 && cases[5].problem is null && cases[6].problem is not null && cases[7].problem is not null;
        return (ok, cases);
    }

    // The resume lead-in, rendered offline from the backing alone and the vocals alone: the backing starts on a downbeat from
    // silence with an equal-power fade over the first bar; the vocals are silent until just before the line's onset.
    private static (bool Ok, object Report) LeadIn(SongMap map, SongAudio audio, VocalEnvelope envelope)
    {
        var line = Math.Min(3, map.Lines.Count - 1);
        var plan = SongTransport.PlanStart(map, new(line, null, $"line {line + 1}"));
        var length = (int)((plan.Onset - plan.Entry).TotalSeconds * Rate) + Rate;
        var backing = Render(new SongMixer(new(audio.Backing, new short[audio.Vocals.Length]), map, envelope, plan), length);
        var vocals = Render(new SongMixer(new(new short[audio.Backing.Length], audio.Vocals), map, envelope, plan), length);
        var source = Source(audio.Backing, plan.Entry, length);
        var fade = (int)(plan.FadeIn.TotalSeconds * Rate);
        double Gain(int at) => Rms(backing, at, at + 480) / Math.Max(1e-9, Rms(source, at, at + 480));
        var first = Gain(0);
        var middle = Gain(Math.Max(0, fade / 2 - 240));
        var full = Gain(Math.Min(length - 480, fade + 480));
        var gate = (int)((plan.VocalsFrom - SongMixer.VocalRamp - plan.Entry).TotalSeconds * Rate);
        var vocalsBefore = Peak(vocals, 0, gate);
        var onset = (int)((plan.Onset - plan.Entry).TotalSeconds * Rate);
        var vocalsAfter = Rms(vocals, onset, onset + Rate / 4);
        var onDownbeat = map.Downbeats.Any(downbeat => Math.Abs((downbeat - plan.Entry).TotalMilliseconds) < 1);
        var ok = onDownbeat && plan.LeadInBars is 1 or 2 && plan.LeadIn >= map.Bar / 2 - TimeSpan.FromMilliseconds(1) &&
            first < 0.05 && Math.Abs(middle - Math.Sqrt(0.5)) < 0.08 && full > 0.97 && vocalsBefore == 0 && vocalsAfter > 0;
        return (ok, new
        {
            target = $"line {line + 1}", onsetSeconds = Seconds(plan.Onset), entrySeconds = Seconds(plan.Entry), entryOnDownbeat = onDownbeat,
            leadInBars = plan.LeadInBars, leadInSeconds = Seconds(plan.LeadIn), fadeInMs = Math.Round(plan.FadeIn.TotalMilliseconds),
            backingGain = new { firstTenMs = Math.Round(first, 3), atHalfFade = Math.Round(middle, 3), afterFade = Math.Round(full, 3), equalPowerHalf = 0.707 },
            vocalsFromSeconds = Seconds(plan.VocalsFrom), vocalsPeakBeforeGate = vocalsBefore, vocalsRmsAfterOnset = Math.Round(vocalsAfter, 1)
        });
    }

    // Martlet still talks when the lead-in reaches the line: the band repeats the bar twice, then the vocals come in.
    private static (bool Ok, object Report) Vamp(SongMap map, SongAudio audio, VocalEnvelope envelope)
    {
        var line = Math.Min(3, map.Lines.Count - 1);
        var plan = SongTransport.PlanStart(map, new(line, null, $"line {line + 1}"));
        var mixer = new SongMixer(new(new short[audio.Backing.Length], audio.Vocals), map, envelope, plan) { Hold = true };
        var output = new List<short>();
        var block = new short[960];
        while (output.Count < Rate * 120)
        {
            if (mixer.Vamps >= 2) mixer.Hold = false;
            var frames = mixer.Render(block, 480);
            output.AddRange(block.AsSpan(0, frames * 2).ToArray());
            if (frames == 0 || mixer.Singing && mixer.Written > plan.Onset + TimeSpan.FromSeconds(1)) break;
        }
        var samples = output.ToArray();
        var firstVoice = FirstLoud(samples, 1);
        var expected = (plan.Onset - plan.Entry + map.Bar * 2).TotalSeconds;
        var heard = firstVoice < 0 ? (double?)null : (double)firstVoice / Rate;
        var ok = mixer.Vamps == 2 && heard is { } at && Math.Abs(at - expected) < 0.15;
        return (ok, new
        {
            vamps = mixer.Vamps, barSeconds = Seconds(map.Bar), vampFromSeconds = Seconds(plan.VampStart), vampToSeconds = Seconds(plan.VampEnd),
            commitSeconds = Seconds(plan.CommitAt), vocalsFirstHeardAfterSeconds = heard is null ? (double?)null : Math.Round(heard.Value, 3),
            expectedAfterSeconds = Math.Round(expected, 3)
        });
    }

    // Martlet talks over the song: it is turned down by 12 dB.
    private static (bool Ok, object Report) Duck(SongMap map, SongAudio audio, VocalEnvelope envelope)
    {
        var plan = SongTransport.PlanStart(map, new(null, null, "from the top", Top: true));
        var normal = Render(new SongMixer(audio, map, envelope, plan), Rate * 3);
        var ducked = Render(new SongMixer(audio, map, envelope, plan) { Ducked = true }, Rate * 3);
        var decibels = 20 * Math.Log10(Rms(ducked, Rate, Rate * 3) / Math.Max(1e-9, Rms(normal, Rate, Rate * 3)));
        return (Math.Abs(decibels + 12) < 0.3, new { duckedDb = Math.Round(decibels, 2), expectedDb = -12, rampMs = SongMixer.DuckTime.TotalMilliseconds });
    }

    // The production player on a fixture output: sung from the top and stopped musically mid-line by the user's words (the stop
    // record, and when the audio it produced went silent), resumed with its lead-in while "Martlet" talks for the first two bars
    // of vamping, then stopped quickly with Esc.
    private static async Task<(bool Ok, object Report)> PlayedAsync(StoredSong song, SongMap map, SongAudio audio, CancellationToken cancellation)
    {
        var line = Math.Min(2, map.Lines.Count - 2);
        var stopAt = map.Lines[line].Start + ((map.Lines[line].End ?? map.Lines[line + 1].Start) - map.Lines[line].Start) * 0.45;
        var output = new OutputSelection(OutputPolicy.DefaultAtStart);
        var first = new FixtureOutput(10);
        var top = SongTransport.PlanStart(map, new(null, null, "from the top", Top: true));
        await using var singing = new SongPlayer(song, map, audio, top, first, output, () => false, pumpWait: TimeSpan.FromMilliseconds(2));
        var watch = Stopwatch.StartNew();
        singing.Start();
        while (singing.Position < stopAt && singing.Active && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(2, cancellation);
        var record = singing.Stop(SongStopCause.UserWords, musical: true, words: "okay okay Martlet, stop singing");
        var stopped = await singing.Completion.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
        var stop = singing.Mixer.Stopping;
        var playedFirst = first.Played();
        var silentAt = (double)(LastLoud(playedFirst) + 1) / Rate;
        var stopOk = record is { Line: { } index, Cause: SongStopCause.UserWords } && index == line && stop is { Musical: true } &&
            stopped.Cause == SongStopCause.UserWords && singing.State == SongPlaybackState.Stopped &&
            Math.Abs(silentAt - stop.SilentAt.TotalSeconds) < 0.06 && stop.AfterRequest <= TimeSpan.FromSeconds(2.2) &&
            stop.VocalsEnd - stop.Requested <= SongTransport.MusicalStopWindow + TimeSpan.FromMilliseconds(15) &&
            (map.Beats.Any(beat => Math.Abs((beat - stop.BackingFrom).TotalMilliseconds) < 1) || stop.BackingFrom == stop.VocalsEnd);

        var (target, problem) = SongTransport.Resolve(map, "resume", record);
        var plan = SongTransport.PlanStart(map, target!);
        var second = new FixtureOutput(10);
        SongPlayer? resumed = null;
        await using var again = resumed = new SongPlayer(song, map, audio, plan, second, output,
            () => resumed?.Mixer.Vamps < 2, pumpWait: TimeSpan.FromMilliseconds(2));
        watch.Restart();
        resumed.Start();
        var states = new List<string>();
        void Seen(SongPlaybackState state)
        {
            lock (states) if (states.Count == 0 || states[^1] != state.ToString()) states.Add(state.ToString());
        }
        while (resumed.Active && watch.Elapsed < TimeSpan.FromSeconds(30) &&
            !(resumed.State == SongPlaybackState.Singing && resumed.Position > plan.Onset + TimeSpan.FromSeconds(1)))
        {
            Seen(resumed.State);
            await Task.Delay(2, cancellation);
        }
        Seen(resumed.State);
        var esc = resumed.Stop(SongStopCause.Button, musical: false, reason: "Esc");
        var ended = await resumed.Completion.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
        Seen(resumed.State);
        var quick = resumed.Mixer.Stopping;
        var playedSecond = second.Played();
        var lastLoud = LastLoud(playedSecond);
        var quietAfter = (resumed.Mixer.SourceAt(lastLoud + 1) - (esc?.At ?? TimeSpan.Zero)).TotalSeconds;
        var resumeOk = target?.Line == line && plan.LeadInBars is 1 or 2 && resumed.Mixer.Vamps == 2 &&
            states.Contains(nameof(SongPlaybackState.LeadIn)) && states.Contains(nameof(SongPlaybackState.Singing)) &&
            quick is { Musical: false } && quick.BackingFade == SongTransport.QuickStopFade &&
            // The fade starts where the audio already handed to the output ends (at most its 100 ms buffer here).
            (quick.SilentAt - quick.Requested).TotalSeconds is >= 0.29 and <= 0.42 &&
            quietAfter < 0.5 && ended.Cause == SongStopCause.Button && Rms(playedSecond, 0, 480) < Rms(playedSecond, Rate * 2, Rate * 2 + 4800) * 0.1;
        return (stopOk && resumeOk, new
        {
            musicalStop = new
            {
                requestedSeconds = Seconds(record?.At ?? TimeSpan.Zero), line = record?.Line + 1, section = record?.Section, cause = record?.Cause.ToString(),
                vocalsEndSeconds = stop is null ? (double?)null : Seconds(stop.VocalsEnd), vocalsFadeMs = stop?.VocalsFade.TotalMilliseconds,
                backingFadeFromSeconds = stop is null ? (double?)null : Seconds(stop.BackingFrom), backingFadeMs = stop is null ? (double?)null : Math.Round(stop.BackingFade.TotalMilliseconds),
                plannedSilentSeconds = stop is null ? (double?)null : Seconds(stop.SilentAt), measuredSilentSeconds = Math.Round(silentAt, 3),
                silentAfterRequestSeconds = stop is null ? (double?)null : Math.Round(stop.AfterRequest.TotalSeconds, 3),
                state = singing.State.ToString(), note = record?.Note(), ok = stopOk
            },
            resume = new
            {
                problem, from = target?.Describe, entrySeconds = Seconds(plan.Entry), leadInBars = plan.LeadInBars,
                fadeInMs = Math.Round(plan.FadeIn.TotalMilliseconds), vocalsFromSeconds = Seconds(plan.VocalsFrom), vamps = resumed.Mixer.Vamps,
                states, quickStop = new
                {
                    requestedSeconds = Seconds(esc?.At ?? TimeSpan.Zero), fadeMs = quick?.BackingFade.TotalMilliseconds,
                    plannedSilentAfterSeconds = quick is null ? (double?)null : Math.Round((quick.SilentAt - quick.Requested).TotalSeconds, 3),
                    measuredQuietAfterSeconds = Math.Round(quietAfter, 3), cause = ended.Cause.ToString(), reason = ended.Reason
                },
                ok = resumeOk
            }
        });
    }

    private static short[] Render(SongMixer mixer, int frames)
    {
        var output = new short[frames * 2];
        var done = 0;
        while (done < frames)
        {
            var count = mixer.Render(output.AsSpan(done * 2), Math.Min(4800, frames - done));
            if (count == 0) break;
            done += count;
        }
        return output;
    }

    private static short[] Source(short[] backing, TimeSpan from, int frames)
    {
        var start = (int)(from.TotalSeconds * Rate) * 2;
        var length = Math.Min(frames * 2, backing.Length - start);
        return length <= 0 ? new short[frames * 2] : backing.AsSpan(start, length).ToArray();
    }

    private static double Rms(short[] stereo, int fromFrame, int toFrame)
    {
        double sum = 0;
        var count = 0;
        for (var i = Math.Max(0, fromFrame) * 2; i < Math.Min(stereo.Length, toFrame * 2); i++, count++) sum += (double)stereo[i] * stereo[i];
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private static int Peak(short[] stereo, int fromFrame, int toFrame)
    {
        var peak = 0;
        for (var i = Math.Max(0, fromFrame) * 2; i < Math.Min(stereo.Length, toFrame * 2); i++) peak = Math.Max(peak, Math.Abs((int)stereo[i]));
        return peak;
    }

    private static int FirstLoud(short[] stereo, int threshold)
    {
        for (var i = 0; i < stereo.Length; i++) if (Math.Abs((int)stereo[i]) > threshold) return i / 2;
        return -1;
    }

    // The last frame louder than about -60 dBFS.
    private static int LastLoud(short[] stereo)
    {
        for (var i = stereo.Length - 1; i >= 0; i--) if (Math.Abs((int)stereo[i]) > 32) return i / 2;
        return 0;
    }

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 3);

    /// <summary>An output that plays <paramref name="speed"/> times faster than real time and keeps everything it was given.</summary>
    private sealed class FixtureOutput(double speed) : IPlaybackDeviceFactory
    {
        private readonly object gate = new();
        private readonly List<short> played = [];

        internal short[] Played() { lock (gate) return [.. played]; }

        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken) => new Device(this, speed);

        private sealed class Device(FixtureOutput owner, double speed) : IPlaybackDevice
        {
            private const int Capacity = 4_800;
            private long written;
            private Stopwatch? clock;

            public PlaybackDeviceInfo Info { get; } = new(Rate, 2, 16, DeviceSampleEncoding.IntegerPcm, Capacity, false);

            private long Consumed => clock is null ? 0 : Math.Min(written, (long)(clock.Elapsed.TotalSeconds * Rate * speed));

            public int GetPadding(CancellationToken cancellationToken) => (int)(written - Consumed);

            public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
            {
                var frames = Math.Min(pcm.Length / 4, Capacity - GetPadding(cancellationToken));
                if (frames <= 0) return 0;
                var samples = new short[frames * 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8);
                lock (owner.gate) owner.played.AddRange(samples);
                written += frames;
                return frames;
            }

            public void Start(CancellationToken cancellationToken) => clock = Stopwatch.StartNew();
            public void StopAndReset() { }
            public void Dispose() { }
        }
    }
}

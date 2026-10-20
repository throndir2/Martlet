using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Audio.Windows;
using Martlet.Core.Singing;
using Martlet.Providers.Singing;

namespace Martlet.Desktop;

/// <summary>
/// The desktop's <see cref="ISongMaker"/>: makes songs with the singing role of the owner's paired Martlet hosts (route
/// <c>martlet.gateway.song.v1</c>) through their pinned gateways, reading the pairing secret from Windows Credential Manager
/// for each song. A song is made in two jobs, each on its own pool (<see cref="SingingPool"/>): its music on the singing pool
/// (<see cref="PoolAreas.Singing"/>, each computer's own quality), then its voice match on the voice match pool
/// (<see cref="PoolAreas.VoiceMatch"/>, each computer's own model), which may be another computer; this PC puts the matched
/// vocals over the backing (<see cref="SongVoice"/>). With nothing on in the voice match list, songs keep the music model's own
/// singer. A computer whose Martlet is too old to make the music apart makes the whole song there. In each pool a computer
/// that makes another job, doesn't answer or can't make the job is passed over for the next; when all are busy the job waits
/// in line on the one with the fewest jobs before it. Hosts that friends share are never used. Songs are sung in a voice of
/// the shared voice library (usually <see cref="SpeakingVoiceId"/>); when the computer that matches the voice lacks its
/// recording, it is sent once from this PC's voice store. Setting <c>MARTLET_SINGING_FIXTURE=1</c> before Martlet starts makes
/// Singing use the FIXTURE - NOT AI <see cref="FixtureSongMaker"/> instead (for automated checks).
/// </summary>
internal sealed class SongClient(string dataDirectory) : ISongMaker
{
    internal const string FixtureVariable = "MARTLET_SINGING_FIXTURE";

    /// <summary>The share of a song's progress its music takes when a voice match follows.</summary>
    private const double MusicShare = 0.6;

    /// <summary>The song maker Martlet uses: this client, or the fixture when <see cref="FixtureVariable"/> is 1.</summary>
    internal static ISongMaker For(string dataDirectory) =>
        Environment.GetEnvironmentVariable(FixtureVariable) == "1" ? new FixtureSongMaker(TimeSpan.FromMilliseconds(400)) : new SongClient(dataDirectory);

    /// <summary>Whether singing is set up, without the network: not turned off on Companion › Singing, and the fixture is on, or
    /// the singing pool list (pools.json) has a member on that is paired here, or, without a list yet, a computer still paired
    /// with this PC ran Singing when the desktop last checked (<see cref="SingingPreferences.Host"/>). Cheap enough to ask for
    /// every reply.</summary>
    internal static bool IsSetUp(string dataDirectory)
    {
        var preferences = SingingPreferences.Load(dataDirectory);
        if (preferences.Off) return false;
        if (Environment.GetEnvironmentVariable(FixtureVariable) == "1") return true;
        if (WorkSharingRoster.Shared.Pool(dataDirectory, PoolAreas.Singing) is not null) return Members(dataDirectory).Count > 0;
        if (preferences.Host is not { } host) return false;
        try { return HostRegistry.Load(dataDirectory).Any(h => h.HostId == host && !h.Shared); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether the voice match is off: its list exists and nothing in it is on, so songs keep the music model's own
    /// singer. Without a list yet, the computers that write the music match the voice too.</summary>
    internal static bool VoiceMatchOff(string dataDirectory) =>
        WorkSharingRoster.Shared.Pool(dataDirectory, PoolAreas.VoiceMatch) is { } list && list.NoneOn;

    /// <summary>The shared-library ID of the voice Martlet speaks with on this PC (its reference revision), or null.</summary>
    internal static string? SpeakingVoiceId(string dataDirectory) => F5Voices.Applied(dataDirectory)?.ReferenceRevision;

    /// <summary>The <paramref name="area"/> pool (default: the singing pool, which writes the music) from this PC's files,
    /// without the network (<see cref="SingingPool.Members"/>): the owner's own paired computers (never hosts friends share),
    /// each with its pool member, in the order jobs try them. With a pool list (the area's list in pools.json): its members that
    /// are on, in the owner's order. The voice match pool without a list yet uses the singing pool's list. Without one either:
    /// the computer Martlet sings on, then those the shared plan (cluster.json) says run Singing, then the other paired
    /// computers.</summary>
    internal static IReadOnlyList<(PoolMember Member, PairedHost? Host)> Members(string dataDirectory, PoolArea? area = null)
    {
        area ??= PoolAreas.Singing;
        PairedHost[] hosts;
        try { hosts = [.. HostRegistry.Load(dataDirectory).Where(h => !h.Shared)]; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
        var list = WorkSharingRoster.Shared.Pool(dataDirectory, area);
        if (list is null && area == PoolAreas.VoiceMatch) list = WorkSharingRoster.Shared.Pool(dataDirectory, PoolAreas.Singing);
        WorkPlace[] singers = [];
        if (list is null)
        {
            var plan = ClusterSync.LoadPlan(dataDirectory);
            singers = [.. plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == HostRoles.Singing))
                .Select(n => new WorkPlace(n.HostId, false, plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))];
        }
        var own = WorkSharingRoster.Shared.OwnHostId ?? hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var match = area == PoolAreas.VoiceMatch;
        // A cloud provider takes part when it does this list's part and this PC has its key.
        bool Cloud(PoolMember member) => (match ? SingingCloud.MatchesVoice(member) : SingingCloud.WritesMusic(member)) &&
            SingingCloudKeys.Has(dataDirectory, member.Provider!);
        return [.. SingingPool.Members(list, WorkSharingRoster.Device, SingingPreferences.Load(dataDirectory).Host, own,
                [.. hosts.Select(h => h.HostId)], singers, area, Cloud)
            .Select(m => (m.Member, m.HostId is null ? null : hosts.First(h => h.HostId == m.HostId)))];
    }

    /// <summary>What a computer's singing service reports through its gateway: its state (ready, busy, loading or
    /// not_provisioned), its engine (song, or fixture), the voice matches set up there (soulx, and vevosing once added), how
    /// many jobs wait in its line, and whether it makes a song's music and voice match apart.</summary>
    internal sealed record SingingService(string? State, string? Engine, IReadOnlyList<SongVoiceMatch> VoiceMatches, string? Error, int Queue = 0)
    {
        internal IReadOnlyList<string> Kinds { get; init; } = ["song"];

        internal IReadOnlyList<SongMusicModel> MusicModels { get; init; } = [SongMusicModel.AceStep15];

        internal bool Has(SongVoiceMatch match) => VoiceMatches.Contains(match);

        internal bool Has(SongMusicModel model) => MusicModels.Contains(model);

        internal SingerState Singer => new(State, Queue, VoiceMatches) { Kinds = Kinds, MusicModels = MusicModels };

        internal static SingingService Parse(IReadOnlyList<JsonElement> answer)
        {
            var service = answer.Count == 1 ? answer[0] : default;
            string? Text(string name) => service.ValueKind == JsonValueKind.Object && service.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var singer = SingerState.Parse(service);
            return new(Text("state"), Text("engine"), singer.VoiceMatches, Text("error"), singer.Waiting)
            {
                Kinds = singer.Kinds, MusicModels = singer.MusicModels
            };
        }
    }

    /// <summary>Reads <paramref name="host"/>'s singing service through its gateway (song route, status operation); null when
    /// that computer doesn't offer Singing.</summary>
    internal static async Task<SingingService?> ReadServiceAsync(AvatarRemoteHost host, CancellationToken token)
    {
        using var connection = ClusterSync.Connect(host);
        var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
        if (routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.SongRouteId) is not { } route) return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return SingingService.Parse(await connection.SongOperationAsync(route,
                    new Dictionary<string, object> { ["operation"] = "status" }, token).ConfigureAwait(false));
            }
            // The gateway runs one request per route at a time: another computer's poll can hold it for a moment.
            catch (Audio2FaceHostException error) when (error.Code == "job.busy" && attempt < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether songs can be made now, across the singing pool: each computer's singing service is read at once; a cloud
    /// provider with its key here is ready. <see cref="SongMakerAvailability.Host"/> is where the next song's music would go
    /// (<see cref="SingingPool.Pick"/>); the voice matches are those set up on any computer that sings.</summary>
    public async Task<SongMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var members = Members(dataDirectory);
        if (members.Count == 0) return SongMakerAvailability.Unavailable("Set up Singing on Companion > Singing first.");
        if (members.FirstOrDefault(m => m.Host is null) is { Member: { } cloud } && members[0].Host is null)
            return new SongMakerAvailability(true, null, SingingCloud.Describe(cloud, PoolAreas.Singing), [SongQuality.HighQuality], [], false);
        var hosts = members.Select(m => m.Host).OfType<PairedHost>().DistinctBy(h => h.HostId).ToArray();
        var read = await Task.WhenAll(hosts.Select(async host =>
        {
            try { return (host.HostId, Service: await ReadServiceAsync(host.Pairing, cancellationToken).ConfigureAwait(false), Problem: (string?)null); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { return (host.HostId, Service: (SingingService?)null, Problem: error.Message); }
        })).ConfigureAwait(false);
        var singing = read.Where(r => r.Service is { State: not "not_provisioned" }).ToArray();
        if (singing.Length == 0)
        {
            // A cloud provider later in the list still writes the music.
            if (members.FirstOrDefault(m => m.Host is null) is { Member: { } later })
                return new SongMakerAvailability(true, null, SingingCloud.Describe(later, PoolAreas.Singing), [SongQuality.HighQuality], [], false);
            if (read.FirstOrDefault(r => r.Service is not null) is { Service: not null } unprovisioned)
                return SongMakerAvailability.Unavailable($"Singing on {unprovisioned.HostId} isn't ready (not set up yet).");
            return read.Length > 0 && read[0].Problem is { } problem
                ? SongMakerAvailability.Unavailable($"Singing on {read[0].HostId} isn't ready ({problem}).")
                : SongMakerAvailability.Unavailable("Set up Singing on Companion > Singing first.");
        }
        var states = read.ToDictionary(r => r.HostId, r => r.Service?.Singer, StringComparer.Ordinal);
        var order = hosts.Select(h => h.HostId).ToArray();
        var matches = singing.SelectMany(r => r.Service!.VoiceMatches).Distinct().Order().ToArray();
        var next = SingingPool.Pick(order, states)?.HostId ?? singing[0].HostId;
        return new SongMakerAvailability(true, null, next, [SongQuality.HighQuality, SongQuality.Fast], matches,
            read.First(r => r.HostId == next).Service?.Engine == "fixture");
    }

    // A cloud provider is always "ready" for the pool: its rate limit and outages come back as busy or unavailable when it is asked.
    private static readonly SingerState CloudState = new("ready", 0, [SongVoiceMatch.ElevenLabs]) { Kinds = ["song", "music", "match", "separate"] };

    /// <summary>What the music part gave: the song (separated and timed, or not), and whether a voice match may follow (no: it
    /// already sings in the voice, it was made whole on an older host, or it couldn't be separated).</summary>
    private sealed record Written(SongResult Song, bool Matchable);

    public async Task<SongResult> GenerateAsync(SongRequest request, IProgress<SongProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        progress?.Report(new SongProgress(SongStage.Queued, 0));
        var music = Members(dataDirectory);
        if (music.Count == 0)
            throw new SongException(SongErrorCodes.Unavailable, "No computer or provider is set up for Singing. Set it up on Companion > Singing.");
        var off = VoiceMatchOff(dataDirectory);
        var matchers = off ? [] : Members(dataDirectory, PoolAreas.VoiceMatch);
        if (!off && matchers.Count == 0)
            throw new SongException(SongErrorCodes.Unavailable,
                "No computer or provider in Companion > Singing's voice match list can be used from this PC. Add one, or turn the list off.");
        var share = matchers.Count == 0 ? 1 : MusicShare;
        try
        {
            // 1. The music, on the singing pool: a host writes it (each with its own quality and model; one too old to make it
            // apart makes the whole song), or a cloud provider writes it and a host (or nothing) separates and times it.
            var written = await SingingPool.RunAsync(WorkQueue.Shared, SingingPool.Lane, music, m => m.Member.Key,
                (m, t) => m.Host is null ? Task.FromResult<SingerState?>(CloudState) : LookAsync(m.Host, t), SingingPool.MusicRefusal,
                async (m, state, t) =>
                {
                    if (m.Host is null) return await CloudMusicAsync(m.Member, request, new StageProgress(progress, 0, share, last: matchers.Count == 0), t)
                        .ConfigureAwait(false);
                    var song = request with { Quality = SingingPool.Quality(m.Member), Model = SingingPool.MusicWith(m.Member, state) };
                    if (state?.Splits == true || matchers.Count == 0 && state is null)
                        return new Written(await OnHostAsync(m.Host, $"writes the music with {SingingPool.Name(song.Model)}", music,
                            (connection, route) => connection.MakeMusicAsync(route, song,
                                new StageProgress(progress, 0, share, last: matchers.Count == 0), t), t).ConfigureAwait(false), true);
                    return new Written(await OnHostAsync(m.Host, "makes the whole song (its Martlet is too old to make the music apart)", music,
                        (connection, route) => connection.MakeSongAsync(route, song with { VoiceMatch = SongVoiceMatch.SoulX },
                            RecordingAsync, progress, t), t).ConfigureAwait(false), false);
                }, Refusal, null, cancellationToken, m => m.Host?.HostId ?? SingingCloud.Describe(m.Member, PoolAreas.Singing)).ConfigureAwait(false);
            if (!written.Result.Matchable || matchers.Count == 0) return written.Result.Song;

            // 2. The voice match, on the voice match pool: a host with its own model (SoulX-Singer where it lacks VevoSing), or
            // ElevenLabs' Voice Changer.
            var vocals = written.Result.Song.Vocals;
            var matched = await SingingPool.RunAsync(WorkQueue.Shared, SingingPool.MatchLane, matchers, m => m.Member.Key,
                (m, t) => m.Host is null ? Task.FromResult<SingerState?>(CloudState) : LookAsync(m.Host, t), SingingPool.MatchRefusal,
                (m, state, t) => m.Host is null ? CloudMatchAsync(m.Member, vocals, t)
                    : OnHostAsync(m.Host, "matches the voice", matchers,
                        (connection, route) => connection.MatchVoiceAsync(route, request.VoiceId, SingingPool.MatchWith(m.Member, state),
                            vocals, RecordingAsync, new StageProgress(progress, MusicShare, 1 - MusicShare, last: true), t), t),
                Refusal, null, cancellationToken, m => m.Host?.HostId ?? SingingCloud.Describe(m.Member, PoolAreas.VoiceMatch)).ConfigureAwait(false);
            return SongVoice.WithVoice(written.Result.Song, matched.Result);
        }
        catch (WorkPreemptedException error)
        {
            throw new SongException(SongErrorCodes.Busy, "Every computer that sings keeps its graphics card for a live conversation now.", error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not SongException && ClusterSync.IsHostFailure(error))
        {
            throw new SongException(SongErrorCodes.Unavailable, $"Singing isn't reachable ({error.Message}).", error);
        }

        static WorkRefusal Refusal(Exception error) => WorkSharingRoster.Classify(error) is var host && host != WorkRefusal.None ? host : SingingPool.Classify(error);

        // A cloud provider writes the song; then it is separated and timed on a singing host when one can, else used as it is.
        async Task<Written> CloudMusicAsync(PoolMember member, SongRequest song, IProgress<SongProgress> stage, CancellationToken token)
        {
            var name = SingingCloud.Describe(member, PoolAreas.Singing);
            var key = await SingingCloudKeys.ReadAsync(dataDirectory, member.Provider!, token).ConfigureAwait(false) ??
                throw new SongException(SongErrorCodes.Unavailable, $"There's no {SingingCloud.Name(member.Provider)} key on this PC.");
            var own = SingingCloud.SingsInVoice(member);
            byte[] sample = [];
            if (own)
            {
                var recording = await RecordingAsync(token).ConfigureAwait(false) ??
                    throw new SongException(SongErrorCodes.VoiceMissing, "The voice's recording isn't on this computer.");
                sample = await Task.Run(() => SongAudioImport.VoiceSampleMp3(recording), token).ConfigureAwait(false);
            }
            ErrorLog.Info($"Singing: {name} writes the music (a cloud provider; each song may cost money).");
            stage.Report(new SongProgress(SongStage.WritingMusic, 0.05) { Host = name });
            using var writer = CloudWriter(member, key);
            var made = await writer.WriteAsync(song, sample, new Progress<double>(f => stage.Report(new SongProgress(SongStage.WritingMusic, 0.05 + 0.6 * f))),
                token).ConfigureAwait(false);
            var mix = await Task.Run(() => SongAudioImport.Decode(made.Mix.Bytes, 2, SongTrackKind.Mix, made.Mix.RawPcmRate), token).ConfigureAwait(false);
            stage.Report(new SongProgress(SongStage.Separating, 0.7));
            var engine = new SongEngineIdentity(made.Generator, "", "", SongQuality.HighQuality, own ? SongVoiceMatch.Mureka : null, false);
            // Separated and timed on a singing host (its vocals, backing, beat grid and lyric times from the vocals' phrases).
            var separators = music.Concat(matchers).Where(m => m.Host is not null).DistinctBy(m => m.Host!.HostId).ToArray();
            if (separators.Length > 0)
            {
                try
                {
                    var separated = await SingingPool.RunAsync(WorkQueue.Shared, SingingPool.MatchLane, separators, m => m.Member.Key,
                        (m, t) => LookAsync(m.Host!, t), (id, state) => SingingPool.MusicRefusal(id, state) ??
                            (state!.Separates ? null : new SongException(SongErrorCodes.Unavailable, $"Singing on {id} can't separate a song yet.")),
                        (m, _, t) => OnHostAsync(m.Host!, $"separates and times {name}'s song", separators,
                            (connection, route) => connection.SeparateAsync(route, song, mix, new StageProgress(stage, 0.7, 0.3, last: true), t), t),
                        Refusal, null, token, m => m.Host!.HostId).ConfigureAwait(false);
                    var lines = made.Lines.Count > 0 ? made.Lines : separated.Result.LyricTimestamps;
                    return new Written(separated.Result with
                    {
                        Engine = engine with { Separator = separated.Result.Engine.Separator },
                        LyricTimestamps = lines, Words = made.Lines.Count > 0 ? [] : separated.Result.Words,
                        VoiceId = song.VoiceId, JobId = "cloud-" + separated.Result.JobId
                    }, !own);
                }
                catch (SongException error) when (error.Code is SongErrorCodes.Unavailable or SongErrorCodes.Busy)
                {
                    ErrorLog.Info($"Singing: no singing computer could separate {name}'s song ({error.Message}); it is used without separating.");
                }
            }
            // Without a host: the mix as it is (the mouth follows the whole mix), lyric lines from the provider or spread over it.
            var (vocals, backing) = SongVoice.Unseparated(mix);
            stage.Report(new SongProgress(SongStage.Completed, 1));
            return new Written(new SongResult
            {
                JobId = "cloud-" + Guid.NewGuid().ToString("N")[..12], VoiceId = song.VoiceId, Mix = SongVoice.Stereo(mix), Vocals = vocals, Backing = backing,
                Engine = engine with { Separator = "none (not separated)" }, Seed = song.Seed ?? 0, Bpm = song.Bpm, Key = song.Key,
                LyricTimestamps = made.Lines.Count > 0 ? made.Lines : SongVoice.SpreadLines(song.Lyrics, vocals),
                WordTimingSource = ""
            }, false);
        }

        // ElevenLabs' Voice Changer sings the separated vocals in the voice; they are fitted to the song as a host's are.
        async Task<SongVoiceMatchResult> CloudMatchAsync(PoolMember member, SongTrack original, CancellationToken token)
        {
            var key = await SingingCloudKeys.ReadAsync(dataDirectory, member.Provider!, token).ConfigureAwait(false) ??
                throw new SongException(SongErrorCodes.Unavailable, $"There's no {SingingCloud.Name(member.Provider)} key on this PC.");
            var recording = await RecordingAsync(token).ConfigureAwait(false) ??
                throw new SongException(SongErrorCodes.VoiceMissing, "The voice's recording isn't on this computer.");
            var started = System.Diagnostics.Stopwatch.StartNew();
            ErrorLog.Info("Singing: ElevenLabs' Voice Changer matches the voice (a cloud provider; each song may cost money).");
            progress?.Report(new SongProgress(SongStage.MatchingVoice, MusicShare) { Host = SingingCloud.Describe(member, PoolAreas.VoiceMatch) });
            using var changer = new ElevenLabsVoiceChanger(key, member.Model!);
            var name = "Martlet " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(recording))[..12];
            var changed = await changer.ChangeAsync(name, recording, original.ToWave(), token).ConfigureAwait(false);
            var decoded = await Task.Run(() => SongAudioImport.Decode(changed.Bytes, 1, SongTrackKind.Vocals, changed.RawPcmRate), token).ConfigureAwait(false);
            var fitted = SongVoice.Gate(decoded, original);
            progress?.Report(new SongProgress(SongStage.Mixing, 0.95));
            return new SongVoiceMatchResult("cloud-match", fitted, $"elevenlabs/{member.Model}", SongVoiceMatch.ElevenLabs, false,
                [new SongStageTiming(SongStage.MatchingVoice, started.Elapsed)]);
        }

        // One job on one computer: its connection and song route, the log line, and its failures in a pool's words.
        async Task<T> OnHostAsync<T>(PairedHost host, string what, IReadOnlyList<(PoolMember Member, PairedHost? Host)> pool,
            Func<Audio2FaceHostConnection, HostRoute, Task<T>> run, CancellationToken token)
        {
            Audio2FaceHostConnection connection;
            HostRoute route;
            try
            {
                connection = ClusterSync.Connect(host.Pairing);
                try
                {
                    route = (await connection.ReadRoutesAsync(token).ConfigureAwait(false))
                        .FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.SongRouteId) ??
                        throw new SongException(SongErrorCodes.Unavailable, $"{host.HostId} doesn't sing any more.");
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not SongException && ClusterSync.IsHostFailure(error))
            {
                throw new SongException(SongErrorCodes.Unavailable, $"{host.HostId} isn't reachable ({error.Message}).", error);
            }
            using (connection)
            {
                ErrorLog.Info($"Singing: {host.HostId} {what} (number {pool.Select(m => m.Member.Key).ToList().IndexOf(pool.First(m => m.Host?.HostId == host.HostId).Member.Key) + 1} " +
                    $"of {pool.Count} in its list).");
                progress?.Report(new SongProgress(SongStage.Queued, 0) { Host = host.HostId });
                try
                {
                    return await run(connection, route).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException)
                {
                    throw new SongException(SongErrorCodes.Unavailable, $"{host.HostId} stopped answering ({error.Message}).", error);
                }
            }
        }

        async Task<byte[]?> RecordingAsync(CancellationToken token)
        {
            var (local, _) = F5Voices.Local(dataDirectory, "f5-host");
            if (!local.TryGetValue(request.VoiceId, out var snapshot)) return null;
            return await F5Voices.ReadAudioAsync(dataDirectory, snapshot, snapshot.AudioSha256, token).ConfigureAwait(false);
        }
    }

    /// <summary>The client that writes music with <paramref name="member"/> (a cloud provider of the music list).</summary>
    internal static ICloudSongWriter CloudWriter(PoolMember member, string key) => member.Provider switch
    {
        SingingCloud.ElevenLabs => new ElevenLabsSongWriter(key, member.Model!),
        SingingCloud.Mureka => new MurekaSongWriter(key, member.Model!, SingingCloud.SingsInVoice(member)),
        SingingCloud.Fal => new FalSongWriter(key, member.Model!),
        _ => throw new SongException(SongErrorCodes.Unavailable, $"Martlet can't write music with {member.Provider}.")
    };
    // A pool member's singing status for the pool (null: it doesn't offer Singing). Not reachable is a refusal: the next one
    // is asked.
    private static async Task<SingerState?> LookAsync(PairedHost host, CancellationToken token)
    {
        try { return (await ReadServiceAsync(host.Pairing, token).ConfigureAwait(false))?.Singer; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            throw new SongException(SongErrorCodes.Unavailable, $"{host.HostId} isn't reachable ({error.Message}).", error);
        }
    }

    /// <summary>One job's progress as a part of the whole song's: its fraction from <c>from</c> over <c>span</c>. A job that
    /// isn't the song's <c>last</c> passes on no Done report.</summary>
    private sealed class StageProgress(IProgress<SongProgress>? inner, double from, double span, bool last) : IProgress<SongProgress>
    {
        public void Report(SongProgress value)
        {
            if (inner is null || value.Stage == SongStage.Completed && !last) return;
            inner.Report(value with { Fraction = Math.Clamp(from + value.Fraction * span, 0, 1) });
        }
    }
}

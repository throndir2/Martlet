using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>Companion › Singing's older choices on this PC (singing.json in the data directory): whether Martlet sings at all
/// (<paramref name="Off"/>: the Off choice from before the pool lists; the role stays set up) and <paramref name="Host"/>, the
/// paired computer the desktop last saw running Singing (written when it checks its computers), so
/// <see cref="SongClient.IsSetUp"/> answers without the network before the lists are made. The quality and the voice match
/// are now each pool member's own settings (pools.json, shared); <see cref="OlderVoiceMatch"/> is the voice match the page
/// chose before, read once to make the voice match list.</summary>
internal sealed record SingingPreferences(string? Host = null, bool Off = false)
{
    internal const string FileName = "singing.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The voice match Companion › Singing used for every song before each computer had its own.</summary>
    internal SongVoiceMatch OlderVoiceMatch { get; init; } = SongVoiceMatch.SoulX;

    private sealed record Document(int Version, string? Quality = null, string? VoiceMatch = null, string? Host = null, bool? Off = null);

    /// <summary>The saved choices, or the defaults (on, no computer yet) when none are saved or the file can't be read.</summary>
    internal static SingingPreferences Load(string dataDirectory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(dataDirectory, FileName));
            if (bytes.Length > 4_096) return new();
            var document = JsonSerializer.Deserialize<Document>(bytes, Json);
            if (document is not { Version: 1 }) return new();
            return new(document.Host is { Length: > 0 and <= 128 } host ? host : null, document.Off == true)
            {
                OlderVoiceMatch = document.VoiceMatch == "vevosing" ? SongVoiceMatch.VevoSing : SongVoiceMatch.SoulX
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document(1, null,
                OlderVoiceMatch == SongVoiceMatch.VevoSing ? "vevosing" : null, Host, Off ? true : null), Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

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
    internal static IReadOnlyList<(PoolMember Member, PairedHost Host)> Members(string dataDirectory, PoolArea? area = null)
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
        return [.. SingingPool.Members(list, WorkSharingRoster.Device, SingingPreferences.Load(dataDirectory).Host, own,
                [.. hosts.Select(h => h.HostId)], singers, area)
            .Select(m => (m.Member, hosts.First(h => h.HostId == m.HostId)))];
    }

    /// <summary>What a computer's singing service reports through its gateway: its state (ready, busy, loading or
    /// not_provisioned), its engine (song, or fixture), the voice matches set up there (soulx, and vevosing once added), how
    /// many jobs wait in its line, and whether it makes a song's music and voice match apart.</summary>
    internal sealed record SingingService(string? State, string? Engine, IReadOnlyList<SongVoiceMatch> VoiceMatches, string? Error, int Queue = 0)
    {
        internal IReadOnlyList<string> Kinds { get; init; } = ["song"];

        internal bool Has(SongVoiceMatch match) => VoiceMatches.Contains(match);

        internal SingerState Singer => new(State, Queue, VoiceMatches) { Kinds = Kinds };

        internal static SingingService Parse(IReadOnlyList<JsonElement> answer)
        {
            var service = answer.Count == 1 ? answer[0] : default;
            string? Text(string name) => service.ValueKind == JsonValueKind.Object && service.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var singer = SingerState.Parse(service);
            return new(Text("state"), Text("engine"), singer.VoiceMatches, Text("error"), singer.Waiting) { Kinds = singer.Kinds };
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

    /// <summary>Whether songs can be made now, across the singing pool: each computer's singing service is read at once.
    /// <see cref="SongMakerAvailability.Host"/> is the computer the next song's music would go to (<see cref="SingingPool.Pick"/>);
    /// the voice matches are those set up on any computer that sings.</summary>
    public async Task<SongMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var members = Members(dataDirectory);
        if (members.Count == 0) return SongMakerAvailability.Unavailable("Set up Singing on Companion > Singing first.");
        var hosts = members.Select(m => m.Host).DistinctBy(h => h.HostId).ToArray();
        var read = await Task.WhenAll(hosts.Select(async host =>
        {
            try { return (host.HostId, Service: await ReadServiceAsync(host.Pairing, cancellationToken).ConfigureAwait(false), Problem: (string?)null); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { return (host.HostId, Service: (SingingService?)null, Problem: error.Message); }
        })).ConfigureAwait(false);
        var singing = read.Where(r => r.Service is { State: not "not_provisioned" }).ToArray();
        if (singing.Length == 0)
        {
            if (read.FirstOrDefault(r => r.Service is not null) is { Service: not null } unprovisioned)
                return SongMakerAvailability.Unavailable($"Singing on {unprovisioned.HostId} isn't ready (not set up yet).");
            return read[0].Problem is { } problem
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

    public async Task<SongResult> GenerateAsync(SongRequest request, IProgress<SongProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        progress?.Report(new SongProgress(SongStage.Queued, 0));
        var music = Members(dataDirectory);
        if (music.Count == 0)
            throw new SongException(SongErrorCodes.Unavailable, "No computer is set up for Singing. Set it up on Companion > Singing.");
        var off = VoiceMatchOff(dataDirectory);
        var matchers = off ? [] : Members(dataDirectory, PoolAreas.VoiceMatch);
        if (!off && matchers.Count == 0)
            throw new SongException(SongErrorCodes.Unavailable,
                "No computer in Companion > Singing's voice match list is paired with this PC. Add one, or turn the list off.");
        try
        {
            // 1. The music, on the singing pool. A computer whose Martlet is too old to make it apart makes the whole song.
            var whole = false;
            var written = await SingingPool.RunAsync(WorkQueue.Shared, SingingPool.Lane, music, m => m.Member.Key,
                (m, t) => LookAsync(m.Host, t), SingingPool.MusicRefusal, (m, state, t) =>
                {
                    var song = request with { Quality = SingingPool.Quality(m.Member) };
                    if (state?.Splits == true || matchers.Count == 0 && state is null)
                        return OnHostAsync(m.Host, "writes the music", music,
                            (connection, route) => connection.MakeMusicAsync(route, song,
                                new StageProgress(progress, 0, matchers.Count == 0 ? 1 : MusicShare, last: matchers.Count == 0), t), t);
                    whole = true;
                    return OnHostAsync(m.Host, "makes the whole song (its Martlet is too old to make the music apart)", music,
                        (connection, route) => connection.MakeSongAsync(route, song with { VoiceMatch = SongVoiceMatch.SoulX },
                            RecordingAsync, progress, t), t);
                }, WorkSharingRoster.Classify, null, cancellationToken, m => m.Host.HostId).ConfigureAwait(false);
            if (whole || matchers.Count == 0) return written.Result;

            // 2. The voice match, on the voice match pool (each computer with its own model, SoulX-Singer where it lacks VevoSing).
            var vocals = written.Result.Vocals;
            var matched = await SingingPool.RunAsync(WorkQueue.Shared, SingingPool.MatchLane, matchers, m => m.Member.Key,
                (m, t) => LookAsync(m.Host, t), SingingPool.MatchRefusal, (m, state, t) =>
                    OnHostAsync(m.Host, "matches the voice", matchers,
                        (connection, route) => connection.MatchVoiceAsync(route, request.VoiceId, SingingPool.MatchWith(m.Member, state),
                            vocals, RecordingAsync, new StageProgress(progress, MusicShare, 1 - MusicShare, last: true), t), t),
                WorkSharingRoster.Classify, null, cancellationToken, m => m.Host.HostId).ConfigureAwait(false);
            return SongVoice.WithVoice(written.Result, matched.Result);
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

        // One job on one computer: its connection and song route, the log line, and its failures in a pool's words.
        async Task<T> OnHostAsync<T>(PairedHost host, string what, IReadOnlyList<(PoolMember Member, PairedHost Host)> pool,
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
                ErrorLog.Info($"Singing: {host.HostId} {what} (number {pool.Select(m => m.Host.HostId).ToList().IndexOf(host.HostId) + 1} " +
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

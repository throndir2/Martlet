using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>The Singing page's choices on this PC (singing.json in the data directory): whether Martlet sings at all
/// (<paramref name="Off"/>: Companion › Singing's Off choice; the role stays set up), how the music is written and how the
/// singing is matched to the voice. A song job reads them when it starts. <paramref name="Host"/> is the paired computer the
/// desktop last saw running Singing (written when it checks its computers, or when you choose a computer to sing on), so
/// <see cref="SongClient.IsSetUp"/> answers without the network.</summary>
internal sealed record SingingPreferences(SongQuality Quality = SongQuality.Fast, SongVoiceMatch VoiceMatch = SongVoiceMatch.SoulX,
    string? Host = null, bool Off = false)
{
    internal const string FileName = "singing.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Document(int Version, string Quality, string VoiceMatch, string? Host = null, bool? Off = null);

    /// <summary>The saved choices, or the defaults (on, Fast, SoulX) when none are saved or the file can't be read.</summary>
    internal static SingingPreferences Load(string dataDirectory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(dataDirectory, FileName));
            if (bytes.Length > 4_096) return new();
            var document = JsonSerializer.Deserialize<Document>(bytes, Json);
            if (document is not { Version: 1 }) return new();
            return new(document.Quality == "high_quality" ? SongQuality.HighQuality : SongQuality.Fast,
                document.VoiceMatch == "vevosing" ? SongVoiceMatch.VevoSing : SongVoiceMatch.SoulX,
                document.Host is { Length: > 0 and <= 128 } host ? host : null, document.Off == true);
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
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document(1,
                Quality == SongQuality.HighQuality ? "high_quality" : "fast",
                VoiceMatch == SongVoiceMatch.VevoSing ? "vevosing" : "soulx", Host, Off ? true : null), Json));
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
/// for each song. Songs go through the singing pool (<see cref="SingingPool"/>): the computer Martlet sings on
/// (<see cref="SingingPreferences.Host"/>) first, then the others that run Singing (<see cref="Members"/>). A computer that
/// sings another song, doesn't answer or lacks the song's voice match is passed over for the next; when all are busy the
/// song waits in line on the one with the fewest songs before it. Hosts that friends share are never used. Songs are sung in
/// a voice of the shared voice library (usually <see cref="SpeakingVoiceId"/>); when the computer that takes the song lacks
/// the voice's recording, it is sent once from this PC's voice store. Setting <c>MARTLET_SINGING_FIXTURE=1</c> before
/// Martlet starts makes Singing use the FIXTURE - NOT AI <see cref="FixtureSongMaker"/> instead (for automated checks).
/// </summary>
internal sealed class SongClient(string dataDirectory) : ISongMaker
{
    internal const string FixtureVariable = "MARTLET_SINGING_FIXTURE";

    /// <summary>The song maker Martlet uses: this client, or the fixture when <see cref="FixtureVariable"/> is 1.</summary>
    internal static ISongMaker For(string dataDirectory) =>
        Environment.GetEnvironmentVariable(FixtureVariable) == "1" ? new FixtureSongMaker(TimeSpan.FromMilliseconds(400)) : new SongClient(dataDirectory);

    /// <summary>Whether singing is set up, without the network: not turned off on Companion › Singing, and the fixture is on, or
    /// a computer still paired with this PC ran Singing when the desktop last checked (<see cref="SingingPreferences.Host"/>).
    /// Cheap enough to ask for every reply.</summary>
    internal static bool IsSetUp(string dataDirectory)
    {
        var preferences = SingingPreferences.Load(dataDirectory);
        if (preferences.Off) return false;
        if (Environment.GetEnvironmentVariable(FixtureVariable) == "1") return true;
        if (preferences.Host is not { } host) return false;
        try { return HostRegistry.Load(dataDirectory).Any(h => h.HostId == host && !h.Shared); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The shared-library ID of the voice Martlet speaks with on this PC (its reference revision), or null.</summary>
    internal static string? SpeakingVoiceId(string dataDirectory) => F5Voices.Applied(dataDirectory)?.ReferenceRevision;

    /// <summary>The singing pool from this PC's files, without the network: the owner's own paired computers (never hosts
    /// friends share) in the order songs try them (<see cref="SingingPool.Order"/>): the computer Martlet sings on, then those
    /// the shared plan (cluster.json) says run Singing, then the other paired computers.</summary>
    internal static IReadOnlyList<PairedHost> Members(string dataDirectory)
    {
        PairedHost[] hosts;
        try { hosts = [.. HostRegistry.Load(dataDirectory).Where(h => !h.Shared)]; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
        var plan = ClusterSync.LoadPlan(dataDirectory);
        var singers = plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == HostRoles.Singing))
            .Select(n => new WorkPlace(n.HostId, false, plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))
            .ToArray();
        var order = SingingPool.Order(SingingPreferences.Load(dataDirectory).Host, [.. hosts.Select(h => h.HostId)], singers);
        return [.. order.Select(id => hosts.First(h => h.HostId == id))];
    }

    /// <summary>What a computer's singing service reports through its gateway: its state (ready, busy, loading or
    /// not_provisioned), its engine (song, or fixture), the voice matches set up there (soulx, and vevosing once added) and
    /// how many songs wait in its line.</summary>
    internal sealed record SingingService(string? State, string? Engine, IReadOnlyList<SongVoiceMatch> VoiceMatches, string? Error, int Queue = 0)
    {
        internal bool Has(SongVoiceMatch match) => VoiceMatches.Contains(match);

        internal SingerState Singer => new(State, Queue, VoiceMatches);

        internal static SingingService Parse(IReadOnlyList<JsonElement> answer)
        {
            var service = answer.Count == 1 ? answer[0] : default;
            string? Text(string name) => service.ValueKind == JsonValueKind.Object && service.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var singer = SingerState.Parse(service);
            return new(Text("state"), Text("engine"), singer.VoiceMatches, Text("error"), singer.Waiting);
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
    /// <see cref="SongMakerAvailability.Host"/> is the computer the next song would go to (<see cref="SingingPool.Pick"/>
    /// for the saved voice match); the voice matches are those set up on any computer that sings.</summary>
    public async Task<SongMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var members = Members(dataDirectory);
        if (members.Count == 0) return SongMakerAvailability.Unavailable("Set up Singing on Companion > Voice first.");
        var read = await Task.WhenAll(members.Select(async host =>
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
                : SongMakerAvailability.Unavailable("Set up Singing on Companion > Voice first.");
        }
        var states = read.ToDictionary(r => r.HostId, r => r.Service?.Singer, StringComparer.Ordinal);
        var order = members.Select(h => h.HostId).ToArray();
        var matches = singing.SelectMany(r => r.Service!.VoiceMatches).Distinct().Order().ToArray();
        var wanted = SingingPreferences.Load(dataDirectory).VoiceMatch;
        var next = SingingPool.Pick(order, states, matches.Contains(wanted) ? wanted : SongVoiceMatch.SoulX)?.HostId ?? singing[0].HostId;
        return new SongMakerAvailability(true, null, next, [SongQuality.Fast, SongQuality.HighQuality], matches,
            read.First(r => r.HostId == next).Service?.Engine == "fixture");
    }

    public async Task<SongResult> GenerateAsync(SongRequest request, IProgress<SongProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        progress?.Report(new SongProgress(SongStage.Queued, 0));
        var members = Members(dataDirectory);
        if (members.Count == 0)
            throw new SongException(SongErrorCodes.Unavailable, "No computer is set up for Singing. Set it up on Companion > Voice.");
        try
        {
            var sung = await SingingPool.RunAsync(WorkQueue.Shared, members, h => h.HostId, LookAsync, SingAsync, request.VoiceMatch,
                WorkSharingRoster.Classify, null, cancellationToken).ConfigureAwait(false);
            return sung.Result;
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

        async Task<SongResult> SingAsync(PairedHost host, CancellationToken token)
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
                ErrorLog.Info($"Singing: {host.HostId} makes the song (number {members.Select(h => h.HostId).ToList().IndexOf(host.HostId) + 1} " +
                    $"of {members.Count} in the singing pool).");
                progress?.Report(new SongProgress(SongStage.Queued, 0) { Host = host.HostId });
                try
                {
                    return await connection.MakeSongAsync(route, request, RecordingAsync, progress, token).ConfigureAwait(false);
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
}

using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>The Singing card's choices on this PC (singing.json in the data directory): how the music is written and how
/// the singing is matched to the voice. A song job reads them when it starts. <paramref name="Host"/> is the paired computer
/// the desktop last saw running Singing (written when it checks its computers), so <see cref="SongClient.IsSetUp"/> answers
/// without the network.</summary>
internal sealed record SingingPreferences(SongQuality Quality = SongQuality.Fast, SongVoiceMatch VoiceMatch = SongVoiceMatch.SoulX,
    string? Host = null)
{
    internal const string FileName = "singing.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Document(int Version, string Quality, string VoiceMatch, string? Host = null);

    /// <summary>The saved choices, or the defaults (Fast, SoulX) when none are saved or the file can't be read.</summary>
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
                document.Host is { Length: > 0 and <= 128 } host ? host : null);
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
                VoiceMatch == SongVoiceMatch.VevoSing ? "vevosing" : "soulx", Host), Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>
/// The desktop's <see cref="ISongMaker"/>: makes songs with the singing role of a paired Martlet host (route
/// <c>martlet.gateway.song.v1</c>) through its pinned gateway, reading the pairing secret from Windows Credential Manager
/// for each song. The first paired host that offers the route sings. Songs are sung in a voice of the shared voice library
/// (usually <see cref="SpeakingVoiceId"/>); when that host lacks the voice's recording, it is sent once from this PC's voice
/// store. Setting <c>MARTLET_SINGING_FIXTURE=1</c> before Martlet starts makes Singing use the FIXTURE - NOT AI
/// <see cref="FixtureSongMaker"/> instead (for automated checks).
/// </summary>
internal sealed class SongClient(string dataDirectory) : ISongMaker
{
    internal const string FixtureVariable = "MARTLET_SINGING_FIXTURE";

    /// <summary>The song maker Martlet uses: this client, or the fixture when <see cref="FixtureVariable"/> is 1.</summary>
    internal static ISongMaker For(string dataDirectory) =>
        Environment.GetEnvironmentVariable(FixtureVariable) == "1" ? new FixtureSongMaker(TimeSpan.FromMilliseconds(400)) : new SongClient(dataDirectory);

    /// <summary>Whether singing is set up, without the network: the fixture is on, or a computer still paired with this PC
    /// ran Singing when the desktop last checked (<see cref="SingingPreferences.Host"/>). Cheap enough to ask for every reply.</summary>
    internal static bool IsSetUp(string dataDirectory)
    {
        if (Environment.GetEnvironmentVariable(FixtureVariable) == "1") return true;
        if (SingingPreferences.Load(dataDirectory).Host is not { } host) return false;
        try { return HostRegistry.Load(dataDirectory).Any(h => h.HostId == host); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The shared-library ID of the voice Martlet speaks with on this PC (its reference revision), or null.</summary>
    internal static string? SpeakingVoiceId(string dataDirectory) => F5Voices.Applied(dataDirectory)?.ReferenceRevision;

    public async Task<SongMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var found = await FindAsync(cancellationToken).ConfigureAwait(false);
            if (found is not { } singing) return SongMakerAvailability.Unavailable("Set up Singing on Companion > Voice first.");
            using var connection = singing.Connection;
            IReadOnlyList<JsonElement> status;
            try
            {
                status = await connection.SongOperationAsync(singing.Route, new Dictionary<string, object> { ["operation"] = "status" },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Audio2FaceHostException error)
            {
                return SongMakerAvailability.Unavailable($"Singing on {singing.Host.HostId} isn't ready ({error.Message}).");
            }
            var service = status.Count == 1 ? status[0] : default;
            var matches = new List<SongVoiceMatch> { SongVoiceMatch.SoulX };
            if (service.ValueKind == JsonValueKind.Object && service.TryGetProperty("voice_matches", out var list) &&
                list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(m => m.GetString() == "vevosing"))
                matches.Add(SongVoiceMatch.VevoSing);
            var fixture = service.ValueKind == JsonValueKind.Object && service.TryGetProperty("engine", out var engine) &&
                engine.GetString() == "fixture";
            return new SongMakerAvailability(true, null, singing.Host.HostId, [SongQuality.Fast, SongQuality.HighQuality], matches, fixture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            return SongMakerAvailability.Unavailable($"Singing isn't reachable ({error.Message}).");
        }
    }

    public async Task<SongResult> GenerateAsync(SongRequest request, IProgress<SongProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        progress?.Report(new SongProgress(SongStage.Queued, 0));
        (PairedHost Host, HostRoute Route, Audio2FaceHostConnection Connection)? found;
        try { found = await FindAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            throw new SongException(SongErrorCodes.Unavailable, $"Singing isn't reachable ({error.Message}).", error);
        }
        if (found is not { } singing)
            throw new SongException(SongErrorCodes.Unavailable, "No computer is set up for Singing. Set it up on Companion > Voice.");
        using var connection = singing.Connection;
        try
        {
            return await connection.MakeSongAsync(singing.Route, request, RecordingAsync, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new SongException(SongErrorCodes.Unavailable, $"{singing.Host.HostId} stopped answering ({error.Message}).", error);
        }

        async Task<byte[]?> RecordingAsync(CancellationToken token)
        {
            var (local, _) = F5Voices.Local(dataDirectory, "f5-host");
            if (!local.TryGetValue(request.VoiceId, out var snapshot)) return null;
            return await F5Voices.ReadAudioAsync(dataDirectory, snapshot, snapshot.AudioSha256, token).ConfigureAwait(false);
        }
    }

    /// <summary>The first paired host whose gateway offers the singing route, connected (the caller disposes the
    /// connection), or null.</summary>
    private async Task<(PairedHost Host, HostRoute Route, Audio2FaceHostConnection Connection)?> FindAsync(CancellationToken token)
    {
        foreach (var host in HostRegistry.Load(dataDirectory))
        {
            Audio2FaceHostConnection? connection = null;
            try
            {
                connection = ClusterSync.Connect(host.Pairing);
                var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
                if (routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.SongRouteId) is { } route)
                {
                    var found = (host, route, connection);
                    connection = null;
                    return found;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { }
            finally { connection?.Dispose(); }
        }
        return null;
    }
}

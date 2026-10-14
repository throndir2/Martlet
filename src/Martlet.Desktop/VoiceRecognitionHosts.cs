using System.IO;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Speakers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>
/// The Voice recognition list on this PC (Companion › People, <see cref="VoiceRecognitionPool"/>, pools.json: shared by all your
/// computers) and the client for the computers in it: their <c>voice-recognition</c> role through their gateways (route
/// <see cref="Audio2FaceHostConnection.VoiceRecognitionRouteId"/>). The list is read from local files only, again only when a file
/// changed (<see cref="WorkSharingRoster"/>), never the network. Until the owner changes it there is no saved list, which means
/// this PC's processor, as before: nothing is saved for a new computer, so it never shares a list over another computer's choice.
/// Each computer's connection is kept between utterances and dropped when that computer fails. The audio goes to a computer as
/// 16 kHz mono PCM16, is analyzed in memory there and is not kept; every answer is checked (<see cref="SpeakerAnswers"/>).
/// </summary>
internal sealed class VoiceRecognitionHosts(string? directory) : IDisposable
{
    internal const string StateFile = "voice-recognition-state.json";
    private readonly object gate = new();
    private readonly Dictionary<string, (HostRoute Route, Audio2FaceHostConnection Connection)> open = new(StringComparer.Ordinal);

    /// <summary>The saved list, or none (this PC's processor).</summary>
    internal static PoolList? Load(string? directory) => WorkSharingRoster.Shared.Pool(directory, PoolAreas.VoiceRecognition);

    /// <summary>The places companion PC <paramref name="device"/> tries, first to last, ending with this PC's processor.</summary>
    internal static IReadOnlyList<VoiceRecognitionTarget> Targets(string? directory, string device)
    {
        var hosts = directory is null ? [] : WorkSharingRoster.Shared.Hosts(directory);
        return VoiceRecognitionPool.Targets(Load(directory), device, id => hosts.Any(h => h.HostId == id));
    }

    /// <summary>A failure's meaning for the queue: busy is busy; anything else passes the utterance on (this PC can always do it).</summary>
    internal static WorkRefusal Classify(Exception error) => WorkSharingRoster.Classify(error) is var refusal && refusal == WorkRefusal.None
        ? WorkRefusal.Unavailable : refusal;

    /// <summary>Recognizes <paramref name="samples"/> on <paramref name="target"/>'s computer. A computer that isn't paired here,
    /// doesn't answer or doesn't run the role refuses as unavailable; a bad answer is a failure of that computer.</summary>
    internal async Task<SpeakerAnalysis> AnalyzeAsync(VoiceRecognitionTarget target, float[] samples, CancellationToken token)
    {
        var host = (directory is null ? [] : WorkSharingRoster.Shared.Hosts(directory)).FirstOrDefault(h => h.HostId == target.HostId)
            ?? throw new Audio2FaceHostException("host.unreachable", $"{target.HostId} isn't paired with this PC.");
        var pcm = SpeakerAnswers.Pcm16(samples);
        var (route, connection) = await OpenAsync(host, token).ConfigureAwait(false);
        try
        {
            var answer = await WorkSharingRoster.Shared.WatchedOnce(host.HostId, "voice recognition",
                connection.VoiceRecognitionAnalyzeAsync(route, pcm, token)).ConfigureAwait(false);
            try { return SpeakerAnswers.Analysis(answer, samples.Length / (double)SpeakerEngine.SampleRate); }
            catch (SherpaException error) { throw new Audio2FaceHostException("response.invalid", $"{host.HostId}: {error.Message}"); }
        }
        catch (Exception error) when (error is not (OperationCanceledException or Audio2FaceHostException { Code: "job.busy" or "job.preempted" }))
        {
            Drop(host.HostId, connection);
            throw;
        }
        finally { Array.Clear(pcm); }
    }

    /// <summary>The role's state on <paramref name="host"/>: "ready" or "loading", where it runs ("cuda" or "cpu") and its model.</summary>
    internal static async Task<(string State, string? Provider, string? Model)> StatusAsync(PairedHost host, CancellationToken token)
    {
        using var client = new VoiceRecognitionHosts(null);
        var (route, connection) = await client.OpenAsync(host, token).ConfigureAwait(false);
        return SpeakerAnswers.Status(await connection.VoiceRecognitionStatusAsync(route, token).ConfigureAwait(false));
    }

    // The computer's kept connection and its route; a new one when there is none. A computer that doesn't answer, or doesn't run
    // the role, refuses as unavailable (host.unreachable, worker.unavailable), so the queue passes it over.
    private async Task<(HostRoute Route, Audio2FaceHostConnection Connection)> OpenAsync(PairedHost host, CancellationToken token)
    {
        lock (gate)
            if (open.TryGetValue(host.HostId, out var known)) return known;
        Audio2FaceHostConnection? connection = null;
        try
        {
            connection = ClusterSync.Connect(host.Pairing);
            var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
            var route = routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.VoiceRecognitionRouteId) ?? throw new Audio2FaceHostException(
                "worker.unavailable", $"{host.HostId} doesn't run the Voice recognition role. Add it there on Devices.");
            lock (gate)
            {
                if (open.TryGetValue(host.HostId, out var raced)) return raced;
                open[host.HostId] = (route, connection);
            }
            var kept = (route, connection);
            connection = null;
            return kept;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error) && error is not Audio2FaceHostException { Code: "worker.unavailable" })
        {
            throw new Audio2FaceHostException("host.unreachable", $"{host.HostId} isn't reachable ({error.Message}).");
        }
        finally { connection?.Dispose(); }
    }

    private void Drop(string host, Audio2FaceHostConnection connection)
    {
        lock (gate)
            if (open.TryGetValue(host, out var kept) && ReferenceEquals(kept.Connection, connection)) open.Remove(host);
        connection.Dispose();
    }

    public void Dispose()
    {
        (HostRoute, Audio2FaceHostConnection Connection)[] all;
        lock (gate)
        {
            all = [.. open.Values];
            open.Clear();
        }
        foreach (var kept in all) kept.Connection.Dispose();
    }

    // ---------- this PC's state, for MCP ----------

    /// <summary>Writes how recognition is doing on this PC to <see cref="StateFile"/> (this PC only, never shared; no audio, no
    /// voiceprints, no names): this PC's engine, the last recognition, the computers tried, the places it tries now and how many
    /// voiceprints of voices it couldn't place yet wait (<paramref name="unplaced"/>, <see cref="Martlet.Core.Speakers.UnplacedVoices"/>).
    /// MCP's voices_status reads it.</summary>
    internal static void WriteState(string directory, bool enabled, VoiceEngineStatus engine, bool loaded, VoiceRecognitionLast? last,
        IReadOnlyList<VoiceRecognitionHost> hosts, IReadOnlyList<VoiceRecognitionTarget> tries, string? problem, int unplaced = 0)
    {
        var state = new
        {
            schema_version = 1, updated_at = DateTimeOffset.UtcNow, enabled, problem, unplaced,
            engine = new
            {
                state = engine.State.ToString().ToLowerInvariant(), loaded, engine.Problem, engine.Failures, failed_at = engine.FailedAt,
                retry_at = engine.RetryAt, worked_at = engine.WorkedAt, engine.Reloads,
                ms_per_second = engine.MillisecondsPerSecond is { } rate ? Math.Round(rate, 1) : (double?)null
            },
            last = last is null ? null : new
            {
                last.Key, last.Where, last.Milliseconds, last.Seconds, last.Raced, last.Passed, last.At
            },
            hosts = hosts.Select(h => new
            {
                host_id = h.HostId, answered_at = h.AnsweredAt, h.Milliseconds, h.Problem, rest_until = h.RestUntil
            }),
            tries = tries.Select(t => new { t.Key, host_id = t.HostId, t.Fallback })
        };
        try
        {
            var path = Path.Combine(directory, StateFile);
            var temporary = Path.Combine(directory, $"voice-recognition-state.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, StateJson));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true
    };
}

using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;

namespace Martlet.F5;

/// <summary>What <see cref="F5SharedVoices.ReconcileAsync"/> did: the library with any voices this computer had that it
/// lacked, how many recordings were copied in and removed, the voices still waiting for their recording, and each live
/// voice's snapshot in the store (by voice ID).</summary>
public sealed record F5SharedVoicesResult(SpeakingVoiceLibrary Library, int Added, int Removed, int Waiting,
    IReadOnlyDictionary<string, F5ReferenceSnapshot> Local);

/// <summary>Keeps a computer's F5 reference store, its working copy of every recording, in step with the shared
/// <see cref="SpeakingVoiceLibrary"/>: live voices it lacks are copied in (a starter voice's recording from Martlet itself,
/// any other from a computer that has it), removed voices are deleted, voices only the store has (added before voices
/// were shared) join the library, and recordings Martlet no longer ships leave both. The store is held only while it
/// changes, never while recordings are fetched.</summary>
public static class F5SharedVoices
{
    /// <summary>The starter voices as library entries' content: name, transcript, recording SHA-256, length and source.</summary>
    public static IEnumerable<(string Name, string Transcript, string AudioSha256, int DurationMilliseconds, string? Note)> Starters =>
        F5BundledVoices.All.Select(voice => (voice.Name, voice.Transcript, voice.AudioSha256, voice.Check().DurationMilliseconds,
            (string?)voice.Description));

    /// <summary>The voice's store snapshot ID for a library voice ID (they are the same reference revision).</summary>
    public static string Id(F5ReferenceSnapshot snapshot) => snapshot.ReferenceRevision;

    public static SpeakingVoiceRights Rights(F5VoiceRightsBasis basis) => basis switch
    {
        F5VoiceRightsBasis.OwnVoice => SpeakingVoiceRights.OwnVoice,
        F5VoiceRightsBasis.ExplicitPermission => SpeakingVoiceRights.ExplicitPermission,
        _ => SpeakingVoiceRights.PublishedSample
    };

    public static F5VoiceRightsBasis Basis(SpeakingVoiceRights rights) => rights switch
    {
        SpeakingVoiceRights.OwnVoice => F5VoiceRightsBasis.OwnVoice,
        SpeakingVoiceRights.ExplicitPermission => F5VoiceRightsBasis.ExplicitPermission,
        _ => F5VoiceRightsBasis.PublishedSample
    };

    /// <summary>Each voice's latest snapshot for <paramref name="destination"/>, by voice ID, without recordings Martlet no
    /// longer ships (<see cref="F5BundledVoices.Retired"/>). When two presets hold the same recording and words, the older one
    /// counts.</summary>
    public static IReadOnlyDictionary<string, F5ReferenceSnapshot> Snapshots(F5ReferenceStoreInspection inspection, string destination) =>
        inspection.Presets
            .Select(p => p.Snapshots.LastOrDefault(s => s.Rights.ProcessingDestinationId == destination))
            .OfType<F5ReferenceSnapshot>()
            .Where(s => !F5BundledVoices.IsRetired(s.AudioSha256))
            .GroupBy(Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CreatedAtUtc).First(), StringComparer.Ordinal);

    /// <summary>The list without the recordings Martlet no longer ships: each such live voice is removed, so the removal wins
    /// on every computer and an older copy of the list can't bring it back.</summary>
    public static SpeakingVoiceLibrary WithoutRetired(SpeakingVoiceLibrary library, string by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        foreach (var voice in library.Live.Where(v => F5BundledVoices.IsRetired(v.AudioSha256!)))
            library = library.Remove(voice.Id, by, now);
        return library;
    }

    /// <summary>Brings the store in <paramref name="storeDirectory"/> in step with <paramref name="library"/>.
    /// <paramref name="fetch"/> returns a recording by SHA-256 from another computer (null when none has it yet); recordings
    /// are staged in <paramref name="stagingDirectory"/>. A removed voice the store still applies, or one in
    /// <paramref name="keep"/> (a voice a route still speaks with), stays until another voice is used. Recordings Martlet no
    /// longer ships leave the list and, on the same terms, the store.</summary>
    public static async Task<F5SharedVoicesResult> ReconcileAsync(string storeDirectory, string stagingDirectory, string destination,
        SpeakingVoiceLibrary library, Func<string, CancellationToken, Task<byte[]?>> fetch, string by, DateTimeOffset now,
        IReadOnlySet<string>? keep = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(fetch);
        library = WithoutRetired(library, by, now);
        IReadOnlyDictionary<string, F5ReferenceSnapshot> local;
        Guid? applied;
        Guid[] retired;
        using (var store = F5ReferencePresetStore.Open(storeDirectory, cancellationToken: cancellationToken))
        {
            var inspection = store.Inspect();
            local = Snapshots(inspection, destination);
            applied = inspection.AppliedPresetId;
            retired = inspection.Presets.Where(p => p.Id != applied && p.Snapshots.Count > 0 &&
                    p.Snapshots.All(s => F5BundledVoices.IsRetired(s.AudioSha256) && keep?.Contains(Id(s)) != true))
                .Select(p => p.Id).ToArray();
            // Voices only this computer has (added before voices were shared) join the library.
            foreach (var (id, snapshot) in local)
            {
                if (library.Find(id) is not null || library.Live.Count >= SpeakingVoiceLibrary.MaximumVoices) continue;
                using var lease = await store.AcquireForPreviewAsync(snapshot.PresetId, snapshot.ReferenceRevision, cancellationToken);
                var name = SpeakingVoiceLibrary.IsName(snapshot.PresetName) ? snapshot.PresetName : "Voice";
                library = library.Add(name, lease.Reference.Transcript, snapshot.AudioSha256, snapshot.AudioFormat.DurationMilliseconds,
                    Rights(snapshot.Rights.Basis), by, snapshot.CreatedAtUtc < now ? snapshot.CreatedAtUtc : now,
                    F5BundledVoices.ForAudio(snapshot.AudioSha256)?.Description);
            }
        }

        var missing = library.Live.Where(v => !local.ContainsKey(v.Id)).ToArray();
        var removed = library.Voices.Where(v => v.Removed && local.TryGetValue(v.Id, out var s) && s.PresetId != applied &&
            keep?.Contains(v.Id) != true).Select(v => local[v.Id]).ToArray();
        var fetched = new List<(SpeakingVoice Voice, string Path)>();
        var waiting = 0;
        try
        {
            foreach (var voice in missing)
            {
                var audio = F5BundledVoices.ForAudio(voice.AudioSha256!) is { } starter ? starter.ReadAudio()
                    : await fetch(voice.AudioSha256!, cancellationToken);
                if (audio is null || audio.Length > SpeakingVoiceLibrary.MaximumAudioBytes ||
                    Convert.ToHexStringLower(SHA256.HashData(audio)) != voice.AudioSha256)
                {
                    waiting++;
                    continue;
                }
                Directory.CreateDirectory(stagingDirectory);
                var path = Path.Combine(stagingDirectory, voice.AudioSha256 + ".wav");
                await File.WriteAllBytesAsync(path, audio, cancellationToken);
                fetched.Add((voice, path));
            }
            if (fetched.Count == 0 && removed.Length == 0 && retired.Length == 0) return new(library, 0, 0, waiting, Live(library, local));

            var added = 0;
            var deleted = 0;
            var current = new Dictionary<string, F5ReferenceSnapshot>(local, StringComparer.Ordinal);
            using var store = F5ReferencePresetStore.Open(storeDirectory, cancellationToken: cancellationToken);
            foreach (var (voice, path) in fetched)
            {
                try
                {
                    current[voice.Id] = await store.SnapshotAsync(new()
                    {
                        PresetName = voice.Name!, AbsoluteSourcePath = path, Transcript = voice.Transcript!,
                        Rights = new()
                        {
                            AcknowledgementId = Guid.NewGuid(), Basis = Basis(voice.Rights!.Value),
                            StatementVersion = F5ReferenceLimits.RightsStatementVersion, ProcessingDestinationId = destination,
                            AcknowledgedAtUtc = now, Confirmed = true
                        }
                    }, cancellationToken);
                    added++;
                }
                catch (F5Exception error) when (error.Failure is F5Failure.LimitExceeded or F5Failure.InvalidAudio)
                {
                    waiting++;
                }
            }
            foreach (var snapshot in removed)
            {
                try
                {
                    await store.DeleteAsync(snapshot.PresetId, cancellationToken);
                    current.Remove(Id(snapshot));
                    deleted++;
                }
                catch (F5Exception error) when (error.Failure is F5Failure.Conflict or F5Failure.NotFound) { }
            }
            foreach (var presetId in retired)
            {
                try
                {
                    await store.DeleteAsync(presetId, cancellationToken);
                    deleted++;
                }
                catch (F5Exception error) when (error.Failure is F5Failure.Conflict or F5Failure.NotFound) { }
            }
            return new(library, added, deleted, waiting, Live(library, current));
        }
        finally
        {
            foreach (var (_, path) in fetched)
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                if (Directory.Exists(stagingDirectory) && !Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
                    Directory.Delete(stagingDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, F5ReferenceSnapshot> Live(SpeakingVoiceLibrary library, IReadOnlyDictionary<string, F5ReferenceSnapshot> local) =>
        local.Where(pair => library.Find(pair.Key) is { Removed: false }).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Reads a voice's recording from the store, for sending it to a computer that lacks it.</summary>
    public static async Task<byte[]?> ReadAsync(string storeDirectory, F5ReferenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var store = F5ReferencePresetStore.Open(storeDirectory, cancellationToken: cancellationToken);
        using var lease = await store.AcquireForPreviewAsync(snapshot.PresetId, snapshot.ReferenceRevision, cancellationToken);
        return lease.Reference.Audio.ToArray();
    }

    /// <summary>Whether <paramref name="error"/> is one a reconcile pass reports rather than throws past.</summary>
    public static bool IsFailure(Exception error) => error is F5Exception or IOException or UnauthorizedAccessException or ContractException;
}

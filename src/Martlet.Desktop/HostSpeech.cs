using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.Credentials.Windows;
using Martlet.F5;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The voices Martlet speaks with. The shared list (speaking-voices.json next to the other local preferences,
/// <see cref="SpeakingVoiceLibrary"/>) names every voice and is the same on all of the owner's computers; Martlet.F5's
/// reference preset store (f5-voices) keeps this PC's copy of each recording with its transcript and voice-rights
/// confirmation, so the original file can be moved or deleted after it is added. A new list starts with the starter voices
/// (<see cref="F5BundledVoices"/>); after that they are voices like any other. Until the owner first uses or changes a voice
/// (or this PC shares voices with a paired host) nothing is written: the list is shown as it would start.</summary>
internal static class F5Voices
{
    internal const string DirectoryName = "f5-voices";
    internal const string LibraryFile = "speaking-voices.json";
    private const string StagingDirectoryName = "speaking-voices-incoming";
    // A recording the owner adds is staged here, already converted to the WAV Martlet keeps, while the voice store copies it.
    private const string AddingDirectoryName = "speaking-voices-adding";
    // Earlier versions staged bundled voices' clips and the retired F5-TTS example clip here.
    private static readonly string[] RetiredDirectoryNames = ["f5-bundled-voices", "f5-sample-voice"];

    /// <summary>The F5-TTS example clip earlier versions bundled; Martlet no longer ships it or starts with it.</summary>
    internal static bool IsRetiredSample(F5ReferenceSnapshot snapshot) => F5BundledVoices.IsRetiredSample(snapshot.AudioSha256);

    internal static string Directory(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    internal static F5ReferencePresetStore Open(string dataDirectory) => F5ReferencePresetStore.Open(Directory(dataDirectory));

    /// <summary>The saved shared list, or null when this PC has none yet. A damaged copy reads as none (the hosts' copies and
    /// this PC's recordings restore it).</summary>
    internal static SpeakingVoiceLibrary? LoadLibrary(string dataDirectory)
    {
        try { return SpeakingVoiceLibrary.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, LibraryFile))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return null; }
    }

    /// <summary>The list as it is, or as it would start (the starter voices) when this PC has none yet.</summary>
    internal static SpeakingVoiceLibrary View(string dataDirectory) =>
        LoadLibrary(dataDirectory) ?? SpeakingVoiceLibrary.Empty.Seed(F5SharedVoices.Starters);

    /// <summary>Merges <paramref name="library"/> into the saved list (so a change saved meanwhile is never lost) and returns
    /// what was saved. An unchanged list is not written again.</summary>
    internal static SpeakingVoiceLibrary Commit(string dataDirectory, SpeakingVoiceLibrary library)
    {
        var saved = LoadLibrary(dataDirectory);
        var merged = SpeakingVoiceLibrary.Merge(saved ?? View(dataDirectory), library);
        if (saved is not null && saved.Digest() == merged.Digest()) return saved;
        System.IO.Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, LibraryFile);
        var temporary = Path.Combine(dataDirectory, $"speaking-voices.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, merged.Write());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        foreach (var name in RetiredDirectoryNames)
        {
            try { System.IO.Directory.Delete(Path.Combine(dataDirectory, name), recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return merged;
    }

    /// <summary>Brings this PC's recordings in step with the shared list and saves it: starter voices and recordings
    /// <paramref name="fetch"/> finds on another computer are copied in, removed voices are deleted (except one still in use
    /// or in <paramref name="keep"/>) and recordings only this PC had join the list.</summary>
    internal static async Task<F5SharedVoicesResult> ReconcileAsync(string dataDirectory, string destination, string by,
        Func<string, CancellationToken, Task<byte[]?>>? fetch, IReadOnlySet<string>? keep, CancellationToken token)
    {
        var result = await F5SharedVoices.ReconcileAsync(Directory(dataDirectory), Path.Combine(dataDirectory, StagingDirectoryName),
            destination, View(dataDirectory), fetch ?? ((_, _) => Task.FromResult<byte[]?>(null)), by, DateTimeOffset.UtcNow, keep, token);
        return result with { Library = Commit(dataDirectory, result.Library) };
    }

    /// <summary>The voice to speak with on <paramref name="destination"/> when the owner has not picked one there: the voice
    /// chosen on all computers, else the one applied here, else the first in the list. Never the retired F5-TTS example clip,
    /// nor a recording <paramref name="engine"/> cannot clone (GPT-SoVITS needs 3-10 seconds). The choice is shared when none
    /// was. Throws <see cref="InvalidOperationException"/> when no voice can be used.</summary>
    internal static async Task<F5ReferenceSnapshot> DefaultAsync(string dataDirectory, string destination, CancellationToken token,
        SpeechEngine? engine = null)
    {
        var by = HostSetupCommands.SuggestedDeviceId();
        var result = await ReconcileAsync(dataDirectory, destination, by, null, null, token);
        var applied = Applied(dataDirectory)?.PresetId;
        var library = result.Library;
        bool Usable(F5ReferenceSnapshot s) => engine is null ||
            SpeechEngines.ReferenceProblem(engine, s.AudioFormat.DurationMilliseconds, library.Find(F5SharedVoices.Id(s))?.ClipMilliseconds) is null;
        var voice = (library.ChosenVoice is { } chosen && result.Local.TryGetValue(chosen.Id, out var picked) && Usable(picked) ? picked : null)
            ?? result.Local.Values.Where(s => s.PresetId == applied && Usable(s)).FirstOrDefault()
            ?? library.Live.Select(v => result.Local.GetValueOrDefault(v.Id)).OfType<F5ReferenceSnapshot>().FirstOrDefault(Usable)
            ?? throw new InvalidOperationException(engine is null || result.Local.Count == 0
                ? "Add a voice under Companion > Voice > Voices first."
                : $"None of your voices suits {engine.Name}. Add a recording it can use under Companion > Voice > Voices.");
        if (library.Chosen is null) Commit(dataDirectory, library.Choose(F5SharedVoices.Id(voice), by, DateTimeOffset.UtcNow));
        return voice;
    }

    /// <summary>This PC's recording of each voice for <paramref name="destination"/> (by voice ID) and the applied voice's
    /// snapshot (which may be the retired F5-TTS example clip, never in the list). Reads nothing when this PC has no
    /// recordings yet.</summary>
    internal static (IReadOnlyDictionary<string, F5ReferenceSnapshot> Local, F5ReferenceSnapshot? Applied) Local(string dataDirectory, string destination)
    {
        if (!System.IO.Directory.Exists(Directory(dataDirectory))) return (new Dictionary<string, F5ReferenceSnapshot>(), null);
        using var store = Open(dataDirectory);
        var inspection = store.Inspect();
        return (F5SharedVoices.Snapshots(inspection, destination), AppliedOf(inspection));
    }

    /// <summary>The applied voice's snapshot, including the retired F5-TTS example clip (which is never in the list).</summary>
    internal static F5ReferenceSnapshot? Applied(string dataDirectory)
    {
        if (!System.IO.Directory.Exists(Directory(dataDirectory))) return null;
        using var store = Open(dataDirectory);
        return AppliedOf(store.Inspect());
    }

    private static F5ReferenceSnapshot? AppliedOf(F5ReferenceStoreInspection inspection) =>
        inspection.Presets.FirstOrDefault(p => p.Id == inspection.AppliedPresetId)?.Snapshots
            .FirstOrDefault(s => s.ReferenceRevision == inspection.AppliedReferenceRevision);

    /// <summary>Removes a voice on every computer and deletes this PC's copy of its recording. The original file is not
    /// touched. The voice this PC speaks with cannot be removed (use another first).</summary>
    internal static async Task RemoveAsync(string dataDirectory, string destination, string voiceId, CancellationToken token)
    {
        var by = HostSetupCommands.SuggestedDeviceId();
        var result = await ReconcileAsync(dataDirectory, destination, by, null, null, token);
        result.Local.TryGetValue(voiceId, out var snapshot);
        if (snapshot is not null && Applied(dataDirectory)?.PresetId == snapshot.PresetId)
            throw new InvalidOperationException("Martlet speaks with this voice now. Use another voice first, then remove it.");
        Commit(dataDirectory, result.Library.Remove(voiceId, by, DateTimeOffset.UtcNow));
        if (snapshot is null) return;
        using var store = Open(dataDirectory);
        try { await store.DeleteAsync(snapshot.PresetId, token); }
        catch (F5Exception error) when (error.Failure == F5Failure.NotFound) { }
    }

    /// <summary>Adds a voice the owner recorded to the shared list; a voice made from several recordings passes where each
    /// lies in its joined recording (<paramref name="joined"/>).</summary>
    internal static SpeakingVoiceLibrary Add(string dataDirectory, F5ReferenceSnapshot snapshot, string transcript,
        JoinedVoiceRecording? joined = null) =>
        Commit(dataDirectory, View(dataDirectory).Add(snapshot.PresetName, transcript, snapshot.AudioSha256,
            snapshot.AudioFormat.DurationMilliseconds, F5SharedVoices.Rights(snapshot.Rights.Basis), HostSetupCommands.SuggestedDeviceId(),
            DateTimeOffset.UtcNow, clips: joined?.Clips, sampleRate: joined?.SampleRate));

    /// <summary>Copies a prepared recording (the exact WAV <see cref="Martlet.Audio.Windows.VoiceRecordingImport"/> made from the
    /// owner's file) into this PC's voice store. The WAV is staged in the data directory only while the store copies it.</summary>
    internal static async Task<F5ReferenceSnapshot> SnapshotAsync(string dataDirectory, string name, byte[] wave, string transcript,
        F5VoiceRightsAcknowledgement rights, CancellationToken token)
    {
        var staging = Path.Combine(dataDirectory, AddingDirectoryName);
        System.IO.Directory.CreateDirectory(staging);
        var staged = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(staged, wave, token);
            using var store = Open(dataDirectory);
            return await store.SnapshotAsync(new() { PresetName = name, AbsoluteSourcePath = staged, Transcript = transcript, Rights = rights },
                token);
        }
        finally
        {
            try
            {
                File.Delete(staged);
                if (!System.IO.Directory.EnumerateFileSystemEntries(staging).Any()) System.IO.Directory.Delete(staging);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Makes a voice the one Martlet speaks with on all of the owner's computers.</summary>
    internal static SpeakingVoiceLibrary Choose(string dataDirectory, string voiceId) =>
        Commit(dataDirectory, View(dataDirectory).Choose(voiceId, HostSetupCommands.SuggestedDeviceId(), DateTimeOffset.UtcNow));

    /// <summary>A voice's recording, for playing it: this PC's copy, or the starter clip Martlet carries.</summary>
    internal static async Task<byte[]> ReadAudioAsync(string dataDirectory, F5ReferenceSnapshot? snapshot, string audioSha256, CancellationToken token)
    {
        if (snapshot is not null)
        {
            using var store = Open(dataDirectory);
            using var lease = await store.AcquireForPreviewAsync(snapshot.PresetId, snapshot.ReferenceRevision, token);
            return lease.Reference.Audio.ToArray();
        }
        return (F5BundledVoices.ForAudio(audioSha256) ?? throw new InvalidOperationException("This voice is still being copied to this PC."))
            .ReadAudio();
    }

    /// <summary>Applies a snapshot as the voice to speak with and returns the settings record the TTS route keeps.</summary>
    internal static async Task<F5ReferenceSettings> ApplyAsync(string dataDirectory, F5ReferenceSnapshot snapshot, CancellationToken token)
    {
        using var store = Open(dataDirectory);
        var preview = await store.CreateApplyPreviewAsync(snapshot.PresetId, snapshot.ReferenceRevision, token);
        var receipt = await store.ApplyAsync(preview, preview.Authorize(F5ApplyDecision.Allow), token);
        return new()
        {
            SchemaVersion = 1, PresetId = snapshot.PresetId, PresetName = snapshot.PresetName,
            ReferenceRevision = snapshot.ReferenceRevision, AudioSha256 = snapshot.AudioSha256,
            TranscriptRevision = snapshot.TranscriptRevision, StoreRevision = receipt.StoreRevision,
            ProcessingDestinationId = snapshot.Rights.ProcessingDestinationId,
            RightsAcknowledgementId = snapshot.Rights.AcknowledgementId,
            RightsStatementVersion = snapshot.Rights.StatementVersion,
            AppliedAtUtc = DateTimeOffset.UtcNow, ApplyRevision = Guid.NewGuid()
        };
    }

    internal static string Describe(F5Exception error) => error.Failure switch
    {
        F5Failure.SourceMissing => "That recording is no longer where you chose it. Choose it again.",
        F5Failure.SourceChanged => "That recording changed while Martlet was reading it. Try again.",
        F5Failure.InvalidAudio => "That recording can't be used. Choose a clear recording of 1 to 30 seconds.",
        F5Failure.RightsRequired => "Confirm that you may use this voice.",
        F5Failure.Busy => "Martlet is busy with your voices. Try again in a moment.",
        F5Failure.LimitExceeded => "The voice list is full. Remove one you no longer use first.",
        F5Failure.Conflict => "Martlet speaks with this voice now. Switch to another voice first, then remove it.",
        F5Failure.NotFound => "That voice is no longer in your list.",
        F5Failure.CorruptStore => "Martlet's copy of this voice is damaged. Remove it and add the recording again.",
        _ => $"Couldn't use this voice ({error.Failure})."
    };
}

/// <summary>Speaks reply segments with a paired host's voice through its pinned gateway: reads the route's reference voice
/// from the F5 preset store and the pairing secret from Windows Credential Manager for each segment. Devices › Sharing work:
/// when that computer is busy with another companion PC's voice, the segment goes to the next paired computer running the
/// same voice engine (<see cref="WorkSharingRoster"/>), or waits for whichever frees first.</summary>
internal sealed class HostSpeechClient(string dataDirectory) : IHostSpeechClient
{
    public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
        long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reference = await ReadReferenceAsync(target, cancellationToken).ConfigureAwait(false);
        var targets = Targets(target);
        await using var frames = WorkQueue.Shared.StreamAsync(WorkSharingJobs.Speaking, targets, t => t.HostId,
                (t, token) => SpeakAsync(t, reference, input, ids, epoch, deadline, token), WorkSharingRoster.Classify, deadline, null,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await Guard(() => frames.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
            yield return frames.Current;
    }

    // The route's own computer first, then the others that run the same voice engine, in Devices › Sharing work's order.
    private IReadOnlyList<HostSpeechTarget> Targets(HostSpeechTarget target)
    {
        if (SpeechEngines.ForRoute(target.RouteId) is not { } engine) return [target];
        return [.. WorkSharingRoster.Order(dataDirectory, WorkSharingJobs.Speaking, engine.HostRoleKind, null, target.HostId)
            .Select(place => place.Host is not { } host || host.HostId == target.HostId && place.Model is null ? target
                : target with
                {
                    Origin = host.Pairing.Origin, HostId = host.HostId, SpkiFingerprint = host.Pairing.SpkiFingerprint,
                    DeviceId = host.Pairing.DeviceId, CredentialId = HostPairingCredential.ToGuid(host.Pairing.CredentialId),
                    ModelId = place.Model ?? target.ModelId
                })];
    }

    private static async IAsyncEnumerable<byte[]> SpeakAsync(HostSpeechTarget target, HostSpeechReference reference,
        BoundedSpeechInput input, CorrelationIds ids, long epoch, DateTimeOffset deadline,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = Connect(target);
        var routes = await connection.ReadRoutesAsync(cancellationToken).ConfigureAwait(false);
        HostRouteGpus.Note(target.HostId, routes);
        var route = routes.FirstOrDefault(r => r.RouteId == target.RouteId && r.ModelId == target.ModelId) ??
            throw HostTextClient.Failed("voice", ProviderFailureCode.ModelNotFound, $"{target.HostId} isn't ready for speaking");
        await foreach (var frame in connection.StreamSpeechAsync(route, ids, epoch, deadline, reference, input.Text, cancellationToken)
            .ConfigureAwait(false))
            yield return frame;
    }

    private async Task<HostSpeechReference> ReadReferenceAsync(HostSpeechTarget target, CancellationToken token)
    {
        // The voice list is held briefly while it changes (sharing voices, or the owner adding one); wait for it.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // The route names the exact voice it was saved with, so a failed settings save after switching voices keeps
                // speaking with the voice the route still records.
                using var store = F5Voices.Open(dataDirectory);
                using var lease = await store.AcquireForPreviewAsync(target.PresetId, target.ReferenceRevision, token).ConfigureAwait(false);
                var reference = lease.Reference;
                // A voice made from several recordings: their lengths, from the shared list, for the engine check.
                var clips = F5Voices.LoadLibrary(dataDirectory)?.Find(reference.ReferenceRevision) is { Removed: false } voice
                    ? voice.ClipMilliseconds : null;
                return new(reference.PresetId, reference.ReferenceRevision, reference.AudioSha256, reference.Transcript,
                    reference.TranscriptRevision, reference.Audio.ToArray(), clips);
            }
            catch (F5Exception error) when (error.Failure == F5Failure.Busy && attempt < 20)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is F5Exception or IOException or UnauthorizedAccessException)
            {
                throw new HostTextException(ProviderFailureCode.VoiceUnsupported);
            }
        }
    }

    private static Audio2FaceHostConnection Connect(HostSpeechTarget target)
    {
        var credentialId = HostPairingCredential.FromGuid(target.CredentialId);
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(target.HostId, credentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            throw new HostTextException(ProviderFailureCode.CredentialUnavailable);
        Audio2FaceHostConnection? connection = null;
        try
        {
            read.Secret.Use(secret => connection = new Audio2FaceHostConnection(new Audio2FaceHostPairing
            {
                Origin = target.Origin, HostId = target.HostId, SpkiFingerprint = target.SpkiFingerprint,
                DeviceId = target.DeviceId, CredentialId = credentialId
            }, secret));
        }
        catch (Audio2FaceHostException) { throw new HostTextException(ProviderFailureCode.CredentialUnavailable); }
        return connection!;
    }

    private static async Task<T> Guard<T>(Func<Task<T>> call, CancellationToken token)
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (HostTextClient.Failure("voice", error) is { } failure) { throw failure; }
    }
}

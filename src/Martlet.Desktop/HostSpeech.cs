using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.F5;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The reference voices F5 clones, in Martlet.F5's reference preset store (f5-voices next to the other local
/// preferences). Each voice keeps its own copy of the recording, its transcript and the owner's voice-rights confirmation,
/// so the original file can be moved or deleted after it is added. Martlet's bundled voices (<see cref="F5BundledVoices"/>)
/// join the list when they are first used.</summary>
internal static class F5Voices
{
    internal const string DirectoryName = "f5-voices";
    private const string BundledDirectoryName = "f5-bundled-voices";
    // Earlier versions staged the retired F5-TTS example clip here.
    private const string RetiredSampleDirectoryName = "f5-sample-voice";

    internal static F5BundledVoice? Bundled(F5ReferenceSnapshot snapshot) => F5BundledVoices.ForAudio(snapshot.AudioSha256);

    /// <summary>The F5-TTS example clip earlier versions bundled; Martlet no longer ships it or starts with it.</summary>
    internal static bool IsRetiredSample(F5ReferenceSnapshot snapshot) => F5BundledVoices.IsRetiredSample(snapshot.AudioSha256);

    internal static string Directory(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    internal static F5ReferencePresetStore Open(string dataDirectory) => F5ReferencePresetStore.Open(Directory(dataDirectory));

    /// <summary>Writes a bundled voice's clip next to the preferences so the store can copy it, and returns its path.</summary>
    internal static string EnsureBundled(string dataDirectory, F5BundledVoice voice)
    {
        var bytes = voice.ReadAudio();
        var folder = Path.Combine(dataDirectory, BundledDirectoryName);
        var path = Path.Combine(folder, voice.Key + ".wav");
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            System.IO.Directory.CreateDirectory(folder);
            var staged = path + ".tmp";
            File.WriteAllBytes(staged, bytes);
            File.Move(staged, path, overwrite: true);
        }
        try { System.IO.Directory.Delete(Path.Combine(dataDirectory, RetiredSampleDirectoryName), recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>The voice F5 speaks with when the owner has not picked one for <paramref name="destination"/>: the applied or
    /// most recent voice already chosen for it, otherwise <see cref="F5BundledVoices.Default"/> (a female voice). The retired
    /// F5-TTS example clip is never chosen this way.</summary>
    internal static async Task<F5ReferenceSnapshot> DefaultAsync(string dataDirectory, string destination, CancellationToken token)
    {
        using var store = Open(dataDirectory);
        var inspection = store.Inspect();
        return inspection.Presets
                .Select(p => p.Snapshots.LastOrDefault(s => s.Rights.ProcessingDestinationId == destination))
                .OfType<F5ReferenceSnapshot>()
                .Where(s => !IsRetiredSample(s))
                .OrderByDescending(s => s.PresetId == inspection.AppliedPresetId)
                .ThenByDescending(s => s.CreatedAtUtc)
                .FirstOrDefault()
            ?? await BundledAsync(store, dataDirectory, destination, F5BundledVoices.Default, token);
    }

    /// <summary>Every voice in the list for <paramref name="destination"/> (each voice's latest recording), oldest first,
    /// and the applied voice's preset.</summary>
    internal static (IReadOnlyList<F5ReferenceSnapshot> Voices, Guid? Applied) List(string dataDirectory, string destination)
    {
        if (!System.IO.Directory.Exists(Directory(dataDirectory))) return ([], null);
        using var store = Open(dataDirectory);
        var inspection = store.Inspect();
        var voices = inspection.Presets
            .Select(p => p.Snapshots.LastOrDefault(s => s.Rights.ProcessingDestinationId == destination))
            .OfType<F5ReferenceSnapshot>()
            .OrderBy(s => s.CreatedAtUtc)
            .ToArray();
        return (voices, inspection.AppliedPresetId);
    }

    /// <summary>Deletes a voice and Martlet's copy of its recording. The original file is not touched.</summary>
    internal static async Task RemoveAsync(string dataDirectory, Guid presetId, CancellationToken token)
    {
        using var store = Open(dataDirectory);
        await store.DeleteAsync(presetId, token);
    }

    /// <summary>A bundled voice's snapshot for <paramref name="destination"/>, added to the voice list if needed.</summary>
    internal static async Task<F5ReferenceSnapshot> BundledAsync(F5ReferencePresetStore store, string dataDirectory, string destination,
        F5BundledVoice voice, CancellationToken token)
    {
        var path = EnsureBundled(dataDirectory, voice);
        if (store.Inspect().Presets.SelectMany(p => p.Snapshots)
                .FirstOrDefault(s => Bundled(s) == voice && s.Rights.ProcessingDestinationId == destination) is { } existing)
            return existing;
        return await store.SnapshotAsync(new()
        {
            PresetName = voice.Name,
            AbsoluteSourcePath = path,
            Transcript = voice.Transcript,
            Rights = new()
            {
                AcknowledgementId = Guid.NewGuid(),
                Basis = F5VoiceRightsBasis.PublishedSample,
                StatementVersion = F5ReferenceLimits.RightsStatementVersion,
                ProcessingDestinationId = destination,
                AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                Confirmed = true
            }
        }, token);
    }

    /// <summary>The exact recording a voice snapshot keeps, for playing it back.</summary>
    internal static async Task<byte[]> ReadAudioAsync(string dataDirectory, F5ReferenceSnapshot snapshot, CancellationToken token)
    {
        using var store = Open(dataDirectory);
        using var lease = await store.AcquireForPreviewAsync(snapshot.PresetId, snapshot.ReferenceRevision, token);
        return lease.Reference.Audio.ToArray();
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
        F5Failure.InvalidAudio => "The recording must be a mono 16-bit PCM WAV of 1 to 30 seconds (16, 22.05, 24, 44.1 or 48 kHz), at most 4 MB.",
        F5Failure.RightsRequired => "Confirm that you may use this voice.",
        F5Failure.Busy => "The voice list is in use by another Martlet window; try again in a moment.",
        F5Failure.LimitExceeded => "The voice list is full (16 voices). Remove one you no longer use first.",
        F5Failure.Conflict => "Martlet speaks with this voice now. Switch to another voice first, then remove it.",
        F5Failure.NotFound => "That voice is no longer in your list.",
        F5Failure.CorruptStore => "Martlet's copy of this voice is damaged. Remove it and add the recording again.",
        _ => $"The voice could not be used ({error.Failure})."
    };
}

/// <summary>Speaks reply segments with a paired host's F5 voice through its pinned gateway: reads the route's reference
/// voice from the F5 preset store and the pairing secret from Windows Credential Manager for each segment.</summary>
internal sealed class HostSpeechClient(string dataDirectory) : IHostSpeechClient
{
    public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
        long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reference = await ReadReferenceAsync(target, cancellationToken).ConfigureAwait(false);
        using var connection = Connect(target);
        var routes = await Guard(() => connection.ReadRoutesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        var route = routes.FirstOrDefault(r => r.RouteId == HostRoute.F5RouteId && r.ModelId == target.ModelId) ??
            throw HostTextClient.Failed("voice", ProviderFailureCode.ModelNotFound, $"the host offers no F5 route for model {target.ModelId}");
        await using var frames = connection.StreamSpeechAsync(route, ids, epoch, deadline, reference, input.Text, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await Guard(() => frames.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
            yield return frames.Current;
    }

    private async Task<HostSpeechReference> ReadReferenceAsync(HostSpeechTarget target, CancellationToken token)
    {
        try
        {
            // The route names the exact voice it was saved with, so a failed settings save after switching voices keeps
            // speaking with the voice the route still records.
            using var store = F5Voices.Open(dataDirectory);
            using var lease = await store.AcquireForPreviewAsync(target.PresetId, target.ReferenceRevision, token).ConfigureAwait(false);
            var reference = lease.Reference;
            return new(reference.PresetId, reference.ReferenceRevision, reference.AudioSha256, reference.Transcript,
                reference.TranscriptRevision, reference.Audio.ToArray());
        }
        catch (Exception error) when (error is F5Exception or IOException or UnauthorizedAccessException)
        {
            throw new HostTextException(ProviderFailureCode.VoiceUnsupported);
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

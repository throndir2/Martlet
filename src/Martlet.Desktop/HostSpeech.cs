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
/// preferences). Each snapshot keeps a copy of the recording, its transcript and the owner's voice-rights confirmation;
/// the original recording must stay where it was chosen, as the store re-checks it before every use.</summary>
internal static class F5Voices
{
    internal const string DirectoryName = "f5-voices";

    internal static string Directory(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    internal static F5ReferencePresetStore Open(string dataDirectory) => F5ReferencePresetStore.Open(Directory(dataDirectory));

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
        F5Failure.SourceMissing => "The voice's original recording is no longer where you chose it. Put it back or choose the voice again.",
        F5Failure.SourceChanged => "The voice's original recording changed since you chose it. Choose the voice again.",
        F5Failure.InvalidAudio => "The recording must be a mono 16-bit PCM WAV of 1 to 30 seconds (16, 22.05, 24, 44.1 or 48 kHz), at most 4 MB.",
        F5Failure.RightsRequired => "Confirm that you may use this voice.",
        F5Failure.Busy => "The voice list is in use by another Martlet window; try again in a moment.",
        F5Failure.LimitExceeded => "The voice list is full (16 voices).",
        _ => $"The voice could not be used ({error.Failure})."
    };
}

/// <summary>Speaks reply segments with a paired host's F5 voice through its pinned gateway: reads the applied reference
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
            throw new HostTextException(ProviderFailureCode.ModelNotFound);
        await using var frames = connection.StreamSpeechAsync(route, ids, epoch, deadline, reference, input.Text, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await Guard(() => frames.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
            yield return frames.Current;
    }

    private async Task<HostSpeechReference> ReadReferenceAsync(HostSpeechTarget target, CancellationToken token)
    {
        try
        {
            using var store = F5Voices.Open(dataDirectory);
            using var lease = await store.AcquireAppliedAsync(target.ReferenceRevision, token).ConfigureAwait(false);
            if (lease.PresetId != target.PresetId) throw new HostTextException(ProviderFailureCode.VoiceUnsupported);
            var reference = lease.Reference;
            return new(reference.PresetId, reference.ReferenceRevision, reference.AudioSha256, reference.Transcript,
                reference.TranscriptRevision, reference.Audio.ToArray());
        }
        catch (F5Exception) { throw new HostTextException(ProviderFailureCode.VoiceUnsupported); }
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
        catch (Audio2FaceHostException error) { throw new HostTextException(HostTextClient.Map(error.Code)); }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw new HostTextException(ProviderFailureCode.Network); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            throw new HostTextException(ProviderFailureCode.ResponseSchema);
        }
    }
}

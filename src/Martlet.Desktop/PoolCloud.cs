using System.IO;
using System.Runtime.CompilerServices;
using Martlet.Core.Audio;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>A cloud member of the Speaking or Listening list taking a request in its turn
/// (docs/CLUSTER.md#sharing-work-between-your-computers): when the members before it are busy or don't answer,
/// <see cref="WorkQueue"/> starts it here. It uses its own key (pool-keys.json's credential ID, else the key of the route the
/// job kept aside when it moved to a computer) and the owner's agreement recorded on the member (<see cref="PoolMember.Consented"/>,
/// checked by <see cref="PoolRouting"/>). Nothing here runs while a member before it is free, so a free first member adds no
/// latency. OpenAI's and ElevenLabs' voices give the same 24 kHz mono PCM16 as a host's voice; OpenAI transcribes the same
/// 16 kHz recording a host does.</summary>
internal static class PoolCloud
{
    /// <summary>A cloud member's request failed before its first answer, or in the middle.</summary>
    internal sealed class Refused(ProviderFailureCode code, int? status = null)
        : Exception($"The cloud provider failed ({code}{(status is { } s ? $", HTTP {s}" : "")}).")
    {
        internal ProviderFailureCode Code { get; } = code;
        internal int? Status { get; } = status;
    }

    /// <summary>What a cloud member needs on this PC: its provider's route type, alias and origin, its model and voice, and the
    /// credential ID of its key.</summary>
    internal sealed record Use(SetupRouteType Type, string Alias, string Origin, string Model, string? Voice, Guid CredentialId);

    /// <summary>A cloud route as a pool member's key (the same as the page's): "cloud:openai/&lt;model&gt;",
    /// "cloud:elevenlabs/&lt;model&gt;" or "cloud:chat-completions@&lt;origin&gt;/&lt;model&gt;"; null for another route.</summary>
    internal static PoolMember? MemberOf(SetupRoute route) => route.RouteType switch
    {
        null or SetupRouteType.OpenAi => PoolMember.Cloud("openai", route.ModelId).WithSetting(PoolSettingKeys.Voice, route.VoiceId),
        SetupRouteType.ElevenLabs => PoolMember.Cloud("elevenlabs", route.ModelId).WithSetting(PoolSettingKeys.Voice, route.VoiceId),
        SetupRouteType.ChatCompletions => PoolMember.Cloud("chat-completions", route.ModelId, route.Origin.TrimEnd('/')),
        _ => null
    };

    /// <summary>The providers a cloud member of <paramref name="role"/> can take a host request's turn with.</summary>
    private static bool Takes(SetupRole role, PoolMember member) => member.Kind == PoolMemberKind.Cloud && member.Model is not null &&
        (role == SetupRole.Tts ? member.Provider is "openai" or "elevenlabs" : role == SetupRole.Stt && member.Provider == "openai");

    /// <summary>Whether <paramref name="member"/> can take a turn on this PC: a provider this job speaks or listens with, a model,
    /// a voice for Speaking and a key here. Reads only local files, again only when they changed.</summary>
    internal static bool Usable(string? directory, SetupRole role, PoolMember member) => Find(directory, role, member) is not null;

    /// <summary>What <paramref name="member"/> uses on this PC, or null when it can't take a turn here.</summary>
    internal static Use? Find(string? directory, SetupRole role, PoolMember member)
    {
        if (directory is null || !Takes(role, member)) return null;
        var area = role == SetupRole.Tts ? PoolAreas.Speaking : PoolAreas.Listening;
        var job = HostJob.For(role)!;
        // The route the job kept aside when it moved to a computer, when this member is that route.
        var saved = WorkSharingRoster.Cached(Path.Combine(directory, job.SavedFile), () => new Saved(JobSavedRoute.Load(directory, job.SavedFile))).Route;
        var savedMember = saved is null ? null : MemberOf(saved);
        var fromSaved = savedMember?.Key == member.Key ? saved : null;
        var keys = WorkSharingRoster.Cached(Path.Combine(directory, PoolKeys.FileName), () => PoolKeys.Load(directory));
        var credential = keys.For(area.Id, member.Key) ?? fromSaved?.CredentialId;
        var voice = member.Setting(PoolSettingKeys.Voice) ?? fromSaved?.VoiceId;
        if (credential is not { } id || role == SetupRole.Tts && string.IsNullOrEmpty(voice)) return null;
        return member.Provider == "elevenlabs"
            ? new(SetupRouteType.ElevenLabs, ElevenLabsSetup.Alias, ElevenLabsSetup.Origin, member.Model!, voice, id)
            : new(SetupRouteType.OpenAi, OpenAiSetup.Alias(role), OpenAiSetup.Origin, member.Model!, voice, id);
    }

    private sealed record Saved(SetupRoute? Route);

    /// <summary>Speaks one reply segment with <paramref name="member"/> (OpenAI or ElevenLabs) as 24 kHz mono PCM16 chunks.</summary>
    internal static async IAsyncEnumerable<byte[]> SpeakAsync(string directory, PoolMember member, BoundedSpeechInput input,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken token)
    {
        var use = Find(directory, SetupRole.Tts, member) ?? throw new Refused(ProviderFailureCode.CredentialUnavailable);
        var key = await KeyAsync(directory, SetupRole.Tts, use, token).ConfigureAwait(false);
        var limits = LiveConversationConfiguration.SpeechLimits;
        if (use.Type == SetupRouteType.ElevenLabs)
        {
            await foreach (var chunk in new ElevenLabsDialogueClient(key).StreamAsync(new(use.Voice!, use.Model), input, limits, deadline, token)
                .ConfigureAwait(false))
                yield return chunk;
            yield break;
        }
        using var adapter = OpenAiSpeechSynthesisAdapter.Create(key);
        var selection = new SpeechSynthesisSelection(OpenAiSetup.Alias(SetupRole.Tts), use.Model, use.Voice!, SpeechOutputFormat.Pcm24KhzMono16Le);
        // The owner agreed to this member (its recorded consent): this segment's own one-use authorization for it.
        var authorization = new SpeechDisclosureAuthorization(new(OpenAiSpeechSynthesisCatalog.Origin, ProviderRole.Tts, use.Model), selection,
            input, ids, epoch, limits, deadline, true, true, true);
        var stream = adapter.Stream(new() { Ids = ids, Epoch = epoch, Deadline = deadline }, selection, input, limits, authorization, token);
        await foreach (var frame in stream.WithCancellation(token).ConfigureAwait(false))
            yield return frame.Data.ToArray();
        if (stream.Result is { Outcome: not SpeechSynthesisOutcome.Completed } result)
            throw new Refused(result.Failure?.Code ?? ProviderFailureCode.Server, result.HttpStatusCode);
    }

    /// <summary>Transcribes one 16 kHz mono PCM16 utterance with <paramref name="member"/> (OpenAI); "" when it heard no speech.</summary>
    internal static async Task<string> TranscribeAsync(string directory, PoolMember member, ReadOnlyMemory<byte> pcm16kMono,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken token)
    {
        var use = Find(directory, SetupRole.Stt, member) ?? throw new Refused(ProviderFailureCode.CredentialUnavailable);
        var key = await KeyAsync(directory, SetupRole.Stt, use, token).ConfigureAwait(false);
        using var adapter = OpenAiTranscriptionAdapter.Create(key);
        var limits = LiveConversationConfiguration.TranscriptionLimits;
        var audio = BoundedWaveAudio.FromPcm(new PcmFormat
        {
            SampleRate = HostTranscriptionAdapter.SampleRate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
        }, pcm16kMono.Span);
        var authorization = new AudioUploadAuthorization(new(OpenAiTranscriptionCatalog.Origin, ProviderRole.Stt, use.Model), ids, epoch,
            limits, deadline, true, true);
        var result = await adapter.TranscribeAsync(new() { Ids = ids, Epoch = epoch, Deadline = deadline }, use.Model, audio, limits,
            authorization, token).ConfigureAwait(false);
        return result.Outcome switch
        {
            TranscriptionOutcome.Completed => result.Text ?? "",
            TranscriptionOutcome.NoSpeech => "",
            TranscriptionOutcome.Canceled => throw new OperationCanceledException(token),
            _ => throw new Refused(result.Failure?.Code ?? ProviderFailureCode.Server)
        };
    }

    /// <summary>The member's key, read from Windows Credential Manager under its own binding when the provider asks for it.</summary>
    private static async Task<IProviderCredentialSource> KeyAsync(string directory, SetupRole role, Use use, CancellationToken token)
    {
        var loaded = await new SettingsStore(directory).LoadAsync(token).ConfigureAwait(false);
        if (loaded.Settings is not { } settings) throw new Refused(ProviderFailureCode.CredentialUnavailable);
        var scope = new CredentialBinding(settings.Profile.Id, use.CredentialId, role, use.Type, use.Alias, use.Origin);
        try { scope.Validate(); }
        catch (ContractException) { throw new Refused(ProviderFailureCode.CredentialBindingMismatch); }
        return new Key(scope);
    }

    private sealed class Key(CredentialBinding scope) : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken)
        {
            using var read = new WindowsCredentialStore().Read(scope);
            if (read.Error != CredentialError.None || read.Secret is null) throw new CredentialUnavailableException();
            BoundProviderCredential? credential = null;
            read.Secret.Use(secret => credential = new(binding, new string(secret)));
            return ValueTask.FromResult(credential);
        }
    }

    /// <summary>What a cloud member's failure means in the queue: a rate limit is busy (try the next, then wait); a missing or
    /// refused key, an exhausted quota, an outage, a timeout or no connection is unavailable (try the next); the rest (a bad
    /// request, a model or voice it doesn't take) is a real failure.</summary>
    internal static WorkRefusal Refusal(Exception error) => error switch
    {
        Refused refused => Of(refused.Code),
        ElevenLabsException eleven => Of(eleven.Code),
        CredentialUnavailableException => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    private static WorkRefusal Of(ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.RateLimited => WorkRefusal.Busy,
        ProviderFailureCode.CredentialUnavailable or ProviderFailureCode.CredentialBindingMismatch or ProviderFailureCode.Authentication or
            ProviderFailureCode.PermissionDenied or ProviderFailureCode.QuotaExceeded or ProviderFailureCode.Network or
            ProviderFailureCode.Server or ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstAudioTimeout or
            ProviderFailureCode.ModelNotFound or ProviderFailureCode.RedirectRejected => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };
}

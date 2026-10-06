using System.Runtime.CompilerServices;
using System.Text;
using Martlet.Companion.Platform;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using ICredentialStore = Martlet.Companion.Platform.ICredentialStore;

namespace Martlet.Companion;

/// <summary>Provider keys from the platform credential store: the OpenAI key for OpenAI's origin, the Chat Completions
/// key for any other origin.</summary>
internal sealed class StoreCredentialSource(ICredentialStore store) : IProviderCredentialSource
{
    public async ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken)
    {
        var name = binding.Origin == OpenAiTextGenerationCatalog.Origin ? CompanionSettings.OpenAiKey : CompanionSettings.ChatCompletionsKey;
        var secret = await store.GetAsync(name, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret)) return null;
        try { return new BoundProviderCredential(binding, secret.Trim()); }
        catch (CredentialUnavailableException) { return null; }
    }
}

/// <summary>A failure the person can act on, in plain words.</summary>
public sealed class CompanionException(string message) : Exception(message);

/// <summary>The companion's conversation over Martlet.Providers: OpenAI (Responses) or any Chat Completions server for
/// thinking, OpenAI for listening and speaking. Keeps the history in memory; the persona is sent first so prompt caches
/// (OpenAI, Ollama) are reused from one reply to the next.</summary>
public sealed class CompanionConversation(ICredentialStore credentials) : IDisposable
{
    private const int MaxHistory = 24;
    private readonly StoreCredentialSource source = new(credentials);
    private readonly List<TextHistoryMessage> history = [];
    private readonly Lazy<OpenAiTextGenerationAdapter> openAi = new(() => OpenAiTextGenerationAdapter.Create(new StoreCredentialSource(credentials)));
    private readonly Lazy<OpenAiTranscriptionAdapter> stt = new(() => OpenAiTranscriptionAdapter.Create(new StoreCredentialSource(credentials)));
    private readonly Lazy<OpenAiSpeechSynthesisAdapter> tts = new(() => OpenAiSpeechSynthesisAdapter.Create(new StoreCredentialSource(credentials)));
    private ChatCompletionsTextGenerationAdapter? chat;
    private string? chatUrl;
    private bool chatKeyed;
    private long epoch;

    public IReadOnlyList<TextHistoryMessage> History => history;

    public void Clear() => history.Clear();

    /// <summary>Whether <paramref name="settings"/> needs the cloud consent before anything is sent.</summary>
    public static bool NeedsCloudConsent(CompanionSettings settings, bool speaking) =>
        settings.Thinking == "openai-llm" || settings.Thinking == "chat-completions" && !CompanionGuardrails.IsLoopback(settings.ChatBaseUrl) ||
        speaking && settings.Speaking == "openai-tts";

    /// <summary>Streams the reply to <paramref name="userText"/>: each text delta as it arrives. The exchange joins the
    /// history once complete.</summary>
    public async IAsyncEnumerable<string> ReplyAsync(CompanionSettings settings, string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) yield break;
        if (NeedsCloudConsent(settings, false) && !settings.CloudConsent)
            throw new CompanionException("Turn on \"Send to the cloud provider\" in Settings first: what you type goes to the provider, which may charge you.");
        var cloud = settings.Thinking == "openai-llm";
        var limits = new TextGenerationLimits { MaxOutputTokens = 400, MaxRequestTime = TimeSpan.FromSeconds(90), FirstDeltaTimeout = TimeSpan.FromSeconds(60) };
        var input = new BoundedTextInput(userText.Trim(), settings.Persona, history.TakeLast(MaxHistory));
        var ids = Ids();
        var deadline = DateTimeOffset.UtcNow + limits.MaxRequestTime;
        var context = new ProviderRequestContext { Ids = ids, Epoch = Interlocked.Increment(ref epoch), Deadline = deadline };
        TextGenerationStream stream;
        if (cloud)
        {
            if (await credentials.GetAsync(CompanionSettings.OpenAiKey, cancellationToken) is null)
                throw new CompanionException("Add your OpenAI API key in Settings.");
            var model = new TextModelSelection("openai-llm", settings.OpenAiModel);
            var authorization = new TextDisclosureAuthorization(new(OpenAiTextGenerationCatalog.Origin, ProviderRole.Llm, model.UpstreamModelId),
                model, ids, context.Epoch, limits, deadline, true, settings.CloudConsent);
            stream = openAi.Value.Stream(context, model, input, limits, authorization, cancellationToken);
        }
        else
        {
            var keyed = await credentials.GetAsync(CompanionSettings.ChatCompletionsKey, cancellationToken) is { Length: > 0 };
            var adapter = Chat(settings.ChatBaseUrl, keyed);
            var model = new TextModelSelection(ChatCompletionsSetup.Alias, settings.ChatModel);
            var authorization = new TextDisclosureAuthorization(new(ChatCompletionsSetup.BaseUri(settings.ChatBaseUrl), ProviderRole.Llm, model.UpstreamModelId),
                model, ids, context.Epoch, limits, deadline, true, true);
            stream = adapter.Stream(context, model, input, limits, authorization, cancellationToken);
        }
        var reply = new StringBuilder();
        await foreach (var item in stream.WithCancellation(cancellationToken))
            if (item.Kind == ProviderEventKind.TextDelta && item.Text is { Length: > 0 } text)
            {
                reply.Append(text);
                yield return text;
            }
        var result = stream.Result;
        if (result?.Outcome != TextGenerationOutcome.Completed && reply.Length == 0)
            throw new CompanionException(Describe("Thinking", result?.Failure, result?.Outcome.ToString()));
        history.Add(new(TextHistoryRole.User, userText.Trim()));
        history.Add(new(TextHistoryRole.Assistant, reply.ToString()));
        if (history.Count > MaxHistory * 2) history.RemoveRange(0, history.Count - MaxHistory * 2);
    }

    /// <summary>What was said in <paramref name="pcm"/> (16-bit mono at <paramref name="sampleRate"/>), or null when nothing was.</summary>
    public async Task<string?> TranscribeAsync(CompanionSettings settings, byte[] pcm, int sampleRate, CancellationToken cancellationToken)
    {
        if (settings.Listening != "openai-stt") throw new CompanionException("Choose a listening engine in Settings.");
        if (!settings.CloudConsent)
            throw new CompanionException("Turn on \"Send to the cloud provider\" in Settings first: your voice goes to OpenAI, which may charge you.");
        if (await credentials.GetAsync(CompanionSettings.OpenAiKey, cancellationToken) is null)
            throw new CompanionException("Add your OpenAI API key in Settings.");
        var format = new PcmFormat { SampleRate = sampleRate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian };
        var audio = BoundedWaveAudio.FromPcm(format, pcm);
        var limits = new TranscriptionLimits();
        var ids = Ids();
        var deadline = DateTimeOffset.UtcNow + limits.MaxRequestTime;
        var context = new ProviderRequestContext { Ids = ids, Epoch = Interlocked.Increment(ref epoch), Deadline = deadline };
        var authorization = new AudioUploadAuthorization(new(OpenAiTranscriptionCatalog.Origin, ProviderRole.Stt, settings.ListeningModel),
            ids, context.Epoch, limits, deadline, true, true);
        var result = await stt.Value.TranscribeAsync(context, settings.ListeningModel, audio, limits, authorization, cancellationToken);
        return result.Outcome switch
        {
            TranscriptionOutcome.Completed => string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim(),
            TranscriptionOutcome.NoSpeech => null,
            _ => throw new CompanionException(Describe("Listening", result.Failure, result.Outcome.ToString()))
        };
    }

    /// <summary>The reply spoken, sentence by sentence, as 24 kHz mono 16-bit PCM.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SpeakAsync(CompanionSettings settings, string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (settings.Speaking != "openai-tts" || string.IsNullOrWhiteSpace(text)) yield break;
        if (!settings.CloudConsent) throw new CompanionException("Turn on \"Send to the cloud provider\" in Settings to hear replies.");
        foreach (var part in SpeechParts(text))
        {
            var selection = new SpeechSynthesisSelection("openai-tts", OpenAiSpeechSynthesisCatalog.DefaultModelId,
                OpenAiSpeechSynthesisCatalog.SupportsVoice(settings.Voice) ? settings.Voice : OpenAiSpeechSynthesisCatalog.DefaultVoice,
                SpeechOutputFormat.Pcm24KhzMono16Le);
            var input = new BoundedSpeechInput(part);
            var limits = new SpeechSynthesisLimits();
            var ids = Ids();
            var deadline = DateTimeOffset.UtcNow + limits.MaxRequestTime;
            var context = new ProviderRequestContext { Ids = ids, Epoch = Interlocked.Increment(ref epoch), Deadline = deadline };
            var authorization = new SpeechDisclosureAuthorization(new(OpenAiSpeechSynthesisCatalog.Origin, ProviderRole.Tts, selection.UpstreamModelId),
                selection, input, ids, context.Epoch, limits, deadline, true, true, true);
            var stream = tts.Value.Stream(context, selection, input, limits, authorization, cancellationToken);
            await foreach (var frame in stream.WithCancellation(cancellationToken))
                yield return frame.Data;
            if (stream.Result is { Outcome: not SpeechSynthesisOutcome.Completed } failed && failed.DeliveredSampleCount == 0)
                throw new CompanionException(Describe("Speaking", failed.Failure, failed.Outcome.ToString()));
        }
    }

    /// <summary>Splits a reply into speech inputs within the provider's bound, at sentence ends where possible.</summary>
    internal static IEnumerable<string> SpeechParts(string text, int maxBytes = 1_000)
    {
        var current = new StringBuilder();
        foreach (var sentence in Sentences(text))
        {
            if (current.Length > 0 && Encoding.UTF8.GetByteCount(current + sentence) > maxBytes)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            var piece = sentence;
            while (Encoding.UTF8.GetByteCount(piece) > maxBytes)
            {
                var cut = Math.Min(piece.Length, maxBytes / 4);
                var space = piece.LastIndexOf(' ', cut - 1);
                if (space > 0) cut = space;
                yield return piece[..cut].Trim();
                piece = piece[cut..];
            }
            current.Append(piece);
        }
        if (current.ToString().Trim() is { Length: > 0 } rest) yield return rest;
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] is '.' or '!' or '?' or '\n' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                yield return text[start..(i + 1)] + " ";
                start = i + 1;
            }
        if (start < text.Length) yield return text[start..];
    }

    /// <summary>The Chat Completions adapter; it sends a key only when one is saved (a server on this computer needs none).</summary>
    private ChatCompletionsTextGenerationAdapter Chat(string baseUrl, bool keyed)
    {
        try { _ = ChatCompletionsSetup.BaseUri(baseUrl); }
        catch (ContractException error) { throw new CompanionException(error.Message); }
        if (chat is null || chatUrl != baseUrl || chatKeyed != keyed)
        {
            chat?.Dispose();
            chat = ChatCompletionsTextGenerationAdapter.Create(baseUrl, keyed ? source : null);
            chatUrl = baseUrl;
            chatKeyed = keyed;
        }
        return chat;
    }

    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static string Describe(string job, ProviderFailure? failure, string? outcome) =>
        failure is null ? $"{job} didn't answer ({outcome ?? "no result"})." : $"{job} failed: {failure.Error.Summary} ({failure.Code})".TrimEnd();

    public void Dispose()
    {
        chat?.Dispose();
        if (openAi.IsValueCreated) openAi.Value.Dispose();
        if (stt.IsValueCreated) stt.Value.Dispose();
        if (tts.IsValueCreated) tts.Value.Dispose();
    }
}

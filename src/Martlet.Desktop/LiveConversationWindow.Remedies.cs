using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Plain-language messages: what went wrong and what to do.
public partial class LiveConversationWindow
{
    // ---------- plain-language messages ----------

    // A message in the talk window is one sentence (issue #992); what to do and why go in the same sentence or in the wiki
    // (Talking-to-Martlet#messages-in-the-talk-window).

    /// <summary>One sentence from a lead and what follows it: "Martlet couldn't speak: the provider is limiting requests ...".</summary>
    internal static string Lead(string lead, string rest) => lead + ": " + Lowered(rest);

    /// <summary><paramref name="text"/> with <paramref name="note"/> in brackets at its end, as one sentence.</summary>
    internal static string Noted(string text, string note) => text.TrimEnd('.', ' ') + " (" + note.TrimEnd('.', ' ') + ").";

    // Inside a sentence a message's first word is lower case, unless it is a name such as Martlet, Ollama or Thinking.
    private static string Lowered(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1]) && !Names.Contains(text.Split(' ', 2)[0]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Martlet", "Martlet's", "Ollama", "ElevenLabs", "Windows", "Windows'", "Voice", "Only", "Thinking", "Thinking's", "OpenAI",
        "OpenRouter", "Parakeet", "Gemma", "Companion", "Settings", "Devices"
    };

    /// <summary>The remedy for a failed reply. <paramref name="job"/> is the job whose request failed (null when unknown): a job
    /// handed to a paired Martlet host gets the host's remedy, Thinking in Ollama on this PC gets Ollama's, and a voice failure
    /// says so, since the reply's text may already be on screen.</summary>
    internal static string ProviderRemedy(ProviderFailureCode code, LiveConversationConfiguration? configuration, ProviderRole? job = null)
    {
        var host = job switch
        {
            ProviderRole.Tts => configuration?.HostSpeechTarget()?.HostId,
            ProviderRole.Stt => configuration?.SttHostTarget()?.HostId,
            _ => configuration?.HostTarget()?.HostId
        };
        if (host is not null && HostRemedy(code, host, job ?? ProviderRole.Llm) is { } hosted) return hosted;
        if (job == ProviderRole.Tts && configuration?.ElevenLabsVoiceTarget() is { } cloned && ElevenLabsRemedy(code, cloned.ModelId) is { } eleven)
            return Lead("Martlet couldn't speak", eleven);
        if (job != ProviderRole.Tts && configuration is { LocalOllama: true } local && LocalOllamaRemedy(code, local) is { } remedy) return remedy;
        return job switch
        {
            ProviderRole.Tts when code is ProviderFailureCode.ModelUnsupported or ProviderFailureCode.ModelNotFound or ProviderFailureCode.VoiceUnsupported =>
                "Martlet couldn't speak, so choose another voice in Companion › Voice.",
            ProviderRole.Tts => Lead("Martlet couldn't speak", ProviderRemedy(code)),
            ProviderRole.Stt => Lead("Couldn't transcribe speech", ProviderRemedy(code)),
            _ => ProviderRemedy(code)
        };
    }

    /// <summary>What to do when ElevenLabs couldn't speak a reply with <paramref name="model"/>, or null for the usual remedy. A
    /// model ElevenLabs refuses (its WebSocket's API reference names only eleven_v3 models) names the other model to choose.</summary>
    internal static string? ElevenLabsRemedy(ProviderFailureCode code, string model) => code switch
    {
        ProviderFailureCode.ModelUnsupported or ProviderFailureCode.ModelNotFound =>
            $"ElevenLabs refused {ElevenLabsSetup.ModelName(model)}, so choose " +
            $"{ElevenLabsSetup.ModelName(model == ElevenLabsSetup.V4Turbo ? ElevenLabsSetup.V3Conversational : ElevenLabsSetup.V4Turbo)} in Companion › Voice.",
        ProviderFailureCode.VoiceUnsupported =>
            "ElevenLabs couldn't use your cloned voice, so verify it on elevenlabs.io if it asked you to, or clone it again in Companion › Voice.",
        ProviderFailureCode.CredentialUnavailable or ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied =>
            "ElevenLabs didn't accept your API key, so paste it again in Companion › Voice.",
        ProviderFailureCode.QuotaExceeded => "Your ElevenLabs account is out of credits or its plan doesn't allow this (check it on elevenlabs.io).",
        _ => null
    };

    /// <summary>The job's process on that computer, not a provider account, is at fault when a paired Martlet host fails:
    /// the gateway answers that the worker is unavailable (stopped, still starting, or cut off from the gateway) as ModelNotFound.</summary>
    internal static string? HostRemedy(ProviderFailureCode code, string hostId, ProviderRole job)
    {
        var what = job switch
        {
            ProviderRole.Tts => "voice",
            ProviderRole.Stt => "listening",
            _ => "Thinking"
        };
        return code switch
        {
            ProviderFailureCode.ModelNotFound or ProviderFailureCode.ModelUnsupported or ProviderFailureCode.Server =>
                $"Your Martlet host {hostId} didn't answer for {what}, so try again in a minute or, if it keeps happening, update or set up that host again on Devices.",
            ProviderFailureCode.Network =>
                $"Couldn't reach your Martlet host {hostId}, so check that the computer is on, then try again.",
            ProviderFailureCode.VoiceUnsupported when job == ProviderRole.Tts =>
                $"Martlet couldn't use the voice from host {hostId}, so choose a voice again in Companion › Voice.",
            ProviderFailureCode.CredentialUnavailable or ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied =>
                $"Martlet couldn't use this PC's pairing with host {hostId}, so pair it again on Devices.",
            ProviderFailureCode.InputLimit when job == ProviderRole.Llm =>
                $"That's too long for Thinking on your Martlet host {hostId}, so update it on Devices if it runs an older Martlet, " +
                "or send a shorter message.",
            _ => null
        };
    }

    internal static string? LocalOllamaRemedy(ProviderFailureCode code, LiveConversationConfiguration local)
    {
        var model = local.Route(SetupRole.Llm).ModelId;
        return code switch
        {
            ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout or
                ProviderFailureCode.ConsentExpired =>
                $"Local model {model} didn't answer in time, so try again or choose a smaller model in Companion › Thinking.",
            ProviderFailureCode.Network => "Couldn't reach Ollama, so start it and try again.",
            ProviderFailureCode.Server => $"Couldn't run local model {model}, so choose a smaller " +
                "model in Companion › Thinking or close programs that use a lot of memory.",
            ProviderFailureCode.ModelNotFound or ProviderFailureCode.ModelUnsupported =>
                $"Download local model {model} in Companion › Thinking.",
            ProviderFailureCode.OutputTokenLimit =>
                "The model hit the max reply length, so raise it in Companion › Replies or ask for a shorter answer.",
            _ => null
        };
    }

    internal static string ProviderRemedy(ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied or ProviderFailureCode.CredentialUnavailable =>
            "The provider didn't accept the API key, so check the key and the account's model access in Companion.",
        ProviderFailureCode.QuotaExceeded => "The provider says the account is out of quota or credit, so check its billing and usage.",
        ProviderFailureCode.RateLimited => "The provider is limiting requests right now, so wait a moment and try again.",
        ProviderFailureCode.ModelUnsupported or ProviderFailureCode.ModelNotFound or ProviderFailureCode.VoiceUnsupported =>
            "The provider doesn't offer that model or voice to this account, so choose another in Companion.",
        ProviderFailureCode.ModelRetired =>
            "The provider has retired this model, so choose another model in Companion › Thinking.",
        ProviderFailureCode.ConsentExpired or ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or
            ProviderFailureCode.FirstAudioTimeout or ProviderFailureCode.IdleTimeout =>
            "The provider took too long to answer, so try again.",
        ProviderFailureCode.Network or ProviderFailureCode.Server => "Couldn't reach the provider, so check your connection and try again.",
        ProviderFailureCode.OriginRejected or ProviderFailureCode.CredentialBindingMismatch or ProviderFailureCode.RedirectRejected =>
            "The provider's address didn't match the one you set up, so nothing was sent there (review it in Companion).",
        ProviderFailureCode.OutputTokenLimit =>
            "The model hit the max reply length, so ask for a shorter answer or raise the limit in Companion › Replies.",
        ProviderFailureCode.InputLimit =>
            "That's too long for the Thinking model, so send a shorter message or raise the context size in Companion › Replies.",
        ProviderFailureCode.ResponseTooLarge =>
            "The answer was longer than Martlet takes from a provider in one reply, so ask for a shorter answer.",
        _ => "Martlet couldn't use the provider's answer, so try again."
    };

    /// <summary>Why remembering an exchange failed, in words: the Thinking request's own remedy or what is wrong with the store.</summary>
    internal static string MemoryFailureReason(string code, LiveConversationConfiguration? configuration) =>
        Enum.TryParse<ProviderFailureCode>(code, out var provider) && !int.TryParse(code, out _)
            ? ProviderRemedy(provider, configuration, ProviderRole.Llm)
            : code switch
            {
                "memory.Busy" or "memory.busy" or "memory.Conflict" =>
                    "Memory was in use by something else, so Martlet tries again with the next thing you say.",
                "memory.AccessDenied" => "Martlet can't write to the memory folder, so choose one you can write to in Companion › Memory.",
                "memory.CorruptStore" or "memory.UnsupportedVersion" =>
                    "The memory store can't be read, so check it in Companion › Memory.",
                "memory.LimitExceeded" => "Memory is full, so delete some facts in Companion › Memory.",
                "memory.IoFailure" or "memory.InvalidPath" or "memory.unavailable" =>
                    "The memory folder couldn't be used, so check it in Companion › Memory.",
                "memory.clock_invalid" => "The system clock needs fixing before Martlet can save facts.",
                "runtime.Failed" or "runtime.Partial" or "runtime.Canceled" =>
                    "Thinking didn't finish its answer, so Martlet tries again with the next thing you say.",
                "conversation.input_limit" => "That exchange was too long to remember.",
                _ => Remedy(code)
            };

    internal static string Remedy(string code) => code switch
    {
        "conversation.canceled" => "Stopped.",
        "conversation.focus_lost" => "The recording was thrown away because the talk button lost focus, so nothing was sent.",
        "conversation.ownership_busy" => "Martlet is still finishing something else, so try again in a moment.",
        "conversation.setup_required" or "conversation.configuration_unsupported" =>
            "Martlet can't use its current setup, so review Thinking (and Voice or Listening) in Companion.",
        "conversation.configuration_changed" => "Your setup changed, so Martlet stopped; try again.",
        "conversation.controls_blocked" or "conversation.locked" => "Windows is locked, so Martlet stopped.",
        "conversation.revoked" => "Stopped.",
        "conversation.input_limit" => "That's too long for Martlet's Thinking model, so try a shorter message.",
        "conversation.invalid_input" => "That message can't be sent, so use shorter plain text.",
        "conversation.expired" => "That took too long, so Martlet gave up; try again.",
        "memory.disabled" => "Memory is off, so turn it on in Companion › Memory.",
        "memory.configuration_changed" => "Memory settings changed during the reply, so try again.",
        "memory.retrieval_invalidated" => "Memory changed while Martlet was reading it, so try again.",
        "stt.deadline_exceeded" => "Transcribing took too long, so try again or type instead.",
        "stt.NoSpeech" => "Martlet didn't catch any words, so try again or type instead.",
        "mic.no_speech" => "Martlet didn't hear anything, so nothing was sent.",
        "voiceid.not_enrolled" => "Only respond to my voice is on, but Voice ID isn't set up (set it up or turn it off in Companion › Listening).",
        "speaker.not_user" => "Voice ID didn't recognize your voice, so nothing was sent.",
        "speaker.too_short" => "That was too short for Voice ID to recognize you, so nothing was sent.",
        "runtime.Refused" => "Martlet declined to answer that.",
        "runtime.Partial" or "runtime.Failed" or "runtime.Canceled" => "Martlet's reply didn't finish, so try again.",
        "conversation.cleanup_quarantined" or "mic.cleanup_quarantined" =>
            "Martlet couldn't confirm that the microphone or speakers stopped, so restart Martlet before talking.",
        "commentary.vision_unsupported" => "Your Thinking model can't see images, so vision is off (Companion › Vision says what to change).",
        "commentary.interrupted" or "commentary.stopped" => "Martlet stopped its remark.",
        _ when code.StartsWith("policy.", StringComparison.Ordinal) => "Martlet didn't reply to that.",
        _ => "Something went wrong, so try again (Settings › Troubleshooting has the details)."
    };
}

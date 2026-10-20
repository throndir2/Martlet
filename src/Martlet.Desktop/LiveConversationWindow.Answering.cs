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

// Answering what was heard: one message once you pause, with what the PC played.
public partial class LiveConversationWindow
{
    // Everything heard since the last reply goes to the Thinking model as one message once you pause; it decides whether to
    // answer. Waits while you are still talking or what you said is being transcribed, and a moment longer when it sounds
    // unfinished. What the PC played meanwhile goes with it, each line marked as the PC's; on its own it goes only every
    // PcPace (or once the PC goes quiet for PcLull), never while you talk.
    private bool TryAnswer()
    {
        if (listener is { } live && (live.Hearing || live.Transcribing > 0)) return false;
        // A line the PC played that may still be your own voice played back is told apart first.
        if (pcHeld.Count > 0) return false;
        if (heardQueue.Count > 0)
        {
            // What went straight to Thinking has no words yet to sound unfinished.
            if (heardQueue[^1].Words is null && Unfinished(heardQueue[^1].Text) && clock.GetElapsedTime(lastHeard) < UnfinishedPause)
                return false;
        }
        else if (!PcDue()) return false;
        var batch = new List<HeardEntry>();
        var length = -1;
        for (var i = heardQueue.Count - 1; i >= 0; i--)
        {
            length += heardQueue[i].Text.Length + 1;
            if (length > MaximumMessage && batch.Count > 0) break;
            batch.Insert(0, heardQueue[i]);
        }
        // Every utterance's recording together, or none when one is missing or they run too long: never half of what was said.
        var recordings = batch.Select(entry => entry.Recording).ToArray();
        var hearing = HearsVoice;
        var joined = hearing.On && batch.Count > 0 && recordings.All(r => r is not null)
            ? BoundedWaveAudio.Join(recordings.Select(r => r!).ToArray(), HeardGap, TimeSpan.FromSeconds(BoundedTextInput.MessageAudioSeconds))
            : null;
        // Straight to Thinking only when everything said went that way and nothing the PC played goes with it; otherwise what went
        // straight waits for its words and goes as words (transcribed first), like the rest.
        var straight = joined is not null && playingQueue.Count == 0 && batch.All(entry => entry.Words is not null);
        if (!straight)
        {
            // Its words are needed now: transcribe what went straight without waiting for a reply.
            foreach (var entry in batch) entry.Words?.Release();
            if (batch.Any(entry => entry.Words is { IsReady: false })) return false;
            batch = [.. batch.Where(entry => entry.Words is null || entry.Words is { Text.Length: > 0, Ignored: null })
                .Select(entry => entry.Words is { } words ? entry with { Text = words.Text!, Confidence = words.Confidence, Words = null } : entry)];
            length = batch.Count == 0 ? -1 : batch.Sum(entry => entry.Text.Length + 1) - 1;
        }
        foreach (var skipped in heardQueue.Take(heardQueue.Count - recordings.Length))
        {
            skipped.Bubble.AddNote("Not answered.");
            skipped.Voice?.Cancel();
        }
        heardQueue.Clear();
        // The newest of what the PC played that fits beside your own words.
        var playing = TakePlaying(length);
        CallAnswered();
        if (batch.Count == 0 && playing.Count == 0) return false;
        var everything = batch.Concat(playing).OrderBy(entry => entry.At).ToList();
        // One moment: the reply takes what else waits too (finished work Martlet may bring up, and the look vision wanted). A
        // reply to you only takes what is already there; it never waits for a look or a job.
        var plan = MomentTurn.Plan(batch.Count > 0 ? MomentTrigger.User : MomentTrigger.PcAudio, playing.Count > 0, JobsWaiting, LookDue);
        var (seen, about) = Sight(plan.Look);
        try
        {
            // What the PC played has no recording, so a message with it goes as words only.
            var recording = straight ? joined
                : hearing.On && playing.Count == 0 && batch.Count > 0 && batch.All(entry => entry.Recording is not null)
                    ? BoundedWaveAudio.Join(batch.Select(entry => entry.Recording!).ToArray(), HeardGap,
                        TimeSpan.FromSeconds(BoundedTextInput.MessageAudioSeconds)) : null;
            // The reply's wait counts from when you last stopped talking (a copy, so a restarted reply counts from there again).
            var timeline = batch.Count > 0 ? batch[^1].Timeline?.Copy() : null;
            // The audio model's words about how you sounded, when an audio model of its own hears you (never waited for).
            VoiceNote[] voiceNotes = [.. batch.Select(entry => entry.Voice).OfType<VoiceNote>()];
            owned = straight
                ? controller.Start(null, Voice, microphone: false, approved: true, spoken: true, heard: batch[^1].Voices,
                    recording: recording, seen: seen, timeline: timeline, words: [.. batch.Select(entry => entry.Words!)],
                    hearLocalOnly: hearing.LocalOnly, attention: about, look: plan.Look, discordCall: CallOn)
                : playing.Count == 0
                ? controller.Start(string.Join(" ", batch.Select(entry => entry.Text)), Voice, microphone: false, approved: true,
                    spoken: true, heard: batch[^1].Voices, confidence: batch.Min(entry => entry.Confidence), recording: recording,
                    seen: seen, timeline: timeline, chattiness: BackgroundChattiness, hearLocalOnly: hearing.LocalOnly,
                    attention: about, look: plan.Look, discordCall: CallOn, voiceNotes: voiceNotes)
                : controller.Start(PcMessage(everything), Voice, microphone: false, approved: true, spoken: true,
                    heard: batch.Count > 0 ? batch[^1].Voices : null,
                    confidence: batch.Count > 0 ? batch.Min(entry => entry.Confidence) : playing.Min(entry => entry.Confidence),
                    seen: seen, pcAudio: true, userWords: batch.Count > 0 ? string.Join(" ", batch.Select(entry => entry.Text)) : null,
                    timeline: timeline, chattiness: BackgroundChattiness, bringUp: batch.Count == 0 && plan.Jobs, attention: about,
                    look: plan.Look, discordCall: CallOn, voiceNotes: voiceNotes);
            answering = everything;
            yielded = null;
            answeredAt = clock.GetTimestamp();
            TookLook(owned);
            Observe();
            return true;
        }
        catch (LiveActionException error) when (error.Code == "conversation.ownership_busy")
        {
            Requeue(everything);
            // A reply started early for other words is leaving the app slot: answer as soon as it has, not at the next tick.
            if (controller.SlotFreed is { IsCompleted: false } freed)
                freed.ContinueWith(_ => Dispatcher.InvokeAsync(Pump), TaskScheduler.Default);
            return false;
        }
        catch (LiveActionException error) { notice = Remedy(error.Code); }
        catch (ContractException) { notice = Remedy("conversation.invalid_input"); }
        if (batch.Count > 0) batch[^1].Bubble.AddNote("Not answered.");
        foreach (var entry in batch) entry.Voice?.Cancel();
        return false;
    }

    // What waited for a reply goes back in line (a restart, or the app slot was busy).
    private void Requeue(List<HeardEntry> entries)
    {
        heardQueue.InsertRange(0, entries.Where(entry => !entry.Pc));
        playingQueue.InsertRange(0, entries.Where(entry => entry.Pc));
    }

    // The newest of what the PC played that fits beside `length` characters of your own words (-1: none); the rest is let go.
    private List<HeardEntry> TakePlaying(int length)
    {
        var playing = new List<HeardEntry>();
        var pcLength = 0;
        for (var i = playingQueue.Count - 1; i >= 0; i--)
        {
            var size = playingQueue[i].Text.Length + PcLine.Length + 1 + (PcActivity.From(playingQueue[i].From?.Named)?.Length + 2 ?? 0);
            if (pcLength + size > MaximumPcMessage || Math.Max(length, 0) + pcLength + size > MaximumMessage) break;
            pcLength += size;
            playing.Insert(0, playingQueue[i]);
        }
        playingQueue.Clear();
        return playing;
    }

    /// <summary>One message in the order things were heard: each line the PC played starts with
    /// <see cref="LiveConversationConfiguration.PcAudioMarker"/>, and the user's own words run together between them.</summary>
    internal static string PcMessage(IEnumerable<(string Text, bool Pc)> heard) =>
        PcMessage(heard.Select(entry => (entry.Text, entry.Pc, (IReadOnlyList<PcSource>?)null)));

    /// <summary>One message in the order things were heard: each line the PC played starts with
    /// <see cref="LiveConversationConfiguration.PcAudioMarker"/> and, when Martlet can tell, where it came from ("[PC audio] From
    /// a YouTube video in Chrome: ..."); the user's own words run together between them.</summary>
    internal static string PcMessage(IEnumerable<(string Text, bool Pc, IReadOnlyList<PcSource>? Sources)> heard)
    {
        var lines = new List<string>();
        var own = new List<string>();
        foreach (var (text, pc, sources) in heard)
        {
            if (!pc)
            {
                own.Add(text);
                continue;
            }
            if (own.Count > 0) lines.Add(string.Join(" ", own));
            own.Clear();
            lines.Add(PcLine + (PcActivity.From(sources) is { } from ? from + ": " : "") + text);
        }
        if (own.Count > 0) lines.Add(string.Join(" ", own));
        return string.Join("\n", lines);
    }

    private static string PcMessage(List<HeardEntry> heard) => PcMessage(heard.Select(entry => (entry.Text, entry.Pc, entry.From?.Named)));

    /// <summary>What the PC played, on its own, is offered to Martlet once PcPace passed since Martlet last answered anything
    /// (and since the oldest line waited), or once the PC has gone quiet for PcLull; never while it is still mid-sentence, and
    /// never as a second reply right after Martlet answered you.</summary>
    private bool PcDue()
    {
        if (playingQueue.Count == 0 || pcListener is { Hearing: true } or { Transcribing: > 0 }) return false;
        if (CallDue()) return true;
        var pace = PcPace;
        if (answeredAt != 0 && clock.GetElapsedTime(answeredAt) < pace) return false;
        return clock.GetElapsedTime(playingQueue[0].At) >= pace || clock.GetElapsedTime(pcHeardAt) >= PcLull;
    }

    // When Martlet was last asked to answer: what you said or typed, or what the PC played.
    private long answeredAt;
    /// <summary>At most how often what the PC played, on its own, goes to Martlet, counted from its last answer: 20 seconds,
    /// 45 while Martlet is quiet and 12 while it is chatty (<see cref="ScreenCommentaryPacer.PcPace"/>).</summary>
    private TimeSpan PcPace => ScreenCommentaryPacer.PcPace(ChattinessNow);
    /// <summary>How long the PC must stay quiet before what it played goes to Martlet sooner than PcPace.</summary>
    internal static TimeSpan PcLull => TimeSpan.FromSeconds(4);
    private static readonly string PcLine = LiveConversationConfiguration.PcAudioMarker + " ";
    // Of a message, at most this much is what the PC played (the newest), so your own words always fit.
    private const int MaximumPcMessage = 1500;
    private const int MaximumPcLine = MaximumPcMessage - 16;
    // A PC bubble in the history takes the PC's next lines until it is about this long.
    private const int MaximumPcBubble = 1200;

    private const int MaximumMessage = BoundedTextInput.HardMaxMessageCharacters;
    // Silence between utterances answered together, when their recordings go to a Thinking model that hears.
    private static readonly TimeSpan HeardGap = TimeSpan.FromMilliseconds(300);
    private static readonly HashSet<string> Continuations = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "so", "because", "cause", "um", "uh", "er", "erm", "like", "the", "a", "an", "to", "of", "with",
        "if", "then", "that", "which", "my", "your", "is", "was", "for", "in", "on", "at"
    };

    /// <summary>What you said sounds unfinished: it trails off with a comma, dash or ellipsis, or ends on a word like "and" or "um".</summary>
    internal static bool Unfinished(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || trimmed.EndsWith('?') || trimmed.EndsWith('!')) return false;
        if (trimmed.EndsWith(',') || trimmed.EndsWith('…') || trimmed.EndsWith("...", StringComparison.Ordinal) ||
            trimmed.EndsWith('-') || trimmed.EndsWith('\u2014') || trimmed.EndsWith(':')) return true;
        var last = trimmed.TrimEnd('.', '"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is not null && Continuations.Contains(last);
    }

    /// <summary>Keeps how soon the Thinking model's first words came on its server (model-speed.json), after the reply ended and
    /// on a thread-pool thread, so Recommended setup plans with the measured time instead of the estimate. Never on the reply's
    /// path; a reply the fallback, Backup Thinking or hidden reasoning answered first isn't the model's own time.</summary>
    private void KeepFirstWord(LiveConversationConfiguration configuration, ConversationSnapshot reply)
    {
        if (controller.DataDirectory is not { } directory || ReplyLatency.ThinkingFirstWordMs(reply) is not { } ms ||
            configuration.Routes.ThinkingRoute() is not { Enabled: true } route) return;
        var (origin, model, at) = (route.Origin, route.ModelId, DateTimeOffset.UtcNow);
        _ = Task.Run(() => Martlet.Core.Planning.MeasuredFirstWords.Record(directory, origin, model, ms, at));
    }
}

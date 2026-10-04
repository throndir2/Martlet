using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Martlet.Providers;

/// <summary>How readily always listening takes what it hears as talking (Companion › Listening › Word check): Relaxed needs
/// clearer, longer speech to answer or to stop Martlet, Sensitive reacts to short words too. Normal is the default (0, so a file
/// or message without it reads as Normal).</summary>
public enum ListeningSensitivity { Normal = 0, Relaxed = 1, Sensitive = 2 }

/// <summary>What an utterance turned out to be: words, or why it isn't.</summary>
public enum UtteranceKind
{
    /// <summary>Words worth a turn (or an interruption).</summary>
    Words,
    /// <summary>Nothing, or only punctuation.</summary>
    Empty,
    /// <summary>Only a sound marker such as [Music], (laughs) or *cough*.</summary>
    Sound,
    /// <summary>Only fillers and vocalizations: mm, hmm, uh, um, oh, haha.</summary>
    Filler,
    /// <summary>A phrase speech-to-text is known to make up from noise ("Thank you.", "Thanks for watching!", subtitle credits)
    /// with weak evidence that it was said.</summary>
    Hallucination,
    /// <summary>A lone word that says nothing on its own ("the", "so"), or one too short or unclear to be sure of.</summary>
    Fragment,
    /// <summary>More words than the voice could have held.</summary>
    Unlikely,
    /// <summary>The engine itself wasn't sure anything was said.</summary>
    Unsure
}

/// <summary>The filter's verdict on one utterance: keep it (a turn, or words that may stop Martlet) or drop it, with a short
/// plain-language reason the talk window shows ("not words") and how many real words it found.</summary>
public sealed record UtteranceDecision(bool Keep, UtteranceKind Kind, string Reason, int Words)
{
    /// <summary>What the talk window shows for a dropped utterance: Ignored "Mmm" (not words).</summary>
    public string Describe(string? text)
    {
        var shown = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (shown.Length > 40) shown = shown[..39].TrimEnd() + "…";
        return shown.Length == 0 ? $"Ignored a sound ({Reason})." : $"Ignored \u201C{shown}\u201D ({Reason}).";
    }
}

/// <summary>What is known about an utterance besides its transcript: how much of it was a voice (loud 20 ms frames the speakers
/// don't explain), how long that voice went on, what the speech-to-text engine said about it, whether Martlet had just asked
/// something, and the names that address Martlet.</summary>
public sealed record UtteranceContext
{
    /// <summary>The loudest part of the voice: 20 ms frames as loud as speech must be to start, which the speakers don't explain.
    /// Fluent speech is only partly this loud (its consonants and short words are quieter).</summary>
    public TimeSpan? Voiced { get; init; }
    /// <summary>How long the voice went on: from where it began to where it ended, short pauses included, leaving out frames
    /// the speakers explain. What bounds how many words the utterance can hold (<see cref="Voiced"/> when unknown).</summary>
    public TimeSpan? Speech { get; init; }
    public TranscriptionEvidence? Evidence { get; init; }
    /// <summary>Martlet's last reply asked something a moment ago, so a short answer ("yes", "mm-hmm") is expected.</summary>
    public bool AfterQuestion { get; init; }
    /// <summary>Names that address Martlet (the persona's name; "Martlet" always counts).</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>Drops what always listening heard that isn't words before it becomes a turn or stops Martlet: fillers and
/// vocalizations (mm, hmm, uh, um, uh-huh, oh, laughter), sound markers ([Music], (coughs)), punctuation, a lone word that says
/// nothing, more words than the voice could hold, and phrases speech-to-text makes up from noise ("Thank you.", "Thanks for
/// watching!", subtitle credits) when the evidence that they were said is weak. The engine's own evidence counts where it gives
/// any (Parakeet's token probabilities, whisper's no-speech and log probabilities), and so does how much of the utterance was a
/// voice. A genuine short answer ("yes", "no", "stop", "wait", "okay") still works, more readily right after Martlet asked
/// something, and anything with Martlet's name is kept. Local, deterministic and allocation-light: it adds no request and no
/// measurable time to a reply.</summary>
public static class UtteranceFilter
{
    /// <summary>The limits one sensitivity applies. A short utterance whose mean token probability is below
    /// <paramref name="MinimumProbability"/> is dropped as unsure; for whisper, a no-speech probability above
    /// <paramref name="MaximumNoSpeech"/> with an average log probability below <paramref name="MinimumLogProbability"/> means
    /// nothing was said; an utterance holds at most <paramref name="WordsPerSecond"/> words per second of speech
    /// (<see cref="UtteranceContext.Speech"/>) plus <paramref name="SpareWords"/>; a lone word (not a short answer) needs
    /// <paramref name="LoneWordVoice"/> of voice; and a phrase speech-to-text makes up from noise needs
    /// <paramref name="MadeUpPhraseVoice"/> of voice when the engine says nothing about it.</summary>
    public sealed record Limits(double MinimumProbability, double MaximumNoSpeech, double MinimumLogProbability,
        double WordsPerSecond, int SpareWords, TimeSpan LoneWordVoice, TimeSpan MadeUpPhraseVoice);

    public static Limits For(ListeningSensitivity sensitivity) => sensitivity switch
    {
        ListeningSensitivity.Sensitive => new(0.30, 0.80, -1.2, 8, 2, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(250)),
        ListeningSensitivity.Relaxed => new(0.60, 0.45, -0.8, 6, 1, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600)),
        _ => new(0.45, 0.60, -1.0, 7, 1, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400))
    };

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "m", "mm", "mmm", "hm", "hmm", "mhm", "mmhm", "mmhmm", "uh", "um", "er", "erm", "ah", "oh", "eh", "huh", "uh-huh", "uhhuh",
        "mm-hmm", "mm-hm", "m-hm", "uh-uh", "mm-mm", "hmph", "psst", "pfft", "tsk", "ugh", "ow", "oof", "ahem", "aw", "ooh",
        "heh", "hee", "ha", "haha", "hehe", "uhm", "umm", "hum", "hmmm", "ahh", "ohh", "uhh", "err", "mmmm", "nn", "nnn", "u", "o"
    };

    // Elongated forms: mmmm, hmmmm, uhhhh, ummmm, ahhhh, ohhhh, errrm, hahaha, hehehe, mm-hmmm, uh-huhhh.
    private static readonly System.Text.RegularExpressions.Regex Elongated = new(
        @"\A(?:m+|h+m+|m+h+m*|u+h+|u+m+|a+h+|o+h+|e+r+m*|e+h+|h+u+h+|a+w+|o+o+h*|n+|(?:h+a+)+h?|(?:h+e+)+h?|m+-?h+m+|u+h+-?h+u+h+|(?:m+-)+m+)\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex Laughter = new(@"\A(?:(?:h+a+)+h?|(?:h+e+)+h?)\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // "uh-huh" and "mm-hmm" are yes, and "uh-uh" and "mm-mm" no, to a question Martlet just asked.
    private static readonly HashSet<string> AnswerSounds = new(StringComparer.Ordinal)
    {
        "uh-huh", "uhhuh", "mm-hmm", "mm-hm", "m-hm", "mhm", "mmhm", "mmhmm", "uh-uh", "mm-mm"
    };

    /// <summary>Short answers and commands that are real on their own.</summary>
    private static readonly HashSet<string> ShortAnswers = new(StringComparer.Ordinal)
    {
        "yes", "yeah", "yep", "yup", "yea", "ya", "no", "nope", "nah", "okay", "ok", "sure", "stop", "wait", "please", "hi", "hello",
        "hey", "right", "correct", "exactly", "maybe", "why", "what", "how", "who", "when", "where", "which", "cool", "nice", "great",
        "good", "fine", "done", "go", "continue", "again", "sorry", "pardon", "absolutely", "definitely", "agreed", "true", "false",
        "never", "always", "enough", "quiet", "shh", "shush", "later", "now", "help", "louder", "slower", "faster", "repeat", "next",
        "more", "less", "both", "neither", "either", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "first", "second", "third", "last", "awesome", "perfect", "really", "seriously", "wow", "oops", "pause", "resume", "cancel",
        "skip", "quieter", "softer", "goodnight", "morning", "night", "not"
    };

    // A lone word like these says nothing on its own (and "you" and "so" are what whisper often hears in noise).
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "so", "of", "to", "in", "on", "at", "for", "with", "from", "by", "as", "is", "it", "its",
        "it's", "i", "i'm", "you", "he", "she", "we", "they", "me", "him", "us", "them", "my", "your", "this", "that", "these",
        "those", "there", "then", "than", "if", "be", "been", "was", "were", "are", "am", "do", "did", "does", "just", "like", "well",
        "um", "uh", "too", "also", "not"
    };

    // Phrases speech-to-text is known to write for silence, music or noise: whisper learned these from subtitles, and Parakeet
    // writes "Yeah." for a short burst of noise (a cough, a breath, a beat of music).
    private static readonly HashSet<string> MadeUpPhrases = new(StringComparer.Ordinal)
    {
        "thank you", "thank you very much", "thank you so much", "thanks", "thank you bye", "thanks bye", "bye", "bye bye",
        "goodbye", "the end", "thanks for listening", "thank you for listening", "see you", "see you next time", "see you later",
        "please subscribe", "subscribe", "music", "applause", "silence", "you", "so", "okay bye", "i'm sorry", "sorry", "yeah"
    };

    // Credits and calls to subscribe: nobody says these to Martlet, and whisper writes them from noise.
    private static readonly string[] Credits =
    [
        "thanks for watching", "thank you for watching", "like and subscribe", "subscribe to", "subtitles by", "subtitle by",
        "captions by", "caption by", "transcribed by", "translated by", "transcription by", "amara.org", "www.", "http",
        ".com", "copyright", "ご視聴ありがとうございました", "продолжение следует", "sous-titres", "untertitel", "sottotitoli",
        "subtítulos", "legendas"
    ];

    /// <summary>The transcript without sound markers ([Music], (laughs), *coughs*, ♪), and whether it had any.</summary>
    public static string WithoutSounds(string text, out bool hadSounds)
    {
        var builder = new StringBuilder(text.Length);
        hadSounds = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var close = c switch { '[' => ']', '(' => ')', '*' => '*', '<' => '>', '{' => '}', _ => '\0' };
            if (close != '\0' && text.IndexOf(close, i + 1) is var end && end > i && end - i <= 64)
            {
                hadSounds = true;
                builder.Append(' ');
                i = end;
                continue;
            }
            if (c is '♪' or '♫' or '♬' || char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) &&
                char.ConvertToUtf32(c, text[i + 1]) is 0x1F3B5 or 0x1F3B6)
            {
                hadSounds = true;
                builder.Append(' ');
                if (char.IsHighSurrogate(c)) i++;
                continue;
            }
            builder.Append(char.IsControl(c) ? ' ' : c);
        }
        return builder.ToString().Trim();
    }

    /// <summary>The words of a transcript, lowercase, without punctuation (letters, digits, inner apostrophes and hyphens).</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            var inner = c is '\'' or '\u2019' or '-' && word.Length > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
            if (char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark || inner)
            {
                word.Append(c == '\u2019' ? '\'' : char.ToLowerInvariant(c));
                continue;
            }
            if (word.Length > 0) words.Add(word.ToString());
            word.Clear();
        }
        return words;
    }

    /// <summary>A filler or vocalization, not a word: mm, hmm, uh, um, uh-huh, oh, haha (and their stretched forms).</summary>
    public static bool IsFiller(string word) => Fillers.Contains(word) || Elongated.IsMatch(word);

    /// <summary>A phrase speech-to-text makes up from noise; <paramref name="credit"/> when it is a credit or a call to subscribe,
    /// which nobody says to Martlet.</summary>
    public static bool IsMadeUpPhrase(string text, out bool credit)
    {
        var lower = text.ToLowerInvariant();
        credit = Credits.Any(c => lower.Contains(c, StringComparison.Ordinal));
        return credit || MadeUpPhrases.Contains(string.Join(" ", Words(text)));
    }

    /// <summary>Keep or drop one utterance (always listening; push-to-talk is never filtered).</summary>
    public static UtteranceDecision Check(string? text, UtteranceContext context, ListeningSensitivity sensitivity)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(text)) return new(false, UtteranceKind.Empty, "nothing was said", 0);
        var limits = For(sensitivity);
        var spoken = WithoutSounds(text, out var sounds);
        var words = Words(spoken);
        if (words.Count == 0)
            return new(false, sounds ? UtteranceKind.Sound : UtteranceKind.Empty, sounds ? "a sound, not words" : "not words", 0);
        if (Addressed(words, context.Names)) return new(true, UtteranceKind.Words, "Martlet's name", words.Count);
        var question = spoken.TrimEnd().EndsWith('?');
        if (words.All(IsFiller))
        {
            if (context.AfterQuestion && words.Any(AnswerSounds.Contains))
                return new(true, UtteranceKind.Words, "an answer to Martlet's question", words.Count);
            return new(false, UtteranceKind.Filler, words.All(Laughter.IsMatch) ? "laughter, not words" : "not words", 0);
        }
        var content = words.Where(w => !IsFiller(w)).ToArray();
        var count = content.Length;
        var evidence = context.Evidence;
        var shortAnswer = count <= 3 && content.All(ShortAnswers.Contains);

        // Phrases speech-to-text makes up from noise go when nothing shows they were really said.
        if (IsMadeUpPhrase(spoken, out var credit) && !question)
        {
            if (credit && !Clear(evidence)) return new(false, UtteranceKind.Hallucination, "speech-to-text makes this up from noise", 0);
            if (!credit && (Weak(evidence, limits) || evidence is null && !(context.Voiced >= limits.MadeUpPhraseVoice)) &&
                !(context.AfterQuestion && shortAnswer))
                return new(false, UtteranceKind.Hallucination, "speech-to-text often hears this in noise", 0);
        }
        // whisper repeating itself ("Thank you. Thank you. Thank you. ...") is a decoding loop, not speech.
        if (spoken.Length >= 40 && CompressionRatio(spoken) > 2.4)
            return new(false, UtteranceKind.Hallucination, "it repeats itself like a speech-to-text glitch", 0);
        // Measured against how long the voice went on, not only its loudest frames: "I'm gonna make it public." is about a second
        // of speech but may be under half a second of loud voice.
        var bySpeech = context.Speech is { } spoke && !(context.Voiced > spoke);
        if ((bySpeech ? context.Speech : context.Voiced) is { } span && count > span.TotalSeconds * limits.WordsPerSecond + limits.SpareWords)
            return new(false, UtteranceKind.Unlikely, $"{count} words from {span.TotalMilliseconds:0} ms of {(bySpeech ? "speech" : "voice")}", 0);
        if (evidence is { NoSpeechProbability: { } noSpeech, AverageLogProbability: { } logProbability } &&
            noSpeech > limits.MaximumNoSpeech && logProbability < limits.MinimumLogProbability)
            return new(false, UtteranceKind.Unsure, "speech-to-text heard no speech", 0);
        if (count <= 3)
        {
            // Right after a question, and for a short answer, the engine may be less sure: "yes" is short and easy to mishear.
            var lenient = (context.AfterQuestion ? 0.6 : 1) * (shortAnswer ? 0.6 : 1);
            if (evidence?.MeanProbability is { } probability && probability < limits.MinimumProbability * lenient)
                return new(false, UtteranceKind.Unsure, "speech-to-text wasn't sure", 0);
            if (evidence?.AverageLogProbability is { } average && average < (limits.MinimumLogProbability - 0.5) / lenient)
                return new(false, UtteranceKind.Unsure, "speech-to-text wasn't sure", 0);
        }
        if (count == 1 && !question)
        {
            var word = content[0];
            if (FunctionWords.Contains(word) && !(shortAnswer && context.AfterQuestion))
                return new(false, UtteranceKind.Fragment, "a lone word", 0);
            if (!shortAnswer && !context.AfterQuestion)
            {
                if (context.Voiced is { } alone && alone < limits.LoneWordVoice)
                    return new(false, UtteranceKind.Fragment, "too short to be sure", 0);
                // Relaxed: one stray word needs the engine to be clearly sure of it.
                if (sensitivity == ListeningSensitivity.Relaxed && !(evidence?.MeanProbability >= 0.75))
                    return new(false, UtteranceKind.Fragment, "a lone word", 0);
            }
        }
        return new(true, UtteranceKind.Words, shortAnswer ? "a short answer" : "words", count);
    }

    /// <summary>Whether any of <paramref name="names"/> (or "Martlet") is among the words.</summary>
    public static bool Addressed(IReadOnlyList<string> words, IReadOnlyList<string> names)
    {
        if (words.Count == 0) return false;
        var joined = " " + string.Join(" ", words) + " ";
        return names.Append("Martlet").Select(name => string.Join(" ", Words(name))).Where(name => name.Length > 0)
            .Any(name => joined.Contains(" " + name + " ", StringComparison.Ordinal));
    }

    // The engine was unsure enough that a phrase it often makes up probably wasn't said. Parakeet's "Yeah." from noise has a
    // fair mean probability but one token it was far from sure of.
    private static bool Weak(TranscriptionEvidence? evidence, Limits limits) => evidence is not null && (
        evidence.MeanProbability < limits.MinimumProbability + 0.2 ||
        evidence.MinimumProbability < limits.MinimumProbability * 0.8 ||
        evidence.NoSpeechProbability > limits.MaximumNoSpeech * 0.5 ||
        evidence.AverageLogProbability < limits.MinimumLogProbability + 0.3);

    // The engine was clearly sure of what it heard.
    private static bool Clear(TranscriptionEvidence? evidence) => evidence is not null &&
        (evidence.MeanProbability ?? 0) >= 0.9 && !(evidence.NoSpeechProbability > 0.1) && !(evidence.AverageLogProbability < -0.3);

    /// <summary>whisper's hallucination measure: how much the text's UTF-8 bytes shrink when compressed (repetitive text compresses
    /// well; above 2.4 whisper treats it as a decoding loop).</summary>
    public static double CompressionRatio(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length == 0) return 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(bytes);
        return bytes.Length / (double)Math.Max(1, compressed.Length);
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Who is talking, for the Thinking model: one labeled block of background data in the reply's instructions, and the
/// short label earlier messages carry in the conversation history ("[Sam] ...").</summary>
internal static class VoicePromptContext
{
    internal const string Label = "MARTLET_VOICES";

    /// <summary>"[Sam] " for a recognized voice (its tag when it has no name yet), nothing otherwise.</summary>
    internal static string Prefix(HeardVoices? heard) =>
        heard?.Speaker?.Voice is { } voice ? $"[{Sanitize(voice.Named ? voice.DisplayName : voice.Tag)}] " : "";

    internal static string? Instructions(HeardVoices? heard, PromptSettings? prompts = null)
    {
        if (Block(heard) is not { } block) return null;
        return Preamble(heard, prompts) is { } preamble ? preamble + "\n" + block : block;
    }

    /// <summary>What the voices block means (Companion › Prompts › Who is talking), for the instructions; null without voices.</summary>
    internal static string? Preamble(HeardVoices? heard, PromptSettings? prompts = null) =>
        heard is null || heard.Voices.Count == 0 ? null : PromptSettings.Fill(prompts, PromptCatalog.Voices, ("label", Label));

    /// <summary>Who is talking in this message, between <see cref="Label"/> labels; null without voices.</summary>
    internal static string? Block(HeardVoices? heard)
    {
        if (heard is null || heard.Voices.Count == 0) return null;
        var text = new StringBuilder();
        text.Append('[').Append(Label).Append("]\n");
        text.Append("Speaking now: ").Append(Describe(heard.Speaker!)).Append('\n');
        foreach (var other in heard.Others) text.Append("Also heard in this message: ").Append(Describe(other)).Append('\n');
        if (heard.Overlap) text.Append("People talked over each other in this message.\n");
        return text.Append("[/").Append(Label).Append(']').ToString();
    }

    private static string Describe(HeardVoice heard)
    {
        if (heard.Voice is not { } voice)
            return "a voice Martlet couldn't tell apart this time (too short or unclear).";
        var name = voice.Named ? Sanitize(voice.DisplayName) : null;
        var also = voice.OtherNames.Select(Sanitize).ToArray();
        return (name is null ? $"someone whose name Martlet doesn't know yet (voice {voice.Tag}" : $"{name} (voice {voice.Tag}") +
            (also.Length > 0 ? "; also called " + string.Join(", ", also) : "") +
            (voice.Owner ? "; the owner of this PC" : "") +
            (heard.Added ? "; heard for the first time" : "") + ").";
    }

    internal static string Sanitize(string text) =>
        MemoryCapture.Clip(text.Replace(Label, "voices", StringComparison.OrdinalIgnoreCase).Replace("[", "(").Replace("]", ")"), VoiceRoster.MaximumNameLength);
}

internal sealed record VoiceNamingPrompt(BoundedTextInput Input, IReadOnlyDictionary<string, string> Voices);

/// <summary>What learning names reads besides the exchange: the voices heard in it (as the voice list has them now), other
/// voices Martlet knows when the words suggest someone is the same person as one of them (only those can be merged with a
/// heard voice), and the companion's own names, which are never a voice's.</summary>
internal sealed record VoiceNamingContext(HeardVoices Heard, IReadOnlyList<KnownVoice> Others, CompanionNames Companion)
{
    /// <summary>The listed voices' tags (V3) and IDs, which the answer's lines refer to.</summary>
    internal IReadOnlyDictionary<string, string> Tags { get; } = Heard.Known.Concat(Others).DistinctBy(v => v.Id)
        .ToDictionary(v => v.Tag, v => v.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The IDs of the voices heard in the exchange: the only ones that may get or lose names.</summary>
    internal IReadOnlyCollection<string> HeardIds { get; } = Heard.Known.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);

    public override string ToString() => $"{nameof(VoiceNamingContext)} {{ Heard = {Heard.Known.Count}, Others = {Others.Count} }}";
}

/// <summary>Learning names from what is said: after a reply, the same Thinking model reads the exchange and answers in a tiny
/// line format which names a voice goes by ("I'm Sam", "thanks, Sam"), the name it asks to be called, a name that was wrong, or
/// that two voices are one person. <see cref="VoiceUpdates"/> checks each line before it changes the voice list; the owner
/// can edit or undo names on the People page.</summary>
internal static partial class VoiceNaming
{
    internal const string Nothing = VoiceUpdates.Nothing;
    /// <summary>The most other known voices listed for a merge.</summary>
    internal const int MaximumOthers = 8;

    internal const string Instructions = PromptCatalog.DefaultVoiceNamingInstructions;

    /// <summary>Whether the exchange is worth asking about: a voice with no name yet, words that often come with a name (not
    /// a greeting to the companion by its own name), or words that say two voices are one person.</summary>
    internal static bool Worth(HeardVoices heard, string user, string reply, CompanionNames companion) =>
        heard.Known.Any(v => !v.Named) || NameCameUp(user, companion) || NameCameUp(reply, companion) || SamePerson(user);

    /// <summary>The voices heard (refreshed from <paramref name="roster"/>, so names learned since are shown) and, when the
    /// words suggest someone is the same person as a voice Martlet knows, up to <see cref="MaximumOthers"/> named others.</summary>
    internal static VoiceNamingContext Context(HeardVoices heard, VoiceRoster roster, string user, CompanionNames companion)
    {
        var current = heard with
        {
            Voices = heard.Voices.Select(v => v.Voice is { } voice ? v with { Voice = roster.Resolve(voice.Id) } : v).ToArray()
        };
        var ids = current.Known.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var others = SamePerson(user)
            ? roster.Live.Where(v => v.Named && !ids.Contains(v.Id)).Take(MaximumOthers).ToArray()
            : [];
        return new(current, others, companion);
    }

    internal static VoiceNamingPrompt Prompt(VoiceNamingContext naming, string? earlierUser, string? earlierReply, string user, string reply,
        PromptSettings? prompts = null)
    {
        var text = new StringBuilder();
        AppendVoices(text, naming);
        if (earlierUser is not null || earlierReply is not null)
        {
            text.Append("\nEarlier in the conversation (context only):\n");
            if (earlierUser is not null) text.Append("User: ").Append(MemoryCapture.Clip(earlierUser, 300)).Append('\n');
            if (earlierReply is not null) text.Append("Martlet: ").Append(MemoryCapture.Clip(earlierReply, 300)).Append('\n');
        }
        text.Append("\nLatest exchange:\n").Append(UserLabel(naming.Heard))
            .Append(MemoryCapture.Clip(user, 1400)).Append("\nMartlet: ").Append(MemoryCapture.Clip(reply, 800));
        var input = new BoundedTextInput(text.ToString(), PromptSettings.Fill(prompts, PromptCatalog.VoiceNaming, ("nothing", Nothing)));
        if (input.Utf8Bytes > LiveConversationConfiguration.DefaultTextLimits.MaxInputBytes ||
            input.InputTokenReservation > LiveConversationConfiguration.DefaultTextLimits.MaxInputTokens)
            throw new LiveActionException("conversation.input_limit");
        return new(input, naming.Tags);
    }

    /// <summary>The companion's own names, the voices heard in the message (each tag with every name it goes by, and which
    /// one spoke to Martlet) and any other voices listed for a merge.</summary>
    internal static void AppendVoices(StringBuilder text, VoiceNamingContext naming)
    {
        text.Append("Martlet's own names (the companion's, never one of the people's): ")
            .Append(string.Join(", ", naming.Companion.Names.Select(VoicePromptContext.Sanitize))).Append('\n');
        text.Append("Voices heard in the latest message:\n");
        foreach (var voice in naming.Heard.Known.DistinctBy(v => v.Id))
        {
            AppendVoice(text, voice);
            if (ReferenceEquals(voice, naming.Heard.Speaker?.Voice)) text.Append(" (the one speaking to Martlet)");
            text.Append('\n');
        }
        if (naming.Others.Count == 0) return;
        text.Append("Other voices Martlet knows (not heard in this message; only for SAME lines):\n");
        foreach (var voice in naming.Others)
        {
            AppendVoice(text, voice);
            text.Append('\n');
        }
    }

    private static void AppendVoice(StringBuilder text, KnownVoice voice) =>
        text.Append(voice.Tag).Append(": ").Append(voice.Named ? "goes by " + string.Join(", ",
            new[] { voice.DisplayName }.Concat(voice.OtherNames).Select(VoicePromptContext.Sanitize)) : "no name yet");

    /// <summary>"User (V3): " for the latest message's speaker, "User: " without one.</summary>
    internal static string UserLabel(HeardVoices heard) => heard.Speaker?.Voice is { } speaker ? $"User ({speaker.Tag}): " : "User: ";

    /// <summary>The changes the model's answer asks for, checked against the listed voices and the companion's names (with
    /// any name Martlet's <paramref name="reply"/> gave itself).</summary>
    internal static VoiceUpdateAnswer Parse(string? answer, VoiceNamingContext naming, string? reply) =>
        VoiceUpdates.Parse(answer, naming.Tags, naming.HeardIds, naming.Companion.WithReply(reply));

    /// <summary>Words that often come with a name; a greeting counts unless it greets the companion by one of its names.</summary>
    internal static bool NameCameUp(string text, CompanionNames companion)
    {
        if (NamePhrase().IsMatch(text)) return true;
        foreach (Match match in Greeting().Matches(text))
            if (!companion.Matches(match.Groups["name"].Value)) return true;
        return false;
    }

    /// <summary>Words saying two voices are one person ("that was me too", "it's me, Sam").</summary>
    internal static bool SamePerson(string text) => SamePhrase().IsMatch(text);

    [GeneratedRegex(@"\b(my name|name is|name's|call me|nickname|known as|introduce|meet|wrong name|not called|i'm called|" +
        @"i am called|go by|goes by)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePhrase();

    [GeneratedRegex(@"(?i:\b(hey|hi|hello|thanks|thank you|bye|goodbye|good night|good morning|sorry|okay|ok))\s*,?\s+(?<name>\p{Lu}\p{Ll}+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Greeting();

    [GeneratedRegex(@"\b((that|it)(['\u2019]s| is| was)( (just|also|still|really))? me|was me too|same person|both (of them are )?me|my voice)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SamePhrase();
}
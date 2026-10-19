using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Conversation;
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
            (heard.Mine ? "; the signed-in user" : "") +
            (heard.Added ? "; heard for the first time" : "") + ").";
    }

    internal static string Sanitize(string text) =>
        MemoryCapture.Clip(text.Replace(Label, "voices", StringComparison.OrdinalIgnoreCase).Replace("[", "(").Replace("]", ")"), VoiceRoster.MaximumNameLength);
}

internal sealed record VoiceNamingPrompt(BoundedTextInput Input, IReadOnlyDictionary<string, string> Voices);

/// <summary>What learning names reads besides the exchange: the voices heard in it (as the voice list has them now), other
/// voices Martlet knows when the words suggest someone is the same person as one of them (only those can be merged with a
/// heard voice) or, when Martlet couldn't tell whose voice said the message, voices the words name, and the companion's own
/// names, which are never a voice's.</summary>
internal sealed class VoiceNamingContext(HeardVoices heard, IReadOnlyList<KnownVoice> others, CompanionNames companion)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);

    internal HeardVoices Heard { get; } = heard;
    internal IReadOnlyList<KnownVoice> Others { get; } = others;
    internal CompanionNames Companion { get; } = companion;

    /// <summary>The voice that sounded most like the one speaking to Martlet when Martlet couldn't tell it apart for sure; null
    /// when it knew who spoke or nobody was close.</summary>
    internal KnownVoice? Closest => Unattributed ? Heard.Speaker?.Closest : null;

    /// <summary>Martlet heard someone speak to it but couldn't tell for sure whose voice it was: a SPOKE line may say.</summary>
    internal bool Unattributed => Heard.Speaker is { Voice: null };

    /// <summary>The listed voices' tags (V3) and IDs, which the answer's lines refer to, with the voices the people tools
    /// showed during the request (<see cref="Saw"/>).</summary>
    internal IReadOnlyDictionary<string, string> Tags
    {
        get
        {
            var tags = new Dictionary<string, string>(seen, StringComparer.OrdinalIgnoreCase);
            foreach (var voice in Listed) tags[voice.Tag] = voice.Id;
            return tags;
        }
    }

    /// <summary>The IDs of the voices heard in the exchange: the only ones that may get or lose names.</summary>
    internal IReadOnlyCollection<string> HeardIds { get; } = heard.Known.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The IDs a SPOKE line may name: when Martlet couldn't tell whose voice said the message, the closest voice, the
    /// other voices listed and those the people tools showed, never a voice heard for sure in it. Empty otherwise.</summary>
    internal IReadOnlyCollection<string> SpeakerIds => !Unattributed ? []
        : new[] { Closest }.OfType<KnownVoice>().Concat(Others).Select(v => v.Id).Concat(seen.Values)
            .Where(id => !HeardIds.Contains(id)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Notes the voices a people tool showed, so the answer may name them in a SPOKE line.</summary>
    internal void Saw(IEnumerable<KnownVoice> voices)
    {
        foreach (var voice in voices) seen[voice.Tag] = voice.Id;
    }

    private IEnumerable<KnownVoice> Listed => Heard.Known.Concat(Others).Concat(new[] { Closest }.OfType<KnownVoice>()).DistinctBy(v => v.Id);

    public override string ToString() => $"{nameof(VoiceNamingContext)} {{ Heard = {Heard.Known.Count}, Others = {Others.Count}, Unattributed = {Unattributed} }}";
}

/// <summary>Learning names: after a reply, one Thinking request whose only job is who the people speaking are. It reads the
/// voices Martlet recognized (and, when it wasn't sure whose voice said the message, the one it sounded most like) and the
/// exchange, and answers in a tiny line format which names a voice goes by ("I'm Sam", "thanks, Sam"), the name it asks to be
/// called, a name that was wrong, that two voices are one person, or whose voice said a message Martlet couldn't attribute
/// (SPOKE, a label only). <see cref="VoiceUpdates"/> checks each line before it changes the voice list; the owner can edit or
/// undo names on the People page.</summary>
internal static partial class VoiceNaming
{
    internal const string Nothing = VoiceUpdates.Nothing;
    /// <summary>What the log, the Thinking requests page and helper-jobs.json call the request.</summary>
    internal const string Purpose = "Learning names";
    /// <summary>Who calls the people tools in the request (<see cref="CheckInToolContext.CheckInId"/>).</summary>
    internal const string ToolCaller = "learning-names";
    /// <summary>The most other known voices listed for a merge or a SPOKE line.</summary>
    internal const int MaximumOthers = 8;
    /// <summary>How many tool rounds the request may use when it is offered the people tools.</summary>
    internal const int MaximumToolRounds = 4;

    internal const string Instructions = PromptCatalog.DefaultVoiceNamingInstructions;

    /// <summary>Whether the exchange is worth asking about: a voice with no name yet, someone Martlet couldn't tell apart for
    /// sure who sounded like a voice it knows, words that often come with a name (not a greeting to the companion by its own
    /// name), or words that say two voices are one person.</summary>
    internal static bool Worth(HeardVoices heard, string user, string reply, CompanionNames companion) =>
        heard.Known.Any(v => !v.Named) || heard.Speaker is { Voice: null, Closest: not null } ||
        NameCameUp(user, companion) || NameCameUp(reply, companion) || SamePerson(user);

    /// <summary>The voices heard (refreshed from <paramref name="roster"/>, so names learned since are shown) and up to
    /// <see cref="MaximumOthers"/> named others: when the words suggest someone is the same person as a voice Martlet knows,
    /// and, when Martlet couldn't tell whose voice said the message, the voices the words name.</summary>
    internal static VoiceNamingContext Context(HeardVoices heard, VoiceRoster roster, string user, CompanionNames companion)
    {
        var current = heard with
        {
            Voices = heard.Voices.Select(v => v with
            {
                Voice = v.Voice is { } voice ? roster.Resolve(voice.Id) : null,
                Closest = v.Closest is { } closest ? roster.Resolve(closest.Id) : null
            }).ToArray()
        };
        var ids = current.Known.Select(v => v.Id).Append(current.Speaker?.Closest?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var named = roster.Live.Where(v => v.Named && !ids.Contains(v.Id)).ToArray();
        var others = (current.Speaker is { Voice: null } ? named.Where(v => Mentioned(v, user)) : [])
            .Concat(SamePerson(user) ? named : []).DistinctBy(v => v.Id).Take(MaximumOthers).ToArray();
        return new(current, others, companion);
    }

    /// <summary>Whether <paramref name="text"/> says one of the names <paramref name="voice"/> goes by, as whole words.</summary>
    internal static bool Mentioned(KnownVoice voice, string text) =>
        new[] { voice.Name }.Concat(voice.Names.Select(n => n.Text)).OfType<string>().Any(name =>
            Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <param name="companion">The name of the persona the companion is: its lines in the excerpt carry it
    /// (<see cref="MemoryCapture.Speaker"/>).</param>
    /// <param name="tools">The request offers the people tools (<see cref="PeopleTools"/>): the text says so.</param>
    internal static VoiceNamingPrompt Prompt(VoiceNamingContext naming, string? earlierUser, string? earlierReply, string user, string reply,
        PromptSettings? prompts = null, string? companion = null, bool tools = false)
    {
        var speaker = MemoryCapture.Speaker(companion);
        var text = new StringBuilder();
        AppendVoices(text, naming, tools);
        MemoryCapture.AppendCompanion(text, companion);
        if (earlierUser is not null || earlierReply is not null)
        {
            text.Append("\nEarlier in the conversation (context only):\n");
            if (earlierUser is not null) text.Append("User: ").Append(MemoryCapture.Clip(earlierUser, 300)).Append('\n');
            if (earlierReply is not null) text.Append(speaker).Append(MemoryCapture.Clip(earlierReply, 300)).Append('\n');
        }
        text.Append("\nLatest exchange:\n").Append(UserLabel(naming.Heard))
            .Append(MemoryCapture.Clip(user, 1400)).Append('\n').Append(speaker).Append(MemoryCapture.Clip(reply, 800));
        var input = new BoundedTextInput(text.ToString(), PromptSettings.Fill(prompts, PromptCatalog.VoiceNaming, ("nothing", Nothing)));
        if (input.Utf8Bytes > LiveConversationConfiguration.DefaultTextLimits.MaxInputBytes ||
            input.InputTokenReservation > LiveConversationConfiguration.DefaultTextLimits.MaxInputTokens)
            throw new LiveActionException("conversation.input_limit");
        return new(input, naming.Tags);
    }

    /// <summary>The companion's own names, the voices heard in the message (each tag with every name it goes by, and which
    /// one spoke to Martlet), the voice the speaker sounded most like when Martlet wasn't sure, and any other voices listed
    /// for a merge or a SPOKE line. <paramref name="tools"/>: the request offers the people tools.</summary>
    internal static void AppendVoices(StringBuilder text, VoiceNamingContext naming, bool tools = false)
    {
        text.Append("Martlet's own names (the companion's, never one of the people's): ")
            .Append(string.Join(", ", naming.Companion.Names.Select(VoicePromptContext.Sanitize))).Append('\n');
        if (naming.Heard.Known.Count > 0) AppendVoices(text, naming.Heard);
        else text.Append("Voices Martlet recognized for sure in the latest message: none.\n");
        if (naming.Unattributed)
        {
            text.Append("The one speaking to Martlet: a voice Martlet couldn't tell apart for sure. ");
            if (naming.Closest is { } closest)
            {
                text.Append("It sounds most like ");
                AppendVoice(text, closest);
                text.Append(", but not enough to be sure. ");
            }
            else text.Append("It didn't sound like anyone Martlet knows. ");
            text.Append("If the words show whose voice it was, write a SPOKE line.\n");
        }
        if (naming.Others.Count > 0)
        {
            text.Append(naming.Unattributed
                ? "Other voices Martlet knows (not heard for sure in this message; only for SAME and SPOKE lines):\n"
                : "Other voices Martlet knows (not heard in this message; only for SAME lines):\n");
            foreach (var voice in naming.Others)
            {
                AppendVoice(text, voice);
                text.Append('\n');
            }
        }
        if (tools)
            text.Append($"You may call {PeopleTools.List} (by name or tag) or {PeopleTools.Lately} to look up the other people Martlet " +
                "knows by voice before you answer; a SPOKE line may name a voice they show.\n");
    }

    /// <summary>The voices heard in the message: each tag with every name it goes by, and which one spoke to Martlet.</summary>
    internal static void AppendVoices(StringBuilder text, HeardVoices heard)
    {
        text.Append("Voices heard in the latest message:\n");
        foreach (var voice in heard.Known.DistinctBy(v => v.Id))
        {
            AppendVoice(text, voice);
            if (ReferenceEquals(voice, heard.Speaker?.Voice)) text.Append(" (the one speaking to Martlet)");
            text.Append('\n');
        }
    }

    /// <summary>The tags of the voices heard in the message (V3) and their voice IDs, which the answer's lines refer to.</summary>
    internal static IReadOnlyDictionary<string, string> Voices(HeardVoices heard) =>
        heard.Known.DistinctBy(v => v.Id).ToDictionary(v => v.Tag, v => v.Id, StringComparer.OrdinalIgnoreCase);

    private static void AppendVoice(StringBuilder text, KnownVoice voice) =>
        text.Append(voice.Tag).Append(": ").Append(voice.Named ? "goes by " + string.Join(", ",
            new[] { voice.DisplayName }.Concat(voice.OtherNames).Select(VoicePromptContext.Sanitize)) : "no name yet");

    /// <summary>"User (V3): " for the latest message's speaker, "User: " without one.</summary>
    internal static string UserLabel(HeardVoices heard) => heard.Speaker?.Voice is { } speaker ? $"User ({speaker.Tag}): " : "User: ";

    /// <summary>The changes the model's answer asks for, checked against the listed voices (and those the people tools showed),
    /// the companion's names (with any name Martlet's <paramref name="reply"/> gave itself) and, for a SPOKE line, the voices
    /// that could have said a message Martlet couldn't attribute.</summary>
    internal static VoiceUpdateAnswer Parse(string? answer, VoiceNamingContext naming, string? reply) =>
        VoiceUpdates.Parse(answer, naming.Tags, naming.HeardIds, naming.Companion.WithReply(reply), naming.SpeakerIds);

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
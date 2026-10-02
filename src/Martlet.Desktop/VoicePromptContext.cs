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
        if (heard is null || heard.Voices.Count == 0) return null;
        var text = new StringBuilder();
        if (PromptSettings.Fill(prompts, PromptCatalog.Voices, ("label", Label)) is { } preamble) text.Append(preamble).Append('\n');
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
        var also = voice.OtherNames.Take(4).Select(Sanitize).ToArray();
        return (name is null ? $"someone whose name Martlet doesn't know yet (voice {voice.Tag}" : $"{name} (voice {voice.Tag}") +
            (also.Length > 0 ? "; also called " + string.Join(", ", also) : "") +
            (voice.Owner ? "; the owner of this PC" : "") +
            (heard.Added ? "; heard for the first time" : "") + ").";
    }

    internal static string Sanitize(string text) =>
        MemoryCapture.Clip(text.Replace(Label, "voices", StringComparison.OrdinalIgnoreCase).Replace("[", "(").Replace("]", ")"), VoiceRoster.MaximumNameLength);
}

internal sealed record VoiceNamingPrompt(BoundedTextInput Input, IReadOnlyDictionary<string, string> Voices);

/// <summary>Learning names from what is said: after a reply, the same Thinking model reads the exchange and answers in a tiny
/// line format which name a voice goes by ("I'm Sam", "thanks, Sam"). Each name is validated before it is added to that
/// voice's names; the owner can edit or remove it on the People page.</summary>
internal static partial class VoiceNaming
{
    internal const int MaximumNames = 3;
    internal const string Nothing = "NOTHING";

    internal const string Instructions = PromptCatalog.DefaultVoiceNamingInstructions;

    /// <summary>Whether the exchange is worth asking about: a voice with no name yet, or words that often come with a name.</summary>
    internal static bool Worth(HeardVoices heard, string user, string reply) =>
        heard.Known.Any(v => !v.Named) || NameSignal().IsMatch(user) || NameSignal().IsMatch(reply);

    internal static VoiceNamingPrompt Prompt(HeardVoices heard, string? earlierUser, string? earlierReply, string user, string reply,
        PromptSettings? prompts = null)
    {
        var voices = heard.Known.DistinctBy(v => v.Id).ToDictionary(v => v.Tag, v => v.Id, StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder("Voices heard in the latest message:\n");
        foreach (var voice in heard.Known.DistinctBy(v => v.Id))
        {
            text.Append(voice.Tag).Append(": ").Append(voice.Named ? "goes by " + string.Join(", ",
                new[] { voice.DisplayName }.Concat(voice.OtherNames).Take(5).Select(VoicePromptContext.Sanitize)) : "no name yet");
            if (ReferenceEquals(voice, heard.Speaker?.Voice)) text.Append(" (the one speaking to Martlet)");
            text.Append('\n');
        }
        if (earlierUser is not null || earlierReply is not null)
        {
            text.Append("\nEarlier in the conversation (context only):\n");
            if (earlierUser is not null) text.Append("User: ").Append(MemoryCapture.Clip(earlierUser, 300)).Append('\n');
            if (earlierReply is not null) text.Append("Martlet: ").Append(MemoryCapture.Clip(earlierReply, 300)).Append('\n');
        }
        text.Append("\nLatest exchange:\n").Append(heard.Speaker?.Voice is { } speaker ? $"User ({speaker.Tag}): " : "User: ")
            .Append(MemoryCapture.Clip(user, 1400)).Append("\nMartlet: ").Append(MemoryCapture.Clip(reply, 800));
        var input = new BoundedTextInput(text.ToString(), PromptSettings.Fill(prompts, PromptCatalog.VoiceNaming, ("nothing", Nothing)));
        if (input.Utf8Bytes > LiveConversationConfiguration.DefaultTextLimits.MaxInputBytes ||
            input.InputTokenReservation > LiveConversationConfiguration.DefaultTextLimits.MaxInputTokens)
            throw new LiveActionException("conversation.input_limit");
        return new(input, voices);
    }

    /// <summary>The (voice ID, name) pairs in the model's answer that name a listed voice with a usable name.</summary>
    internal static IReadOnlyList<(string VoiceId, string Name)> Parse(string? answer, IReadOnlyDictionary<string, string> voices,
        IEnumerable<string> forbidden)
    {
        var names = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(answer)) return names;
        var blocked = new HashSet<string>(forbidden.Append("Martlet").Append("User").Append("Unknown").Append("Nobody").Append("Someone"),
            StringComparer.OrdinalIgnoreCase);
        foreach (var raw in answer.Split('\n'))
        {
            if (names.Count == MaximumNames) break;
            var line = raw.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)
                .Trim().TrimStart('-', '*', '\u2022', '>', ' ', '\t').Trim();
            var match = NameLine().Match(line);
            if (!match.Success || !voices.TryGetValue("V" + match.Groups["n"].Value, out var id)) continue;
            if (VoiceRoster.CleanName(match.Groups["name"].Value) is not { } name || blocked.Contains(name) ||
                name.Split(' ').Length > 3 || VoiceTagName().IsMatch(name) ||
                string.Equals(name, Nothing, StringComparison.OrdinalIgnoreCase)) continue;
            if (!names.Any(n => n.Item1 == id && string.Equals(n.Item2, name, StringComparison.OrdinalIgnoreCase))) names.Add((id, name));
        }
        return names;
    }

    [GeneratedRegex(@"^NAME\s*V(?<n>\d{1,7})\s*[:=\-\u2013]\s*(?<name>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^(voice|v)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoiceTagName();

    [GeneratedRegex(@"(?i:\b(my name|name is|name's|call me|nickname|known as|introduce|meet)\b)|" +
        @"(?i:\b(hey|hi|hello|thanks|thank you|bye|goodbye|good night|good morning|sorry|okay|ok))\s*,?\s+(?!Martlet\b)\p{Lu}\p{Ll}+",
        RegexOptions.CultureInvariant)]
    private static partial Regex NameSignal();

    internal static string Describe(IReadOnlyList<(KnownVoice Voice, string Name)> learned) =>
        string.Join(" ", learned.Select(l => string.Create(CultureInfo.InvariantCulture,
            $"Learned that voice {l.Voice.Tag} goes by {l.Name}.")));
}

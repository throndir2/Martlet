using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

internal enum MemoryCaptureKind { Remember, Update, Forget }

/// <summary>One change the model asked for. A new fact (Remember) belongs to <paramref name="VoiceId"/>: the voice the model named
/// (REMEMBER V2: ...) or else the speaker's; null for no one in particular.</summary>
internal sealed record MemoryCaptureOperation(MemoryCaptureKind Kind, int? Index = null, string? Content = null, string? VoiceId = null)
{
    public override string ToString() => $"{nameof(MemoryCaptureOperation)} {{ Kind = {Kind}, Index = {Index} }}";
}

internal sealed record MemoryCapturePrompt(BoundedTextInput Input, int ShownFacts);

/// <summary>What one finished exchange changed in memory, or why it could not be remembered.</summary>
internal sealed record MemoryCaptureReport(IReadOnlyList<MemoryCaptureChange>? Changes = null, string? Failure = null)
{
    public override string ToString() => $"{nameof(MemoryCaptureReport)} {{ Changes = {Changes?.Count ?? 0}, Failure = {Failure} }}";
}

/// <summary>Remembering things that are talked about: after a reply, the same Thinking model reads the exchange plus the
/// related facts already saved and answers in a tiny line format. Every proposal is validated before anything is saved. When
/// Martlet recognized who spoke, the related facts say whose each is, the voices heard are listed and a new fact belongs to the
/// speaker unless the model names another voice heard.</summary>
internal static partial class MemoryCapture
{
    internal const int MaximumOperations = 3;
    internal const int MaximumFactCharacters = 300;
    internal const int MaximumShownFacts = 10;
    internal const string Nothing = "NOTHING";

    internal const string Instructions = PromptCatalog.DefaultMemoryCaptureInstructions;

    /// <param name="heard">The voices heard in the latest message (to say whose new facts are), or null.</param>
    /// <param name="people">The label of each voice the known facts belong to (<see cref="MemoryPeople.Labels"/>).</param>
    /// <param name="companion">The name of the persona the companion is: its lines in the excerpt carry it (<see cref="Speaker"/>).</param>
    internal static MemoryCapturePrompt Prompt(string? earlierUser, string? earlierReply, string user, string reply,
        IReadOnlyList<MemoryFact> known, PromptSettings? prompts = null, HeardVoices? heard = null,
        IReadOnlyDictionary<string, string>? people = null, string? companion = null)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (heard is { Known.Count: 0 }) heard = null;
        var speaker = Speaker(companion);
        // Drop context before the latest exchange if an unusually large excerpt would not fit the LLM input budget.
        foreach (var (earlier, shown) in new[] { (true, Math.Min(known.Count, MaximumShownFacts)), (false, Math.Min(known.Count, 4)), (false, 0) })
        {
            var text = new StringBuilder();
            AppendKnown(text, known, shown, people);
            if (heard is not null)
            {
                text.Append('\n');
                VoiceNaming.AppendVoices(text, heard);
                AppendWhose(text, heard);
            }
            AppendCompanion(text, companion);
            if (earlier && (earlierUser is not null || earlierReply is not null))
            {
                text.Append("\nEarlier in the conversation (context only):\n");
                if (earlierUser is not null) text.Append("User: ").Append(Clip(earlierUser, 300)).Append('\n');
                if (earlierReply is not null) text.Append(speaker).Append(Clip(earlierReply, 300)).Append('\n');
            }
            text.Append("\nLatest exchange:\n").Append(heard is null ? "User: " : VoiceNaming.UserLabel(heard)).Append(Clip(user, 1400))
                .Append('\n').Append(speaker).Append(Clip(reply, 800));
            try
            {
                var input = new BoundedTextInput(text.ToString(), PromptSettings.Fill(prompts, PromptCatalog.MemoryCapture, ("nothing", Nothing)));
                if (input.Utf8Bytes <= LiveConversationConfiguration.DefaultTextLimits.MaxInputBytes &&
                    input.InputTokenReservation <= LiveConversationConfiguration.DefaultTextLimits.MaxInputTokens)
                    return new(input, shown);
            }
            catch (ContractException)
            {
            }
        }
        throw new LiveActionException("conversation.input_limit");
    }

    /// <summary>The numbered facts already remembered that the answer's UPDATE and FORGET lines refer to, each starting with whose
    /// it is ("[Sam] ") when it belongs to someone.</summary>
    internal static void AppendKnown(StringBuilder text, IReadOnlyList<MemoryFact> known, int shown,
        IReadOnlyDictionary<string, string>? people = null)
    {
        text.Append("Already remembered:\n");
        if (shown == 0)
            text.Append("(nothing related)\n");
        for (var index = 0; index < shown; index++)
            text.Append(index + 1).Append(". ").Append(MemoryPromptContext.Whose(known[index], people))
                .Append(Clip(known[index].Content, 160)).Append('\n');
    }

    /// <summary>Whose a new fact is: the speaker's (by their voice tag) unless the answer names another voice heard. Nothing
    /// without a recognized voice.</summary>
    internal static void AppendWhose(StringBuilder text, HeardVoices heard)
    {
        if (heard.Known.Count == 0) return;
        text.Append(heard.Speaker?.Voice is { } speaker
            ? $"A new fact is saved as {speaker.Tag}'s, the one speaking to Martlet. For a fact about another voice listed, write "
            : "For a fact about one of the voices listed, write ").Append("REMEMBER V<number>: <fact>.\n");
    }

    /// <param name="voices">The tags of the voices heard in the message and their voice IDs (V3 → ID), which REMEMBER V3: lines
    /// may name.</param>
    /// <param name="speaker">The speaker's voice ID: whose a REMEMBER line's fact is when it names no listed voice.</param>
    internal static IReadOnlyList<MemoryCaptureOperation> Parse(string? text, int shownFacts,
        IReadOnlyDictionary<string, string>? voices = null, string? speaker = null)
    {
        var operations = new List<MemoryCaptureOperation>();
        if (string.IsNullOrWhiteSpace(text))
            return operations;
        foreach (var raw in text.Split('\n'))
        {
            if (operations.Count == MaximumOperations)
                break;
            var line = raw.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)
                .Trim().TrimStart('-', '*', '\u2022', '>', ' ', '\t').Trim();
            Match match;
            if ((match = RememberLine().Match(line)).Success)
            {
                var voice = match.Groups["v"].Success && voices?.TryGetValue("V" + match.Groups["v"].Value, out var named) == true
                    ? named : speaker;
                if (Fact(match.Groups["fact"].Value) is { } fact &&
                    !operations.Any(operation => operation.Content is { } other && operation.VoiceId == voice && SameFact(other, fact)))
                    operations.Add(new(MemoryCaptureKind.Remember, null, fact, voice));
            }
            else if ((match = UpdateLine().Match(line)).Success)
            {
                if (Index(match, shownFacts) is { } index && Fact(match.Groups["fact"].Value) is { } fact &&
                    !operations.Any(operation => operation.Index == index))
                    operations.Add(new(MemoryCaptureKind.Update, index, fact));
            }
            else if ((match = ForgetLine().Match(line)).Success)
            {
                if (Index(match, shownFacts) is { } index && !operations.Any(operation => operation.Index == index))
                    operations.Add(new(MemoryCaptureKind.Forget, index));
            }
        }
        return operations;
    }

    /// <summary>The same fact in slightly different words (same or nearly the same set of words).</summary>
    internal static bool SameFact(string left, string right)
    {
        var a = Words(left);
        var b = Words(right);
        if (a.Count == 0 || b.Count == 0)
            return false;
        var shared = a.Count(b.Contains);
        return shared / (double)(a.Count + b.Count - shared) >= 0.8;
    }

    private static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        foreach (var character in text + " ")
        {
            if (char.IsLetterOrDigit(character))
                word.Append(char.ToLowerInvariant(character));
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        return words;
    }

    private static int? Index(Match match, int shownFacts) =>
        int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        index >= 1 && index <= shownFacts ? index : null;

    private static string? Fact(string value)
    {
        var fact = value.Trim().Trim('"', '\'', '\u201C', '\u201D').Trim();
        if (fact.Length is 0 or > MaximumFactCharacters || fact.Count(char.IsLetter) < 3 ||
            fact.Any(char.IsControl) || string.Equals(fact, Nothing, StringComparison.OrdinalIgnoreCase) ||
            fact.Contains(MemoryPromptContext.Label, StringComparison.OrdinalIgnoreCase))
            return null;
        return fact;
    }

    /// <summary>"Ivy: ", the label of the companion's lines in an excerpt: the name of the persona it is ("Martlet" without one),
    /// so the Thinking model reads who said them as the character it plays.</summary>
    internal static string Speaker(string? companion) =>
        Clip(Martlet.Core.Speakers.CompanionNames.Character(companion), PersonaProfile.MaximumNameCharacters) + ": ";

    /// <summary>When the companion goes by its persona's name, a line that says the lines with that label are its own, since
    /// the Remembering and Learning names instructions call it Martlet. Nothing when it is called Martlet.</summary>
    internal static void AppendCompanion(StringBuilder text, string? companion)
    {
        var label = Speaker(companion);
        if (label == Martlet.Core.Speakers.CompanionNames.Default + ": ") return;
        text.Append("\nMartlet, the companion, goes by its persona's name here: the lines marked \"").Append(label.TrimEnd())
            .Append("\" are its own words.\n");
    }

    /// <summary>One line of at most <paramref name="maximum"/> characters, never splitting a surrogate pair.</summary>
    internal static string Clip(string text, int maximum)
    {
        var clipped = new StringBuilder(Math.Min(text.Length, maximum));
        foreach (var character in text)
        {
            if (clipped.Length == maximum)
                break;
            clipped.Append(char.IsControl(character) ? ' ' : character);
        }
        if (clipped.Length > 0 && char.IsHighSurrogate(clipped[^1]))
            clipped.Length--;
        var result = clipped.ToString().Trim();
        return clipped.Length < text.Length ? result + "\u2026" : result;
    }

    [GeneratedRegex(@"^(?:REMEMBER|NEW|ADD)(?:\s*(?:FOR|ABOUT)?\s*[(\[]?\s*V(?<v>\d{1,7})\s*[)\]]?)?\s*[:\-\u2013]\s*(?<fact>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RememberLine();

    [GeneratedRegex(@"^UPDATE\s*#?\s*(?<n>\d{1,3})\s*[:\-\u2013.)]\s*(?<fact>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateLine();

    [GeneratedRegex(@"^(?:FORGET|DELETE|REMOVE)\s*#?\s*(?<n>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForgetLine();
}

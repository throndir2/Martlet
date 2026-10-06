using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What one manage_memories call did: the answer for the model, a short outcome for the tool log (never a fact or a
/// name) and the changes, which the talk window notes like background remembering's.</summary>
internal sealed record MemoryToolOutcome(ConversationToolResult Result, string Outcome, IReadOnlyList<MemoryCaptureChange> Changes);

/// <summary>manage_memories: the reply model looks up, adds, corrects, reassigns and forgets facts in Martlet's memory when the
/// user asks it to (background remembering after each reply only sees a few related facts and can't move a fact to someone
/// else). Offered to every tool-capable reply while memory is on, always with the same text, so the start of every request stays
/// the same. Facts can belong to people Martlet knows by voice (People), named by name, tag (V3), "me" or "everyone".</summary>
internal static class MemoryTools
{
    internal const string Name = "manage_memories";
    internal const int MaximumFound = 20;
    internal const int MaximumForget = 50;
    internal const int IdLength = 8;

    internal const string Description =
        "Your long-term memory of the user and the people you know by voice. Use it when the user asks what you remember, or asks " +
        "you to remember, correct, reassign or forget something. action find: look up facts (query words and/or person) to get " +
        "their ids. remember: save a new fact. update: change one fact's text and/or whose it is (person). forget: delete facts " +
        "by id. Always find first for ids; never guess them. Facts are short standalone third-person sentences.";

    internal const string ParametersJson =
        """{"type":"object","properties":{"action":{"type":"string","enum":["find","remember","update","forget"]},"query":{"type":"string","description":"find: words to look for (empty lists the newest)."},"person":{"type":"string","description":"Whose facts: a name or voice tag like V3, \"me\" for who is speaking, or \"everyone\" for no one in particular. find: only theirs. remember/update: who it belongs to."},"fact":{"type":"string","description":"remember/update: the fact."},"ids":{"type":"array","items":{"type":"string"},"description":"update: one id; forget: one or more ids, from find."}},"required":["action"],"additionalProperties":false}""";

    internal static TextToolDefinition Definition { get; } = new(Name, Description, ParametersJson);

    private static readonly string[] Everyone = ["everyone", "everybody", "no one", "noone", "nobody", "none", "general", "anyone"];
    private static readonly string[] Me = ["me", "i", "myself", "user", "the user", "speaker", "the speaker"];

    /// <param name="speaker">The voice speaking now, whose a fact is for "me" (else the owner's voice).</param>
    internal static async Task<MemoryToolOutcome> RunAsync(DesktopMemoryService service, Guid configurationRevision, string argumentsJson,
        VoiceRoster? roster, KnownVoice? speaker, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(service);
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        if (arguments is null || Text(arguments, "action")?.ToLowerInvariant() is not { } action)
            return Refused("Pass one JSON object with an action, like {\"action\": \"find\", \"query\": \"dog\"}.");
        var person = Text(arguments, "person");
        var fact = Text(arguments, "fact");
        var ids = Ids(arguments);
        string? voice = null;
        if (person is not null)
        {
            var (resolved, problem) = Person(person, roster, speaker);
            if (problem is not null) return Refused(problem);
            voice = resolved;
        }
        switch (action)
        {
            case "find":
                return await service.UseStoreAsync(configurationRevision, false, async (store, operationToken) =>
                    Find((await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts, Text(arguments, "query"),
                        person is not null, voice, roster), token).ConfigureAwait(false);
            case "remember":
                if (Fact(fact) is not { } content)
                    return Refused($"Pass the fact: one short sentence of up to {MemoryCapture.MaximumFactCharacters} characters.");
                if (person is null) voice = speaker?.Id;
                return await service.UseStoreAsync(configurationRevision, true, async (store, operationToken) =>
                {
                    var facts = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts;
                    if (facts.FirstOrDefault(f => MemoryCapture.SameFact(f.Content, content) && Same(f.VoiceId, voice, roster)) is { } known)
                        return new MemoryToolOutcome(new($"Already remembered as {ShortId(known)}: {known.Content}"), "already remembered", []);
                    if (facts.Count >= MemoryLimits.MaximumFacts)
                        return Refused($"Memory is full ({MemoryLimits.MaximumFacts} facts). Tell the user to delete some on the Memory page.");
                    var saved = await store.SaveAsync(new()
                    {
                        Content = content,
                        Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), service.UtcNow),
                        Retention = MemoryRetention.UntilDeleted(),
                        VoiceId = voice
                    }, operationToken).ConfigureAwait(false);
                    return new MemoryToolOutcome(new($"Remembered as {ShortId(saved.Fact)}{Whose(voice, roster)}."), "remembered",
                        [new(MemoryCaptureKind.Remember, saved.Fact.Content, saved.Fact.VoiceId)]);
                }, token).ConfigureAwait(false);
            case "update":
                if (ids.Count != 1) return Refused("Pass exactly one id from find in ids.");
                string? text = null;
                if (fact is not null && (text = Fact(fact)) is null)
                    return Refused($"The fact must be one short sentence of up to {MemoryCapture.MaximumFactCharacters} characters.");
                if (text is null && person is null) return Refused("Pass the corrected fact, a person to give it to, or both.");
                return await service.UseStoreAsync(configurationRevision, true, async (store, operationToken) =>
                {
                    var facts = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts;
                    var (target, problem) = Resolve(facts, ids[0]);
                    if (target is null) return Refused(problem!);
                    var edited = await store.EditAsync(new()
                    {
                        Id = target.Id,
                        ExpectedRevision = target.Revision,
                        Content = text ?? target.Content,
                        Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), service.UtcNow),
                        Retention = target.Retention,
                        VoiceId = person is null ? target.VoiceId : voice
                    }, operationToken).ConfigureAwait(false);
                    return new MemoryToolOutcome(new($"Updated {ShortId(edited.Fact)}: {edited.Fact.Content}{Whose(edited.Fact.VoiceId, roster)}."),
                        "updated", [new(MemoryCaptureKind.Update, edited.Fact.Content, edited.Fact.VoiceId)]);
                }, token).ConfigureAwait(false);
            case "forget":
                if (ids.Count is 0 or > MaximumForget) return Refused($"Pass 1 to {MaximumForget} ids from find in ids.");
                return await service.UseStoreAsync(configurationRevision, true, async (store, operationToken) =>
                {
                    var facts = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts;
                    var targets = new List<MemoryFact>();
                    foreach (var id in ids)
                    {
                        var (target, problem) = Resolve(facts, id);
                        if (target is null) return Refused(problem!);
                        if (!targets.Contains(target)) targets.Add(target);
                    }
                    await store.DeleteManyAsync(targets.Select(t => new DeleteFactRequest
                    {
                        Id = t.Id, ExpectedRevision = t.Revision, ConsentId = Guid.NewGuid()
                    }).ToArray(), operationToken).ConfigureAwait(false);
                    return new MemoryToolOutcome(new(targets.Count == 1 ? "Forgot 1 fact." : $"Forgot {targets.Count} facts."),
                        $"forgot {targets.Count}", targets.Select(t => new MemoryCaptureChange(MemoryCaptureKind.Forget, t.Content, t.VoiceId)).ToArray());
                }, token).ConfigureAwait(false);
            default:
                return Refused("action must be find, remember, update or forget.");
        }
    }

    /// <summary>The facts that match (every query word, or the most words when none match them all), newest first, as JSON.</summary>
    internal static MemoryToolOutcome Find(IReadOnlyList<MemoryFact> facts, string? query, bool byPerson, string? voice, VoiceRoster? roster)
    {
        var words = Words(query);
        var matches = facts
            .Where(f => !byPerson || Same(f.VoiceId, voice, roster, exact: true))
            .Select(f => (Fact: f, Score: words.Count(w => f.Content.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                MemoryPeople.Label(f.VoiceId, roster)?.Contains(w, StringComparison.OrdinalIgnoreCase) == true)))
            .Where(m => words.Length == 0 || m.Score > 0)
            .OrderByDescending(m => m.Score).ThenByDescending(m => m.Fact.UpdatedAtUtc)
            .ToArray();
        var listed = matches.Take(MaximumFound).Select(m => new
        {
            id = ShortId(m.Fact),
            person = MemoryPeople.Label(m.Fact.VoiceId, roster) ?? "everyone",
            fact = m.Fact.Content,
            updated = m.Fact.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }).ToArray();
        var json = JsonSerializer.Serialize(new { count = matches.Length, shown = listed.Length, facts = listed });
        return new(new(matches.Length == 0 ? json + "\nNothing matches." : json), $"found {matches.Length}", []);
    }

    /// <summary>Whose a fact is from what the model wrote: (voice ID or null for everyone, null) or (null, why not).</summary>
    internal static (string? Voice, string? Problem) Person(string text, VoiceRoster? roster, KnownVoice? speaker)
    {
        var name = text.Trim().Trim('"', '\'', '[', ']').Trim();
        if (Everyone.Contains(name, StringComparer.OrdinalIgnoreCase)) return (null, null);
        var live = roster?.Live ?? [];
        if (Me.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (speaker?.Id ?? live.FirstOrDefault(v => v.Owner)?.Id, null);
        var tagged = name.Length > 1 && name[0] is 'V' or 'v' && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? live.FirstOrDefault(v => v.Number == number) : null;
        if (tagged is not null) return (tagged.Id, null);
        var named = live.Where(v => string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) ||
            v.Names.Any(n => string.Equals(n.Text, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (named.Length == 1) return (named[0].Id, null);
        var known = live.Count == 0 ? "nobody yet"
            : string.Join(", ", live.Take(12).Select(v => $"{MemoryPeople.Label(v)} ({v.Tag})"));
        return (null, named.Length > 1
            ? $"More than one voice goes by \"{name}\"; use their tag instead. Voices: {known}."
            : $"No one Martlet knows by voice is called \"{name}\". Voices: {known}. For someone else, use \"everyone\" and name them in the fact.");
    }

    private static (MemoryFact? Fact, string? Problem) Resolve(IReadOnlyList<MemoryFact> facts, string id)
    {
        var key = id.Trim().ToLowerInvariant();
        var found = key.Length >= 4 ? facts.Where(f => f.Id.ToString("N").StartsWith(key, StringComparison.Ordinal)).Take(2).ToArray() : [];
        return found.Length == 1 ? (found[0], null)
            : (null, $"There is no fact with id {MemoryCapture.Clip(id, 20)}. Call find for the ids; don't guess.");
    }

    internal static string ShortId(MemoryFact fact) => fact.Id.ToString("N")[..IdLength];

    private static string Whose(string? voice, VoiceRoster? roster) =>
        MemoryPeople.Label(voice, roster) is { } label ? $" (belongs to {label})" : " (about no one in particular)";

    private static bool Same(string? left, string? right, VoiceRoster? roster, bool exact = false) =>
        left is null || right is null ? !exact || left == right
            : string.Equals(MemoryPeople.Canonical(left, roster), MemoryPeople.Canonical(right, roster), StringComparison.Ordinal);

    private static string? Fact(string? value)
    {
        var fact = value?.Trim().Trim('"', '\u201C', '\u201D').Trim();
        return string.IsNullOrEmpty(fact) || fact.Length > MemoryCapture.MaximumFactCharacters || fact.Any(char.IsControl) ||
            fact.Contains(MemoryPromptContext.Label, StringComparison.OrdinalIgnoreCase) ? null : fact;
    }

    private static string[] Words(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', '"', '\'')).Where(w => w.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static List<string> Ids(JsonObject arguments)
    {
        var ids = new List<string>();
        if (arguments["ids"] is JsonArray array)
            foreach (var item in array)
                if (item is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
        if (Text(arguments, "id") is { } single) ids.Add(single);
        return ids;
    }

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static MemoryToolOutcome Refused(string text) => new(new(text, true), "refused", []);
}

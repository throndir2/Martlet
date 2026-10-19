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
        "by id. Always find first for ids; never guess them. Facts are short standalone third-person sentences. A fact kept for one " +
        "game (where) comes up only while the user plays it.";

    internal const string ParametersJson =
        """{"type":"object","properties":{"action":{"type":"string","enum":["find","remember","update","forget"]},"query":{"type":"string","description":"find: words to look for (empty lists the newest)."},"person":{"type":"string","description":"Whose facts: a name or voice tag like V3, \"me\" for who is speaking, or \"everyone\" for no one in particular. find: only theirs. remember/update: who it belongs to."},"fact":{"type":"string","description":"remember/update: the fact."},"where":{"type":"string","description":"remember/update: where the fact matters: \"everywhere\" (the default), \"here\" for only what the user does now (such as the game they play), \"games\" for any game, or a game's name."},"ids":{"type":"array","items":{"type":"string"},"description":"update: one id; forget: one or more ids, from find."}},"required":["action"],"additionalProperties":false}""";

    internal static TextToolDefinition Definition { get; } = new(Name, Description, ParametersJson);

    /// <summary>find_memories: the read-only find the reply is offered in place of manage_memories while the Memory check-in tool
    /// set takes it over (<see cref="MemoryToolSet"/>), so the reply can still answer what Martlet remembers. Always the same text.</summary>
    internal const string FindName = "find_memories";

    internal static TextToolDefinition FindDefinition { get; } = new(FindName,
        "Your long-term memory of the user and the people you know by voice. Use it when the user asks what you remember: look up " +
        "facts by query words and/or person (empty lists the newest).",
        """{"type":"object","properties":{"query":{"type":"string","description":"Words to look for (empty lists the newest)."},"person":{"type":"string","description":"Whose facts: a name or voice tag like V3, \"me\" for who is speaking, or \"everyone\" for no one in particular."}},"additionalProperties":false}""");

    /// <summary>What the reply is told while the Memory check-in tool set takes manage_memories over. Always the same text.</summary>
    internal const string HandedOffGuidance =
        "Your memory changes after your reply: when the user asks you to remember, correct, give to someone else or forget " +
        "something, say briefly that you will, and it is done after you answer. Use find_memories to look up what you remember.";

    /// <summary><paramref name="argumentsJson"/> (a JSON object, else empty) with its action set to <paramref name="action"/>, for a
    /// tool that runs one action of manage_memories.</summary>
    internal static string WithAction(string? argumentsJson, string action)
    {
        JsonObject arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? []; }
        catch (JsonException) { arguments = []; }
        arguments["action"] = action;
        return arguments.ToJsonString();
    }

    private static readonly JsonSerializerOptions FoundJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly string[] Everyone = ["everyone", "everybody", "no one", "noone", "nobody", "none", "general", "anyone"];
    private static readonly string[] Me = ["me", "i", "myself", "user", "the user", "speaker", "the speaker"];

    /// <param name="speaker">The voice speaking now, whose a fact is for "me" (else <paramref name="yours"/>: the signed-in
    /// person's voice).</param>
    /// <param name="similar">Finds facts by meaning for find's query words (best first; null when it can't now): they are fused
    /// with the facts found by their words.</param>
    /// <param name="activity">What the user does now (<see cref="MemoryActivity"/>), which "here" names.</param>
    internal static async Task<MemoryToolOutcome> RunAsync(DesktopMemoryService service, Guid configurationRevision, string argumentsJson,
        VoiceRoster? roster, KnownVoice? speaker, CancellationToken token, KnownVoice? yours = null,
        Func<string, CancellationToken, Task<IReadOnlyList<Guid>?>>? similar = null, string? activity = null)
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
            var (resolved, problem) = Person(person, roster, speaker, yours);
            if (problem is not null) return Refused(problem);
            voice = resolved;
        }
        var (setWhere, where, whereProblem) = Where(Text(arguments, "where"), activity);
        if (whereProblem is not null && action is "remember" or "update") return Refused(whereProblem);
        switch (action)
        {
            case "find":
                var query = Text(arguments, "query");
                var near = query is not null && similar is not null ? await similar(query, token).ConfigureAwait(false) : null;
                return await service.UseSpaceAsync(configurationRevision, null, false, async (set, _, store, operationToken) =>
                {
                    var here = Current((await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts);
                    var elsewhere = service.OtherFacts(set);
                    return Find([.. here, .. elsewhere.Select(f => f.Fact)], query, person is not null, voice, roster,
                        elsewhere.ToDictionary(f => f.Fact.Id, f => MemorySpaces.Label(f.Space, service.Account)), near);
                }, token).ConfigureAwait(false);
            case "remember":
                if (Fact(fact) is not { } content)
                    return Refused($"Pass the fact: one short sentence of up to {MemoryCapture.MaximumFactCharacters} characters.");
                if (person is null) voice = speaker?.Id;
                return await service.UseSpaceAsync(configurationRevision, null, true, async (set, _, store, operationToken) =>
                {
                    var all = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts;
                    var facts = Current(all);
                    if (facts.Concat(service.OtherFacts(set).Select(f => f.Fact))
                        .FirstOrDefault(f => MemoryCapture.SameFact(f.Content, content) && Same(f.VoiceId, voice, roster) &&
                            (f.Activity is null || MemoryActivity.Same(f.Activity, where))) is { } known)
                        return new MemoryToolOutcome(new($"Already remembered as {ShortId(known)}: {known.Content}"), "already remembered", []);
                    if (all.Count >= MemoryLimits.MaximumFacts)
                        return Refused($"Memory is full ({MemoryLimits.MaximumFacts:N0} facts). Tell the user to delete some on the Memory page.");
                    var saved = await store.SaveAsync(new()
                    {
                        Content = content,
                        Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), service.UtcNow),
                        Retention = MemoryRetention.UntilDeleted(),
                        VoiceId = voice,
                        Activity = where
                    }, operationToken).ConfigureAwait(false);
                    return new MemoryToolOutcome(new($"Remembered as {ShortId(saved.Fact)}{Whose(voice, roster)}, " +
                        $"{MemoryActivity.Describe(saved.Fact.Activity)}."), "remembered",
                        [new(MemoryCaptureKind.Remember, saved.Fact.Content, saved.Fact.VoiceId, Activity: saved.Fact.Activity)]);
                }, token).ConfigureAwait(false);
            case "update":
                if (ids.Count != 1) return Refused("Pass exactly one id from find in ids.");
                string? text = null;
                if (fact is not null && (text = Fact(fact)) is null)
                    return Refused($"The fact must be one short sentence of up to {MemoryCapture.MaximumFactCharacters} characters.");
                if (text is null && person is null && !setWhere) return Refused("Pass the corrected fact, a person to give it to, where it matters, or more of them.");
                return await ChangeAsync(service, configurationRevision, ids, async (store, targets, operationToken) =>
                {
                    var target = targets[0];
                    // A correction keeps the earlier version as history, dated when it stopped being true.
                    var edited = await store.EditAsync(new()
                    {
                        Id = target.Id,
                        ExpectedRevision = target.Revision,
                        Content = text ?? target.Content,
                        Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), service.UtcNow),
                        Retention = target.Retention,
                        VoiceId = person is null ? target.VoiceId : voice,
                        KeepHistory = true,
                        SetActivity = setWhere,
                        Activity = where
                    }, operationToken).ConfigureAwait(false);
                    return new MemoryToolOutcome(new($"Updated {ShortId(edited.Fact)}: {edited.Fact.Content}{Whose(edited.Fact.VoiceId, roster)}, " +
                        $"{MemoryActivity.Describe(edited.Fact.Activity)}."),
                        "updated", [new(MemoryCaptureKind.Update, edited.Fact.Content, edited.Fact.VoiceId, Activity: edited.Fact.Activity)]);
                }, token).ConfigureAwait(false);
            case "forget":
                if (ids.Count is 0 or > MaximumForget) return Refused($"Pass 1 to {MaximumForget} ids from find in ids.");
                return await ChangeAsync(service, configurationRevision, ids, async (store, targets, operationToken) =>
                {
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

    /// <summary>Finds the facts <paramref name="ids"/> name and runs <paramref name="change"/> with the store of the memory space
    /// they are kept in: the active space's store first (where almost every fact is, so it takes one store opening), else the
    /// space recall found them in. Facts in two spaces are changed in two calls; a space shared with this account is refused.</summary>
    private static async Task<MemoryToolOutcome> ChangeAsync(DesktopMemoryService service, Guid configurationRevision,
        IReadOnlyList<string> ids, Func<MemoryStore, IReadOnlyList<MemoryFact>, CancellationToken, Task<MemoryToolOutcome>> change,
        CancellationToken token)
    {
        string? elsewhere = null;
        var outcome = await service.UseSpaceAsync<MemoryToolOutcome?>(configurationRevision, null, true, async (set, _, store, operationToken) =>
        {
            var facts = Current((await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts);
            var targets = new List<MemoryFact>();
            foreach (var id in ids)
            {
                if (Resolve(facts, id).Fact is { } here)
                {
                    if (elsewhere is not null)
                        return Refused("Those facts are kept in different places (yours and the household's). Change them in separate calls.");
                    if (!targets.Contains(here)) targets.Add(here);
                    continue;
                }
                var others = service.OtherFacts(set);
                var (found, problem) = Resolve([.. others.Select(f => f.Fact)], id);
                if (found is null) return Refused(problem!);
                var space = others.First(f => f.Fact.Id == found.Id).Space;
                if (elsewhere is not null && elsewhere != space || targets.Count > 0)
                    return Refused("Those facts are kept in different places (yours and the household's). Change them in separate calls.");
                elsewhere = space;
            }
            return elsewhere is null ? await change(store, targets, operationToken).ConfigureAwait(false) : null;
        }, token).ConfigureAwait(false);
        if (outcome is not null || elsewhere is null) return outcome!;
        try
        {
            return await service.UseSpaceAsync(configurationRevision, elsewhere, true, async (_, _, store, operationToken) =>
            {
                var facts = Current((await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts);
                var targets = new List<MemoryFact>();
                foreach (var id in ids)
                {
                    var (target, problem) = Resolve(facts, id);
                    if (target is null) return Refused(problem!);
                    if (!targets.Contains(target)) targets.Add(target);
                }
                return await change(store, targets, operationToken).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (DesktopMemoryException error) when (error.Code == "memory.read_only")
        {
            return Refused("That fact is in memories someone else shares with you; only they can change it.");
        }
    }

    /// <summary>The facts that match (every query word, or the most words when none match them all), newest first, as JSON. A
    /// word matches a fact's text, whose it is or its hidden search words (which are never shown). History (replaced facts) is
    /// never listed. <paramref name="kept"/> names where a fact from another memory space than the active one is kept.
    /// <paramref name="similar"/> (facts an embedding search found for the query, best first) is fused with that order by the
    /// search engine's reciprocal rank fusion, so a fact that says it in other words is found too.</summary>
    internal static MemoryToolOutcome Find(IReadOnlyList<MemoryFact> facts, string? query, bool byPerson, string? voice, VoiceRoster? roster,
        IReadOnlyDictionary<Guid, string>? kept = null, IReadOnlyList<Guid>? similar = null)
    {
        var words = Words(query);
        var eligible = facts.Where(f => !f.IsHistory && (!byPerson || Same(f.VoiceId, voice, roster, exact: true))).ToArray();
        var matches = eligible
            .Select(f => (Fact: f, Score: words.Count(w => f.Content.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                f.SearchWords?.Contains(w, StringComparison.OrdinalIgnoreCase) == true ||
                MemoryPeople.Label(f.VoiceId, roster)?.Contains(w, StringComparison.OrdinalIgnoreCase) == true)))
            .Where(m => words.Length == 0 || m.Score > 0)
            .OrderByDescending(m => m.Score).ThenByDescending(m => m.Fact.UpdatedAtUtc)
            .ToArray();
        if (words.Length > 0 && similar is { Count: > 0 })
        {
            var number = new Dictionary<Guid, int>();
            for (var i = 0; i < eligible.Length; i++) number.TryAdd(eligible[i].Id, i);
            var lexical = matches.Select(m => number[m.Fact.Id]).ToArray();
            var meaning = similar.Where(number.ContainsKey).Select(id => number[id]).ToArray();
            matches = [.. Martlet.Core.Search.SearchFusion.Fuse([lexical, meaning]).Select(i => (eligible[i], 0))];
        }
        var listed = matches.Take(MaximumFound).Select(m => new
        {
            id = ShortId(m.Fact),
            person = MemoryPeople.Label(m.Fact.VoiceId, roster) ?? "everyone",
            fact = m.Fact.Content,
            where = m.Fact.Activity is null ? null : MemoryActivity.Describe(m.Fact.Activity),
            updated = m.Fact.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            kept = kept?.GetValueOrDefault(m.Fact.Id)
        }).ToArray();
        var json = JsonSerializer.Serialize(new { count = matches.Length, shown = listed.Length, facts = listed }, FoundJson);
        return new(new(matches.Length == 0 ? json + "\nNothing matches." : json), $"found {matches.Length}", []);
    }

    /// <summary>Whose a fact is from what the model wrote: (voice ID or null for everyone, null) or (null, why not). "Me" is the
    /// <paramref name="speaker"/>, else <paramref name="yours"/> (the signed-in person's voice, <see cref="LocalVoices.Yours"/>).</summary>
    internal static (string? Voice, string? Problem) Person(string text, VoiceRoster? roster, KnownVoice? speaker, KnownVoice? yours = null)
    {
        var name = text.Trim().Trim('"', '\'', '[', ']').Trim();
        if (Everyone.Contains(name, StringComparer.OrdinalIgnoreCase)) return (null, null);
        var live = roster?.Live ?? [];
        if (Me.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (speaker?.Id ?? yours?.Id, null);
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

    private static readonly string[] EverywhereWords = ["everywhere", "global", "always", "anywhere", "all", "life", "everything"];
    private static readonly string[] HereWords = ["here", "now", "this game", "current", "the current game", "this", "current game"];
    private static readonly string[] GamesWords = ["games", "gaming", "any game", "all games", "every game", "while gaming"];

    /// <summary>Where a fact matters from what the model wrote (<see cref="MemoryActivity"/>): (false, null, null) when it said
    /// nothing; (true, activity or null for everywhere, null); or a problem. "here" is <paramref name="now"/>; any other words are a
    /// game's name.</summary>
    internal static (bool Set, string? Activity, string? Problem) Where(string? text, string? now)
    {
        var name = text?.Trim().Trim('"', '\'', '.').Trim();
        if (string.IsNullOrEmpty(name)) return (false, null, null);
        if (EverywhereWords.Contains(name, StringComparer.OrdinalIgnoreCase)) return (true, null, null);
        if (HereWords.Contains(name, StringComparer.OrdinalIgnoreCase))
            return now is null ? (false, null, "Martlet doesn't know of a game or other activity now; use \"everywhere\", \"games\" or a game's name.")
                : (true, now, null);
        if (GamesWords.Contains(name, StringComparer.OrdinalIgnoreCase)) return (true, MemoryActivity.Gaming, null);
        foreach (var prefix in new[] { "while playing ", "playing ", "in " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..].Trim();
        return MemoryActivity.Game(name) is { } game ? (true, game, null) : (false, null, "where must be everywhere, here, games or a game's name.");
    }

    private static (MemoryFact? Fact, string? Problem) Resolve(IReadOnlyList<MemoryFact> facts, string id)
    {
        var key = id.Trim().ToLowerInvariant();
        var found = key.Length >= 4 ? facts.Where(f => f.Id.ToString("N").StartsWith(key, StringComparison.Ordinal)).Take(2).ToArray() : [];
        return found.Length == 1 ? (found[0], null)
            : (null, $"There is no fact with id {MemoryCapture.Clip(id, 20)}. Call find for the ids; don't guess.");
    }

    internal static string ShortId(MemoryFact fact) => fact.Id.ToString("N")[..IdLength];

    /// <summary>The current facts: history (replaced facts) can't be found, corrected or forgotten by id here.</summary>
    private static IReadOnlyList<MemoryFact> Current(IReadOnlyList<MemoryFact> facts) => [.. facts.Where(f => !f.IsHistory)];

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

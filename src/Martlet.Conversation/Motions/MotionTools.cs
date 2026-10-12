using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What body_motion asked for: the motion's <paramref name="Name"/> (a tag, such as <c>cheer</c>), what the body does
/// (<paramref name="Motion"/>, null when only the name was given) and how long a new one lasts.</summary>
public sealed record MotionArguments(string Name, string? Motion, double Seconds);

/// <summary>A motion the motion computer made (docs/AVATARS.md#motions-in-conversation): the VRMA clip, how long it lasts, its
/// seed, the model and where it was made, whether it is a FIXTURE test motion (not AI) and how long making it took.</summary>
public sealed record MotionMade(byte[] Clip, double Seconds, long? Seed, string Model, string Where, bool Fixture, TimeSpan Took)
{
    public override string ToString() => $"{nameof(MotionMade)} ({Clip.Length} bytes, {Seconds} s, {Where})";
}

/// <summary>Martlet moves its body in conversation (docs/AVATARS.md#motions-in-conversation): while motions are set up and the
/// character is a VRM, body_motion does a motion the character has at once, or makes a new one in the background on the motion
/// computer (HY-Motion) and plays it on the character when it is ready. The motion is kept (a <c>motion</c> creation, shared
/// with every Martlet computer), so it plays again at once, by its tag, without the motion computer. Offered the same way in
/// every request while motions are set up and the character is a VRM, last of Martlet's own tools, so the start of every
/// request stays the same. Speech never waits for a motion.</summary>
public static class MotionTools
{
    public const string Name = "body_motion";
    /// <summary>The background job kind: motion-1, motion-2...</summary>
    public const string KindName = "motion";
    public const int PerHour = 20;
    /// <summary>The motion computer's bounds (<see cref="Martlet.Core.Motion.MotionRequest"/>): 1 to 12 seconds; HY-Motion does
    /// best under 5, so a motion lasts 3 by default.</summary>
    public const double MinimumSeconds = Martlet.Core.Motion.MotionRequest.MinimumSeconds,
        MaximumSeconds = Martlet.Core.Motion.MotionRequest.MaximumSeconds, DefaultSeconds = 3;
    public const int MaximumMotionCharacters = 300;
    /// <summary>A motion's name is a tag (Companion › Emotes and motions allows 24 characters).</summary>
    public const int MaximumNameCharacters = 24;

    /// <summary>One at a time (the motion computer makes one at a time), <see cref="PerHour"/> an hour, at most 5 minutes each.
    /// Quiet: a finished motion already played, so it is only mentioned with the user's next message.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, PerHour, TimeSpan.FromMinutes(5), Doing: "Making a motion") { Quiet = true };

    public const string ParametersJson =
        """{"type":"object","properties":{"name":{"type":"string","description":"One or two English words, like cheer or deep_bow. A new motion keeps it as its tag."},"motion":{"type":"string","description":"Only for a new motion: what the body does, in plain English, starting \"A person\", like \"A person raises both fists above the head and jumps once.\" Body movement only: no feelings, places, objects or repeating."},"seconds":{"type":"number","description":"How long a new motion lasts: 1 to 12 seconds (default 3; short is best)."}},"required":["name"],"additionalProperties":false}""";

    public const string Description =
        "Move your whole body (you are a 3D character). With the name of a motion you have, you do it at once. With a new name " +
        "and a motion, it is made in the background (seconds to a minute) and you do it when it's ready; then you keep it. Use " +
        "it for a body movement none of your tags does. Never wait for it or talk about it; keep talking.";

    public static TextToolDefinition Definition { get; } = new(Name, Description, ParametersJson);

    /// <summary>The file in the account's creations folder that says a computer saw Martlet's starter motions made (or made
    /// them), so they come back neither after the owner deletes them nor on each start.</summary>
    public const string StartersFile = "motion-starters.json";

    /// <summary>Martlet's starter motions, made once for the owner's VRM characters when a motion computer is first ready
    /// (docs/AVATARS.md#starter-motions). Martlet's own gestures already nod, shake, wave, shrug and bow the head, so these add
    /// whole-body motions beside them.</summary>
    public static IReadOnlyList<MotionArguments> Starters { get; } =
    [
        new("cheer", "A person raises both fists above the head and jumps once.", 3),
        new("clap", "A person claps the hands in front of the chest four times.", 3),
        new("deep_bow", "A person bows deeply from the waist, holds it, then stands up straight.", 3),
        new("stretch", "A person stretches both arms up over the head, then lowers them.", 4),
        new("facepalm", "A person raises the right hand to cover the face and lowers the head.", 3)
    ];

    public static (MotionArguments? Arguments, string? Problem) Parse(string argumentsJson)
    {
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        var name = TagOf(Read(arguments, "name"));
        if (arguments is null || name is null)
            return (null, "Pass one JSON object with the motion's name, and for a new motion what the body does, like " +
                "{\"name\": \"cheer\", \"motion\": \"A person raises both fists above the head and jumps once.\", \"seconds\": 3}.");
        var motion = Read(arguments, "motion") is { Length: > 0 } given ? Clean(given) : null;
        if (motion is { Length: > MaximumMotionCharacters }) motion = motion[..MaximumMotionCharacters].TrimEnd();
        return (new(name, motion, Seconds(arguments["seconds"])), null);
    }

    /// <summary><paramref name="text"/> as a tag: lowercase English letters and digits, other characters as one underscore,
    /// at most <see cref="MaximumNameCharacters"/>; null when no letter is left.</summary>
    public static string? TagOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tag = new StringBuilder();
        foreach (var c in text.Trim().Trim('{', '}', '[', ']'))
        {
            if (tag.Length >= MaximumNameCharacters) break;
            if (char.IsAsciiLetterOrDigit(c)) tag.Append(char.ToLowerInvariant(c));
            else if (tag.Length > 0 && tag[^1] != '_') tag.Append('_');
        }
        var result = tag.ToString().Trim('_');
        return result.Any(char.IsAsciiLetter) ? result : null;
    }

    /// <summary>What the model is told when its motion is done at once (a tag the character already has).</summary>
    public static string Played(string tag) =>
        $"Doing {tag} now. Don't describe it; keep talking normally.";

    /// <summary>What the model is told when a new motion started: its job, and to say nothing about it.</summary>
    public static string Started(BackgroundJob job, string where, string tag)
    {
        ArgumentNullException.ThrowIfNull(job);
        return JsonSerializer.Serialize(new { status = "started", id = job.Id, name = tag, time_limit = BackgroundJobs.Duration(job.Kind.TimeLimit!.Value) }) + "\n" +
            $"It's being made on {where} and plays on you when it's ready. Don't mention it or wait for it; keep talking normally.";
    }

    /// <summary>What the model is told when a new motion needs its description.</summary>
    public static string NeedsMotion(string tag) =>
        $"You have no motion called {tag} yet. To make it, call body_motion again with name and motion: what the body does, in plain " +
        "English, starting \"A person\". Or carry on without it.";

    public static string Refused(BackgroundJobStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return start.Refusal switch
        {
            "busy" => $"Not started: {start.Message} You're still making another motion; carry on without this one and don't mention it.",
            "hourly_limit" => $"Not started: {start.Message} Carry on without it and don't mention it.",
            _ => $"Not started: {start.Message ?? "it isn't available right now."} Carry on without it and don't mention it."
        };
    }

    /// <summary>What the model is told when motions can't be made now.</summary>
    public static string Unavailable(string reason) =>
        $"Not started: {reason.TrimEnd('.')}. Carry on without the motion and don't mention it unless the user asked for it.";

    /// <summary>A finished motion as the conversation hears of it (with the user's next message): kept, and whether it played
    /// (<paramref name="notPlayed"/>: why not).</summary>
    public static string Ready(string key, string tag, MotionMade made, string? notPlayed = null)
    {
        ArgumentNullException.ThrowIfNull(made);
        return $"Motion {tag} ready: {key} ({MotionCreations.Number(made.Seconds)} s, made on {made.Where} in " +
            $"{made.Took.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s)" + (made.Fixture ? " (a FIXTURE test motion, NOT AI)" : "") +
            (notPlayed is null ? "; it played on you." : $"; it didn't play ({notPlayed.TrimEnd('.')}).") +
            $" You keep it: from the next conversation, write {{{tag}}} to do it; until then, call body_motion with name {tag}. Don't describe it.";
    }

    private static double Seconds(JsonNode? node)
    {
        double? seconds = node is JsonValue value
            ? value.TryGetValue<double>(out var number) ? number
                : value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            : null;
        return seconds is { } given && double.IsFinite(given) ? Math.Round(Math.Clamp(given, MinimumSeconds, MaximumSeconds), 1) : DefaultSeconds;
    }

    private static string? Read(JsonObject? arguments, string name) =>
        arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static string Clean(string text) =>
        string.Join(' ', new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

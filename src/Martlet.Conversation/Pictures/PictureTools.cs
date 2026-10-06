using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Pictures;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What draw_picture asked for: the description to draw from, a short title, what the user asked for in a few words,
/// what to leave out and the shape.</summary>
public sealed record DrawArguments(string Description, string Title, string About, string? Avoid, PictureShape Shape);

/// <summary>Martlet draws in conversation (docs/PICTURES.md): draw_picture draws a picture in the background where the owner set
/// pictures up (Companion › Pictures) and shows it in the talk window when it is ready; the picture is kept as a creation, so
/// perform_creation shows it again later. Offered the same way in every request while pictures are set up, so the start of every
/// request stays the same.</summary>
public static class PictureTools
{
    public const string DrawName = "draw_picture";
    /// <summary>The background job kind: picture-1, picture-2...</summary>
    public const string KindName = "picture";
    public const int PerHour = 20;
    public const int MaxTitleCharacters = 80;

    /// <summary>Two at a time, <see cref="PerHour"/> an hour, at most 10 minutes each; shared when done.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 2, PerHour, TimeSpan.FromMinutes(10), Doing: "Drawing a picture");

    public const string DrawParametersJson =
        """{"type":"object","properties":{"description":{"type":"string","description":"A vivid English description of the whole image: subject, setting, style, lighting, colours, composition."},"title":{"type":"string","description":"A short title."},"shape":{"type":"string","enum":["square","landscape","portrait","wide","tall"]},"avoid":{"type":"string","description":"Optional things to leave out."}},"required":["description"],"additionalProperties":false}""";

    public const string DrawDescription =
        "Draw a picture and show it to the user (in the background, seconds to a minute). Call it as soon as the user asks you to draw, " +
        "paint, sketch or show them a picture of something. Don't describe it in words instead.";

    public static TextToolDefinition Definition { get; } = new(DrawName, DrawDescription, DrawParametersJson);

    public static (DrawArguments? Arguments, string? Problem) Parse(string argumentsJson)
    {
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        var description = Read(arguments, "description");
        if (arguments is null || string.IsNullOrWhiteSpace(description))
            return (null, "Pass one JSON object with a description of the picture, like {\"description\": \"a watercolour fox curled up in autumn leaves\", \"title\": \"Sleepy fox\"}.");
        description = Clean(description);
        if (description.Length > PictureRequest.MaximumPromptCharacters) description = description[..PictureRequest.MaximumPromptCharacters];
        var title = Read(arguments, "title") is { Length: > 0 } given ? Clean(given) : Label(description);
        if (title.Length > MaxTitleCharacters) title = title[..MaxTitleCharacters].TrimEnd();
        var avoid = Read(arguments, "avoid") is { Length: > 0 } leave ? Clean(leave) : null;
        if (avoid is { Length: > PictureRequest.MaximumNegativeCharacters }) avoid = avoid[..PictureRequest.MaximumNegativeCharacters];
        var shape = PictureShapes.Parse(Read(arguments, "shape")) ?? PictureShape.Square;
        return (new(description, title, Label(title), avoid, shape), null);
    }

    public static PictureRequest Request(DrawArguments arguments) =>
        new() { Prompt = arguments.Description, NegativePrompt = arguments.Avoid, Shape = arguments.Shape };

    /// <summary>A few words for the talk window: at most 60 characters.</summary>
    public static string Label(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }

    /// <summary>What the model is told when the picture started: its job, and to tell the user now unless it already did.</summary>
    public static string Started(BackgroundJob job, bool toldUser, string where) =>
        JsonSerializer.Serialize(new { status = "started", id = job.Id, time_limit = BackgroundJobs.Duration(job.Kind.TimeLimit!.Value) }) + "\n" +
        $"It's being drawn now on {where}; it appears in the talk window when it's ready and a note tells you." +
        (toldUser ? " You already told the user, so add nothing more, or at most a few words."
            : " Tell the user now, in one short sentence in character, that you're drawing it.") +
        " Keep talking normally meanwhile. Don't describe the finished picture before you've seen the note.";

    public static string Refused(BackgroundJobStart start) => start.Refusal switch
    {
        "busy" => $"Not started: {start.Message} Tell the user you're still drawing the others; ask again in a moment.",
        "hourly_limit" => $"Not started: {start.Message} Tell the user lightly you need a short break from drawing.",
        _ => $"Not started: {start.Message ?? "it isn't available right now."} Tell the user you can't draw right now."
    };

    /// <summary>What the model is told when pictures aren't set up or the place can't draw now.</summary>
    public static string Unavailable(string reason) =>
        $"Not started: {reason.TrimEnd('.')}. Tell the user, in character and in a few words, that you can't draw right now and why.";

    /// <summary>A finished picture as the conversation hears of it: shown to the user already, and how to show it again.</summary>
    public static string Ready(string key, string title, PictureResult result) =>
        $"Picture ready and shown to the user in the talk window: {key}, \"{title}\" ({result.Width}x{result.Height}, drawn on {result.Where} " +
        $"in {PictureCreations.Seconds(result.Took)} s)" + (result.Fixture ? " (a FIXTURE test image, NOT AI)" : "") + ". " +
        "Say something short about it in character. To show it again later, use perform_creation with that id.";

    /// <summary>What the model is told when a kept picture is shown again (perform_creation).</summary>
    public static string Shown(string title) =>
        $"\"{title}\" is showing in the talk window now. Say a few words about it if it fits.";

    private static string? Read(JsonObject? arguments, string name) =>
        arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static string Clean(string text) =>
        string.Join(' ', new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

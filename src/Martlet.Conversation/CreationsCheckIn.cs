using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>
/// The Songs, pictures and creations tool set (docs/CONVERSATION.md#check-in-tool-sets): sing_song, play_song, draw_picture and
/// perform_creation as a check-in calls them after an exchange, with the same arguments, limits and gates as the reply's tools
/// (docs/SINGING.md, docs/PICTURES.md, docs/CREATIONS.md), and list_creations to find a creation's id. While the After each
/// exchange check-in takes these over (<see cref="CheckInToolSet.Replaces"/>), the reply only says in a few words that it will
/// do the thing (<see cref="ReplyGuidance"/>), and the check-in does it right after the reply, on the Thinking pool. stop_singing
/// (an instant control) and list_creations (the reply needs its answer) stay on the reply.
/// </summary>
public static class CreationsCheckIn
{
    public const string SetId = "songs-pictures";

    public const string SingDescription =
        "Make a song sung in the character's voice, in the background (minutes). Call it when, in this exchange, the user asked " +
        "for a song or the reply said it would make one. Leave out lyrics to have them written. One at a time.";

    public const string PlayDescription =
        "Sing a finished song now, from any point (the band leads in). Call it when, in this exchange, the user said yes to a " +
        "finished song or asked to hear it again. song_id is in the conversation's notes, or use list_creations.";

    public const string DrawDescription =
        "Draw a picture and show it to the user (in the background, seconds to a minute). Call it when, in this exchange, the user " +
        "asked for a picture or the reply said it would draw one. Describe the whole image.";

    public const string ListDescription =
        "List the things the character made before (songs, pictures), newest first: ids, titles, kinds and short descriptions. Use " +
        "it to find the id perform_creation needs.";

    public const string PerformDescription =
        "Perform, show or activate something the character made, by its id from list_creations (songs: it sings it; pictures: it " +
        "shows it in the talk window). Call it when, in this exchange, the user asked for it or the reply said it would. For a song, " +
        "options are {\"from\": \"start\"}, or resume, a section (chorus), line:N or a time like 1:05.";

    /// <summary>The reply tools this set takes over.</summary>
    public static IReadOnlyList<string> Replaced { get; } =
        [SongTools.SingName, SongTools.PlayName, PictureTools.DrawName, CreationTools.PerformName];

    /// <summary>The tools, as the model is offered them, always the same.</summary>
    public static IReadOnlyList<TextToolDefinition> Tools { get; } =
    [
        new(SongTools.SingName, SingDescription, SongTools.SingParametersJson),
        new(SongTools.PlayName, PlayDescription, SongTools.PlayParametersJson),
        new(PictureTools.DrawName, DrawDescription, PictureTools.DrawParametersJson),
        new(CreationTools.ListName, ListDescription, CreationTools.ListParametersJson),
        new(CreationTools.PerformName, PerformDescription, CreationTools.PerformParametersJson)
    ];

    /// <summary>The set a check-in chooses on its card (<see cref="CheckInToolSets.All"/>).</summary>
    public static CheckInToolSet Set { get; } = new(SetId, "Songs, pictures and creations",
        "Makes and sings songs, draws pictures and performs or shows what Martlet made, when you asked for it in the exchange. " +
        "The same limits apply as in a conversation.", Tools)
    { Replaces = Replaced };

    /// <summary>Whether a check-in's call failed in a way the user should hear about (the start was refused or the feature can't
    /// be used now), not a mistake the check-in model can correct itself (bad arguments, an unknown id).</summary>
    public static bool TellUser(ConversationToolResult result) =>
        result is { IsError: true } && result.Output.Contains("Tell the user", StringComparison.Ordinal);

    /// <summary>The short line a reply gets in place of the tools in <paramref name="handedOff"/> (of <see cref="Replaced"/>),
    /// built from that set alone so the start of every request stays the same; null when none is handed off.
    /// <paramref name="singing"/>: the reply still has stop_singing, so it hears how to act while it sings.</summary>
    public static string? ReplyGuidance(IReadOnlySet<string> handedOff, bool singing)
    {
        ArgumentNullException.ThrowIfNull(handedOff);
        var things = new List<string>();
        if (handedOff.Contains(SongTools.SingName)) things.Add("make a song");
        if (handedOff.Contains(SongTools.PlayName)) things.Add("sing a finished song once they say yes");
        if (handedOff.Contains(PictureTools.DrawName)) things.Add("draw a picture");
        if (handedOff.Contains(CreationTools.PerformName)) things.Add("perform or show something you made");
        if (things.Count == 0) return null;
        var list = things.Count == 1 ? things[0] : string.Join(", ", things.Take(things.Count - 1)) + " or " + things[^1];
        return $"When the user asks you to {list}, say in a few words in character that you will; it starts right after your " +
            "reply, and a note tells you when it's ready. Don't sing, draw or make up lyrics in words." +
            (singing ? $" While you sing, answer only when talked to, otherwise reply [{StayQuiet.Marker}]; when asked to stop, call stop_singing." : "");
    }
}

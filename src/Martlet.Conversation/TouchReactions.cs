using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>
/// The Touch reactions tool set (docs/CONVERSATION.md#how-i-react): what a check-in may call to read how the character reacts
/// to touches now and to change it for a while, as the character itself. Martlet.Avatar.Hosting's CharacterReactionTools runs
/// the calls (on the desktop, and in Martlet's MCP); every change is bounded and lasts a few hours at most, and the owner sees
/// and undoes the character's changes on Companion › Touch.
/// </summary>
public static class TouchReactions
{
    public const string SetId = "touch-reactions";
    public const string Read = "read_touch_reactions", Feel = "change_touch_feeling", React = "change_zone_reactions",
        Mood = "set_touch_mood", Undo = "undo_touch_change";

    private const string Hours = "\"hours\":{\"type\":\"number\",\"minimum\":0.25,\"maximum\":72,\"description\":\"How long the change " +
        "lasts, in hours (0.25 to 72; 6 when left out). It ends on its own then.\"}";
    private const string Why = "\"why\":{\"type\":\"string\",\"maxLength\":200,\"description\":\"Why, in one short line of your own " +
        "words. The owner reads it.\"}";

    /// <summary>The tools, as the model is offered them.</summary>
    public static IReadOnlyList<TextToolDefinition> Tools { get; } =
    [
        new(Read, "Read how you react to touches now: how you feel about each category and each zone of your body on screen, " +
            "what each zone plays, what a zone can play, your own changes in effect and how many more you may make. Call it first.",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        new(Feel, "Change how you feel about being touched on a category (head, torso, arms, lower_body, extras, intimate) or " +
            "one zone, for a while: its touches then play the reaction words you give (or that feeling's own). A feeling moves at " +
            "most 2 steps from what the owner chose.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"target\":{\"type\":\"string\",\"description\":\"A category id or a zone id from read_touch_reactions.\"}," +
            "\"feeling\":{\"type\":\"string\",\"enum\":[\"hates\",\"dislikes\",\"neutral\",\"likes\",\"loves\",\"craves\"]}," +
            "\"reactions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":3,\"description\":\"Up to 3 reaction " +
            "words from read_touch_reactions, most important first; [\\\"none\\\"] for no reaction. Leave out to play the feeling's own.\"}," +
            Hours + "," + Why + "},\"required\":[\"target\",\"feeling\",\"why\"],\"additionalProperties\":false}"),
        new(React, "Choose exactly what touching one zone plays, for a while: emotes, motions, gestures and voice sounds, by the " +
            "ids read_touch_reactions lists, in order. An empty list plays nothing.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"zone\":{\"type\":\"string\",\"description\":\"A zone id from read_touch_reactions.\"}," +
            "\"plays\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":8,\"description\":\"Ids from " +
            "read_touch_reactions, or sound:<sound> for a voice sound.\"}," +
            Hours + "," + Why + "},\"required\":[\"zone\",\"plays\",\"why\"],\"additionalProperties\":false}"),
        new(Mood, "Set a mood for every touch, for a while: shift -1 or -2 makes every touch one or two steps less liked (for " +
            "example while you are angry with or hurt by the user), 1 or 2 more liked (while you feel warm toward them). A new mood " +
            "replaces the one before.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"shift\":{\"type\":\"integer\",\"minimum\":-2,\"maximum\":2,\"description\":\"-2, -1, 1 or 2.\"}," +
            Hours + "," + Why + "},\"required\":[\"shift\",\"why\"],\"additionalProperties\":false}"),
        new(Undo, "End one of your own changes now (for example once you calmed down), or all of them: touches then play what " +
            "the owner chose again.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"change\":{\"type\":\"string\",\"description\":\"The id of one of your changes in effect, or all.\"}," +
            Why + "},\"required\":[\"change\",\"why\"],\"additionalProperties\":false}")
    ];

    /// <summary>The set a check-in chooses on its card (<see cref="CheckInToolSets.All"/>).</summary>
    public static CheckInToolSet Set { get; } = new(SetId, "Touch reactions",
        "Lets the character change how it reacts to your touches for a while, as itself: a mood for every touch, how it feels " +
        "about one part, or what one zone plays. Each change is limited and ends on its own; see and undo them on Companion › Touch.",
        Tools);
}

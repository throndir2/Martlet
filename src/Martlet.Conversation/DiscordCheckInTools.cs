using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>
/// The Discord calls and camera tool set (docs/DISCORD.md): call_on_discord and set_camera_background with the same names,
/// arguments and limits as the reply's tools of those names. The desktop runs them only while they apply (Martlet can call a
/// Discord friend, or is in the owner's Discord calls); the reply then leaves them to the After each exchange check-in.
/// </summary>
public static class DiscordCheckInTools
{
    public const string SetId = "discord";
    public const string Call = "call_on_discord", Background = "set_camera_background";

    /// <summary>The tools, as the check-in's model is offered them.</summary>
    public static IReadOnlyList<TextToolDefinition> Tools { get; } =
    [
        new(Call, "Call one of your Discord friends: you open a private voice channel in your Discord server, DM them a link and " +
            "join it. Use it only when the user just asked you to call someone on Discord, and only once for each ask.",
            """{"type":"object","properties":{"person":{"type":"string","description":"The friend's name as the user said it."}},"required":["person"],"additionalProperties":false}"""),
        new(Background, "Change your webcam background in the owner's Discord call (the camera view behind your character). Give " +
            "exactly one of: color (a plain keying color), picture (the id of one of your pictures) or draw (describe a new " +
            "picture; it's drawn in the background and becomes your background when it's ready). Use it when someone just asked " +
            "for a different background, or now and then when a new one fits the conversation.",
            """{"type":"object","properties":{"color":{"type":"string","enum":["green","blue","magenta","black"]},"picture":{"type":"string","description":"The id of one of your pictures."},"draw":{"type":"string","description":"A vivid English description of a new background picture: setting, style, lighting, colours. No people in front."},"title":{"type":"string","description":"A short title for a new picture."}},"additionalProperties":false}""")
    ];

    /// <summary>The set a check-in chooses on its card (<see cref="CheckInToolSets.All"/>). It takes over the reply's tools of
    /// the same names.</summary>
    public static CheckInToolSet Set { get; } = new(SetId, "Discord calls and camera",
        "Calls one of Martlet's Discord friends when you ask, or changes Martlet's webcam background in your Discord call. " +
        "Offered only while Martlet can call someone or is in your Discord calls.", Tools)
    {
        Replaces = [Call, Background]
    };
}

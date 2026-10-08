using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>vision_history_check: how what Martlet sees is kept in the conversation (docs/SCREEN_COMMENTARY.md), rehearsed with
/// the desktop's production code: Companion › Prompts › What you saw as a data directory's settings.json sends it, then sample
/// look replies (or the one given) through the production speech segmenter and chat stripper with the seen and chattiness tags
/// a look is offered (what is spoken and shown, the tags found and the description kept), each kept in a production
/// conversation buffer as the desktop keeps a look (passed looks in a row keep only the last), then a message that came with
/// a picture. It returns the conversation's lines as the next reply sends them and what memory and learning names may read of
/// each user line. The live conversation's looks show in the desktop log ("Vision: the conversation keeps ..."). Reads no
/// credentials and contacts nothing.</summary>
internal static class VisionHistoryCheck
{
    private static readonly string[] Samples =
    [
        "[pass] [seen: a code editor with a C# file open]",
        "[pass] [seen: the same code editor, a build running]",
        "Ooh, build's green! Nice. [seen: the editor, a green build result]",
        "[pass]"
    ];

    private static readonly string Where = VisionHistory.Screen(false, "Program.cs - Visual Studio Code", "Visual Studio Code");

    internal static async Task<object> RunAsync(string directory, string? reply, CancellationToken cancellation)
    {
        if (reply is not null && (string.IsNullOrWhiteSpace(reply) || reply.Length > 1024 || reply.Any(char.IsControl)))
            throw new ArgumentException("'reply' must be 1-1024 characters of one-line text.");
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var prompts = loaded.Settings?.Prompts;
        var instructions = SeenTags.Instructions(prompts, StayQuiet.Marker);
        IReadOnlyList<string> offered = instructions is null ? ChattinessTags.All : [.. ChattinessTags.All, .. SeenTags.All];
        var context = new ConversationContextBuffer();
        var looks = (reply is null ? Samples : [reply]).Select(text =>
        {
            var preview = SpeechTextPreview.For(text, null, silentWord: StayQuiet.Marker, controlTags: offered);
            var passed = StayQuiet.IsQuiet(preview.Shown);
            var seen = SeenTags.Description(preview.Controls);
            var replaced = context.AddLook(VisionHistory.Look(false, Where, null, seen),
                passed ? $"[{StayQuiet.Marker}]" : preview.Shown.Trim(), passed);
            return new
            {
                reply = text, spoken = preview.Spoken, shown = preview.Shown, passed, tags = preview.Controls, seen,
                replacedPassedLook = replaced,
                tagHidden = !preview.Shown.Contains(SeenTags.Opener, StringComparison.OrdinalIgnoreCase) &&
                    preview.Spoken.All(piece => !piece.Contains(SeenTags.Opener, StringComparison.OrdinalIgnoreCase))
            };
        }).ToArray();
        // A typed message that came with a picture: its words, then the line about the picture.
        const string message = "What do you think of this?";
        var described = SeenTags.Description(SpeechTextPreview.For("Looks tidy to me! [seen: a C# file with a Main method]", null,
            controlTags: offered).Controls);
        context.Add(VisionHistory.After(message, VisionHistory.WithMessage(false, Where, described)), "Looks tidy to me!");
        var history = context.Snapshot();
        return new
        {
            markers = new[] { VisionHistory.ScreenMarker, VisionHistory.CameraMarker },
            tag = SeenTags.Tag,
            prompts = new
            {
                state = loaded.State switch
                {
                    SettingsLoadState.Loaded => "loaded",
                    SettingsLoadState.FirstRun => "none",
                    _ => "unreadable"
                },
                seen = instructions
            },
            looks,
            exchanges = context.Count,
            history = history.Select(line => new
            {
                role = line.Role == TextHistoryRole.User ? "user" : "assistant",
                text = line.Text,
                vision = line.Role == TextHistoryRole.User && VisionHistory.Has(line.Text),
                memoryReads = line.Role == TextHistoryRole.User ? VisionHistory.Without(line.Text) : null
            }).ToArray()
        };
    }
}

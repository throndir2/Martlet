using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>chattiness_status: Companion › Vision › How often it comments (the same choice as Listening › Watch along) as saved
/// in a data directory, what Martlet decides tells the Thinking model, and a rehearsal of how replies switch the level: each
/// sample reply (or the one given) goes through the production speech segmenter and chat stripper with the chattiness tags a
/// reply is offered while Martlet decides, returning what is spoken and shown, the tags found and the level they switch to.
/// The level Martlet picked in a running conversation lives in the desktop (the talk window's LiveChattiness line and the
/// desktop log's "Chattiness: Martlet went from ..." lines). Reads no credentials and contacts nothing.</summary>
internal static class ChattinessCheck
{
    private static readonly string[] Samples =
    [
        "Sure, I'll keep it down while you focus. [chattiness:quiet]",
        "Ooh, this boss fight looks nasty! [Chattiness: Chatty]",
        "[pass] [chattiness:normal]",
        "[chattiness:quiet] Okay. Actually, tell me everything. [chattiness:chatty]",
        "Nice dodge!"
    ];

    internal static async Task<object> RunAsync(string directory, string? reply, CancellationToken cancellation)
    {
        if (reply is not null && (string.IsNullOrWhiteSpace(reply) || reply.Length > 1024 || reply.Any(char.IsControl)))
            throw new ArgumentException("'reply' must be 1-1024 characters of one-line text.");
        var (saved, source, watch, hearPc) = Saved(directory);
        var choice = ChattinessTags.Choice(saved);
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var prompts = loaded.Settings?.Prompts;
        var rehearsed = (reply is null ? Samples : [reply]).Select(text =>
        {
            var preview = SpeechTextPreview.For(text, null, controlTags: ChattinessTags.All);
            var level = ChattinessTags.Last(preview.Controls);
            return new
            {
                reply = text, spoken = preview.Spoken, shown = preview.Shown, silent = StayQuiet.IsQuiet(preview.Shown),
                tags = preview.Controls, switchesTo = level is { } picked ? ChattinessTags.Name(picked) : null
            };
        }).ToArray();
        return new
        {
            choice = ChattinessTags.Label(choice),
            saved,
            source,
            martletDecides = choice == ChattinessChoice.MartletDecides,
            // Replies are told about it only while there is something in the background: vision on, or hearing the PC.
            visionOn = watch,
            hearPcOn = hearPc,
            startsAt = ChattinessTags.Name(Chattiness.Normal),
            tags = ChattinessTags.All,
            prompts = new
            {
                state = loaded.State switch
                {
                    SettingsLoadState.Loaded => "loaded",
                    SettingsLoadState.FirstRun => "none",
                    _ => "unreadable"
                },
                decides = ChattinessTags.Instructions(prompts, StayQuiet.Marker),
                notes = Enum.GetValues<Chattiness>().ToDictionary(ChattinessTags.Name, level => ChattinessTags.Note(prompts, level))
            },
            rehearsal = rehearsed
        };
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): ScreenChattiness is Normal (1) unless saved; Watch and HearPc are
    // off unless saved on.
    private static (int Saved, string Source, bool Watch, bool HearPc) Saved(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var root = document.RootElement;
            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            var chosen = root.TryGetProperty("ScreenChattiness", out var number) && number.ValueKind == JsonValueKind.Number &&
                number.TryGetInt32(out var value) ? value : (int?)null;
            return (chosen ?? (int)ChattinessChoice.Normal, chosen is null ? "default" : "saved", Flag("Watch"), Flag("HearPc"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ((int)ChattinessChoice.Normal, "default", false, false);
        }
    }
}

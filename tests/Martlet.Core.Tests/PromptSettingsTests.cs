using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class PromptSettingsTests
{
    [Fact]
    public void RetiredPromptEditsAreDroppedSoOlderSettingsStillLoad()
    {
        var settings = new PromptSettings
        {
            Overrides = new Dictionary<string, string> { ["character_theme"] = "old", [PromptCatalog.Persona] = "Name: {name}" }
        };
        settings.Validate();
        Assert.Equal([PromptCatalog.Persona], settings.Overrides.Keys);
        Assert.Null(PromptCatalog.Find("character_theme"));
    }

    [Fact]
    public void ResponseStylePromptEditsAndThePersonaStyleLineAreDropped()
    {
        // What an older Martlet could save: edits to its response-style prompts, and a Persona prompt edited while it still
        // had the style line.
        var json = """
            {"overrides": {"style": "Style now: {style}", "style_helpful": "helpful.", "style_sarcastic": "snarky.",
              "style_silly": "silly.", "style_distracted": "distracted.", "style_playful_teasing": "teasing.",
              "persona": "Companion name: {name}\nPersona:\n{persona}\n\nDominant style for this reply: {style}",
              "reply_length": "Keep it short."}}
            """;
        var settings = Martlet.Core.Contracts.ContractJson.Read<PromptSettings>(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal([PromptCatalog.Persona, PromptCatalog.ReplyLength], settings.Overrides.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Companion name: {name}\nPersona:\n{persona}", settings.Overrides[PromptCatalog.Persona]);
        Assert.Equal("Companion name: Mira\nPersona:\nKind.",
            PromptSettings.Fill(settings, PromptCatalog.Persona, ("name", "Mira"), ("persona", "Kind.")));
        foreach (var id in new[] { "style", "style_helpful", "style_sarcastic", "style_silly", "style_distracted", "style_playful_teasing" })
            Assert.Null(PromptCatalog.Find(id));
        Assert.DoesNotContain("{style}", PromptCatalog.Default(PromptCatalog.Persona), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortFirstSentenceClosesSpokenRepliesJustBeforeReplyLength()
    {
        var length = PromptSettings.Fill(null, PromptCatalog.ReplyLength)!;
        var first = PromptSettings.Fill(null, PromptCatalog.ShortFirstSentence, ("silent", "pass"))!;
        Assert.Contains("short first sentence", first);
        Assert.EndsWith("write only [pass].", first);
        // On by default: a spoken reply's instructions close with it, then reply length last.
        var spoken = PromptSettings.ReplyClosing(null, null, spoken: true, "pass");
        Assert.Equal(first + "\n\n" + length, spoken);
        Assert.True(GenerationSettings.StartsShort(null));
        // The same text whatever else the reply settings say, so the start of every request stays the same.
        Assert.Equal(spoken, PromptSettings.ReplyClosing(null, new() { Temperature = 0.4, Reasoning = true }, true, "pass"));
        // A reply that isn't spoken, the setting turned off, or the prompt emptied: reply length alone, as before.
        Assert.Equal(length, PromptSettings.ReplyClosing(null, null, spoken: false, "pass"));
        Assert.Equal(length, PromptSettings.ReplyClosing(null, new() { ShortFirstSentence = false }, true, "pass"));
        var emptied = PromptSettings.Normalize(new Dictionary<string, string> { [PromptCatalog.ShortFirstSentence] = "" });
        emptied!.Validate();
        Assert.Equal(length, PromptSettings.ReplyClosing(emptied, null, true, "pass"));
        // An edit is sent as written, and reply length emptied leaves it closing alone.
        var edited = PromptSettings.Normalize(new Dictionary<string, string>
        {
            [PromptCatalog.ShortFirstSentence] = "Start short. Quiet is [{silent}].", [PromptCatalog.ReplyLength] = ""
        });
        Assert.Equal("Start short. Quiet is [pass].", PromptSettings.ReplyClosing(edited, null, true, "pass"));
        Assert.Null(PromptSettings.ReplyClosing(edited, new() { ShortFirstSentence = false }, true, "pass"));
    }

    [Fact]
    public void ShortFirstSentenceIsOnUnlessTurnedOffAndOnlyOffIsSaved()
    {
        Assert.True(new GenerationSettings().IsDefault);
        var off = new GenerationSettings { ShortFirstSentence = false };
        Assert.False(off.IsDefault);
        Assert.False(GenerationSettings.StartsShort(off));
        Assert.Same(off, GenerationSettings.Normalize(off));
        var settings = CompanionSettings.Begin(null) with { Generation = off };
        settings.Validate();
        var json = System.Text.Encoding.UTF8.GetString(Martlet.Core.Contracts.ContractJson.Write(settings));
        Assert.Contains("\"short_first_sentence\": false", json);
        Assert.Equal(off, SettingsJson.Read(System.Text.Encoding.UTF8.GetBytes(json)).Generation);
        Assert.DoesNotContain("short_first_sentence",
            System.Text.Encoding.UTF8.GetString(Martlet.Core.Contracts.ContractJson.Write(CompanionSettings.Begin(null))));
    }

    [Fact]
    public void ScreenGlancesReactToWhatTheUserDoesNotToTheirSetup()
    {
        var look = PromptSettings.Fill(null, PromptCatalog.CommentaryScreen, ("silent", "pass"))!;
        // The model first guesses the user's activity from the picture and what it saw, heard and was told lately...
        Assert.Contains("educated guess", look);
        Assert.Contains("what they are doing right now", look);
        foreach (var clue in new[] { "this picture", "your last looks", "what they said lately", "playing on their PC" })
            Assert.Contains(clue, look);
        // ...then remarks on that activity, never on the computer, its layout or how busy it looks.
        Assert.Contains("talk about that activity", look);
        Assert.Contains("Never comment on their computer or setup", look);
        Assert.Contains("\"Wow, you have such a complicated setup!\"", look);
        Assert.Contains("can't tie a remark to what they are doing or what just happened, reply [pass]", look);
        Assert.DoesNotContain("{", look);

        // The look's message, the kept [seen: ...] words, chatty moods and the screen summary all point the same way.
        Assert.EndsWith("Reply [pass] or one short remark on what they're doing.)", PromptSettings.Fill(null, PromptCatalog.GlanceScreen,
            ("title", "Boss fight"), ("remarks", ""), ("silent", "pass")));
        Assert.Contains("mainly what the user is doing", PromptSettings.Fill(null, PromptCatalog.SeenTag, ("silent", "pass")));
        Assert.Contains("never to their setup", PromptSettings.Fill(null, PromptCatalog.ChattinessChatty, ("silent", "pass")));
        Assert.Contains("never to their setup", PromptCatalog.DefaultChattinessDecidesInstructions);
        Assert.Contains("what the user did and what changed", PromptCatalog.Default(PromptCatalog.ScreenDigest));
        Assert.Contains("skip their setup", PromptCatalog.Default(PromptCatalog.ScreenDigest));
    }
}

using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>said_lately_check: what Martlet said lately (<see cref="SaidLately"/>; docs/CONVERSATION.md#what-you-said-lately),
/// rehearsed with the production code and FIXTURE sayings at fixed times (NOT anything Martlet said): what is noted (never a
/// [pass] or nothing; one line, cut to its length; the newest within the hour), the lines with the time of day and how long
/// ago, the note through the production prompt (Companion › Prompts › What you said lately; nothing when it is emptied or
/// nothing was said), which requests carry it (what Martlet says on its own, never a reply to the user's words or touches),
/// where it sits in a request (the last notes, sent once and never kept), and the Saying the same things check-in reading the
/// same lines. No model, network or credentials.</summary>
internal static class SaidLatelyCheck
{
    internal static Task<object> RunAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object? detail = null)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // 1. What is noted: what Martlet said, on one line and cut to its length; never a [pass] or nothing; the newest within
        //    the hour.
        var now = new DateTimeOffset(2026, 10, 7, 22, 17, 0, TimeSpan.FromHours(-7));
        var said = new SaidLately();
        var noted = new Dictionary<string, bool>
        {
            ["75 min ago"] = said.Add(now.AddMinutes(-75), "FIXTURE: Good evening! Ready to play?"),
            ["a [pass]"] = said.Add(now.AddMinutes(-40), "[pass]"),
            ["nothing"] = said.Add(now.AddMinutes(-39), "   "),
            ["30 min ago"] = said.Add(now.AddMinutes(-30), "FIXTURE: Ooh, that boss is almost down!"),
            ["two lines"] = said.Add(now.AddMinutes(-25), "FIXTURE: Nice dodge!\nThat was close."),
            ["12 min ago, again"] = said.Add(now.AddMinutes(-12), "FIXTURE: Ooh, that boss is almost down!"),
            ["a long one"] = said.Add(now.AddMinutes(-3), "FIXTURE: " + string.Concat(Enumerable.Repeat("so much to say ", 20))),
            ["40 s ago, again"] = said.Add(now.AddSeconds(-40), "FIXTURE: Ooh, that boss is almost down!")
        };
        var recent = said.Recent(now);
        var full = new SaidLately();
        for (var i = 12; i >= 1; i--) full.Add(now.AddMinutes(-i), $"FIXTURE remark {13 - i}");
        var newest = full.Recent(now);
        Step("what is noted", !noted["a [pass]"] && !noted["nothing"] && said.Count == 6 && recent.Count == 5 &&
            recent[0].Text == "FIXTURE: Ooh, that boss is almost down!" && recent[1].Text == "FIXTURE: Nice dodge! That was close." &&
            recent[3].Text.Length == SaidLately.MaximumCharacters + 1 && recent[3].Text.EndsWith('…') &&
            newest.Count == SaidLately.MaximumSayings && newest[0].Text == "FIXTURE remark 3" && newest[^1].Text == "FIXTURE remark 12",
            new
            {
                noted, kept = said.Count, withinTheHour = recent.Select(s => s.Text), windowMinutes = SaidLately.Window.TotalMinutes,
                maximumSayings = SaidLately.MaximumSayings, maximumCharacters = SaidLately.MaximumCharacters,
                twelveSaidKeeps = newest.Select(s => s.Text)
            });

        // 2. The lines: oldest first, each with the time of day and how long ago.
        var lines = SaidLately.Lines(recent, now);
        Step("lines with when", lines.StartsWith("- 9:47 PM (30 min ago): \"FIXTURE: Ooh, that boss is almost down!\"", StringComparison.Ordinal) &&
            lines.Contains("- 10:05 PM (12 min ago): \"FIXTURE: Ooh, that boss is almost down!\"", StringComparison.Ordinal) &&
            lines.EndsWith("- 10:16 PM (40 s ago): \"FIXTURE: Ooh, that boss is almost down!\"", StringComparison.Ordinal) &&
            lines.Split('\n').Length == recent.Count, lines.Split('\n'));

        // 3. The note through the production prompt; nothing when the owner emptied it or nothing was said lately.
        var note = SaidLately.Note(null, recent, now, StayQuiet.Marker);
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.SaidLately] = "" } };
        var mine = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.SaidLately] = "Lately ({time}):\n{said}" } };
        Step("the note", note is not null && note.StartsWith("What you said lately, oldest first (it is 10:17 PM now):\n" + lines, StringComparison.Ordinal) &&
            note.Contains("worth saying again", StringComparison.Ordinal) && note.Contains("[pass]", StringComparison.Ordinal) &&
            SaidLately.Note(emptied, recent, now, StayQuiet.Marker) is null && SaidLately.Note(null, [], now, StayQuiet.Marker) is null &&
            SaidLately.Note(mine, recent, now, StayQuiet.Marker) == "Lately (10:17 PM):\n" + lines,
            new { note, prompt = PromptCatalog.SaidLately, placeholders = PromptCatalog.Find(PromptCatalog.SaidLately)!.Placeholders });

        // 4. Which requests carry it: what Martlet says on its own while nobody waits for its first words.
        var carries = Enum.GetValues<MomentTrigger>().ToDictionary(t => t.ToString(), SaidLately.Carries);
        Step("which requests carry it", carries[nameof(MomentTrigger.Look)] && carries[nameof(MomentTrigger.Report)] &&
            carries[nameof(MomentTrigger.PcAudio)] && !carries[nameof(MomentTrigger.User)] && !carries[nameof(MomentTrigger.Touch)], carries);

        // 5. Where it sits: in the last notes of the message, which are sent with this request only and never kept, so the next
        //    request starts like this one. The talk window's line counts it.
        var block = "[MARTLET_NOTES]\n" + note + "\n[/MARTLET_NOTES]";
        var look = new BoundedTextInput("(Screen glance. Active app: FIXTURE. Active window: \"FIXTURE\". Reply [pass] or one short remark on what they're doing.)",
            "FIXTURE instructions", [], notes: "[MARTLET_NOTES]\nFIXTURE notes.\n[/MARTLET_NOTES]", context: block);
        var took = MomentTurn.Describe(false, 0, true, null, 0, said: recent.Count);
        Step("sent once, never kept", look.SentUserText.EndsWith(block, StringComparison.Ordinal) &&
            look.SentUserText.IndexOf("FIXTURE notes.", StringComparison.Ordinal) < look.SentUserText.IndexOf("What you said lately", StringComparison.Ordinal) &&
            !look.KeptUserText.Contains("What you said lately", StringComparison.Ordinal) && look.SentUserText.StartsWith(look.KeptUserText, StringComparison.Ordinal) &&
            took == "the picture and 5 things Martlet said lately",
            new { sent = look.SentUserText, kept = look.KeptUserText, turnTook = took });

        // 6. The Saying the same things check-in reads the same lines and waits until there are enough.
        var repeats = CheckIns.All(null).Single(c => c.Id == CheckIns.Repeats);
        var state = new CheckInState { Now = now, Name = "Mira", Conversation = true, Exchanged = 6, Away = TimeSpan.FromSeconds(20), Said = recent };
        var message = CheckIns.Message(repeats, state, null);
        var tooFew = CheckIns.Wait(repeats, state with { Said = [.. recent.Take(CheckIns.RepeatsSayings - 1)] }, null);
        Step("the check-in reads the same lines", message is not null && message.Contains(lines, StringComparison.Ordinal) &&
            message.Contains("REMIND:", StringComparison.Ordinal) && CheckIns.Wait(repeats, state, null) is null && tooFew is not null,
            new { message, waitsWithTwo = tooFew });

        return Task.FromResult<object>(new
        {
            passed = ok,
            note = "The production SaidLately, prompt and check-in with FIXTURE sayings at fixed times (NOT anything Martlet said).",
            steps
        });
    }
}

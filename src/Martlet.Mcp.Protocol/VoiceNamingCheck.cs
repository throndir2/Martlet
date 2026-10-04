using System.IO;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;

namespace Martlet.Mcp;

/// <summary>Rehearses learning names (Companion › People) with the production checks and changes (<see cref="CompanionNames"/>,
/// <see cref="VoiceUpdates"/> and the voice list's own name rules) on a fixture voice list in memory: the companion's own names
/// are never given to a voice, a heard voice drops one it learned by mistake, a voice keeps many names and shows the one it asked
/// for, a wrong learned name is dropped but never one the owner typed, and two voices merge into the owner's at most once per
/// exchange. An optional answer (as the Thinking model would give) is checked against the same fixture. The saved voice list is
/// never read or written; the data directory only supplies the companion's persona names. No audio, model or network is used.</summary>
internal static class VoiceNamingCheck
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string By = "mcp-check";

    /// <summary>V1 is the owner (typed Robert, learned Bob), not heard; V2 (typed also-called Sammy, learned Sam and, by mistake,
    /// the companion's Jane) and V3 (new, no name yet, speaking) are heard.</summary>
    private sealed record Fixture(VoiceRoster Roster, KnownVoice Owner, KnownVoice Mislabeled, KnownVoice Speaker)
    {
        internal IReadOnlyDictionary<string, string> Tags { get; } =
            new[] { Owner, Mislabeled, Speaker }.ToDictionary(v => v.Tag, v => v.Id, StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyCollection<string> Heard { get; } = [Mislabeled.Id, Speaker.Id];
    }

    private static readonly CompanionNames FixtureCompanion = CompanionNames.From(["Jane Doe"], ["You are Jane, a cheerful companion."]);

    internal static object Run(string? dataDirectory, string? answer, string? reply)
    {
        var (saved, source) = SavedCompanion(dataDirectory);
        var scenarios = new List<object>();
        var passed = true;
        void Scenario(string name, bool ok, string detail)
        {
            passed &= ok;
            scenarios.Add(new { name, passed = ok, detail });
        }

        (Fixture Fixture, VoiceRoster Roster, VoiceUpdateAnswer Parsed, IReadOnlyList<VoiceUpdateResult> Applied, IReadOnlyList<VoiceUpdateRefusal> Refused)
            Answer(string text, string? said = null, CompanionNames? companion = null)
        {
            var fixture = Build();
            var parsed = VoiceUpdates.Parse(text, fixture.Tags, fixture.Heard, (companion ?? FixtureCompanion).WithReply(said));
            var (roster, applied, refused) = VoiceUpdates.Apply(fixture.Roster, parsed.Updates, By, Start.AddMinutes(5));
            return (fixture, roster, parsed, applied, refused);
        }
        static bool Refused(VoiceUpdateAnswer parsed, string reason) => parsed.Updates.Count == 0 && parsed.Refused.Any(r => r.Reason == reason);

        var own = Answer("NAME V3: Jane");
        Scenario("companion-name-refused", Refused(own.Parsed, "the companion's own name"), "NAME V3: Jane while the persona is Jane Doe");
        var word = Answer("CALL V3: Doe");
        Scenario("companion-name-word-refused", Refused(word.Parsed, "the companion's own name"), "CALL V3: Doe, a word of the persona's name");
        var self = Answer("NAME V3: Kit", "Hi there! I'm Kit, by the way.");
        Scenario("reply-self-name-refused", Refused(self.Parsed, "the companion's own name"), "NAME V3: Kit after Martlet's reply said \"I'm Kit\"");
        if (saved is not null && saved.Names.Skip(1).FirstOrDefault() is { } persona)
        {
            var name = persona.Split(' ').Length > 3 ? persona.Split(' ')[0] : persona;
            var mine = Answer($"NAME V3: {name}", null, saved);
            Scenario("saved-persona-name-refused", Refused(mine.Parsed, "the companion's own name"), "NAME V3: <a saved persona's name>");
        }

        var repairFixture = Build();
        var repaired = repairFixture.Roster.DropHeardNames(repairFixture.Mislabeled.Id, FixtureCompanion.Matches, By, Start.AddMinutes(5))
            .Resolve(repairFixture.Mislabeled.Id)!;
        Scenario("heard-voice-repaired", Names(repaired).SequenceEqual(["Sammy", "Sam"]) && repaired.DisplayName == "Sam",
            $"a heard voice that learned Jane by mistake now goes by {string.Join(", ", Names(repaired))}");

        var learned = Answer("NAME V3: Alex");
        var alex = learned.Roster.Resolve(learned.Fixture.Speaker.Id)!;
        Scenario("name-learned", alex.DisplayName == "Alex" && learned.Applied.Count == 1, learned.Applied.FirstOrDefault()?.Text ?? "nothing changed");

        var many = Answer("NAME V3: Alexander\nNAME V3: Alex\nNAME V3: Lex\nNAME V3: Xander\nCALL V3: Al");
        var al = many.Roster.Resolve(many.Fixture.Speaker.Id)!;
        Scenario("many-names", al.Names.Count == 5 && al.DisplayName == "Al",
            $"V3 goes by {al.DisplayName}, also called {string.Join(", ", al.OtherNames)}");

        var wrong = Answer("NOT V2: Sam");
        var dropped = wrong.Roster.Resolve(wrong.Fixture.Mislabeled.Id)!;
        Scenario("wrong-name-dropped", !Names(dropped).Contains("Sam"), wrong.Applied.FirstOrDefault()?.Text ?? "nothing changed");

        var typed = Answer("NOT V2: Sammy");
        Scenario("typed-name-kept", typed.Refused.Any(r => r.Reason == "a name the owner typed") &&
            Names(typed.Roster.Resolve(typed.Fixture.Mislabeled.Id)!).Contains("Sammy"), "NOT V2: Sammy, a name the owner typed");

        var unheard = Answer("NAME V1: Bobby");
        Scenario("unheard-voice-refused", Refused(unheard.Parsed, "the voice wasn't heard in this message"), "NAME V1: Bobby for a voice not heard");

        var same = Answer("SAME V3: V1\nSAME V2: V1");
        var kept = same.Roster.Resolve(same.Fixture.Speaker.Id)!;
        Scenario("same-merged-into-owner", kept.Id == same.Fixture.Owner.Id && kept.Owner && kept.DisplayName == "Robert" &&
            same.Roster.Live.Count == 2, same.Applied.FirstOrDefault()?.Text ?? "nothing changed");
        Scenario("one-merge-per-exchange", same.Parsed.Refused.Any(r => r.Reason == "only one merge per exchange"),
            "a second SAME line in the same answer");

        object? checkedAnswer = null;
        if (answer is not null)
        {
            if (answer.Length > 8192) throw new ArgumentException("answer must be at most 8192 characters.");
            var given = Answer(answer, reply, saved);
            string Tag(string id) => given.Fixture.Tags.First(t => t.Value == id).Key;
            checkedAnswer = new
            {
                updates = given.Parsed.Updates.Select(u => new
                {
                    line = u.Line, kind = u.Kind.ToString().ToUpperInvariant(), voice = Tag(u.VoiceId), name = u.Name,
                    sameAs = u.SameAsId is { } other ? Tag(other) : null
                }).ToArray(),
                refused = given.Parsed.Refused.Concat(given.Refused).Select(r => new { line = r.Line, reason = r.Reason }).ToArray(),
                applied = given.Applied.Select(a => a.Text).ToArray(),
                voices = given.Roster.Live.OrderBy(v => v.Number).Select(v => new
                {
                    tag = v.Tag, display = v.DisplayName, names = Names(v), owner = v.Owner, mergedVoices = v.MergedVoices
                }).ToArray()
            };
        }

        return new
        {
            passed,
            companionNames = (saved ?? FixtureCompanion).Names,
            companionSource = source,
            fixture = "V1: the owner, typed Robert and learned Bob, not heard; V2: typed Sammy, learned Sam and (by mistake) Jane, heard; " +
                "V3: new, no name yet, speaking. The fixture companion is the persona \"Jane Doe\" (\"You are Jane, ...\").",
            scenarios,
            answer = checkedAnswer
        };
    }

    private static string[] Names(KnownVoice voice) => voice.Names.Select(n => n.Text).ToArray();

    private static Fixture Build()
    {
        var roster = VoiceRoster.Empty;
        (roster, var owner) = roster.Add(Print(1), 6, By, Start);
        (roster, var mislabeled) = roster.Add(Print(2), 4, By, Start.AddMinutes(1));
        (roster, var speaker) = roster.Add(Print(3), 3, By, Start.AddMinutes(2));
        roster = roster.SetNames(owner!.Id, "Robert", [], By, Start).AddHeardName(owner.Id, "Bob", By, Start).SetOwner(owner.Id, true, By, Start)
            .SetNames(mislabeled!.Id, null, ["Sammy"], By, Start)
            .AddHeardName(mislabeled.Id, "Jane", By, Start).AddHeardName(mislabeled.Id, "Jane", By, Start).AddHeardName(mislabeled.Id, "Sam", By, Start);
        return new(roster, roster.Resolve(owner.Id)!, roster.Resolve(mislabeled.Id)!, roster.Resolve(speaker!.Id)!);
    }

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    /// <summary>The companion's names from a data directory's saved personas, as a conversation uses them.</summary>
    internal static (CompanionNames? Names, string Source) SavedCompanion(string? dataDirectory)
    {
        if (dataDirectory is null) return (null, "fixture (no dataDirectory)");
        var path = Path.Combine(dataDirectory, "settings.json");
        if (!File.Exists(path)) return (null, "fixture (no saved settings)");
        var companion = SettingsJson.Read(File.ReadAllBytes(path)).Companion;
        return companion is null ? (null, "fixture (no saved personas)")
            : (CompanionNames.From(companion.Personas.Select(p => p.Name), companion.Personas.Select(p => p.Text)), "saved personas");
    }
}

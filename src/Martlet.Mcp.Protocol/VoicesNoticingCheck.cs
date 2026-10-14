using Martlet.Core.Speakers;

namespace Martlet.Mcp;

/// <summary>voices_noticing_check: rehearses how Companion › People notices a new voice (docs/VOICES.md#voices-heard-only-a-little)
/// with the production voice list matching (<see cref="VoiceRoster.Identify"/>) and the memory of voices heard only a little
/// (<see cref="UnplacedVoices"/>), on FIXTURE voiceprints (seeded random numbers, NOT real voices). In memory; it reads, records
/// and contacts nothing.</summary>
internal static class VoicesNoticingCheck
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

    private static float[] Voice(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    // One utterance of the voice: noise of its own, noise times the voice's size.
    private static float[] Heard(float[] voice, int variant, double noise)
    {
        var random = new Random(variant * 7919 + 13);
        var amplitude = noise * Math.Sqrt(12) / Math.Sqrt(VoicePrints.Dimension);
        return VoicePrints.Normalize(voice.Select(v => v + (float)((random.NextDouble() - 0.5) * amplitude)).ToArray());
    }

    // A different voice that scores about cosine against known.
    private static float[] Alike(float[] known, int seed, double cosine)
    {
        var other = Voice(seed);
        var along = VoicePrints.Cosine(other, known);
        var orthogonal = VoicePrints.Normalize(other.Select((v, i) => (float)(v - along * known[i])).ToArray());
        return VoicePrints.Normalize(known.Select((v, i) => (float)(cosine * v + Math.Sqrt(1 - cosine * cosine) * orthogonal[i])).ToArray());
    }

    internal static object Run()
    {
        var owner = Voice(1);
        var roster = VoiceRoster.Empty.Add(owner, 30, "desk-check", Start.AddDays(-1)).Roster;
        List<object> scenarios = [];
        var ok = true;

        // Notes each (voiceprint, seconds, minutes after the start) and says on which utterance a new voice was made, if any.
        void Scenario(string name, string expect, bool wantVoice, int? wantOn, IEnumerable<(float[] Print, double Seconds, double Minutes)> remarks)
        {
            var unplaced = new UnplacedVoices();
            var matches = new List<string>();
            int? madeOn = null;
            UnplacedGroup? group = null;
            var index = 0;
            foreach (var (print, seconds, minutes) in remarks)
            {
                index++;
                var match = roster.Identify(print);
                matches.Add($"{match.Kind.ToString().ToLowerInvariant()} {match.Score:0.00}");
                if (match.Kind == VoiceMatchKind.Known || group is not null) continue;
                // A clearly new voice with enough clean speech joins the list at once, as before.
                if (match.Kind == VoiceMatchKind.New && seconds >= Martlet.Sherpa.SpeakerEngine.MinimumCleanSeconds)
                {
                    madeOn = index;
                    group = new(print, seconds, 1);
                    continue;
                }
                if (unplaced.Note(print, seconds, Start.AddMinutes(minutes), roster) is { } made)
                {
                    madeOn = index;
                    group = made;
                }
            }
            var passed = (group is not null) == wantVoice && (wantOn is null || madeOn == wantOn);
            ok &= passed;
            scenarios.Add(new
            {
                name, expect, passed, utterances = index, matchesWithTheOwner = matches, newVoiceOn = madeOn,
                newVoice = group is null ? null : new
                {
                    utterances = group.Utterances, seconds = group.Seconds, againstTheOwner = Math.Round(roster.Identify(group.Voiceprint).Score, 2)
                },
                waiting = unplaced.Count
            });
        }

        var guest = Voice(5);
        Scenario("long", "A new voice with 2 s or more of clean speech joins the list at once.", true, 1,
            [(Heard(guest, 1, 0.3), 3.0, 0)]);
        Scenario("short remarks", "Three short remarks of a new voice within 30 minutes add it on the third.", true, 3,
            [(Heard(guest, 1, 0.5), 1.2, 0), (Heard(guest, 2, 0.5), 1.2, 4), (Heard(guest, 3, 0.5), 1.2, 9)]);
        Scenario("two remarks", "Two short remarks are not enough.", false, null,
            [(Heard(guest, 1, 0.5), 1.5, 0), (Heard(guest, 2, 0.5), 1.5, 1)]);
        Scenario("too old", "Remarks more than 30 minutes apart don't add up.", false, null,
            [(Heard(guest, 1, 0.5), 1.5, 0), (Heard(guest, 2, 0.5), 1.5, 1), (Heard(guest, 3, 0.5), 1.5, 32)]);
        Scenario("different people", "Single remarks of six different people never make one voice.", false, null,
            Enumerable.Range(10, 6).Select(seed => (Heard(Voice(seed), seed, 0.5), 1.5, (double)(seed - 10))));
        var sibling = Alike(owner, 7, 0.52);
        Scenario("sounds a little like you", "Someone each of whose remarks is an uncertain match for a known voice is added once three agree.",
            true, 3, Enumerable.Range(1, 3).Select(i => (Heard(sibling, i, 0.3), 2.5, (double)i)));
        var far = Alike(owner, 8, 0.8);
        Scenario("you, far from the microphone", "A known voice's uncertain remarks that together sound like it never make a new voice.",
            false, null, Enumerable.Range(0, 8).Select(i => (Heard(far, i, 0.9), 2.5, (double)i)));

        return new
        {
            ok, fixture = "FIXTURE voiceprints (seeded random numbers), NOT real voices or audio",
            rules = new
            {
                windowMinutes = UnplacedVoices.Window.TotalMinutes, minimumUtterances = UnplacedVoices.MinimumUtterances, minimumSeconds = UnplacedVoices.MinimumSeconds,
                agreement = UnplacedVoices.Agreement, margin = UnplacedVoices.Margin, matchThreshold = VoiceMatchOptions.Default.MatchThreshold,
                newVoiceThreshold = VoiceMatchOptions.Default.NewVoiceThreshold, minimumCleanSeconds = Martlet.Sherpa.SpeakerEngine.MinimumCleanSeconds
            },
            scenarios
        };
    }
}

using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Martlet.Conversation.Tests;

/// <summary>A held-out evaluation of how well past conversations come back: synthetic conversations (with everyday chatter around
/// them) and questions with the exchanges they should bring back. Set A was written before the ranking was changed and never
/// tuned on; set B (PastConversationsEvaluationTests.SetB.cs) was written afterwards by a separate author who never read the
/// ranking code, and was run once. Set B includes cases lexical search can't solve (synonyms such as "bicycle" for "bike").
/// Each question has groups of exchanges: a group counts as found when any one of its exchanges is in the first k (so
/// near-identical exchanges count once). recall@k is the share of groups found in the first k, MRR the mean of 1 / the rank of
/// the first expected exchange. "Recall" questions are messages that go through automatic recall (3 exchanges go in the notes);
/// "tool" questions are search_conversations calls (6 exchanges come back).</summary>
public sealed partial class PastConversationsEvaluationTests(ITestOutputHelper output) : IDisposable
{
    // Saturday 2026-10-03 15:00 in a fixed UTC-7 zone.
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-7), "Test", "Test");
    private static readonly DateTimeOffset Midnight = new(2026, 10, 3, 0, 0, 0, TimeSpan.FromHours(-7));
    private readonly string folder = Path.Combine(Path.GetTempPath(), "Martlet.Past.Eval." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private sealed record Said(string Key, string User, string Reply, string? Speaker = null);

    private sealed record Talk(int DaysAgo, int Hour, int Minute, params Said[] Exchanges);

    private sealed record Question(string Id, string? Message, string? Query, string? When, params string[][] Groups);

    private static readonly Talk[] Talks =
    [
        new(40, 10, 0,
            new Said("g1", "My tomato plants finally have flowers on them.", "That's exciting! Tomatoes usually set fruit a few weeks after flowering."),
            new Said("g2", "Should I water them every day?", "Water deeply two or three times a week instead; daily watering keeps the roots shallow."),
            new Said("g3", "Something is eating the leaves of my basil.", "Check the undersides for caterpillars or slugs, and try a ring of crushed eggshells.")),
        new(25, 18, 0,
            new Said("b1", "I baked my first sourdough loaf today!", "Congratulations! How did the crumb turn out?"),
            new Said("b2", "It was dense and a bit gummy in the middle.", "Let the starter get more active and bake it ten minutes longer; let it cool fully before slicing.")),
        new(30, 20, 0,
            new Said("l1", "I'm going to Lisbon in May with my sister.", "Lisbon in May is lovely: warm days, and the jacaranda trees start blooming.", "Sam"),
            new Said("l2", "Where should we stay?", "Alfama is charming; the Memmo Alfama hotel has a rooftop pool with a view of the river.", "Sam"),
            new Said("l3", "And what should we eat?", "Try pastéis de nata at Manteigaria and grilled sardines in a tasca.", "Sam")),
        new(12, 9, 0,
            new Said("d1", "We're thinking about adopting a dog from the shelter.", "That's wonderful. Do you have a yard, or are you in an apartment?"),
            new Said("d2", "An apartment, third floor, no elevator.",
                "Then a calm, smaller breed might suit you: a greyhound is surprisingly lazy indoors, or a cavalier king charles spaniel.")),
        new(60, 19, 0, new Said("gu1", "How do I play a G chord on guitar?",
            "Put your second finger on the low E string, third fret, first finger on the A string, second fret, and third finger on the high E, third fret.")),
        new(45, 19, 0, new Said("gu2", "How do I play a G chord on the guitar again?",
            "Second finger on the low E string, third fret, first finger on the A string, second fret, and third finger on the high E string, third fret.")),
        new(20, 19, 0, new Said("gu3", "Remind me how to play a G chord on guitar.",
            "Second finger low E third fret, first finger A string second fret, third finger high E third fret.")),
        new(8, 17, 0, new Said("gu4", "What strings should I buy for my acoustic guitar?",
            "Light gauge phosphor bronze strings are easy on the fingers; Elixir and D'Addario both make good ones.")),
        new(3, 17, 0, new Said("gu5", "My guitar teacher wants me to practice scales every day.",
            "Start with the C major scale slowly, with a metronome at 60 beats per minute.")),
        new(55, 8, 0, new Said("de1", "I have a dentist appointment on Friday for a cleaning.", "Good luck! Don't forget to floss the night before.")),
        new(6, 11, 0, new Said("de2", "The dentist said I need a filling next month.", "Fillings are quick these days; ask them about numbing gel if you're nervous.")),
        new(15, 21, 0, new Said("r1", "We had dinner at the Red Dragon last night, the dumplings were amazing.",
            "The Red Dragon's soup dumplings are famous. Did you try the pork ones?")),
        new(35, 12, 0, new Said("x1", "My red car needs new tires.", "Winter tires are worth it if you drive a lot in snow.")),
        new(22, 22, 0, new Said("x2", "I finally beat the dragon boss in Elden Ring.", "Well done! That fight takes real patience.")),
        new(50, 17, 0, new Said("x3", "What should I make for dinner tonight?", "How about a stir fry with whatever vegetables you have?")),
        new(33, 20, 0, new Said("m1", "Can you recommend a movie for tonight?", "How about The Grand Budapest Hotel? It's funny and beautiful.")),
        new(18, 20, 0, new Said("m2", "I want to watch a movie about space exploration.", "Try The Martian: an astronaut stranded on Mars grows potatoes to survive.")),
        new(10, 22, 0, new Said("m3", "That movie was too long.", "Fair. Next time I'll suggest something shorter.")),
        new(2, 23, 30, new Said("s1", "I can't sleep, my mind keeps racing about work.",
            "Try writing down tomorrow's tasks so your brain can let them go, then breathe slowly: in for four, out for six.")),
        new(14, 16, 0,
            new Said("t1", "Can we build a treehouse in the oak tree?", "A treehouse in the oak sounds great; start with a sturdy platform.", "Ana"),
            new Said("t2", "The treehouse should have a rope ladder.", "A rope ladder is fun, but add a handrail at the top.", "Leo"),
            new Said("t3", "I want the treehouse painted purple.", "Purple it is! Use exterior paint so it lasts.", "Ana")),
        new(28, 7, 30, new Said("ru1", "I jogged five kilometers this morning without stopping!", "That's a big milestone. Your next goal could be a 10K.")),
        new(9, 19, 0,
            new Said("bg1", "My mom's birthday is on the 20th, what should I get her?", "Maybe a framed family photo, or a cooking class you can take together."),
            new Said("bg2", "She loves cooking, so the class is perfect.", "Great choice. Book early, weekend classes fill up fast.")),
        new(16, 18, 0, new Said("mv1", "I'm packing boxes for the move next weekend.",
            "Label every box by room and pack a first-night box with sheets, chargers and a kettle.")),
        new(7, 13, 0, new Said("lp1", "My laptop battery dies after an hour.", "Check the battery health in settings; it may be time to replace it.")),
    ];

    // Everyday chatter around the conversations above: the words people say every day, many times, nearly the same each time.
    private static readonly (string User, string Reply)[] Chatter =
    [
        ("What's the weather like today?", "Sunny with a high of 24."),
        ("Set a timer for 10 minutes.", "Timer set for 10 minutes."),
        ("Tell me a joke.", "Why did the scarecrow win an award? He was outstanding in his field."),
        ("Play some music.", "Playing your favorites."),
        ("I'm tired today.", "Maybe take a short break and drink some water."),
        ("Good morning!", "Good morning! What's the plan for today?"),
        ("Thanks for the help.", "Anytime!"),
        ("What time is it?", "It's 3 o'clock."),
        ("Add milk to my shopping list.", "Added milk to your shopping list."),
        ("How was your day?", "Quiet, but I liked hearing about yours."),
        ("I watched a movie last night.", "Nice, was it any good?"),
        ("I need to plan my week.", "Let's list the three most important things first."),
    ];

    private static readonly Question[] Questions =
    [
        new("Q1", "Do you remember what I said about my tomato plant?", null, null, ["g1"]),
        new("Q2", "Remember when I baked bread?", null, null, ["b1"], ["b2"]),
        new("Q3", "Did I tell you about the hotel in Lisbon?", null, null, ["l2"], ["l1"]),
        new("Q4", "Do you remember which dog breed you suggested for our apartment?", null, null, ["d2"], ["d1"]),
        new("Q5", "What did we talk about regarding my guitar?", null, null, ["gu1", "gu2", "gu3"], ["gu4"], ["gu5"]),
        new("Q6", "Do you remember what the dentist said last week?", null, null, ["de2"]),
        new("Q7", "Do you remember the Red Dragon place I told you about?", null, null, ["r1"]),
        new("Q8", "Did I mention a movie about astronauts?", null, null, ["m2"]),
        new("Q9", "What did I say yesterday about not being able to sleep?", null, null, ["s1"]),
        new("Q10", "Do you remember what Ana wanted for the treehouse?", null, null, ["t3"], ["t1"]),
        new("Q11", "Remember when I told you about my jogging?", null, null, ["ru1"]),
        new("Q12", "Do you remember what I was getting my mom for her birthday?", null, null, ["bg1"], ["bg2"]),
        new("Q13", "Did I tell you I was packing boxes?", null, null, ["mv1"]),
        new("Q14", "What did you say about my laptop battery?", null, null, ["lp1"]),
        new("Q15", "Do you remember what you told me about watering?", null, null, ["g2"]),
        new("Q16", "Remember what you suggested to eat in Lisbon?", null, null, ["l3"], ["l1"]),
        new("Q17", "Did we talk about the dumplings?", null, null, ["r1"]),
        new("Q18", "Do you remember the scales my teacher wanted me to practice?", null, null, ["gu5"]),
        new("Q19", "What did we say about the shelter dog a couple of weeks ago?", null, null, ["d1"], ["d2"]),
        new("Q20", "In our last conversation about the move, what did you tell me to pack?", null, null, ["mv1"]),
        new("T1", null, "tomatoes", null, ["g1"]),
        new("T2", null, "sourdough baking", null, ["b1"], ["b2"]),
        new("T3", null, "hotel with a rooftop pool", null, ["l2"]),
        new("T4", null, "guitar", null, ["gu1", "gu2", "gu3"], ["gu4"], ["gu5"]),
        new("T5", null, "dentist", "last week", ["de2"]),
        new("T6", null, "red dragon", null, ["r1"]),
        new("T7", null, "jogging", null, ["ru1"]),
        new("T8", null, "birthday present for mom", null, ["bg1"]),
        new("T9", null, "packing", null, ["mv1"]),
        new("T10", null, "battery", null, ["lp1"]),
    ];

    /// <summary>recall@k and MRR of automatic recall and of search_conversations over the held-out questions.</summary>
    public sealed record Scores(int Questions, double RecallAt1, double RecallAt3, double RecallAt6, double Mrr);

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public async Task HeldOutRecallAndRanking(string set)
    {
        var (talks, questions) = set == "A" ? (Talks, Questions) : (TalksB, QuestionsB);
        Assert.NotEmpty(questions);
        var (history, keys, current) = await RecordAsync(talks);
        var lines = new StringBuilder();
        var recall = Evaluate(questions.Where(q => q.Message is not null), q =>
        {
            Assert.True(PastConversations.RefersToPast(q.Message), q.Id);
            return PastConversations.RankRecall(history, q.Message, Now, Zone, current);
        }, keys, lines);
        var tool = Evaluate(questions.Where(q => q.Query is not null), q =>
        {
            var (request, problem) = PastConversations.Parse(
                System.Text.Json.JsonSerializer.Serialize(new { query = q.Query, when = q.When }), Now, Zone);
            Assert.True(request is not null, problem);
            return PastConversations.RankFound(history, request!, current);
        }, keys, lines);
        var all = new Scores(recall.Questions + tool.Questions,
            Mean(recall.RecallAt1, tool.RecallAt1, recall.Questions, tool.Questions), Mean(recall.RecallAt3, tool.RecallAt3, recall.Questions, tool.Questions),
            Mean(recall.RecallAt6, tool.RecallAt6, recall.Questions, tool.Questions), Mean(recall.Mrr, tool.Mrr, recall.Questions, tool.Questions));
        output.WriteLine($"set {set}");
        output.WriteLine(lines.ToString());
        output.WriteLine($"{set} recall: {Format(recall)}");
        output.WriteLine($"{set} tool:   {Format(tool)}");
        output.WriteLine($"{set} all:    {Format(all)}");
        // Floors a little under what the ranking reached when it was written (set A: recall@3 1.000, MRR 1.000; set B: recall@3
        // 0.661, MRR 0.804; before it, BM25 over whole words: A 0.783 and 0.835, B 0.470 and 0.632), so a change that makes
        // recall worse fails here.
        var (recallFloor, mrrFloor) = set == "A" ? (0.9, 0.9) : (0.6, 0.75);
        Assert.True(all.RecallAt3 >= recallFloor, $"recall@3 {all.RecallAt3:0.000} < {recallFloor}");
        Assert.True(all.Mrr >= mrrFloor, $"MRR {all.Mrr:0.000} < {mrrFloor}");
    }

    private static double Mean(double a, double b, int na, int nb) => (a * na + b * nb) / (na + nb);

    private static string Format(Scores s) => string.Create(CultureInfo.InvariantCulture,
        $"questions {s.Questions}, recall@1 {s.RecallAt1:0.000}, recall@3 {s.RecallAt3:0.000}, recall@6 {s.RecallAt6:0.000}, MRR {s.Mrr:0.000}");

    private static Scores Evaluate(IEnumerable<Question> questions, Func<Question, IReadOnlyList<HistoryExchange>> rank,
        IReadOnlyDictionary<Guid, string> keys, StringBuilder lines)
    {
        double at1 = 0, at3 = 0, at6 = 0, mrr = 0;
        var count = 0;
        foreach (var question in questions)
        {
            var ranked = rank(question).Select(exchange => keys.GetValueOrDefault(exchange.Id, "?")).ToArray();
            double Found(int k) => question.Groups.Count(group => ranked.Take(k).Any(group.Contains)) / (double)question.Groups.Length;
            var first = Array.FindIndex(ranked, key => question.Groups.Any(group => group.Contains(key)));
            at1 += Found(1);
            at3 += Found(3);
            at6 += Found(6);
            mrr += first < 0 ? 0 : 1.0 / (first + 1);
            count++;
            lines.Append(question.Id).Append(": ").AppendJoin(' ', ranked.Take(6)).Append('\n');
        }
        return new(count, at1 / count, at3 / count, at6 / count, mrr / count);
    }

    private async Task<(ConversationHistory History, IReadOnlyDictionary<Guid, string> Keys, Guid Current)> RecordAsync(IEnumerable<Talk> talks)
    {
        Directory.CreateDirectory(folder);
        var keys = new Dictionary<Guid, string>();
        var exchanges = new List<HistoryExchange>();
        foreach (var talk in talks)
        {
            var conversation = Guid.NewGuid();
            var at = Midnight.AddDays(-talk.DaysAgo).AddHours(talk.Hour).AddMinutes(talk.Minute);
            foreach (var said in talk.Exchanges)
            {
                var exchange = new HistoryExchange(Guid.NewGuid(), conversation, at.ToUniversalTime(), HistoryInputKind.Typed, said.User, said.Reply,
                    said.Speaker);
                keys[exchange.Id] = said.Key;
                exchanges.Add(exchange);
                at = at.AddMinutes(2);
            }
        }
        for (var i = 0; i < 160; i++)
        {
            var (user, reply) = Chatter[i % Chatter.Length];
            var at = Midnight.AddDays(-(1 + i * 7 % 89)).AddHours(8 + i % 12).AddMinutes(i % 50);
            exchanges.Add(new(Guid.NewGuid(), Guid.NewGuid(), at.ToUniversalTime(), HistoryInputKind.Spoken, user, reply, null));
        }
        var current = Guid.NewGuid();
        exchanges.Add(new(Guid.NewGuid(), current, Now.AddMinutes(-10), HistoryInputKind.Typed, "What's for dinner tonight? Something with guitar vibes.",
            "Pasta?", null));
        foreach (var month in exchanges.GroupBy(exchange => ConversationHistory.FileName(exchange.At)))
            await File.WriteAllLinesAsync(Path.Combine(folder, month.Key), month.OrderBy(exchange => exchange.At).Select(ConversationHistory.ToLine));
        var history = new ConversationHistory(folder);
        await history.LoadAsync();
        Assert.Equal(exchanges.Count, history.Stats.Exchanges);
        return (history, keys, current);
    }
}
